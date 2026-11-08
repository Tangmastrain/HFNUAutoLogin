using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CampusAutoLogin.Platform;

/// <summary>
/// 应用图标工厂：把嵌入的校徽 app.ico 作为托盘底图，并在右下角叠加一枚
/// 带 1px 白色描边的状态圆点，从而在"校徽"外观下保留原有的状态语义：
/// 已连接=绿色、登录失败=红色、其他（未连接/启动中）=橙色。
///
/// 设计要点：
/// - 基准尺寸跟随系统小图标尺寸（随 DPI 变化），直接取用 app.ico 对应像素帧，
///   避免 Windows 把 16px 放大导致托盘图标发虚。
/// - 所有 Icon 对象 static 缓存，只构造一次；退出时调用 <see cref="DisposeCache"/> 释放。
/// - 整个加载/绘制过程包在 try/catch 中，任何失败都回退到 SystemIcons，
///   保证该类型绝不抛异常（它位于程序启动路径上）。
/// </summary>
internal static class AppIcon
{
    /// <summary>嵌入资源逻辑名，需与 csproj 中 EmbeddedResource 的 LogicalName 一致。</summary>
    private const string ResName = "CampusAutoLogin.app.ico";

    // 状态圆点配色（承接原 SystemIcons 语义）
    private static readonly Color ColorOk = Color.FromArgb(46, 160, 67);    // 已连接（原 Shield）
    private static readonly Color ColorFail = Color.FromArgb(214, 69, 69);  // 登录失败（原 Error）
    private static readonly Color ColorIdle = Color.FromArgb(232, 132, 42); // 其他未连接（原 Warning）

    // ---- 缓存（static，只构造一次）----
    private static int _size;    // 托盘图标边长（物理像素，随 DPI 变化）
    private static Icon? _base;  // 裸校徽帧（按 _size 取）
    private static Icon? _ok;    // 已连接（绿点角标）
    private static Icon? _fail;  // 登录失败（红点角标）
    private static Icon? _idle;  // 未连接/启动中（橙点角标）

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// 托盘图标边长：跟随系统小图标尺寸（100%=16、125%=20、150%=24），
    /// 避免 Windows 把 16px 放大导致托盘图标发虚。
    /// </summary>
    private static int TraySize()
    {
        if (_size != 0) return _size;
        int n = 16;
        try { n = SystemInformation.SmallIconSize.Width; } catch { n = 16; }
        if (n < 16) n = 16;
        if (n > 64) n = 64;
        _size = n;
        Logger.Instance.Info($"图标：托盘基准尺寸 {n}x{n}px");
        return n;
    }

    /// <summary>
    /// 取托盘基准尺寸的裸校徽帧（只构造一次）；取不到时回退 SystemIcons.Application。
    /// </summary>
    private static Icon Base()
        => _base ??= Load(TraySize()) ?? SystemIcons.Application;

    /// <summary>
    /// 从嵌入资源读取 app.ico 并按指定尺寸取帧；失败（资源缺失/损坏）返回 null。
    /// </summary>
    private static Icon? Load(int size)
    {
        try
        {
            using var st = typeof(AppIcon).Assembly.GetManifestResourceStream(ResName);
            if (st is null)
            {
                Logger.Instance.Warn($"图标：嵌入资源 {ResName} 未找到，回退系统图标");
                return null;
            }
            var ico = new Icon(st, new Size(size, size));
            Logger.Instance.Info($"图标：嵌入校徽 {size}x{size} 帧加载成功");
            return ico;
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn($"图标：嵌入校徽 {size}x{size} 帧加载失败，回退系统图标：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 按状态文案返回带状态角标的托盘图标（缓存）。任何异常都回退 SystemIcons.Application。
    /// status 取值与 TrayContext 中传入的字符串一致，例如"已连接"、"登录失败：xxx"、"启动中"、"未连接"。
    /// </summary>
    internal static Icon ForStatus(string status)
    {
        try
        {
            if (status == "已连接") return _ok ??= Tint(ColorOk);
            if (status.StartsWith("登录失败")) return _fail ??= Tint(ColorFail);
            return _idle ??= Tint(ColorIdle);
        }
        catch (Exception ex)
        {
            Logger.Instance.Warn($"图标：状态角标绘制失败，回退系统图标：{ex.GetType().Name}: {ex.Message}");
            return SystemIcons.Application;
        }
    }

    /// <summary>
    /// 以托盘基准尺寸的裸校徽为底，右下角绘制带 1px 白色描边的状态圆点，返回新的 Icon 副本。
    /// </summary>
    private static Icon Tint(Color dot)
    {
        int n = TraySize();
        var baseIcon = Base();
        using var bmp = new Bitmap(n, n, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawIcon(baseIcon, new Rectangle(0, 0, n, n));

            // 角标按比例缩放：16px 时 6px + 1px 边距，20px 时 7px，24px 时 9px
            int d = Math.Max(6, n * 6 / 16);
            int m = Math.Max(1, n / 16);
            var rect = new Rectangle(n - d - m, n - d - m, d, d);
            using var halo = new SolidBrush(Color.White);
            g.FillEllipse(halo, Rectangle.Inflate(rect, 1, 1));
            using var brush = new SolidBrush(dot);
            g.FillEllipse(brush, rect);
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(h).Clone()!;
        }
        finally
        {
            // 必须释放 HIcon，否则句柄泄漏
            DestroyIcon(h);
        }
    }

    /// <summary>
    /// 释放所有缓存的图标句柄，程序退出时调用。
    /// </summary>
    internal static void DisposeCache()
    {
        _base?.Dispose();
        _ok?.Dispose();
        _fail?.Dispose();
        _idle?.Dispose();
        _base = _ok = _fail = _idle = null;
        _size = 0;
    }
}
