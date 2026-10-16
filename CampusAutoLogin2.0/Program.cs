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

        // 开机模式（v2.0.2）：系统启动即无界面登录，不等用户登录 Windows。
        // 由计划任务（ONSTART / SYSTEM）拉起：--boot <安装用户的数据目录>。
        int bootIdx = Array.IndexOf(args, "--boot");
        if (bootIdx >= 0 && bootIdx + 1 < args.Length)
        {
            RunBootMode(args[bootIdx + 1]);
            return;
        }

        // 单实例：自启 + 手动双击只允许一个进程。
        // 2.0 用独立互斥体名，可与旧版 CampusAutoLogin 同时运行、互不干扰。
        using var mutex = new Mutex(true, @"Global\CampusAutoLogin2.0", out bool isNew);
        if (!isNew) return;

        // 通知残留的 --boot 后台实例退出，登录后的监控由本托盘实例接管。
        // 放在单实例检查之后：真正的重复托盘实例不产生任何副作用。
        BootTakeover.Signal();

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
    /// --boot 开机模式：计划任务（ONSTART / SYSTEM）拉起的无界面登录。
    /// SYSTEM 会话没有用户的 %APPDATA%，数据目录由任务参数传入（安装时固化）；
    /// 凭据读机器域 DPAPI 副本（credential-boot.dat）；热点功能跳过（session 0 无法操作）。
    /// 阻塞等待 BootTakeover 事件 —— 用户登录后托盘实例置位，本实例退出交权。
    /// </summary>
    private static void RunBootMode(string dataDir)
    {
        // 必须在任何人触碰 Logger 之前设置：Logger 单例构造时读它决定日志目录
        Environment.SetEnvironmentVariable(
            "CampusAutoLogin2.0_DataDir", dataDir, EnvironmentVariableTarget.Process);

        AppDomain.CurrentDomain.UnhandledException +=
            (_, e) => Logger.Instance.Error("BOOT 未处理异常：" + e.ExceptionObject);

        try
        {
            var cfgStore = new AppConfigStore(dataDir);
            var credStore = new CredentialStore(dataDir, bootMode: true);
            var auth = new PortalAuthProvider(cfgStore.Load);
            using var coord = new Coordinator(auth, cfgStore, credStore, allowHotspot: false);

            // 版本号派生自 csproj InformationalVersion（带 "+<提交哈希>" 后缀，需截断）
            Logger.Instance.Info($"BOOT 开机模式启动 v{Application.ProductVersion.Split('+')[0]}，数据目录={dataDir}");

            using var takeover = BootTakeover.OpenOrCreate();
            takeover.Reset(); // 清掉历史残留信号：刚启动的实例不应立即退出

            coord.Start();
            if (!cfgStore.Load().AutoLogin)
                coord.Pause(); // 与托盘实例一致：尊重「自动登录」开关

            takeover.WaitOne(); // 阻塞直到托盘实例接管（或进程被系统结束）

            coord.Dispose();
            Logger.Instance.Info("BOOT 已被交互实例接管，后台实例退出");
        }
        catch (Exception ex)
        {
            try { Logger.Instance.Error("BOOT 致命异常，后台实例退出：" + ex); } catch { }
        }
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
