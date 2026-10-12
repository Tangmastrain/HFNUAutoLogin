namespace CampusAutoLogin.Core;

/// <summary>登录凭据。密码只在内存中短暂存在，绝不落盘明文、绝不写日志。</summary>
public sealed record Credential(string Username, string Password);

/// <summary>失败分类：网络类可重试，业务类（密码错误等）不得重试（防止账号锁定）。</summary>
public enum AuthFailureKind
{
    None,
    Network,
    Business,
}

public sealed class AuthResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public AuthFailureKind FailureKind { get; init; }

    public static AuthResult Ok(string msg) => new() { Success = true, Message = msg };
    public static AuthResult NetFail(string msg) => new() { Success = false, Message = msg, FailureKind = AuthFailureKind.Network };
    public static AuthResult BizFail(string msg) => new() { Success = false, Message = msg, FailureKind = AuthFailureKind.Business };
}

/// <summary>应用配置（%APPDATA%\CampusAutoLogin2.0\config.json，不含密码）。旧文件缺字段自动取默认值。</summary>
public sealed class AppConfig
{
    public string PortalHost { get; set; } = "192.168.1.100";
    public string PortalProgram { get; set; } = "201706081423";
    public string WlanAcName { get; set; } = "me60";
    public string WlanAcIp { get; set; } = "";
    public string Username { get; set; } = "";
    public int CheckIntervalSec { get; set; } = 60;
    public int StartupDelaySec { get; set; } = 30;
    public bool AutoStart { get; set; } = true;
    public bool FirstRunDone { get; set; }

    // ---- v1.2 新增 ----
    public bool AutoLogin { get; set; } = true;          // 自动登录开关（关 → Coordinator.Pause）
    public bool RememberPassword { get; set; } = true;   // 保存密码开关
    public bool HideOnStartup { get; set; }              // 启动时隐藏窗口（只进托盘）—— 2.0 起默认关闭
    public bool HotspotEnabled { get; set; }             // 自动开启热点（默认关）
    public string HotspotSsid { get; set; } = "HFNU-Hotspot";
    public string HotspotPassword { get; set; } = "87654321";
}
