using CampusAutoLogin.Core;
using CampusAutoLogin.Platform;
using CampusAutoLogin.Storage;

namespace CampusAutoLogin.UI;

/// <summary>
/// 高级设置弹窗（主窗口 ⚙ / 托盘「高级设置」打开）：
/// Portal 网络（地址/模板号/AC 名称）、调度（检测间隔/开机延迟）。
/// 热点名称/密码不再在此配置（自动开启时沿用系统当前热点配置）。
/// v1.2 起账号/开关等常用项移入主窗口，本窗口只收高级参数。
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly AppConfigStore _cfgStore;
    private readonly Coordinator _coord;

    private readonly TextBox _txtHost = new();
    private readonly TextBox _txtProgram = new();
    private readonly TextBox _txtAcName = new();
    private readonly TextBox _txtAcIp = new();
    private readonly NumericUpDown _numInterval = new() { Minimum = 10, Maximum = 3600, Value = 60 };
    private readonly NumericUpDown _numDelay = new() { Minimum = 0, Maximum = 3600, Value = 30 };
    private readonly Button _btnSave = new() { Text = "保存", AutoSize = true };
    private readonly Button _btnCancel = new() { Text = "取消", AutoSize = true };

    public SettingsForm(AppConfigStore cfgStore, Coordinator coord)
    {
        _cfgStore = cfgStore;
        _coord = coord;

        // 高分辨率适配：同 MainWindow —— 布局挂起期间以 96 DPI 为设计基准构建，
        // 结尾 ResumeLayout(true) 时按「实际 DPI / 96」整体缩放（详见 MainWindow 注释）
        SuspendLayout();

        Text = "高级设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        // 高分辨率适配：不用 AutoSize 窗体 —— AutoSize 链条的宽度取决于百分比列的
        // 最小内容宽度，DPI 缩放后测量失效会把输入框压扁。改用显式 96 DPI 设计尺寸，
        // 随 AutoScaleDimensions(96) 基准整体缩放（200% 屏 → 960×1000）。
        ClientSize = new Size(480, 430);
        Font = new Font("Microsoft YaHei UI", 9.75F);
        BackColor = UiTheme.Background;

        var cfg = _cfgStore.Load();
        _txtHost.Text = cfg.PortalHost;
        _txtProgram.Text = cfg.PortalProgram;
        _txtAcName.Text = cfg.WlanAcName;
        _txtAcIp.Text = cfg.WlanAcIp;
        _numInterval.Value = Math.Clamp(cfg.CheckIntervalSec, 10, 3600);
        _numDelay.Value = Math.Clamp(cfg.StartupDelaySec, 0, 3600);

        UiTheme.ApplyInput(_txtHost);
        UiTheme.ApplyInput(_txtProgram);
        UiTheme.ApplyInput(_txtAcName);
        UiTheme.ApplyInput(_txtAcIp);
        UiTheme.ApplyInput(_numInterval);
        UiTheme.ApplyInput(_numDelay);
        UiTheme.ApplyPrimary(_btnSave);
        UiTheme.ApplySecondary(_btnCancel);

        _btnSave.Click += (_, _) => OnSave();
        _btnCancel.Click += (_, _) => Close();

        BuildLayout();
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            BackColor = UiTheme.Background,
            ColumnCount = 1,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        // 三块内容行 + 末尾弹性行（吸收窗体多余高度，避免内容行被拉伸）
        root.RowCount = 4;
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // ---- Portal 网络卡片 ----
        var net = UiTheme.MakeCard("Portal 网络（校园网认证参数）");
        var nt = MakeTable();
        AddRow(nt, "Portal 地址", _txtHost);
        AddRow(nt, "模板号", _txtProgram);
        AddRow(nt, "AC 名称", _txtAcName);
        AddRow(nt, "AC IP（可选）", _txtAcIp);
        net.Controls.Add(nt);
        root.Controls.Add(net);

        // ---- 调度卡片 ----
        var sched = UiTheme.MakeCard("调度");
        var st = MakeTable();
        AddRow(st, "检测间隔（秒）", _numInterval);
        AddRow(st, "开机延迟（秒）", _numDelay);
        sched.Controls.Add(st);
        root.Controls.Add(sched);

        // ---- 按钮行 ----
        var flow = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, // 先加的在最右
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 2, 0, 0),
        };
        flow.Controls.Add(_btnCancel);
        flow.Controls.Add(_btnSave);
        root.Controls.Add(flow);

        Controls.Add(root);

        // 布局构建完毕：恢复布局并按「实际 DPI / 96」执行整体缩放（见构造函数开头注释）
        AutoScaleDimensions = new SizeF(96F, 96F);
        ResumeLayout(true);
    }

    /// <summary>卡片内两列表格：左标签（AutoSize 跟随文字，DPI 缩放安全）+ 右输入（自适应拉满）。</summary>
    private static TableLayoutPanel MakeTable()
    {
        var t = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            BackColor = UiTheme.Card,
            Padding = new Padding(4, 2, 4, 4),
        };
        // 标签列不用固定像素：高分屏缩放后固定列宽不变而文字变宽，会挤压/截断标签
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return t;
    }

    private static void AddRow(TableLayoutPanel t, string label, Control input)
    {
        t.Controls.Add(new Label
        {
            Text = label,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = true,
            ForeColor = UiTheme.TextSecondary,
            Margin = new Padding(4, 10, 4, 4),
        });
        input.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        input.Margin = new Padding(4, 7, 4, 4);
        input.BackColor = UiTheme.Card;
        t.Controls.Add(input);
    }

    private void OnSave()
    {
        var cfg = _cfgStore.Load();
        cfg.PortalHost = _txtHost.Text.Trim();
        cfg.PortalProgram = _txtProgram.Text.Trim();
        cfg.WlanAcName = _txtAcName.Text.Trim();
        cfg.WlanAcIp = _txtAcIp.Text.Trim(); // 部分 AC 固件要求非空才能定位会话；探测不到时用它回退
        cfg.CheckIntervalSec = (int)_numInterval.Value;
        cfg.StartupDelaySec = (int)_numDelay.Value;
        _cfgStore.Save(cfg);

        Logger.Instance.Info(
            $"高级设置已保存 host={cfg.PortalHost} interval={cfg.CheckIntervalSec}s");

        _coord.RefreshConfig(); // 新检测间隔立即生效（不改变暂停状态）
        Close();
    }
}
