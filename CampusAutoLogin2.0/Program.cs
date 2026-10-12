using System.Drawing.Imaging;
using System.Text;
using CampusAutoLogin.Auth;
using CampusAutoLogin.Core;
using CampusAutoLogin.Platform;
using CampusAutoLogin.Storage;
using CampusAutoLogin.UI;

namespace CampusAutoLogin;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Portal 响应为 GBK 编码，必须先注册代码页提供程序，否则中文关键字判定永远失败
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // 诊断入口（离线截图核对布局用）：--shot <png绝对路径> [状态文案]
        // 跳过单实例检查，渲染主窗口一张截图后真退出，不影响正常运行路径。
        int shotIdx = Array.IndexOf(args, "--shot");
        if (shotIdx >= 0 && shotIdx + 1 < args.Length)
        {
            string? status = shotIdx + 2 < args.Length ? args[shotIdx + 2] : null;
            RunShotMode(args[shotIdx + 1], status);
            return;
        }

        // 单实例：自启 + 手动双击只允许一个进程。
        // 2.0 用独立互斥体名，可与旧版 CampusAutoLogin 同时运行、互不干扰。
        using var mutex = new Mutex(true, @"Global\CampusAutoLogin2.0", out bool isNew);
        if (!isNew) return;

        Application.ThreadException += (_, e) => Logger.Instance.Error("UI异常：" + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.Instance.Error("未处理异常：" + e.ExceptionObject);

        ApplicationConfiguration.Initialize();

        // P1 修复：必须在任何控件创建之前安装 WinForms 同步上下文。
        // 否则 TrayContext 构造时 SynchronizationContext.Current 为 null，
        // 基类 SynchronizationContext.Post 会把 Coordinator.StatusChanged 回调派发到线程池，
        // 造成跨线程操作 NotifyIcon/控件（未定义行为）。
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        Application.Run(new TrayContext());
    }

    /// <summary>
    /// --shot 诊断模式：构造主窗口 → Show → 泵消息 1.8s 等待首次绘制与状态刷新 →
    /// DrawToBitmap 存 PNG → 真退出（不触发「隐藏到托盘」）。
    /// </summary>
    private static void RunShotMode(string path, string? status)
    {
        ApplicationConfiguration.Initialize();
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

        using var tray = new NotifyIcon { Visible = false }; // 不出现在托盘
        var cfgStore = new AppConfigStore();
        var credStore = new CredentialStore();
        var auth = new PortalAuthProvider(cfgStore.Load);
        var coord = new Coordinator(auth, cfgStore, credStore);

        using var win = new MainWindow(coord, auth, cfgStore, credStore, tray);
        win.Show();

        if (!string.IsNullOrEmpty(status))
            win.ShotForceStatus(status);

        var deadline = DateTime.Now.AddMilliseconds(1800);
        while (DateTime.Now < deadline)
        {
            Application.DoEvents(); // 让布局/绘制/状态刷新跑完
            Thread.Sleep(40);
        }

        using var bmp = win.RenderShot();
        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        bmp.Save(full, ImageFormat.Png);
        Logger.Instance.Info("SHOT 已保存：" + full);
    }
}
