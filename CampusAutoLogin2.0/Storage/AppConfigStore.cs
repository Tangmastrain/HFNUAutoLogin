using System.Text.Json;
using CampusAutoLogin.Core;

namespace CampusAutoLogin.Storage;

/// <summary>配置读写：%APPDATA%\CampusAutoLogin2.0\config.json（不含密码）。</summary>
public sealed class AppConfigStore
{
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppConfigStore()
    {
        // 独立配置目录：与旧版 CampusAutoLogin 互不干扰，可并存
        var dir = Path.Combine(
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
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, JsonOpts));
        File.Move(tmp, _path, true); // 原子替换
    }
}
