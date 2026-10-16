using System.Text.Json;
using CampusAutoLogin.Core;
using CampusAutoLogin.Platform;

namespace CampusAutoLogin.Storage;

/// <summary>配置读写：%APPDATA%\CampusAutoLogin2.0\config.json（不含密码）。可传 dataDir 覆盖（--boot 模式用）。</summary>
public sealed class AppConfigStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppConfigStore(string? dataDir = null)
    {
        // 独立配置目录：与旧版 CampusAutoLogin 互不干扰，可并存
        // --boot 模式以 SYSTEM 运行、无用户 %APPDATA%，直接读安装用户的数据目录（SYSTEM 有权限）
        var dir = dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CampusAutoLogin2.0");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "config.json");
    }

    public AppConfig Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path));
                if (cfg is not null) return cfg;
            }
        }
        catch
        {
            // 配置损坏不崩溃，回退默认值
        }
        return new AppConfig();
    }

    public void Save(AppConfig cfg)
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonOpts));
            File.Move(tmp, _path, true); // 原子替换
        }
        catch (Exception ex)
        {
            // 文件被占用（杀软扫描等）不应把异常冒泡到 UI 事件，记日志即可
            Logger.Instance.Warn("配置保存失败：" + ex.Message);
        }
    }
}
