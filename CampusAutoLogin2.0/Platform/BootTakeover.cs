using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;

namespace CampusAutoLogin.Platform;

/// <summary>
/// 开机实例 ↔ 托盘实例的接管握手（v2.0.2）。
/// 开机模式（--boot，SYSTEM 会话）无界面常驻登录；用户登录 Windows 后托盘实例启动，
/// 通过置位本命名事件通知 --boot 实例退出、由托盘实例接管监控。
/// 显式授予 Everyone 完全控制：SYSTEM 创建的内核对象默认 DACL 不含普通用户，
/// 不放开权限的话用户会话根本 Set 不了事件，接管会静默失败。
/// </summary>
public static class BootTakeover
{
    public const string EventName = @"Global\CampusAutoLogin2.0.BootTakeover";

    /// <summary>
    /// 打开（不存在则创建）命名事件。创建方带 Everyone-FullControl DACL；
    /// 已存在时安全描述符被忽略（由先创建者决定），双方都用本方法取句柄即可。
    /// </summary>
    public static EventWaitHandle OpenOrCreate()
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            EventWaitHandleRights.FullControl, AccessControlType.Allow));
        return EventWaitHandleAcl.Create(
            false, EventResetMode.AutoReset, EventName, out _, security);
    }

    /// <summary>托盘实例启动时调用：通知 --boot 后台实例退出并交出控制权。绝不抛出。</summary>
    public static void Signal()
    {
        try
        {
            using var ev = OpenOrCreate();
            ev.Set();
        }
        catch
        {
            // 通知失败不影响启动：--boot 实例与托盘实例短暂并存是无害的（登录幂等）
        }
    }
}
