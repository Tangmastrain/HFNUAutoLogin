using System.Net.NetworkInformation;
using CampusAutoLogin.Auth;
using CampusAutoLogin.Platform;
using CampusAutoLogin.Storage;
using Microsoft.Win32;

namespace CampusAutoLogin.Core;

/// <summary>
/// 调度中枢：连通性巡检 → 未联网则自动登录 → 失败按类型分流（网络类指数退避重试 / 业务类停止重试）。
/// 事件驱动加成：网线插拔、休眠唤醒立即触发，把「最多等 60 秒」变成「秒级响应」。
/// </summary>
public sealed class Coordinator : IDisposable
{
    private readonly PortalAuthProvider _auth;
    private readonly AppConfigStore _cfg;
    private readonly CredentialStore _creds;
    private readonly RetryPolicy _retry = new();

    private readonly object _sigLock = new();
    private TaskCompletionSource _signal = NewSignal();

    private CancellationTokenSource _cts = new();
    private Task? _loop;
    private volatile bool _paused;
    private bool _disposed;

    private volatile string _currentStatus = "";
    private long _lastLoginTicks; // DateTime.Now.ToBinary()，0 表示从未成功登录

    private volatile bool _hotspotOkThisPeriod; // 当前在线周期热点已开启成功（防抖）
    private long _lastHotspotAttemptTicks;      // 上次热点尝试时间（UtcNow.Ticks），失败后 5 分钟冷却

    public event Action<string>? StatusChanged;

    /// <summary>当前状态快照（string 引用写原子；供状态窗主动拉取，不依赖事件时序）。</summary>
    public string CurrentStatus => _currentStatus;

    /// <summary>上次登录成功时间（null = 尚未成功过）。用 long 存储避免 DateTime? 跨线程读撕裂。</summary>
    public DateTime? LastLoginAt => _lastLoginTicks == 0
        ? null
        : DateTime.FromBinary(_lastLoginTicks);

    public Coordinator(PortalAuthProvider auth, AppConfigStore cfg, CredentialStore creds)
    {
        _auth = auth;
        _cfg = cfg;
        _creds = creds;
    }

    public void Start()
    {
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        SystemEvents.PowerModeChanged += OnPowerMode;
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    // ------------------------------------------------ 触发控制（线程安全）

    /// <summary>立即执行一次检测/登录，并清除暂停状态（托盘菜单「立即登录」/ 设置保存后调用）。</summary>
    public void TriggerNow()
    {
        _paused = false;
        Wake();
    }

    /// <summary>暂停自动登录（托盘菜单「断开连接」/ 主窗口「自动登录」开关关闭后调用）。</summary>
    public void Pause()
    {
        _paused = true;
        Wake(); // 唤醒循环以便其进入暂停等待
    }

    /// <summary>
    /// 立即注销并暂停自动登录（主窗口「断开」按钮 / 托盘「断开连接」菜单共用），
    /// 避免注销后被立刻拉回。
    /// </summary>
    public async Task LogoutAsync()
    {
        Pause();
        SetStatus("已断开");
        var r = await _auth.LogoutAsync(CancellationToken.None);
        Logger.Instance.Info("LOGOUT " + r.Message);
        if (!r.Success)
            SetStatus("注销失败");
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Wake()
    {
        lock (_sigLock) _signal.TrySetResult();
    }

    // ------------------------------------------------ 事件钩子

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        if (e.IsAvailable) Wake(); // 网线插上 / Wi-Fi 连上（不清除暂停状态）
    }

    private void OnPowerMode(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Wake(); // 休眠唤醒（不清除暂停状态）
    }

    // ------------------------------------------------ 主循环

    private async Task RunAsync(CancellationToken ct)
    {
        SetStatus("正在检测…");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_paused)
                {
                    SetStatus("自动登录已暂停");
                    await WaitAsync(Timeout.Infinite, ct); // 等待 TriggerNow
                    continue;
                }

                bool online = await ConnMonitor.IsOnlineAsync(ct);
                if (online)
                {
                    _retry.Reset();
                    SetStatus("已连接");
                    MaybeStartHotspot();
                    await WaitAsync(_cfg.Load().CheckIntervalSec, ct);
                    continue;
                }

                _hotspotOkThisPeriod = false; // 掉线 → 进入新的在线周期，允许再次尝试开热点

                var cred = _creds.Load();
                if (cred is null || string.IsNullOrEmpty(cred.Username))
                {
                    SetStatus("未配置账号");
                    await WaitAsync(15, ct);
                    continue;
                }

                SetStatus("正在登录…");
                var r = await _auth.LoginAsync(cred, ct);
                _log(r.Success ? $"AUTH 登录成功" : $"AUTH 失败[{r.FailureKind}] {r.Message}");

                if (r.Success)
                {
                    _retry.Reset();
                    Interlocked.Exchange(ref _lastLoginTicks, DateTime.Now.ToBinary());
                    SetStatus("已连接");
                    MaybeStartHotspot(); // 登录成功 → 联动开热点
                    await WaitAsync(_cfg.Load().CheckIntervalSec, ct);
                }
                else if (r.FailureKind == AuthFailureKind.Business)
                {
                    // 密码错误等业务失败：停止自动重试，防止触发账号安全锁定
                    SetStatus("登录失败：" + r.Message);
                    await WaitAsync(600, ct); // 10 分钟后才允许自动再试；可随时手动触发
                }
                else
                {
                    // 网络类失败：指数退避 5s → 10s → ... → 300s
                    SetStatus("重试中…");
                    int wait = _retry.Next();
                    await WaitAsync(wait, ct);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log("ERROR 调度异常：" + ex.Message);
                try { await WaitAsync(10, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void _log(string msg) => Platform.Logger.Instance.Info(msg);

    /// <summary>
    /// 已在线时尝试开启移动热点（fire-and-forget）。
    /// 防抖：同一在线周期只尝试一次；失败（含不支持热点的机器）至少间隔 5 分钟才重试。
    /// 开关关闭时不触发；绝不抛出、绝不影响主登录流程。
    /// </summary>
    private void MaybeStartHotspot()
    {
        try
        {
            var cfg = _cfg.Load();
            if (!cfg.HotspotEnabled) return;
            if (_hotspotOkThisPeriod) return;

            long last = Interlocked.Read(ref _lastHotspotAttemptTicks);
            if (last != 0 && DateTime.UtcNow.Ticks - last < TimeSpan.FromMinutes(5).Ticks) return;

            Interlocked.Exchange(ref _lastHotspotAttemptTicks, DateTime.UtcNow.Ticks);
            string ssid = cfg.HotspotSsid;
            string pass = cfg.HotspotPassword;

            _ = Task.Run(async () =>
            {
                bool ok = await HotspotManager.EnsureStartedAsync(ssid, pass);
                if (ok) _hotspotOkThisPeriod = true;
            });
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("HOTSPOT 调度异常：" + ex.Message);
        }
    }

    private void SetStatus(string s)
    {
        _currentStatus = s;
        StatusChanged?.Invoke(s);
    }

    /// <summary>
    /// 等待 n 秒或被唤醒（秒级响应）。seconds == Timeout.Infinite 表示无限等待直到触发。
    /// </summary>
    private async Task WaitAsync(int seconds, CancellationToken ct)
    {
        Task wakeTask;
        lock (_sigLock)
        {
            wakeTask = _signal.Task;
            if (wakeTask.IsCompleted)
            {
                _signal = NewSignal(); // 消费掉积压信号，立即返回
                return;
            }
        }

        if (seconds == Timeout.Infinite)
        {
            await wakeTask.WaitAsync(ct);
            ResetSignal(wakeTask);
            return;
        }

        var delay = Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)), ct);
        var done = await Task.WhenAny(wakeTask.WaitAsync(ct), delay);
        if (done != delay)
            ResetSignal(wakeTask);
        // 超时路径不重置信号：若等待期间/循环体执行期间有新触发，下次等待立即返回，事件不丢失
    }

    private void ResetSignal(Task consumed)
    {
        lock (_sigLock)
        {
            if (ReferenceEquals(_signal.Task, consumed))
                _signal = NewSignal();
        }
    }

    // ------------------------------------------------ 释放

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        SystemEvents.PowerModeChanged -= OnPowerMode;

        try { _loop?.Wait(3000); } catch { }
        _auth.Dispose();
        _cts.Dispose();
    }
}
