using CampusAutoLogin.Core;

namespace CampusAutoLogin.Auth;

/// <summary>认证层接口（策略模式）。新增一所学校 = 新增一个实现类，其余代码零改动。</summary>
public interface IAuthProvider : IDisposable
{
    string Name { get; }

    Task<AuthResult> LoginAsync(Credential cred, CancellationToken ct);

    Task<AuthResult> LogoutAsync(CancellationToken ct);
}
