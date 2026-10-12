using System.Drawing.Drawing2D;

namespace CampusAutoLogin.UI;

/// <summary>
/// 统一 UI 主题：现代蓝主色 #2D7DFA、浅灰背景 #F5F6FA、白色卡片分组、
/// 扁平按钮（主操作蓝底白字 / 次操作白底描边）+ Hover 效果。
/// v1.2 扩展：深色标题栏/深黑主按钮、状态横幅语义色、圆角路径（输入框/开关）。
/// 不引入第三方 UI 库。
/// </summary>
internal static class UiTheme
{
    // ---- 色板 ----
    public static readonly Color Primary = Color.FromArgb(0x2D, 0x7D, 0xFA);
    public static readonly Color PrimaryDark = Color.FromArgb(0x1B, 0x63, 0xE0);
    public static readonly Color PrimaryLight = Color.FromArgb(0xEA, 0xF1, 0xFF);
    public static readonly Color Background = Color.FromArgb(0xF5, 0xF6, 0xFA);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(0xE3, 0xE6, 0xEC);
    public static readonly Color TextPrimary = Color.FromArgb(0x33, 0x33, 0x33);
    public static readonly Color TextSecondary = Color.FromArgb(0x8A, 0x8F, 0x99);
    public static readonly Color Success = Color.FromArgb(0x1F, 0xA9, 0x55);
    public static readonly Color Warning = Color.FromArgb(0xF5, 0x8A, 0x1F);
    public static readonly Color Danger = Color.FromArgb(0xE5, 0x48, 0x4D);

    // ---- v1.2 主界面（深色标题栏 / 深黑按钮 / 横幅语义色）----
    public static readonly Color TitleBar = Color.FromArgb(0x1F, 0x21, 0x26);
    public static readonly Color TitleBarHover = Color.FromArgb(0x33, 0x36, 0x3D);
    public static readonly Color TitleBarText = Color.FromArgb(0xC8, 0xCB, 0xD0);
    public static readonly Color TitleBarSubText = Color.FromArgb(0x9A, 0xA0, 0xA8);
    public static readonly Color DarkButton = Color.FromArgb(0x16, 0x18, 0x1D);
    public static readonly Color DarkButtonHover = Color.FromArgb(0x2E, 0x32, 0x3A);
    public static readonly Color IconGray = Color.FromArgb(0x9A, 0xA0, 0xA8);
    public static readonly Color ToggleOff = Color.FromArgb(0xD5, 0xDA, 0xE1);
    public static readonly Color ToggleCard = Color.FromArgb(0xF3, 0xF4, 0xF6);
    public static readonly Color BottomBar = Color.FromArgb(0xF7, 0xF8, 0xFA);

    // v1.2.2：实底 + 白字（强对比方案，用户点名要求）
    public static readonly Color BannerOkBg = Color.FromArgb(0x1B, 0x8A, 0x4C);
    public static readonly Color BannerOkFg = Color.White;
    public static readonly Color BannerWarnBg = Color.FromArgb(0xE8, 0x84, 0x2A);
    public static readonly Color BannerWarnFg = Color.White;
    public static readonly Color BannerErrBg = Color.FromArgb(0xD6, 0x45, 0x45);
    public static readonly Color BannerErrFg = Color.White;
    public static readonly Color BannerIdleBg = Color.FromArgb(0x5A, 0x62, 0x70);
    public static readonly Color BannerIdleFg = Color.White;

    private static readonly Font InputFont = new("Microsoft YaHei UI", 9.75F);

    /// <summary>主操作按钮（保存 / 立即检测）：主色底白字，扁平、Hover 加深、手型光标。</summary>
    public static void ApplyPrimary(Button btn)
    {
        btn.FlatStyle = FlatStyle.Flat;
        btn.UseVisualStyleBackColor = false;
        btn.BackColor = Primary;
        btn.ForeColor = Color.White;
        btn.Cursor = Cursors.Hand;
        btn.MinimumSize = new Size(96, 34); // 统一高度 34（32+）
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = PrimaryDark;
        btn.FlatAppearance.MouseDownBackColor = PrimaryDark;
    }

    /// <summary>次操作按钮（取消 / 测试登录 / 打开日志）：白底浅描边，Hover 浅蓝。</summary>
    public static void ApplySecondary(Button btn)
    {
        btn.FlatStyle = FlatStyle.Flat;
        btn.UseVisualStyleBackColor = false;
        btn.BackColor = Card;
        btn.ForeColor = TextPrimary;
        btn.Cursor = Cursors.Hand;
        btn.MinimumSize = new Size(96, 34);
        btn.FlatAppearance.BorderSize = 1;
        btn.FlatAppearance.BorderColor = Border;
        btn.FlatAppearance.MouseOverBackColor = PrimaryLight;
        btn.FlatAppearance.MouseDownBackColor = Color.FromArgb(0xD8, 0xE7, 0xFD);
    }

    /// <summary>输入控件统一：微软雅黑 9.75F + 单线边框。</summary>
    public static void ApplyInput(Control input)
    {
        input.Font = InputFont;
        if (input is TextBoxBase tb) tb.BorderStyle = BorderStyle.FixedSingle;
        else if (input is UpDownBase ud) ud.BorderStyle = BorderStyle.FixedSingle;
    }

    /// <summary>白卡片分组（GroupBox 扁平化 + 白底 + 内边距 + 底部间距）。</summary>
    public static GroupBox MakeCard(string title)
    {
        return new GroupBox
        {
            Text = " " + title,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 12),
            Padding = new Padding(12, 2, 12, 10),
            BackColor = Card,
            ForeColor = TextPrimary,
            FlatStyle = FlatStyle.Flat,
        };
    }

    /// <summary>深黑主按钮（登录 / 断开）：#16181D 底白字，扁平 + Hover。</summary>
    public static void ApplyDarkButton(Button btn)
    {
        btn.FlatStyle = FlatStyle.Flat;
        btn.UseVisualStyleBackColor = false; // 关键：否则系统视觉样式强制背景色，自绘配色失效
        btn.BackColor = DarkButton;
        btn.ForeColor = Color.White;
        btn.Cursor = Cursors.Hand;
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = DarkButtonHover;
        btn.FlatAppearance.MouseDownBackColor = DarkButtonHover;
    }

    /// <summary>
    /// 深色标题栏上的图标按钮（⚙ / 🔔 / ✕）。danger=true 时 Hover 用系统红（Win11 关闭按钮风格）。
    /// 注意：必须关闭 UseVisualStyleBackColor，否则 BackColor 被系统视觉样式覆盖 → 深色栏上出现白块。
    /// </summary>
    public static void ApplyTitleButton(Button btn, bool danger = false)
    {
        btn.FlatStyle = FlatStyle.Flat;
        btn.UseVisualStyleBackColor = false;
        btn.BackColor = TitleBar;
        btn.ForeColor = Color.White; // 高对比：纯白图标
        btn.Cursor = Cursors.Hand;
        btn.FlatAppearance.BorderSize = 0;
        btn.FlatAppearance.MouseOverBackColor = danger ? Color.FromArgb(0xE8, 0x11, 0x23) : TitleBarHover;
        btn.FlatAppearance.MouseDownBackColor = danger ? Color.FromArgb(0xC4, 0x0E, 0x1D) : TitleBarHover;
    }

    /// <summary>圆角矩形路径（输入框描边 / 开关胶囊共用）。</summary>
    public static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
