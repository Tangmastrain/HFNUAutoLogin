using System.Diagnostics;
using Microsoft.Win32;

namespace CampusAutoLogin.Platform;

/// <summary>
/// 开机自启双轨机制（v1.1 修复非管理员自启失败）：
/// ① 主路径：schtasks ONLOGON 计划任务 —— 管理员环境下可用，且有 /DELAY 延迟优势（等网卡拿到 DHCP）；
///    非管理员执行 /SC ONLOGON 会 Access is denied，原三级降级全失败，故新增回退：
/// ② 回退路径：HKCU 注册表 Run 键 —— 同样无需管理员权限。
///    Run 键没有延迟能力，但应用内部本身就有指数退避重试，网络未就绪会自动重试，无需延迟。
/// </summary>
public static class AutoStart
{
    // 独立名称：与旧版 CampusAutoLogin 的自启项分离，两者互不覆盖
    private const string TaskName = "CampusAutoLogin2.0";
    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "CampusAutoLogin2.0";

    /// <summary>计划任务存在 或 Run 键存在，都算已安装自启。</summary>
    public static bool IsInstalled()
    {
        // schtasks 探测失败可能因异常（被策略禁止等），吞掉异常仅返回 false，绝不抛出
        return Run($"/Query /TN \"{TaskName}\"") || RunKeyExists();
    }

    public static bool Install(int delaySec)
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0) return false;

        string delay = $"{Math.Max(0, delaySec) / 60:0000}:{Math.Max(0, delaySec) % 60:00}";

        // 主路径：计划任务（逐级降级：/RL HIGHEST → 无 /RL → 无 /DELAY）
        if (Run($"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /DELAY {delay} /RL HIGHEST /F") ||
            Run($"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /DELAY {delay} /F") ||
            Run($"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /F"))
        {
            RemoveRunKey(); // 清理可能残留的回退注册项，避免双轨并存重复启动
            Logger.Instance.Info($"AUTOSTART 安装成功，机制=计划任务({TaskName})，延迟={delay}");
            return true;
        }

        // 回退路径：schtasks 全部失败（典型：非管理员执行 ONLOGON 被 Access denied）→ HKCU Run 键
        Logger.Instance.Info("AUTOSTART 计划任务创建失败（非管理员环境常见），回退到注册表 Run 键");
        if (WriteRunKey(exe))
        {
            Logger.Instance.Info($"AUTOSTART 安装成功，机制=注册表Run键(HKCU\\{RunKeyPath}\\{RunValueName})");
            return true;
        }

        Logger.Instance.Warn("AUTOSTART 安装失败：计划任务与注册表 Run 键均不可用");
        return false;
    }

    /// <summary>双轨都清理；schtasks 删除失败不视为错误（可能本来就无权限创建过）。</summary>
    public static bool Uninstall()
    {
        bool taskDeleted = Run($"/Delete /TN \"{TaskName}\" /F");
        if (!taskDeleted)
            Logger.Instance.Info("AUTOSTART 计划任务删除失败或不存在（忽略）");

        bool runRemoved = RemoveRunKey();
        if (!runRemoved)
            Logger.Instance.Warn("AUTOSTART 注册表 Run 键删除失败");

        // 两者都已清理（或本来就不存在）才算干净
        bool clean = !RunKeyExists() && !Run($"/Query /TN \"{TaskName}\"");
        if (clean)
            Logger.Instance.Info("AUTOSTART 已卸载（计划任务 + 注册表 Run 键均已清理）");
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
            return false; // schtasks 不可用（被策略禁止等）→ 走 Run 键回退
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
