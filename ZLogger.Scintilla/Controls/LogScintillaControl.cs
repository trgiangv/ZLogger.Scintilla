using System.ComponentModel;
using System.Windows.Forms;
namespace ZLogger.Scintilla.Controls;

/// <summary>
/// Scintilla control that reports scrolling done by the user. Scintilla's own notifications cannot tell
/// that apart from scrolling done by the viewer, or from word wrap changing the height of the document.
/// </summary>
internal sealed class LogScintillaControl : ScintillaNET.Scintilla
{
    private const int WmKeyDown = 0x0100;
    private const int WmVScroll = 0x0115;
    private const int WmMouseWheel = 0x020A;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonDblClk = 0x0203;

    public event EventHandler? UserScrolled;

    /// <summary>
    /// True from the second press of a double click until the next press. The control's own
    /// <see cref="MouseEventArgs.Clicks"/> cannot say, because ScintillaNET turns off standard double clicks.
    /// </summary>
    public bool IsInDoubleClick { get; private set; }

    /// <summary>
    /// Sees shortcut keys first, both for the editor and for controls placed on it such as the find bar.
    /// Returns true when it handled the key.
    /// </summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Func<Keys, bool>? CommandKeyHandler { get; set; }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        CommandKeyHandler?.Invoke(keyData) == true || base.ProcessCmdKey(ref msg, keyData);

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is WmLButtonDown or WmLButtonDblClk)
            IsInDoubleClick = m.Msg == WmLButtonDblClk;

        base.WndProc(ref m);
        if (m.Msg is WmVScroll or WmMouseWheel or WmKeyDown)
            UserScrolled?.Invoke(this, EventArgs.Empty);
    }
}
