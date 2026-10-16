using CampusAutoLogin.Core;
using CampusAutoLogin.Platform;
using CampusAutoLogin.Storage;

namespace CampusAutoLogin.UI;

/// <summary>
/// 高级设置弹窗（主窗口 ⚙ / 托盘「高级设置」打开）：
/// Portal 网络（地址/模板号/AC 名称）、调度（检测间隔/开机延迟）、移动热点（SSID/密码，≥8 位校验）。
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
    private readonly TextBox _txtSsid = new();
    private readonly TextBox _txtHotspotPass = new() { UseSystemPasswordChar = true };
    private readonly Label _lblNote = new()
    {
        Text = "联网后将按此名称/密码自动开启热点；密码至少 8 位。",
        AutoSize = true,
        ForeColor = UiTheme.TextSecondary,
    };
    private readonly Button _btnSave = new() { Text = "保存", AutoSize = true };
    private readonly Button _btnCancel = new() { Text = "取消", AutoSize = true };

    public SettingsForm(AppConfigStore cfgStore, Coordinator coord)
    {
        _cfgStore = cfgStore;
        _coord = coord;

        Text = "高级设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Microsoft YaHei UI", 9.75F);
        BackColor = UiTheme.Background;

        var cfg = _cfgStore.Load();
        _txtHost.Text = cfg.PortalHost;
        _txtProgram.Text = cfg.PortalProgram;
        _txtAcName.Text = cfg.WlanAcName;
        _txtAcIp.Text = cfg.WlanAcIp;
        _numInterval.Value = Math.Clamp(cfg.CheckIntervalSec, 10, 3600);
        _numDelay.Value = Math.Clamp(cfg.StartupDelaySec, 0, 3600);
        _txtSsid.Text = cfg.HotspotSsid;
        _txtHotspotPass.Text = cfg.HotspotPassword;

        UiTheme.ApplyInput(_txtHost);
        UiTheme.ApplyInput(_txtProgram);
        UiTheme.ApplyInput(_txtAcName);
        UiTheme.ApplyInput(_txtAcIp);
        UiTheme.ApplyInput(_txtSsid);
        UiTheme.ApplyInput(_txtHotspotPass);
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
            AutoSize = true,
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

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

        // ---- 移动热点卡片 ----
        var hotspot = UiTheme.MakeCard("移动热点");
        var ht = MakeTable();
        AddRow(ht, "热点名称", _txtSsid);
        AddRow(ht, "热点密码", _txtHotspotPass);
        AddNoteRow(ht, _lblNote);
        hotspot.Controls.Add(ht);
        root.Controls.Add(hotspot);

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
    }

    /// <summary>卡片内两列表格：左标签（固定 120）+ 右输入（自适应拉满）。</summary>
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
        t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
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

    private static void AddNoteRow(TableLayoutPanel t, Label lbl)
    {
        t.Controls.Add(new Label { AutoSize = true, Margin = new Padding(0) });
        lbl.Anchor = AnchorStyles.Left;
        lbl.Margin = new Padding(4, 4, 4, 4);
        t.Controls.Add(lbl);
    }

    private void OnSave()
    {
        if (_txtSsid.Text.Trim().Length == 0)
        {
            MessageBox.Show(this, "请填写热点名称。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        // 热点密码 ≥8 位校验（Windows 移动热点要求）
        if (_txtHotspotPass.Text.Length < 8)
        {
            MessageBox.Show(this, "热点密码至少需要 8 位。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _txtHotspotPass.Focus();
            return;
        }

        var cfg = _cfgStore.Load();
        cfg.PortalHost = _txtHost.Text.Trim();
        cfg.PortalProgram = _txtProgram.Text.Trim();
        cfg.WlanAcName = _txtAcName.Text.Trim();
        cfg.WlanAcIp = _txtAcIp.Text.Trim(); // 部分 AC 固件要求非空才能定位会话；探测不到时用它回退
        cfg.CheckIntervalSec = (int)_numInterval.Value;
        cfg.StartupDelaySec = (int)_numDelay.Value;
        cfg.HotspotSsid = _txtSsid.Text.Trim();
        cfg.HotspotPassword = _txtHotspotPass.Text;
        _cfgStore.Save(cfg);

        Logger.Instance.Info(
            $"高级设置已保存 host={cfg.PortalHost} interval={cfg.CheckIntervalSec}s " +
            $"hotspotSsid={cfg.HotspotSsid}");

        _coord.RefreshConfig(); // 新检测间隔 / 热点配置立即生效（不改变暂停状态）
        Close();
    }
}
