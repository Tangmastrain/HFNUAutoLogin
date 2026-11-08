using CampusAutoLogin.Auth;
using CampusAutoLogin.Core;
using CampusAutoLogin.Platform;
using CampusAutoLogin.Storage;

namespace CampusAutoLogin.UI;

/// <summary>
/// 托盘常驻宿主：NotifyIcon + MainWindow（程序的脸面）。
/// 托盘单击/双击 = 弹主窗口；菜单：打开主界面 / 立即登录 / 断开连接 / 高级设置 / 查看日志 / 退出。
/// 主窗口 X / Alt+F4 只隐藏，真正退出走「退出」菜单。
/// </summary>
public sealed class TrayContext : ApplicationContext
{
    private const string BaseText = "合师锦绣";

    private readonly NotifyIcon _icon;
    private readonly Coordinator _coord;
    private readonly PortalAuthProvider _auth;
    private readonly AppConfigStore _cfgStore = new();
    private readonly CredentialStore _credStore = new();
    private readonly SynchronizationContext _ui;
    private readonly MainWindow _main;

    private string _lastStatus = "";
    private bool _wasConnected;

    public TrayContext()
    {
        // Program.Main 已安装 WindowsFormsSynchronizationContext，此处捕获即为 UI 线程派发器（P1 修复）
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        var cfg = _cfgStore.Load();
        _auth = new PortalAuthProvider(_cfgStore.Load);
        _coord = new Coordinator(_auth, _cfgStore, _credStore);
        _coord.StatusChanged += status => _ui.Post(_ => ApplyStatus(status), null);

        _icon = new NotifyIcon
        {
            Icon = AppIcon.ForStatus("启动中"),
            Visible = true,
            Text = BaseText + " · 启动中…", // NotifyIcon.Text 上限 63 字符
        };

        // 主窗口先构造（不显示；需要托盘图标引用做「已最小化」气泡），再挂托盘事件
        _main = new MainWindow(_coord, _auth, _cfgStore, _credStore, _icon);
        _icon.ContextMenuStrip = BuildMenu();
        _icon.DoubleClick += (_, _) => _main.ShowMain();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && e.Clicks == 1) _main.ShowMain();
        };

        // 版本号派生自 csproj InformationalVersion（带 "+<提交哈希>" 后缀，需截断）
        Logger.Instance.Info($"启动完成 v{Application.ProductVersion.Split('+')[0]}，模式=托盘常驻，hideOnStartup={cfg.HideOnStartup}");
        _coord.Start();
        if (!cfg.AutoLogin)
            _coord.Pause(); // 上次关闭时关了自动登录 → 本次启动保持暂停

        // v2.0.3 自启自检（取代 v2.0.2 的「升级迁移」判定）：cfg.AutoStart 为真时，每次启动都
        // 核对三条自启轨道（开机任务 / 登录任务 / 注册表 Run 键）。旧逻辑只在「已有登录轨道、
        // 只差开机轨道」时才补装，三轨被整体清除后返回 false → 开机自启静默失效（2026-10-16 实测：
        // 任务被清、config 仍为 true、启动无任何补装动作、日志零记录）。改为缺失即重建。
        // 静默后台执行，绝不阻塞 UI。
        _ = Task.Run(() =>
        {
            try
            {
                if (!cfg.AutoStart) return;

                bool anyInstalled = AutoStart.IsInstalled();
                bool bootMissing = !AutoStart.BootTaskExists();

                // 覆盖两种缺失：① 三条轨道全被外部清除（本次事故）② 只有登录轨道、缺开机轨道
                // （非管理员首次运行 / 由 v2.0.1 升级上来）。Install 幂等，非管理员时自然只保住登录轨道。
                if (!anyInstalled || bootMissing)
                {
                    bool ok = AutoStart.Install(cfg.StartupDelaySec);
                    if (!anyInstalled)
                        Logger.Instance.Info(ok
                            ? "AUTOSTART 自检：三条自启轨道均缺失，已重建"
                            : "AUTOSTART 自检：三条自启轨道均缺失，且重建失败（三轨均不可用）");

                    bool bootOk = AutoStart.BootTrackInstalled;
                    if (ok && (bootOk || !anyInstalled))
                        _ui.Post(_ => _icon.ShowBalloonTip(4000, BaseText,
                            bootOk ? "开机自启已自动恢复（开机即联网）" : "登录自启已自动恢复",
                            ToolTipIcon.Info), null);
                }

                // 开机轨道在但机器域凭据副本丢失 → SYSTEM 实例解不开密码，静默补一份（幂等）
                if (AutoStart.BootTaskExists() && !_credStore.BootCopyExists())
                {
                    bool credOk = _credStore.WriteBootCopy();
                    Logger.Instance.Info("AUTOSTART 开机轨道在但机器域凭据副本缺失，"
                        + (credOk ? "已补导出" : "补导出失败（未保存密码？）"));
                }
            }
            catch (Exception ex)
            {
                Logger.Instance.Warn("AUTOSTART 自检异常：" + ex.Message);
            }
        });

        // 首次运行未配置账号 → 必弹主窗口；否则按「启动时隐藏窗口」决定
        if (!cfg.FirstRunDone || string.IsNullOrEmpty(cfg.Username))
            _main.ShowMain();
        else if (!cfg.HideOnStartup)
            _main.ShowMain();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => _main.ShowMain());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("立即登录", null, (_, _) => _coord.RunOnceNow());
        menu.Items.Add("断开连接", null, async (_, _) => await _coord.LogoutAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("高级设置", null, (_, _) => ShowAdvancedSettings());
        menu.Items.Add("查看日志", null, (_, _) => Logger.OpenLogFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        return menu;
    }

    // ------------------------------------------------ 状态展示

    private void ApplyStatus(string status)
    {
        if (status == _lastStatus) return;
        _lastStatus = status;

        string tip = $"{BaseText} · {status}";
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;

        bool connected = status == "已连接";
        if (connected && !_wasConnected)
        {
            _icon.Icon = AppIcon.ForStatus("已连接");
            _icon.ShowBalloonTip(3000, BaseText, "网络已连接", ToolTipIcon.Info);
        }
        else if (status.StartsWith("登录失败"))
        {
            _icon.Icon = AppIcon.ForStatus(status);
            _icon.ShowBalloonTip(5000, BaseText, status + "（已停止自动重试，请打开主界面检查密码）", ToolTipIcon.Warning);
        }
        else if (!connected)
        {
            _icon.Icon = AppIcon.ForStatus(status);
        }
        _wasConnected = connected;

        // 主窗口横幅由 MainWindow 自行订阅 StatusChanged 刷新，此处无需转发
    }

    // ------------------------------------------------ 菜单动作

    private void ShowAdvancedSettings()
    {
        foreach (var f in Application.OpenForms.OfType<SettingsForm>())
        {
            f.Activate();
            return;
        }
        new SettingsForm(_cfgStore, _coord).Show(_main); // 传 Owner：CenterParent 才有效
    }

    private void ExitApp()
    {
        // 必须 Dispose，否则托盘残留死图标
        _icon.Visible = false;
        _icon.Dispose();
        _main.Dispose(); // 直接 Dispose（不走 FormClosing，不会被「隐藏到托盘」拦截）
        _coord.Dispose();
        Logger.Instance.Info("程序退出");
        AppIcon.DisposeCache();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _main?.Dispose();
            _coord.Dispose();
            _icon.Dispose();
        }
        base.Dispose(disposing);
    }
}
