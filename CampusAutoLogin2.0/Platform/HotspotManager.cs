using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace CampusAutoLogin.Platform;

/// <summary>
/// Windows 移动热点（Mobile Hotspot）自动开启（WinRT / CsWinRT，需 net8.0-windows10.0.19041.0）。
/// 需求：检测到有网络时自动开启热点，SSID/密码来自 config。
/// 全程 try/catch：不支持移动热点的机器（无网卡/驱动不支持/系统策略）记录日志并通知，绝不抛出、绝不影响主登录流程。
/// </summary>
public static class HotspotManager
{
    /// <summary>
    /// 确保热点已按期望的 SSID/密码开启。返回 true = 热点已开启（含本来就在线的情况）。
    /// </summary>
    public static async Task<bool> EnsureStartedAsync(string ssid, string passphrase)
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null)
            {
                Logger.Instance.Info("HOTSPOT 跳过：当前无互联网连接配置（未联网）");
                return false;
            }

            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            var conf = manager.GetCurrentAccessPointConfiguration();

            // 配置与期望不符 → 先改再存（SSID/密码来自 config.json）
            if (!string.Equals(conf.Ssid, ssid, StringComparison.Ordinal) ||
                !string.Equals(conf.Passphrase, passphrase, StringComparison.Ordinal))
            {
                conf.Ssid = ssid;
                conf.Passphrase = passphrase;
                await manager.ConfigureAccessPointAsync(conf);
                Logger.Instance.Info($"HOTSPOT 配置已更新 ssid={ssid}");
            }

            if (manager.TetheringOperationalState == TetheringOperationalState.On)
            {
                Logger.Instance.Info("HOTSPOT 已处于开启状态");
                return true;
            }

            var result = await manager.StartTetheringAsync();
            if (result.Status == TetheringOperationStatus.Success)
            {
                Logger.Instance.Info($"HOTSPOT 开启成功 ssid={ssid}");
                return true;
            }

            Logger.Instance.Warn($"HOTSPOT 开启失败：Status={result.Status}，{result.AdditionalErrorMessage}");
            return false;
        }
        catch (Exception ex)
        {
            // 机器不支持移动热点 / 权限不足 / 驱动问题：只记录，不抛出，不影响主登录流程
            Logger.Instance.Warn("HOTSPOT 本机不支持或执行异常：" + ex.Message);
            return false;
        }
    }
}
