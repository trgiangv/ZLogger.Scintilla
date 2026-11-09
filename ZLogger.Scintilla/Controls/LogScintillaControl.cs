using System.ComponentModel;
using System.Windows.Forms;
using ZLogger.Scintilla.Services;
namespace ZLogger.Scintilla.Controls;

/// <summary>
/// Scintilla control that reports scrolling done by the user. Scintilla's own notifications cannot tell
/// that apart from scrolling done by the viewer, or from word wrap changing the height of the document.
/// </summary>
internal sealed class LogScintillaControl : ScintillaNET.Scintilla
{
    private const int WmDestroy = 0x0002;
    private const int WmKeyDown = 0x0100;
    private const int WmVScroll = 0x0115;
    private const int WmMouseWheel = 0x020A;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonDblClk = 0x0203;

    public event EventHandler? UserScrolled;

    /// <summary>The window ScintillaNET moved the editor under when its own window was destroyed.</summary>
    private IntPtr _parkedUnder;

    /// <summary>
    /// True while the editor waits under a message-only window. When a host destroys the window holding the
    /// editor, ScintillaNET keeps the editor alive there instead of destroying it. ScintillaNET also keeps its
    /// own index of lines, updated by notifications that only a WinForms parent passes back to it; edits made
    /// while parked are missed, and the next edit after the host gives the editor a window again reads past
    /// the end of that index.
    /// </summary>
    public bool IsParked
    {
        get
        {
            if (_parkedUnder == IntPtr.Zero)
                return false;
            if (IsHandleCreated && NativeWindowsHelper.GetParent(Handle) == _parkedUnder)
                return true;

            _parkedUnder = IntPtr.Zero;
            return false;
        }
    }

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

    /// <summary>Under a WinForms or WPF message loop, which preprocesses keys before dispatching them.</summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData) =>
        CommandKeyHandler?.Invoke(keyData) == true || base.ProcessCmdKey(ref msg, keyData);

    /// <summary>
    /// Under a host's own message loop, such as Revit's, which dispatches keys straight to the focused window,
    /// so <see cref="ProcessCmdKey"/> never runs. A key handled there does not arrive here.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (HandleCommandKey(e))
            return;
        base.OnKeyDown(e);
    }

    /// <summary>The same for a control placed on the editor, which receives its keys itself.</summary>
    public bool HandleCommandKey(KeyEventArgs e)
    {
        if (CommandKeyHandler?.Invoke(e.KeyData) != true)
            return false;

        e.Handled = true;
        e.SuppressKeyPress = true;
        return true;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WmLButtonDown:
            case WmLButtonDblClk:
                IsInDoubleClick = m.Msg == WmLButtonDblClk;
                base.WndProc(ref m);
                break;
            case WmDestroy:
                WmDestroyMayPark(ref m);
                break;
            case WmVScroll:
            case WmMouseWheel:
            case WmKeyDown:
                base.WndProc(ref m);
                UserScrolled?.Invoke(this, EventArgs.Empty);
                break;
            default:
                base.WndProc(ref m);
                break;
        }
    }

    /// <summary>A destroy that leaves the editor under another parent is ScintillaNET parking it.</summary>
    private void WmDestroyMayPark(ref Message m)
    {
        var parentBefore = NativeWindowsHelper.GetParent(m.HWnd);
        base.WndProc(ref m);
        var parentAfter = NativeWindowsHelper.GetParent(m.HWnd);
        if (parentAfter != parentBefore)
            _parkedUnder = parentAfter;
    }
}
