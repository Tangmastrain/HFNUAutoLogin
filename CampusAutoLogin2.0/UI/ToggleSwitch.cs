using System.Drawing.Drawing2D;

namespace CampusAutoLogin.UI;

/// <summary>
/// iOS 风格滑动开关（自绘，无第三方库）：
/// On = 主色圆角胶囊 + 白色圆钮居右；Off = 灰色胶囊 + 圆钮居左。单击切换并触发 CheckedChanged。
/// </summary>
public sealed class ToggleSwitch : Control
{
    private bool _checked;

    /// <summary>开关状态变化事件（主窗口据此写 config / 触发 Coordinator）。</summary>
    public event EventHandler? CheckedChanged;

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public ToggleSwitch()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw
            | ControlStyles.SupportsTransparentBackColor,
            true);
        Size = new Size(46, 26);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Checked = !Checked;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 轨道：圆角胶囊
        using (var track = UiTheme.RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2))
        using (var brush = new SolidBrush(_checked ? UiTheme.Primary : UiTheme.ToggleOff))
        {
            g.FillPath(brush, track);
        }

        // 圆钮：On 右 / Off 左，留 3px 边距
        int d = Height - 6;
        int x = _checked ? Width - d - 3 : 3;
        using (var knob = new SolidBrush(Color.White))
        {
            g.FillEllipse(knob, x, 3, d, d);
        }
    }
}
