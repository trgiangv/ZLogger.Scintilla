using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace ZLogger.Scintilla.Controls;

/// <summary>
/// Tooltip for the find bar in the bar's own colors. The system tooltip ignores the theme, and without visual
/// styles, which a WPF host does not always enable, it falls back to the yellow box of old Windows.
/// On Windows 11 the window manager rounds its corners and draws its border and shadow.
/// </summary>
internal sealed class FindBarToolTip : Form
{
    private const int CsDropShadow = 0x00020000;
    private const int WsExTopmost = 0x00000008;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaBorderColor = 34;
    private const int DwmwcpRoundSmall = 3;

    // Sizes at 96 DPI.
    private const int PaddingX = 8;
    private const int PaddingY = 5;
    private const int ShortcutGap = 14;
    private const int AnchorGap = 6;

    private const TextFormatFlags TextFlags =
        TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter;

    private readonly Font _font = new("Segoe UI", 9f);
    private string _title = string.Empty;
    private string _shortcut = string.Empty;
    private Color _back = Color.White;
    private Color _fore = Color.Black;
    private Color _muted = Color.DimGray;
    private Color _border = Color.Silver;
    private bool _framedByWindowManager;
    private Rectangle _titleBounds;
    private Rectangle _shortcutBounds;

    public FindBarToolTip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WsExTopmost | WsExToolWindow | WsExNoActivate;
            cp.ClassStyle |= CsDropShadow;
            return cp;
        }
    }

    public void ApplyColors(Color back, Color fore, Color muted, Color border)
    {
        (_back, _fore, _muted, _border) = (back, fore, muted, border);
        if (IsHandleCreated)
            ApplyWindowFrame();
        Invalidate();
    }

    /// <summary>Shows the tip centred under <paramref name="anchor"/>, or above it near the bottom of the screen.</summary>
    public void ShowNear(Rectangle anchor, string title, string shortcut, int dpi)
    {
        _title = title;
        _shortcut = shortcut;
        var scale = dpi / 96f;
        int Scale(int value) => (int)Math.Round(value * scale);

        var titleSize = TextRenderer.MeasureText(title, _font, Size.Empty, TextFlags);
        var shortcutSize = shortcut.Length == 0 ? Size.Empty : TextRenderer.MeasureText(shortcut, _font, Size.Empty, TextFlags);
        var gap = shortcut.Length == 0 ? 0 : Scale(ShortcutGap);
        var padX = Scale(PaddingX);
        var padY = Scale(PaddingY);
        var height = Math.Max(titleSize.Height, shortcutSize.Height) + 2 * padY;
        var width = titleSize.Width + gap + shortcutSize.Width + 2 * padX;

        _titleBounds = new Rectangle(padX, 0, titleSize.Width, height);
        _shortcutBounds = new Rectangle(_titleBounds.Right + gap, 0, shortcutSize.Width, height);

        var area = Screen.FromRectangle(anchor).WorkingArea;
        var x = Math.Max(area.Left, Math.Min(area.Right - width, anchor.Left + (anchor.Width - width) / 2));
        var y = anchor.Bottom + Scale(AnchorGap);
        if (y + height > area.Bottom)
            y = anchor.Top - Scale(AnchorGap) - height;

        Bounds = new Rectangle(x, y, width, height);
        Invalidate();
        if (!Visible)
            Show();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowFrame();
    }

    /// <summary>Windows 10 knows neither attribute; the tip then stays square and draws its own border.</summary>
    private void ApplyWindowFrame()
    {
        var corner = DwmwcpRoundSmall;
        _framedByWindowManager = DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref corner, sizeof(int)) == 0;
        if (!_framedByWindowManager)
            return;

        var border = ColorTranslator.ToWin32(_border);
        DwmSetWindowAttribute(Handle, DwmwaBorderColor, ref border, sizeof(int));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_back);
        if (!_framedByWindowManager)
        {
            using var pen = new Pen(_border);
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }

        TextRenderer.DrawText(g, _title, _font, _titleBounds, _fore, TextFlags | TextFormatFlags.Left);
        if (_shortcut.Length > 0)
            TextRenderer.DrawText(g, _shortcut, _font, _shortcutBounds, _muted, TextFlags | TextFormatFlags.Right);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _font.Dispose();
        base.Dispose(disposing);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
