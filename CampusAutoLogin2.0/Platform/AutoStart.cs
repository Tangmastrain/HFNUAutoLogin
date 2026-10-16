using System.Diagnostics;
using Microsoft.Win32;

namespace CampusAutoLogin.Platform;

/// <summary>
/// 开机自启三轨机制（v2.0.2 在 v1.1 双轨上新增开机轨道）：
/// ① 开机轨道：schtasks ONSTART / SYSTEM 任务 —— 系统启动（无需登录 Windows）即以
///    --boot 无界面模式登录校园网；需管理员权限。SYSTEM 无用户 profile，
///    安装用户的 %APPDATA% 数据目录在装任务时固化进命令行参数。
/// ② 登录轨道：schtasks ONLOGON 任务 —— 用户登录后拉起托盘界面，接管 --boot 实例的监控。
///    非管理员执行 /SC ONLOGON 会 Access is denied，故有 ③ 回退。
/// ③ 回退轨道：HKCU 注册表 Run 键 —— 无需管理员，应用内部指数退避弥补无延迟的劣势。
/// 非管理员环境：①②均失败 → 只有 ③，行为与旧版完全一致（登录后运行），无回归。
///
/// v2.0.3：移除「升级迁移」专用判定 NeedsBootMigration()。该判定要求「已有登录轨道、只差开机轨道」，
/// 三条轨道被外部整体清除时返回 false，导致开机自启静默失效（2026-10-16 实测：任务被清、
/// config 仍为 true、启动无任何补装动作、日志零记录）。现由调用方以 IsInstalled() 全程自检。
/// </summary>
public static class AutoStart
{
    // 独立名称：与旧版 CampusAutoLogin 的自启项分离，两者互不覆盖
    private const string LogonTaskName = "CampusAutoLogin2.0";
    private const string BootTaskName = "CampusAutoLogin2.0Boot";
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CampusAutoLogin2.0";

    /// <summary>最近一次 Install 的结果：开机轨道（免登录运行）是否装上。</summary>
    public static bool BootTrackInstalled { get; private set; }

    /// <summary>开机轨道（ONSTART/SYSTEM 任务）在本机是否存在。</summary>
    public static bool BootTaskExists() => Run($"/Query /TN \"{BootTaskName}\"");

    /// <summary>登录轨道（ONLOGON 任务）在本机是否存在。</summary>
    public static bool LogonTaskExists() => Run($"/Query /TN \"{LogonTaskName}\"");

    /// <summary>任一轨存在（开机任务 / 登录任务 / Run 键）都算已安装自启。</summary>
    public static bool IsInstalled() => BootTaskExists() || LogonTaskExists() || RunKeyExists();

    public static bool Install(int delaySec)
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0) return false;

        string delay = $"{Math.Max(0, delaySec) / 60:0000}:{Math.Max(0, delaySec) % 60:00}";
        string dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CampusAutoLogin2.0");

        // ---- 轨道①：开机任务（ONSTART / SYSTEM，无界面 --boot 模式）----
        // 命令行：exe --boot 数据目录；/TR 内层引号转义，路径含空格必须带引号
        // 逐级降级：带延迟 → 无延迟（部分环境 /DELAY 受限）
        bool bootOk =
            Run($"/Create /TN \"{BootTaskName}\" /TR \"\\\"{exe}\\\" --boot \\\"{dataDir}\\\"\" /SC ONSTART /RU SYSTEM /RL HIGHEST /DELAY {delay} /F") ||
            Run($"/Create /TN \"{BootTaskName}\" /TR \"\\\"{exe}\\\" --boot \\\"{dataDir}\\\"\" /SC ONSTART /RU SYSTEM /RL HIGHEST /F");
        BootTrackInstalled = bootOk;
        if (bootOk)
            Logger.Instance.Info($"AUTOSTART 开机轨道安装成功：任务={BootTaskName}，ONSTART/SYSTEM，延迟={delay}");
        else
            Logger.Instance.Info("AUTOSTART 开机轨道安装失败（非管理员环境预期内），回退为登录后运行");

        // ---- 轨道②③：登录任务 + Run 键回退（原 v1.1 双轨，逐级降级：/RL HIGHEST → 无 /RL → 无 /DELAY）----
        bool logonOk =
            Run($"/Create /TN \"{LogonTaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /DELAY {delay} /RL HIGHEST /F") ||
            Run($"/Create /TN \"{LogonTaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /DELAY {delay} /F") ||
            Run($"/Create /TN \"{LogonTaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /F");
        if (logonOk)
        {
            RemoveRunKey(); // 清理可能残留的回退注册项，避免多轨并存重复启动
            Logger.Instance.Info($"AUTOSTART 登录轨道安装成功：任务={LogonTaskName}，延迟={delay}");
        }
        else
        {
            Logger.Instance.Info("AUTOSTART 登录任务创建失败（非管理员环境常见），回退到注册表 Run 键");
            if (WriteRunKey(exe))
                Logger.Instance.Info($"AUTOSTART 登录轨道安装成功：注册表Run键(HKCU\\{RunKeyPath}\\{RunValueName})");
        }

        bool anyOk = bootOk || logonOk || RunKeyExists();
        if (!anyOk)
            Logger.Instance.Warn("AUTOSTART 安装失败：开机任务、登录任务与注册表 Run 键均不可用");
        return anyOk;
    }

    /// <summary>三轨都清理；schtasks 删除失败不视为错误（可能本来就无权限创建过）。</summary>
    public static bool Uninstall()
    {
        bool bootDeleted = Run($"/Delete /TN \"{BootTaskName}\" /F");
        if (!bootDeleted)
            Logger.Instance.Info("AUTOSTART 开机任务删除失败或不存在（忽略）");
        BootTrackInstalled = false;

        bool taskDeleted = Run($"/Delete /TN \"{LogonTaskName}\" /F");
        if (!taskDeleted)
            Logger.Instance.Info("AUTOSTART 登录任务删除失败或不存在（忽略）");

        bool runRemoved = RemoveRunKey();
        if (!runRemoved)
            Logger.Instance.Warn("AUTOSTART 注册表 Run 键删除失败");

        // 全部清理（或本来不存在）才算干净
        bool clean = !RunKeyExists() && !BootTaskExists() && !LogonTaskExists();
        if (clean)
            Logger.Instance.Info("AUTOSTART 已卸载（开机任务 + 登录任务 + 注册表 Run 键均已清理）");
        return clean;
    }

    // ------------------------------------------------ schtasks 封装（不抛异常）

    private static bool Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(); } catch { } // P2 修复：超时杀掉子进程，避免孤儿 schtasks
                return false;
            }
            return p.ExitCode == 0;
        }
        catch
        {
            return false; // schtasks 不可用（被策略禁止等）→ 走回退轨道
        }
    }

    // ------------------------------------------------ HKCU Run 键（Microsoft.Win32.Registry，不启动进程）

    private static bool RunKeyExists()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool WriteRunKey(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;
            key.SetValue(RunValueName, $"\"{exe}\""); // 路径含空格需带引号
            return true;
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("AUTOSTART 写入注册表 Run 键失败：" + ex.Message);
            return false;
        }
    }

    private static bool RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null || key.GetValue(RunValueName) is null) return true; // 本来就没有，视为已清理
            key.DeleteValue(RunValueName);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("AUTOSTART 删除注册表 Run 键失败：" + ex.Message);
            return false;
        }
    }
}
