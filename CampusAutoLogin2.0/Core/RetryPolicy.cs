namespace CampusAutoLogin.Core;

/// <summary>
/// 指数退避：5s → 10s → 20s → 40s → 80s → ... 上限 300s。
/// 成功后必须 Reset()。
/// </summary>
public sealed class RetryPolicy
{
    private const int Initial = 5;
    private const int Max = 300;

    private int _backoffSec = Initial;

    public int Next()
    {
        int cur = _backoffSec;
        _backoffSec = Math.Min(_backoffSec * 2, Max);
        return cur;
    }

    public void Reset() => _backoffSec = Initial;
}
