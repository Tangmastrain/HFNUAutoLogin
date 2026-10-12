using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CampusAutoLogin.Core;

namespace CampusAutoLogin.Storage;

/// <summary>
/// 凭据存储：DPAPI（DataProtectionScope.CurrentUser）加密密码，密文绑定当前 Windows 用户，
/// 拷走文件也解不开。换机器/重装系统后解密失败 → 返回 null → 提示用户重新输入，绝不崩溃。
/// </summary>
public sealed class CredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CampusAutoLogin.v1");
    private readonly string _path;

    public CredentialStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CampusAutoLogin2.0");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "credential.dat");
    }

    public void Save(Credential cred)
    {
        var dto = new CredentialDto
        {
            Username = cred.Username,
            Secret = Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(cred.Password), Entropy, DataProtectionScope.CurrentUser)),
        };
        File.WriteAllText(_path, JsonSerializer.Serialize(dto));
    }

    public Credential? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var dto = JsonSerializer.Deserialize<CredentialDto>(File.ReadAllText(_path));
            if (dto is null || string.IsNullOrEmpty(dto.Username)) return null;
            var plain = Encoding.UTF8.GetString(ProtectedData.Unprotect(
                Convert.FromBase64String(dto.Secret), Entropy, DataProtectionScope.CurrentUser));
            return new Credential(dto.Username, plain);
        }
        catch
        {
            return null; // 解密失败（换机器/换用户）→ 视为未配置，引导重新输入
        }
    }

    /// <summary>凭据是否已保存（credential.dat 存在且用户名非空，不解密密码）。</summary>
    public bool Exists() => LoadUsername() is not null;

    /// <summary>只取已保存的用户名，不解密密码（供 UI 显示「已记住密码」状态）。</summary>
    public string? LoadUsername()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var dto = JsonSerializer.Deserialize<CredentialDto>(File.ReadAllText(_path));
            return string.IsNullOrEmpty(dto?.Username) ? null : dto.Username;
        }
        catch
        {
            return null; // 文件损坏 / 解析失败 → 视为未保存
        }
    }

    /// <summary>删除已保存的凭据（取消「记住密码」时调用；登录将退化为必须每次输入）。</summary>
    public void Delete()
    {
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch
        {
            // 删除失败不影响主流程，下次保存会整体覆盖
        }
    }

    private sealed class CredentialDto
    {
        public string Username { get; set; } = "";
        public string Secret { get; set; } = "";
    }
}
