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

        Logger.Instance.Info($"启动完成 v2.0.0，模式=托盘常驻，hideOnStartup={cfg.HideOnStartup}");
        _coord.Start();
        if (!cfg.AutoLogin)
            _coord.Pause(); // 上次关闭时关了自动登录 → 本次启动保持暂停

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
        menu.Items.Add("立即登录", null, (_, _) => _coord.TriggerNow());
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
        new SettingsForm(_cfgStore, _coord).Show();
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
