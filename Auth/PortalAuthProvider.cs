using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using CampusAutoLogin.Core;

namespace CampusAutoLogin.Auth;

/// <summary>
/// 华为 ePortal 认证实现（2026-11-16 认证页改版）。
/// 旧接口 /eportal/?c=ACSetting&a=Login（POST 表单）已停用，
/// 现走新接口 /eportal/portal/login（GET + JSONP，dr1003 回调），密码明文直传。
/// 动态参数 wlan_user_ip/wlan_ac_ip/wlan_ac_name/mac 每次登录前从 AC 劫持跳转中提取；
/// 劫持 Location 可能是旧命名（wlanuserip）或新命名（wlan_user_ip），两者都兼容。
/// </summary>
public sealed class PortalAuthProvider : IAuthProvider
{
    private const int EportalPort = 801;
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/157.0.0.0 Safari/537.36 Edg/157.0.0.0";

    // 用于触发 ACL 劫持跳转的探测地址（目的是拿到 Portal 动态参数）
    private static readonly string[] ProbeUrls =
    {
        "http://connect.rom.miui.com/generate_204",
        "http://www.msftconnecttest.com/connecttest.txt",
        "http://www.baidu.com/",
    };

    // 命中即失败（业务类，不得重试，防止账号锁定）
    private static readonly string[] FailKeys =
    {
        "认证失败", "Authentication failure", "AC认证失败", "REQ_CHALLENGE失败",
        "参数签名失败", "临时账号上线失败", "网络错误", "密码错误", "账号或密码",
        "用户不存在", "已欠费", "用户被锁定", "Login failed",
    };

    // 命中即成功。
    // !! 致命陷阱 !!：页面同时存在「注销成功」「密码修改成功」等文案，
    // 绝不能只匹配「成功」二字，必须匹配完整词。
    private static readonly string[] SuccessKeys =
    {
        "登录成功", "认证成功", "Loginsuccessfully", "Loginsucceeded", "loginsuccess",
        "IP已经在线", "IPalreadyonline", "\"result\":1", "'result':1", "\"result\":\"1\"",
    };

    // 旧命名（302 劫持 Location 常见）+ 新命名（portal 页 v4.2.1 版式），统一提取后归一化
    private static readonly string[] ParamKeys =
    {
        "wlanuserip", "wlanacip", "wlanacname", "mac",
        "wlan_user_ip", "wlan_ac_ip", "wlan_ac_name", "wlan_user_mac",
    };

    private static readonly Dictionary<string, string[]> ParamAliases = new()
    {
        ["wlanuserip"] = new[] { "wlanuserip", "wlan_user_ip" },
        ["wlanacip"] = new[] { "wlanacip", "wlan_ac_ip" },
        ["wlanacname"] = new[] { "wlanacname", "wlan_ac_name" },
        ["mac"] = new[] { "mac", "wlan_user_mac" },
    };

    private readonly HttpClient _noRedirect;
    private readonly HttpClient _plain;
    private readonly Func<AppConfig> _cfg;

    /// <summary>
    /// 最近一次探测到的 Portal 动态参数。注销时本机在线 → AC 不劫持 → DiscoverAsync 探不到，
    /// 用登录时缓存的参数比 GetLocalIp 回退可靠（多网卡机器上 UDP 选路可能拿到虚拟网卡 IP）。
    /// 引用赋值原子，跨线程读到旧值无害。
    /// </summary>
    private Dictionary<string, string>? _lastSeenParams;

    public string Name => "Portal";

    public PortalAuthProvider(Func<AppConfig> cfg)
    {
        _cfg = cfg;
        // 探测用：不跟随重定向，才能读到 AC 劫持的 Location
        _noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(4) };
        // 登录用：跟随重定向
        _plain = new HttpClient(new HttpClientHandler { UseCookies = false })
        { Timeout = TimeSpan.FromSeconds(8) };
    }

    // ------------------------------------------------------------ 登录

    public async Task<AuthResult> LoginAsync(Credential cred, CancellationToken ct)
    {
        var cfg = _cfg();
        var p = await DiscoverAsync(ct);
        if (p.ContainsKey("wlanuserip"))
            _lastSeenParams = p; // 缓存：注销时在线探不到参数，回退用（见 LogoutAsync）

        string ip = Str(p, "wlanuserip", GetLocalIp(cfg.PortalHost));
        string acip = Str(p, "wlanacip", cfg.WlanAcIp);
        string acname = Str(p, "wlanacname", string.IsNullOrEmpty(cfg.WlanAcName) ? "me60" : cfg.WlanAcName);
        string mac = NormalizeMac(Str(p, "mac", "000000000000"));

        // 2026-11-16 改版：/eportal/portal/login，GET + JSONP（dr1003 回调），
        // 与新版认证页抓包逐字一致；user_password 就是密码原文，无需任何加密。
        // v 参数为缓存穿透随机值，服务端不校验。
        var url = $"http://{cfg.PortalHost}:{EportalPort}/eportal/portal/login" +
                  $"?callback=dr1003&login_method=1" +
                  $"&user_account={Uri.EscapeDataString(BuildUserAccount(cred.Username, cfg.AccountSuffix))}" +
                  $"&user_password={Uri.EscapeDataString(cred.Password)}" +
                  $"&wlan_user_ip={Uri.EscapeDataString(ip)}&wlan_user_ipv6=" +
                  $"&wlan_user_mac={Uri.EscapeDataString(mac)}" +
                  $"&wlan_ac_ip={Uri.EscapeDataString(acip)}&wlan_ac_name={Uri.EscapeDataString(acname)}" +
                  $"&jsVersion=4.2.1&terminal_type=1&lang=zh-cn&v={Random.Shared.Next(1000, 9999)}&lang=zh";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.Referrer = new Uri($"http://{cfg.PortalHost}/");
        // 字段名叫 md5_login2，实际是「用户名|密码」明文拼接，| URL 编码为 %7C
        req.Headers.TryAddWithoutValidation("Cookie",
            $"program={cfg.PortalProgram}; vlan=0; " +
            $"md5_login2={Uri.EscapeDataString(cred.Username + "|" + cred.Password)}; ip={ip}");

        string text;
        try
        {
            using var resp = await _plain.SendAsync(req, ct);
            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
            text = DecodeBody(resp.Content.Headers.ContentType?.CharSet, bytes);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return AuthResult.NetFail("网络异常：" + ex.Message);
        }

        // 判定顺序：先失败词表（命中即业务失败，不再重试）→ 再成功词表 → 最后连通性
        var (ok, kw) = Judge(text);
        if (ok == false)
            return AuthResult.BizFail($"认证失败（{kw}）");

        // 成功或无法判定：等 AC 放开策略后，以连通性为最终判据
        await Task.Delay(1500, ct);
        if (await ConnMonitor.IsOnlineAsync(ct))
            return AuthResult.Ok(ok == true ? "登录成功" : "登录成功（连通性验证通过）");

        // P2 修复：消息不得携带响应片段（片段可能含敏感字段，随 Coordinator 日志落盘）
        return AuthResult.NetFail(ok == true
            ? "Portal 已应答成功，但外网仍不通"
            : "登录后仍未联网");
    }

    // ------------------------------------------------------------ 注销

    public async Task<AuthResult> LogoutAsync(CancellationToken ct)
    {
        var cfg = _cfg();
        var p = await DiscoverAsync(ct);
        // 在线时探测不到劫持参数 → 回退到最近一次登录时缓存的参数，最后才轮到 GetLocalIp
        if (!p.ContainsKey("wlanuserip") && _lastSeenParams is not null)
            p = _lastSeenParams;

        string ip = Str(p, "wlanuserip", GetLocalIp(cfg.PortalHost));
        string acip = Str(p, "wlanacip", cfg.WlanAcIp);
        string acname = Str(p, "wlanacname", string.IsNullOrEmpty(cfg.WlanAcName) ? "me60" : cfg.WlanAcName);
        string mac = NormalizeMac(Str(p, "mac", "000000000000"));

        // 2026-11-16 新版注销抓包：/eportal/portal/logout（GET + JSONP，dr1004 回调）。
        // 关键：user_account/user_password 是认证页注销链接里的固定占位值（drcom/123），
        // 服务端靠 wlan_user_ip 定位会话，与登录账号无关 —— 绝不能传真实账号。
        var url = $"http://{cfg.PortalHost}:{EportalPort}/eportal/portal/logout" +
                  $"?callback=dr1004&login_method=1" +
                  $"&user_account=drcom&user_password=123" +
                  $"&ac_logout=0&register_mode=0" +
                  $"&wlan_user_ip={Uri.EscapeDataString(ip)}&wlan_user_ipv6=&wlan_vlan_id=0" +
                  $"&wlan_user_mac={Uri.EscapeDataString(mac)}" +
                  $"&wlan_ac_ip={Uri.EscapeDataString(acip)}&wlan_ac_name={Uri.EscapeDataString(acname)}" +
                  $"&jsVersion=4.2.1&v={Random.Shared.Next(1000, 9999)}&lang=zh";

        // 注销为 GET（与新版认证页抓包一致）；响应体读掉以释放连接，但不作为判据
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.Referrer = new Uri($"http://{cfg.PortalHost}/");
        try
        {
            using var resp = await _plain.SendAsync(req, ct);
            _ = await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return AuthResult.NetFail("注销请求失败：" + ex.Message);
        }

        // 与登录同一最终判据：注销后复测连通性，如实上报（不再无脑报「已注销」）
        await Task.Delay(1000, ct);
        return await ConnMonitor.IsOnlineAsync(ct)
            ? AuthResult.NetFail("注销请求已发出，但网络仍在线（注销可能未生效）")
            : AuthResult.Ok("已注销");
    }

    // ------------------------------------------------------------ 认证态检测

    /// <summary>
    /// 校园网认证态检测：未认证时 AC 会对任意 HTTP 请求 302 劫持到 Portal 页。
    /// 部分校园网对连通性探测地址（generate_204 等）直接放行/伪造 204，导致「只探外网」
    /// 会把未认证误判为已联网 —— 用任意常规站点（百度）的劫持特征兜底区分。
    /// 返回 false = 检测到劫持（未认证）；true = 未见劫持（已认证，或非校园网环境）。
    /// 检测异常一律返回 true：辅助检测不得影响主流程。
    /// </summary>
    public async Task<bool> IsPortalAuthenticatedAsync(CancellationToken ct)
    {
        try
        {
            // 不跟随重定向才能读到 AC 的 302 劫持 Location
            using var resp = await _noRedirect.GetAsync("http://www.baidu.com/", ct);
            if (resp.StatusCode is HttpStatusCode.MovedPermanently
                or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect)
            {
                var loc = resp.Headers.Location?.ToString() ?? "";
                if (ExtractParams(loc).ContainsKey("wlanuserip") ||
                    loc.Contains("eportal", StringComparison.OrdinalIgnoreCase))
                    return false; // 被劫持到 Portal：未认证
                return true;      // 与 Portal 无关的正常跳转不算劫持
            }
            if ((int)resp.StatusCode == 200)
            {
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                var text = DecodeBody(resp.Content.Headers.ContentType?.CharSet, bytes);
                if (ExtractParams(text).ContainsKey("wlanuserip"))
                    return false; // 200 但内容是 Portal 劫持页
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return true;
    }

    // ------------------------------------------------------------ 动态参数探测

    /// <summary>
    /// 未认证时访问任意外网会被 AC 302 劫持到 Portal 页，从 Location 里提取
    /// wlanuserip / wlanacip / wlanacname / mac。绝不能写死。
    /// </summary>
    private async Task<Dictionary<string, string>> DiscoverAsync(CancellationToken ct)
    {
        foreach (var probe in ProbeUrls)
        {
            try
            {
                using var resp = await _noRedirect.GetAsync(probe, ct);
                var p = ExtractParams(resp.Headers.Location?.ToString() ?? "");
                if (p.ContainsKey("wlanuserip")) return p;

                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    p = ExtractParams(DecodeBody(resp.Content.Headers.ContentType?.CharSet, bytes));
                    if (p.ContainsKey("wlanuserip")) return p;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 换下一个探测地址 */ }
        }
        return new Dictionary<string, string>();
    }

    private static Dictionary<string, string> ExtractParams(string text)
    {
        var found = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(text)) return found;

        var raw = new Dictionary<string, string>();
        foreach (var key in ParamKeys)
        {
            var m = Regex.Match(text, key + @"=([^&""'<>\s;]+)");
            if (m.Success)
                raw[key] = Uri.UnescapeDataString(m.Groups[1].Value);
        }

        // 新旧命名归一化为内部规范键，调用方只需认 wlanuserip / wlanacip / wlanacname / mac
        foreach (var (canonical, aliases) in ParamAliases)
        {
            foreach (var alias in aliases)
            {
                if (raw.TryGetValue(alias, out var v) && !string.IsNullOrEmpty(v))
                {
                    found[canonical] = v;
                    break;
                }
            }
        }
        return found;
    }

    // ------------------------------------------------------------ 响应解码与判定

    /// <summary>
    /// Portal 响应页为 GBK/GB2312 编码。若一律按 UTF-8 解码，中文乱码会导致关键字判定永远失败。
    /// 先看 Content-Type 的 charset，无法判定时用「严格 UTF-8 试解 → 失败则 GBK」。
    /// </summary>
    private static string DecodeBody(string? charset, byte[] bytes)
    {
        if (!string.IsNullOrEmpty(charset))
        {
            try { return Encoding.GetEncoding(charset).GetString(bytes); }
            catch { /* 未知编码名，走下面的探测 */ }
        }
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes); // 严格 UTF-8
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding("GBK").GetString(bytes);
        }
    }

    private static (bool? Ok, string Keyword) Judge(string text)
    {
        foreach (var k in FailKeys)
            if (text.Contains(k)) return (false, k);

        // 英文关键字可能被换行/空格打断，用去空白后的文本匹配
        var flat = Regex.Replace(text, @"\s+", "");
        foreach (var k in SuccessKeys)
            if (flat.Contains(k)) return (true, k);

        return (null, "");
    }

    // ------------------------------------------------------------ 本机回退值

    /// <summary>本机在通往 Portal 那条路由上的源 IP（探测不到 wlanuserip 时的回退）。</summary>
    private static string GetLocalIp(string host)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(host, 80);
            return (s.LocalEndPoint as IPEndPoint)?.Address.ToString() ?? "0.0.0.0";
        }
        catch
        {
            return "0.0.0.0";
        }
    }

    /// <summary>
    /// 新版接口的 user_account 固定为「,0,账号@运营商」（示例：,0,262005014@telecom）。
    /// 账号已自带 @ 时不重复追加；运营商后缀（config.AccountSuffix）为空时退化为「,0,账号」。
    /// </summary>
    private static string BuildUserAccount(string username, string suffix)
    {
        if (username.Contains('@')) return ",0," + username;
        return string.IsNullOrEmpty(suffix) ? ",0," + username : $",0,{username}@{suffix}";
    }

    /// <summary>
    /// 统一 MAC 形态：去掉 - 与 : 分隔符。新版认证页直接传零值 000000000000（抓包实证），
    /// 探测到劫持 URL 携带 MAC 时归一化后透传。
    /// </summary>
    private static string NormalizeMac(string mac) => mac.Replace("-", "").Replace(":", "");

    private static string Str(Dictionary<string, string> p, string key, string fallback) =>
        p.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;

    public void Dispose()
    {
        _noRedirect.Dispose();
        _plain.Dispose();
    }
}
