using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using CampusAutoLogin.Core;

namespace CampusAutoLogin.Auth;

/// <summary>
/// 华为 ePortal (ACSetting) 认证实现。
/// 已实证：密码明文直传（服务端 enMd5=0），单次 POST 完成认证，无握手。
/// 动态参数 wlanuserip/wlanacip/wlanacname/mac 每次登录前从 AC 劫持跳转中提取。
/// </summary>
public sealed class PortalAuthProvider : IAuthProvider
{
    private const int EportalPort = 801;
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/155.0.0.0 Safari/537.36 Edg/155.0.0.0";

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
        "登录成功", "Loginsuccessfully", "Loginsucceeded", "loginsuccess",
        "IP已经在线", "IPalreadyonline", "\"result\":1", "'result':1", "\"result\":\"1\"",
    };

    private static readonly string[] ParamKeys = { "wlanuserip", "wlanacip", "wlanacname", "mac" };

    private readonly HttpClient _noRedirect;
    private readonly HttpClient _plain;
    private readonly Func<AppConfig> _cfg;

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

        string ip = Str(p, "wlanuserip", GetLocalIp(cfg.PortalHost));
        string acip = Str(p, "wlanacip", cfg.WlanAcIp);
        string acname = Str(p, "wlanacname", string.IsNullOrEmpty(cfg.WlanAcName) ? "me60" : cfg.WlanAcName);
        string mac = Str(p, "mac", GetLocalMac());

        var url = $"http://{cfg.PortalHost}:{EportalPort}/eportal/" +
                  $"?c=ACSetting&a=Login&protocol=http:&hostname={Uri.EscapeDataString(cfg.PortalHost)}" +
                  $"&iTermType=1&wlanuserip={Uri.EscapeDataString(ip)}&wlanacip={Uri.EscapeDataString(acip)}" +
                  $"&wlanacname={Uri.EscapeDataString(acname)}&mac={Uri.EscapeDataString(mac)}" +
                  $"&ip={Uri.EscapeDataString(ip)}" +
                  "&enAdvert=0&queryACIP=0&loginMethod=1";

        // 15 个表单字段，与抓包逐字一致；upass 就是密码原文，无需任何加密
        var form = new Dictionary<string, string>
        {
            ["DDDDD"] = cred.Username,
            ["upass"] = cred.Password,
            ["R1"] = "0",
            ["R2"] = "0",
            ["R6"] = "0",
            ["para"] = "00",
            ["0MKKey"] = cred.Password, // 老版本兼容字段，值必须与 upass 相同
            ["buttonClicked"] = "",
            ["redirect_url"] = "",
            ["err_flag"] = "",
            ["username"] = "",
            ["password"] = "",
            ["user"] = "",
            ["cmd"] = "",
            ["Login"] = "",
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.TryAddWithoutValidation("Origin", $"http://{cfg.PortalHost}");
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
        string ip = Str(p, "wlanuserip", GetLocalIp(cfg.PortalHost));
        string acip = Str(p, "wlanacip", cfg.WlanAcIp);
        string acname = Str(p, "wlanacname", string.IsNullOrEmpty(cfg.WlanAcName) ? "me60" : cfg.WlanAcName);

        var url = $"http://{cfg.PortalHost}:{EportalPort}/eportal/" +
                  $"?c=ACSetting&a=Logout&protocol=http:&hostname={Uri.EscapeDataString(cfg.PortalHost)}" +
                  $"&wlanuserip={Uri.EscapeDataString(ip)}&wlanacip={Uri.EscapeDataString(acip)}" +
                  $"&wlanacname={Uri.EscapeDataString(acname)}&ip={Uri.EscapeDataString(ip)}";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        req.Headers.Referrer = new Uri($"http://{cfg.PortalHost}/");
        try
        {
            using var resp = await _plain.SendAsync(req, ct);
            return AuthResult.Ok("已注销");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return AuthResult.NetFail("注销请求失败：" + ex.Message);
        }
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
        foreach (var key in ParamKeys)
        {
            var m = Regex.Match(text, key + @"=([^&""'<>\s;]+)");
            if (m.Success)
                found[key] = Uri.UnescapeDataString(m.Groups[1].Value);
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

    /// <summary>本机物理网卡 MAC，格式 AA-BB-CC-DD-EE-FF（探测不到时的回退）。</summary>
    private static string GetLocalMac()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var ha = nic.GetPhysicalAddress().GetAddressBytes();
                if (ha.Length != 6 || ha.All(b => b == 0)) continue;
                return string.Join("-", ha.Select(b => b.ToString("X2")));
            }
        }
        catch { }
        return "00-00-00-00-00-00";
    }

    private static string Str(Dictionary<string, string> p, string key, string fallback) =>
        p.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;

    public void Dispose()
    {
        _noRedirect.Dispose();
        _plain.Dispose();
    }
}
