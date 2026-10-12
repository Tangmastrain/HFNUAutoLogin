using System.Diagnostics;

namespace CampusAutoLogin.Platform;

/// <summary>
/// 滚动日志：%APPDATA%\CampusAutoLogin2.0\logs\app.log，单文件 1MB，保留 5 个。
/// 格式：[时间] 级别 事件。绝不记录密码。
/// </summary>
public sealed class Logger
{
    public static Logger Instance { get; } = new();

    private const long MaxSize = 1024 * 1024;
    private const int Keep = 5;

    private readonly object _lock = new();
    private readonly string _dir;
    private readonly string _path;

    private Logger()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CampusAutoLogin2.0", "logs");
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "app.log");
    }

    public string LogDir => _dir;

    /// <summary>在资源管理器中打开日志目录（托盘菜单 / 状态窗共用）。</summary>
    public static void OpenLogFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Instance.LogDir}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Instance.Error("打开日志失败：" + ex.Message);
        }
    }

    public void Info(string msg) => Write("INFO ", msg);
    public void Warn(string msg) => Write("WARN ", msg);
    public void Error(string msg) => Write("ERROR", msg);

    private void Write(string level, string msg)
    {
        try
        {
            lock (_lock)
            {
                RotateIfNeeded();
                File.AppendAllText(_path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {level} {msg}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    private void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length < MaxSize) return;
            for (int i = Keep - 1; i >= 1; i--)
            {
                string from = Path.Combine(_dir, $"app.{i}.log");
                string to = Path.Combine(_dir, $"app.{i + 1}.log");
                if (File.Exists(from)) File.Move(from, to, true);
            }
            File.Move(_path, Path.Combine(_dir, "app.1.log"), true);
        }
        catch
        {
            // 滚动失败不影响主流程
        }
    }
}
