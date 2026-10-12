namespace CampusAutoLogin.Core;

/// <summary>
/// 连通性探测 —— 唯一可靠的「是否已联网」判断。
/// 绝对不能只看 HTTP 200：Portal 劫持时会返回 200 + 一段门户 HTML。
/// 必须：generate_204 返回 204 且无 body；备用 msftconnecttest.txt body 严格等于期望值。
/// </summary>
public static class ConnMonitor
{
    private const string Url204 = "http://connect.rom.miui.com/generate_204";
    private const string UrlTxt = "http://www.msftconnecttest.com/connecttest.txt";
    private const string TxtExpect = "Microsoft Connect Test";

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    })
    { Timeout = TimeSpan.FromSeconds(3) };

    public static async Task<bool> IsOnlineAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(Url204, ct);
            if ((int)resp.StatusCode == 204)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (body.Length == 0) return true;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 换备用探测 */ }

        try
        {
            using var resp = await Http.GetAsync(UrlTxt, ct);
            if (resp.IsSuccessStatusCode)
            {
                var body = (await resp.Content.ReadAsStringAsync(ct)).Trim();
                return body == TxtExpect;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return false;
    }
}
