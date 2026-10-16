using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CampusAutoLogin.Auth;
using CampusAutoLogin.Core;
using CampusAutoLogin.Platform;
using CampusAutoLogin.Storage;
using Timer = System.Windows.Forms.Timer;

namespace CampusAutoLogin.UI;

/// <summary>
/// 主窗口：自定义深色标题栏（可拖动）+ 状态横幅（实底白字，只显示连接状态）+
/// 学号/密码表单（图标内嵌 + 明文切换）+ 五项 iOS 风格开关 + 登录/断开 + 底部状态栏（IP + 时钟）。
/// 无边框、不可缩放；X 与 Alt+F4 = 隐藏到托盘；真正退出走托盘菜单「退出」。
/// </summary>
public sealed class MainWindow : Form
{
    private const int WmNclButtonDown = 0xA1;
    private const int HtCaption = 0x2;

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DwmwaWindowCornerPreference = 33; // DWMWA_WINDOW_CORNER_PREFERENCE
    private const int DwmWindowCornerPreferenceRound = 2; // DWMWCP_ROUND

    private readonly Coordinator _coord;
    private readonly PortalAuthProvider _auth;
    private readonly AppConfigStore _cfgStore;
    private readonly CredentialStore _credStore;
    private readonly NotifyIcon _tray;

    private readonly Panel _bannerHost = new()
    {
        Dock = DockStyle.Top,
        Height = 78,
        BackColor = Color.White,
        Padding = new Padding(20, 14, 20, 14),
    };
    private readonly Panel _banner = new() { Dock = DockStyle.Fill };
    // 横幅内层表格必须是字段：否则 ApplyStatus 改 _banner.BackColor 会被内层固定背景完全盖住
    private readonly TableLayoutPanel _bannerInner = new()
    {
        Dock = DockStyle.Fill,
        ColumnCount = 1,
        RowCount = 1,
        Margin = new Padding(0),
    };
    private readonly Label _lblBanner = new()
    {
        Text = "●  正在检测…",
        AutoSize = true,
        Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
        BackColor = Color.Transparent,
        Margin = new Padding(0),
    };

    private readonly TextBox _txtUser = new()
    {
        BorderStyle = BorderStyle.None,
        Font = new Font("Microsoft YaHei UI", 10.5F),
    };
    private readonly TextBox _txtPass = new()
    {
        BorderStyle = BorderStyle.None,
        Font = new Font("Microsoft YaHei UI", 10.5F),
        UseSystemPasswordChar = true,
    };
    private readonly Button _btnEye = new()
    {
        Text = "\uE890",
        AutoSize = false,
        Size = new Size(32, 32),
        FlatStyle = FlatStyle.Flat,
        UseVisualStyleBackColor = false,
        BackColor = Color.White,
        ForeColor = UiTheme.IconGray,
        Font = new Font("Segoe MDL2 Assets", 11F),
        Cursor = Cursors.Hand,
        TabStop = false,
    };

    private readonly ToggleSwitch _tgRemember = new();
    private readonly ToggleSwitch _tgAutoLogin = new();
    private readonly ToggleSwitch _tgAutoStart = new();
    private readonly ToggleSwitch _tgHide = new();
    private readonly ToggleSwitch _tgHotspot = new();

    private readonly Button _btnLogin = new() { Text = "→  登 录", FlatStyle = FlatStyle.Flat, Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold) };
    private readonly Button _btnLogout = new() { Text = "←  断 开", FlatStyle = FlatStyle.Flat, Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold) };
    private readonly Label _lblHint = new() { AutoSize = true, ForeColor = UiTheme.TextSecondary, Margin = new Padding(0, 2, 0, 0) };

    private readonly Label _lblIpIcon = new() { Text = "\uE701", AutoSize = true, Font = new Font("Segoe MDL2 Assets", 10F), ForeColor = UiTheme.IconGray };
    private readonly Label _lblIp = new() { AutoSize = true, ForeColor = UiTheme.TextSecondary };
    private readonly Label _lblClockIcon = new() { Text = "\uE823", AutoSize = true, Font = new Font("Segoe MDL2 Assets", 10F), ForeColor = UiTheme.IconGray };
    private readonly Label _lblClock = new() { AutoSize = true, ForeColor = UiTheme.TextSecondary };
    private readonly Timer _clockTimer = new() { Interval = 1000 };

    private string _bannerStatus = "正在检测…"; // 当前状态文案
    private string? _pendingStatus;             // 句柄未创建时的最新状态，等首次 Show 再刷
    private bool _firstHide = true;

    public MainWindow(Coordinator coord, PortalAuthProvider auth, AppConfigStore cfgStore, CredentialStore credStore, NotifyIcon tray)
    {
        _coord = coord;
        _auth = auth;
        _cfgStore = cfgStore;
        _credStore = credStore;
        _tray = tray;

        Text = "合师锦绣";
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        SizeGripStyle = SizeGripStyle.Hide;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(520, 760);
        Font = new Font("Microsoft YaHei UI", 9.75F);
        BackColor = Color.White;
        ShowInTaskbar = true;

        UiTheme.ApplyDarkButton(_btnLogin);
        UiTheme.ApplyDarkButton(_btnLogout);
        _btnLogin.MinimumSize = new Size(0, 48);
        _btnLogout.MinimumSize = new Size(0, 48);

        LoadConfigToControls(); // 先设值再挂事件，避免触发开关副作用

        // ⚠️ WinForms 停靠铁律：控件集合中【最后 Add 的控件最先被停靠】，先停靠者占据最外边缘。
        // 因此必须按 Content(Fill) → BottomBar(Bottom) → Banner(Top) → TitleBar(Top) 顺序 Add，
        // 使停靠处理顺序为 TitleBar → Banner → BottomBar → Content。顺序一旦改动会导致
        // 标题栏跑到横幅下方、Content 从 y=0 铺满而盖住学号/密码（v1.2 曾出现的 P0 Bug）。
        BuildContent();      // Fill 必须最先 Add（最后停靠 → 拿到剩余空间）
        BuildBottomBar();
        BuildBanner();
        BuildTitleBar();     // 最后 Add → 最先停靠 → 占据最顶端

        WireToggles();
        WireButtons();

        _clockTimer.Tick += (_, _) => TickClock();
        _clockTimer.Start();

        _coord.StatusChanged += OnCoordStatus;
    }

    // ------------------------------------------------ 配置回填

    private void LoadConfigToControls()
    {
        var cfg = _cfgStore.Load();
        _txtUser.Text = cfg.Username;

        // 已记住的密码回填输入框（明文仅驻内存，不落盘、不写日志）
        var cred = _credStore.Load();
        if (cred is not null && cred.Username == cfg.Username)
            _txtPass.Text = cred.Password;

        _tgRemember.Checked = cfg.RememberPassword;
        _tgAutoLogin.Checked = cfg.AutoLogin;
        _tgAutoStart.Checked = cfg.AutoStart;
        _tgHide.Checked = cfg.HideOnStartup;
        _tgHotspot.Checked = cfg.HotspotEnabled;
    }

    // ------------------------------------------------ 布局

    private void BuildTitleBar()
    {
        var bar = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = UiTheme.TitleBar };
        bar.MouseDown += DragWindow;

        var lblTitle = new Label
        {
            Text = "合师锦绣",
            AutoSize = true,
            ForeColor = Color.White,
            BackColor = Color.Transparent,
            Font = new Font("Microsoft YaHei UI", 14F, FontStyle.Bold),
            Location = new Point(20, 7),
        };
        lblTitle.MouseDown += DragWindow;

        var lblVersion = new Label
        {
            // 跟随 csproj <Version>（避免硬编码漏改：2.0.2 时曾出现界面 2.0.1、程序已是 2.0.2）；
            // ProductVersion 是 InformationalVersion，带 "+<提交哈希>" 后缀，截掉只留主版本号
            Text = "v" + Application.ProductVersion.Split('+')[0],
            AutoSize = true,
            ForeColor = Color.FromArgb(0xD0, 0xD4, 0xDA), // 提高对比度（原 #9AA0A8 偏暗）
            BackColor = Color.Transparent,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            Location = new Point(22, 33),
        };
        lblVersion.MouseDown += DragWindow;

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.RightToLeft, // 先加的在最右
            BackColor = UiTheme.TitleBar,
            WrapContents = false,
            Margin = new Padding(0),
            Padding = new Padding(0),
        };
        buttons.MouseDown += DragWindow;

        var btnClose = TitleButton("\uE8BB", danger: true); // ✕（Hover 系统红）
        btnClose.Click += (_, _) => HideToTray();
        var btnLog = TitleButton("\uEA8F");                 // 🔔 日志
        btnLog.Click += (_, _) => Logger.OpenLogFolder();
        var btnSettings = TitleButton("\uE713");            // ⚙ 设置
        btnSettings.Click += (_, _) => ShowAdvancedSettings();

        buttons.Controls.Add(btnClose);
        buttons.Controls.Add(btnLog);
        buttons.Controls.Add(btnSettings);

        bar.Controls.Add(lblTitle);
        bar.Controls.Add(lblVersion);
        bar.Controls.Add(buttons);
        Controls.Add(bar); // 最后 Add → 最先停靠 → 占据最顶端
    }

    private static Button TitleButton(string glyph, bool danger = false)
    {
        var btn = new Button
        {
            Text = glyph,
            Size = new Size(48, 56),
            Margin = new Padding(0, 0, 0, 0),
            Font = new Font("Segoe MDL2 Assets", 12F), // 必须有图标字体，否则私用区字符显示不出图标
            TabStop = false,
        };
        UiTheme.ApplyTitleButton(btn, danger);
        return btn;
    }

    private void DragWindow(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture();
        _ = SendMessage(Handle, WmNclButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
    }

    private void BuildBanner()
    {
        _banner.BackColor = UiTheme.BannerIdleBg;
        _bannerInner.BackColor = UiTheme.BannerIdleBg;
        _bannerInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _bannerInner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _lblBanner.Anchor = AnchorStyles.None; // 单元格内水平垂直居中
        _lblBanner.ForeColor = UiTheme.BannerIdleFg;
        _bannerInner.Controls.Add(_lblBanner);
        _banner.Controls.Add(_bannerInner);
        _bannerHost.Controls.Add(_banner);
        Controls.Add(_bannerHost);
    }

    private void BuildContent()
    {
        var content = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(24, 16, 24, 8),
        };

        var stack = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            BackColor = Color.White,
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 0 学号标签
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));   // 1 学号输入
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 12));   // 2 间隔
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 3 密码标签
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));   // 4 密码输入
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 14));   // 5 间隔
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 6 开关组
        stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));   // 7 弹性留白
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));       // 8 操作提示
        stack.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));   // 9 按钮行

        void Put(Control c, int row)
        {
            stack.Controls.Add(c, 0, row);
        }

        Put(new Label { Text = "学号", AutoSize = true, ForeColor = UiTheme.TextPrimary, Margin = new Padding(0, 0, 0, 6) }, 0);
        Put(MakeInput("\uE77B", _txtUser, withEye: false), 1);
        Put(new Label { Text = "密码", AutoSize = true, ForeColor = UiTheme.TextPrimary, Margin = new Padding(0, 0, 0, 6) }, 3);
        Put(MakeInput("\uE72E", _txtPass, withEye: true), 4);
        Put(BuildToggleGrid(), 6);
        Put(_lblHint, 8);
        Put(BuildButtonRow(), 9);

        content.Controls.Add(stack);
        Controls.Add(content); // 最先 Add → 最后停靠 → 拿到剩余空间（Fill）
    }

    /// <summary>圆角输入容器：左侧内嵌图标 + 无边框文本框（+ 右侧明文切换按钮）。</summary>
    private Control MakeInput(string glyph, TextBox txt, bool withEye)
    {
        var box = new Panel
        {
            Height = 44,
            BackColor = Color.White,
            Margin = new Padding(0, 2, 0, 2),
            // 上/下也必须留内边距：否则 Dock=Fill 的内层表格会铺满整个客户区，
            // 把描边的上、下两条横边整条盖住（只剩左右竖边可见 → 圆角看起来是断的）。
            Padding = new Padding(14, 3, 10, 3),
            Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };
        box.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            // 描边矩形向内缩 1px：画笔骑在路径上（左右各占 0.7px），画在边缘会被裁掉
            // → 表现为右侧竖线残缺、圆角断开。内缩 + 细画笔可保证四边粗细一致、四角闭合。
            using var path = UiTheme.RoundedPath(new Rectangle(1, 1, box.Width - 3, box.Height - 3), 10);
            using var pen = new Pen(UiTheme.Border, 1.2F);
            e.Graphics.DrawPath(pen, path);
        };

        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = withEye ? 3 : 2,
            RowCount = 1,
            BackColor = Color.White,
            Margin = new Padding(0),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));        // 图标
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));    // 输入框
        if (withEye) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); // 明文切换

        var icon = new Label
        {
            Text = glyph,
            AutoSize = true,
            Font = new Font("Segoe MDL2 Assets", 11F),
            ForeColor = UiTheme.IconGray,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0),
        };
        t.Controls.Add(icon, 0, 0);

        txt.Dock = DockStyle.Fill;
        txt.Margin = new Padding(10, 0, 0, 0);
        t.Controls.Add(txt, 1, 0);

        if (withEye)
        {
            _btnEye.FlatAppearance.BorderSize = 0;
            _btnEye.FlatAppearance.MouseOverBackColor = UiTheme.BannerIdleBg;
            _btnEye.Anchor = AnchorStyles.None;
            _btnEye.Margin = new Padding(4, 0, 0, 0);
            _btnEye.Click += (_, _) =>
            {
                _txtPass.UseSystemPasswordChar = !_txtPass.UseSystemPasswordChar;
                _btnEye.Text = _txtPass.UseSystemPasswordChar ? "\uE890" : "\uE7B3";
            };
            t.Controls.Add(_btnEye, 2, 0);
        }

        box.Controls.Add(t);
        return box;
    }

    private Control BuildToggleGrid()
    {
        var grid = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            BackColor = Color.White,
            Margin = new Padding(0, 4, 0, 4),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        void Card(int col, int row, ToggleSwitch tg, string text)
        {
            var card = new Panel
            {
                BackColor = UiTheme.ToggleCard,
                Height = 54,
                Margin = new Padding(0, 6, col == 0 ? 10 : 0, 6),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            var inner = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = UiTheme.ToggleCard,
                Margin = new Padding(0),
            };
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 62));
            inner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tg.Anchor = AnchorStyles.Left;
            tg.Margin = new Padding(12, 0, 0, 0);
            var lbl = new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = UiTheme.TextPrimary,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(8, 0, 0, 0),
            };
            inner.Controls.Add(tg, 0, 0);
            inner.Controls.Add(lbl, 1, 0);
            card.Controls.Add(inner);
            grid.Controls.Add(card, col, row);
        }

        Card(0, 0, _tgRemember, "保存密码");
        Card(1, 0, _tgAutoLogin, "自动登录");
        Card(0, 1, _tgAutoStart, "开机自启动");
        Card(1, 1, _tgHide, "启动时隐藏窗口");
        Card(0, 2, _tgHotspot, "自动开启热点");
        return grid;
    }

    private Control BuildButtonRow()
    {
        var row = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.White,
            Margin = new Padding(0),
        };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _btnLogin.Dock = DockStyle.Fill;
        _btnLogin.Margin = new Padding(0, 0, 10, 0);
        _btnLogout.Dock = DockStyle.Fill;
        _btnLogout.Margin = new Padding(10, 0, 0, 0);
        row.Controls.Add(_btnLogin, 0, 0);
        row.Controls.Add(_btnLogout, 1, 0);
        return row;
    }

    private void BuildBottomBar()
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = UiTheme.BottomBar };
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            BackColor = UiTheme.BottomBar,
            Margin = new Padding(0),
            Padding = new Padding(20, 0, 20, 0),
        };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // IP 图标
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));  // IP 值
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // 时钟图标
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));      // 时钟值

        _lblIpIcon.Anchor = AnchorStyles.Left;
        _lblIp.Anchor = AnchorStyles.Left;
        _lblClockIcon.Anchor = AnchorStyles.Right;
        _lblClock.Anchor = AnchorStyles.Left;
        _lblClock.Margin = new Padding(6, 0, 0, 0);
        t.Controls.Add(_lblIpIcon, 0, 0);
        t.Controls.Add(_lblIp, 1, 0);
        t.Controls.Add(_lblClockIcon, 2, 0);
        t.Controls.Add(_lblClock, 3, 0);

        var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = UiTheme.Border };
        bar.Controls.Add(t);
        bar.Controls.Add(line); // 后加的先停靠：分隔线在顶部，表格占满其余
        Controls.Add(bar);
    }

    // ------------------------------------------------ 开关 ↔ config ↔ Coordinator 闭环

    private void WireToggles()
    {
        _tgRemember.CheckedChanged += (_, _) =>
        {
            bool on = _tgRemember.Checked;
            var cfg = _cfgStore.Load();
            cfg.RememberPassword = on;
            _cfgStore.Save(cfg);
            if (!on && _credStore.Exists())
            {
                _credStore.Delete();
                _txtPass.Clear();
                Logger.Instance.Info("已取消保存密码，删除本地凭据");
                ShowHint("已清除已保存的密码");
            }
        };

        _tgAutoLogin.CheckedChanged += (_, _) =>
        {
            var cfg = _cfgStore.Load();
            cfg.AutoLogin = _tgAutoLogin.Checked;
            _cfgStore.Save(cfg);
            if (cfg.AutoLogin)
                _coord.TriggerNow();
            else
                _coord.Pause();
        };

        _tgAutoStart.CheckedChanged += async (_, _) =>
        {
            var cfg = _cfgStore.Load();
            cfg.AutoStart = _tgAutoStart.Checked;
            _cfgStore.Save(cfg);
            // schtasks 子进程单次最长 15s，必须移出 UI 线程，否则切开关时界面假死
            bool installed = await Task.Run(AutoStart.IsInstalled);
            if (cfg.AutoStart && !installed)
            {
                bool ok = await Task.Run(() => AutoStart.Install(cfg.StartupDelaySec));
                if (!ok)
                    MessageBox.Show(this, "开机自启动设置失败（计划任务与注册表 Run 键均不可用）。",
                        "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (AutoStart.BootTrackInstalled)
                {
                    // 开机轨道已装上 → 导出机器域凭据副本，否则 SYSTEM 开机实例解不开密码
                    if (_credStore.WriteBootCopy())
                        ShowHint("已设置开机自动登录：开机后无需登录 Windows 即自动联网");
                    else
                        ShowHint("开机任务已装，但未保存密码，开机模式暂不可用");
                }
                else
                    ShowHint("已设置为登录后自动运行（管理员权限可启用免登录的开机运行）");
            }
            else if (!cfg.AutoStart && installed)
            {
                await Task.Run(AutoStart.Uninstall);
                _credStore.RemoveBootCopy(); // 不留机器级密文
            }
        };

        _tgHide.CheckedChanged += (_, _) =>
        {
            var cfg = _cfgStore.Load();
            cfg.HideOnStartup = _tgHide.Checked;
            _cfgStore.Save(cfg);
        };

        _tgHotspot.CheckedChanged += (_, _) =>
        {
            var cfg = _cfgStore.Load();
            cfg.HotspotEnabled = _tgHotspot.Checked;
            _cfgStore.Save(cfg);
            if (cfg.HotspotEnabled)
                _coord.TriggerHotspotNow(); // 在线则立即尝试开热点（不改变登录调度/暂停状态）
            ShowHint(cfg.HotspotEnabled ? "将在联网后自动开启热点" : "已关闭自动开热点");
        };
    }

    // ------------------------------------------------ 登录 / 断开

    private void WireButtons()
    {
        _btnLogin.Click += async (_, _) => await DoLoginAsync();
        _btnLogout.Click += async (_, _) => await DoLogoutAsync();
    }

    private async Task DoLoginAsync()
    {
        string user = _txtUser.Text.Trim();
        string pass = _txtPass.Text;
        if (user.Length == 0 || pass.Length == 0)
        {
            ShowHint("请先填写学号和密码");
            return;
        }

        _btnLogin.Enabled = false;
        _btnLogout.Enabled = false;
        ShowHint("正在登录…");
        try
        {
            var r = await _auth.LoginAsync(new Credential(user, pass), CancellationToken.None);
            Logger.Instance.Info("手动登录：" + (r.Success ? "成功" : $"失败[{r.FailureKind}] {r.Message}"));
            if (r.Success)
            {
                var cfg = _cfgStore.Load();
                cfg.Username = user;
                cfg.FirstRunDone = true;
                _cfgStore.Save(cfg);

                if (_tgRemember.Checked)
                {
                    _credStore.Save(new Credential(user, pass));
                    // 密码变了 → 同步刷新开机模式的机器域副本（仅开机自启已开启时才有副本）
                    if (cfg.AutoStart)
                        _credStore.WriteBootCopy();
                }

                ShowHint("登录成功"); // 不用 emoji：WinForms Label 默认字体会渲染成方框
                _coord.RunOnceNow(); // 刷新状态并联动热点；单次语义，不偷偷恢复自动登录
            }
            else
            {
                ShowHint("登录失败：" + r.Message);
            }
        }
        catch (Exception ex)
        {
            ShowHint("登录异常：" + ex.Message);
        }
        finally
        {
            _btnLogin.Enabled = true;
            _btnLogout.Enabled = true;
        }
    }

    private async Task DoLogoutAsync()
    {
        _btnLogin.Enabled = false;
        _btnLogout.Enabled = false;
        try
        {
            var r = await _coord.LogoutAsync(); // 注销（先取消在途登录）+ 暂停自动登录
            ShowHint(r.Success ? "已断开连接" : "断开失败：" + r.Message);
        }
        catch (Exception ex)
        {
            ShowHint("断开异常：" + ex.Message);
        }
        finally
        {
            _btnLogin.Enabled = true;
            _btnLogout.Enabled = true;
        }
    }

    private void ShowHint(string text) => _lblHint.Text = text;

    // ------------------------------------------------ 状态横幅

    private void OnCoordStatus(string status)
    {
        if (IsDisposed || Disposing) return;
        if (!IsHandleCreated)
        {
            _pendingStatus = status; // 尚未 Show，等 OnLoad 再刷
            return;
        }
        if (InvokeRequired)
        {
            _ = BeginInvoke(new Action(() => ApplyStatus(status)));
        }
        else
        {
            ApplyStatus(status);
        }
    }

    private void ApplyStatus(string status)
    {
        (Color bg, Color fg) = BannerColors(status);
        _bannerStatus = status;
        // 内层表格与标签都必须一起改：只改外层会被内层固定背景盖住（P1 根因）
        _banner.BackColor = bg;
        _bannerInner.BackColor = bg;
        _lblBanner.ForeColor = fg;
        UpdateBannerText();
    }

    /// <summary>只更新横幅文字（仅状态文案，不含连接时长），不重设颜色。</summary>
    private void UpdateBannerText()
    {
        string text = "●  " + _bannerStatus;

        if (_lblBanner.Text != text)
            _lblBanner.Text = text;
    }

    /// <summary>状态文字 → 横幅配色（实底白字）：已连接绿 / 进行中橙 / 失败红 / 其他灰。</summary>
    private static (Color bg, Color fg) BannerColors(string s) => s switch
    {
        "已连接" => (UiTheme.BannerOkBg, UiTheme.BannerOkFg),
        var x when x.StartsWith("正在") || x.StartsWith("重试") => (UiTheme.BannerWarnBg, UiTheme.BannerWarnFg),
        var x when x.Contains("失败") || x == "已断开" => (UiTheme.BannerErrBg, UiTheme.BannerErrFg),
        _ => (UiTheme.BannerIdleBg, UiTheme.BannerIdleFg),
    };

    // ------------------------------------------------ 底部状态栏

    private int _clockTicks;

    private void TickClock()
    {
        _lblClock.Text = DateTime.Now.ToString("HH:mm:ss");
        // IP 每 10 秒刷一次即可：每秒读 config.json + UDP 选路纯属浪费
        if (_clockTicks++ % 10 != 0) return;
        string host = _cfgStore.Load().PortalHost;
        _lblIp.Text = string.IsNullOrEmpty(host) ? "未配置 Portal" : GetLocalIp(host);
    }

    /// <summary>本机在通往 Portal 路由上的源 IP（UDP connect 不发包，仅让内核选路）。</summary>
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

    // ------------------------------------------------ 窗口行为

    /// <summary>从托盘/菜单打开主窗口（托盘单击 / 双击 / 「打开主界面」菜单共用）。</summary>
    public void ShowMain()
    {
        if (IsDisposed) return;
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>隐藏到托盘（X 按钮 / Alt+F4）；首次隐藏给气泡提示。</summary>
    public void HideToTray()
    {
        Hide();
        if (_firstHide)
        {
            _firstHide = false;
            try
            {
                _tray.ShowBalloonTip(3000, "合师锦绣", "已最小化到托盘，单击托盘图标可重新打开。", ToolTipIcon.Info);
            }
            catch
            {
                // 气泡失败不影响主流程
            }
        }
    }

    private void ShowAdvancedSettings()
    {
        foreach (var f in Application.OpenForms.OfType<SettingsForm>())
        {
            f.Activate();
            return;
        }
        new SettingsForm(_cfgStore, _coord).Show(this);
    }

    // ------------------------------------------------ 诊断（--shot）：离线截图核对用

    /// <summary>诊断用：强制把横幅设为指定状态（仅 --shot 使用，不影响正常运行路径）。</summary>
    public void ShotForceStatus(string status)
    {
        ApplyStatus(status);
    }

    /// <summary>诊断用：把当前窗口渲染为位图（--shot 使用）。</summary>
    public Bitmap RenderShot()
    {
        var bmp = new Bitmap(Math.Max(1, Width), Math.Max(1, Height));
        DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        return bmp;
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        ApplyStatus(_pendingStatus
                    ?? (string.IsNullOrEmpty(_coord.CurrentStatus) ? "正在检测…" : _coord.CurrentStatus));
        _pendingStatus = null;
        TickClock();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        TryEnableRoundedCorners();
    }

    /// <summary>
    /// Win11 圆角窗口（设计稿为圆角）。Win10 及更早 DwmSetWindowAttribute 返回错误码，
    /// 忽略即可；全程 try/catch，绝不影响启动。
    /// 注：DrawToBitmap 截的是窗口内容位图，不含 DWM 合成层的实际圆角裁剪，故截图上圆角不可见，
    /// 是否生效以返回的 HRESULT 为准（记入日志）。
    /// </summary>
    private void TryEnableRoundedCorners()
    {
        try
        {
            int pref = DwmWindowCornerPreferenceRound;
            int hr = DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref pref, sizeof(int));
            if (hr == 0)
                Logger.Instance.Info("圆角窗口已启用（DWM_WINDOW_CORNER_PREFERENCE=ROUND）");
            else
                Logger.Instance.Info($"圆角窗口属性未生效（HRESULT=0x{hr:X8}，Win10 及更早属正常，不影响启动）");
        }
        catch (Exception ex)
        {
            Logger.Instance.Info("圆角窗口设置跳过：" + ex.Message);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // X 与 Alt+F4 都走隐藏到托盘；真正退出由托盘「退出」菜单调用 Dispose
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _coord.StatusChanged -= OnCoordStatus;
            _clockTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
