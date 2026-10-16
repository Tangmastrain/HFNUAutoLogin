using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CampusAutoLogin.Core;
using CampusAutoLogin.Platform;

namespace CampusAutoLogin.Storage;

/// <summary>
/// 凭据存储：DPAPI 加密密码，密文绑定机器，拷走文件也解不开。换机器/重装系统后解密失败 →
/// 返回 null → 提示用户重新输入，绝不崩溃。
/// 两个域（v2.0.2）：
/// ① 用户域（CurrentUser，credential.dat）：交互实例用，密文仅本 Windows 用户可解 —— 原行为不变；
/// ② 机器域（LocalMachine，credential-boot.dat）：开机实例（SYSTEM，登录 Windows 前运行）用。
///    开机模式必须走机器域：SYSTEM 解不开用户域密文。代价是本机任意用户均可解，
///    个人单用户机器可接受；仅开启「开机自启」时才导出副本，平时不留机器域密文。
/// </summary>
public sealed class CredentialStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CampusAutoLogin.v1");
    private const string MainFile = "credential.dat";
    private const string BootFile = "credential-boot.dat";

    private readonly string _dir;
    private readonly string _path;
    private readonly bool _bootMode;

    public CredentialStore(string? dataDir = null, bool bootMode = false)
    {
        // --boot 模式以 SYSTEM 运行、无用户 %APPDATA%，直接用任务参数传入的安装用户数据目录
        _dir = dataDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CampusAutoLogin2.0");
        Directory.CreateDirectory(_dir);
        _bootMode = bootMode;
        _path = Path.Combine(_dir, bootMode ? BootFile : MainFile);
    }

    public void Save(Credential cred)
    {
        var dto = new CredentialDto
        {
            Username = cred.Username,
            Secret = Convert.ToBase64String(ProtectedData.Protect(
                Encoding.UTF8.GetBytes(cred.Password), Entropy, Scope)),
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
                Convert.FromBase64String(dto.Secret), Entropy, Scope));
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
        // 两域一起清：用户域删了，机器域副本绝不能孤零零留着（里面是同一个密码）
        if (!_bootMode) RemoveBootCopy();
    }

    /// <summary>
    /// 把当前用户域凭据导出为机器域副本（credential-boot.dat）。
    /// 开启「开机自启」/ 密码变更时调用；无已保存凭据时返回 false（开机模式将显示未配置）。
    /// </summary>
    public bool WriteBootCopy()
    {
        if (_bootMode) return false; // 开机实例只读副本，不写
        try
        {
            var cred = Load();
            if (cred is null || string.IsNullOrEmpty(cred.Username)) return false;
            var dto = new CredentialDto
            {
                Username = cred.Username,
                Secret = Convert.ToBase64String(ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(cred.Password), Entropy, DataProtectionScope.LocalMachine)),
            };
            File.WriteAllText(Path.Combine(_dir, BootFile), JsonSerializer.Serialize(dto));
            return true;
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn("BOOT 凭据副本导出失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>机器域凭据副本是否已存在（开机自检用：开机任务在但副本丢失时补导出）。</summary>
    public bool BootCopyExists() => !_bootMode && File.Exists(Path.Combine(_dir, BootFile));

    /// <summary>删除机器域副本（关闭开机自启时调用，不留机器级密文）。</summary>
    public void RemoveBootCopy()
    {
        try
        {
            var p = Path.Combine(_dir, BootFile);
            if (File.Exists(p)) File.Delete(p);
        }
        catch
        {
            // 删除失败不影响主流程
        }
    }

    private DataProtectionScope Scope => _bootMode
        ? DataProtectionScope.LocalMachine
        : DataProtectionScope.CurrentUser;

    private sealed class CredentialDto
    {
        public string Username { get; set; } = "";
        public string Secret { get; set; } = "";
    }
}
