using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using ScintillaNET;
using ZLogger.Scintilla.Models;
using ZLogger.Scintilla.Pipeline;
using ZLogger.Scintilla.Public;
using ZLogger.Scintilla.Services;
namespace ZLogger.Scintilla.Controls;

/// <summary>
/// Find bar over the top-right corner of the editor, opened with Ctrl+F while the editor has the focus, so that
/// the host application's own shortcuts keep working everywhere else. It is a WinForms child window of the
/// editor, so the WPF viewer shows it too: nothing WPF draws can appear over the editor's window.
/// </summary>
internal sealed class LogFindBar : Control
{
    private enum Part { None, MatchCase, WholeWord, Regex, Previous, Next, InSelection, Close }

    // Sizes at 96 DPI.
    private const int EdgeMargin = 8;
    private const int CardHeight = 32;
    private const int CardPadding = 4;
    private const int CardRadius = 4;
    private const int FieldHeight = 24;
    private const int FieldRadius = 2;
    private const int FieldTextInset = 6;
    // Sized for narrow hosts such as a Revit dockable pane, 550px at the least: the bar stays about two thirds
    // of that wide, and the input gives up width before anything else does.
    private const int MaxFieldWidth = 180;
    private const int MinFieldWidth = 110;
    private const int ToggleSize = 18;
    private const int ToggleGap = 1;
    private const int ToggleInset = 2;
    private const int CountPadding = 4;
    private const int ButtonSize = 22;
    private const int ButtonRadius = 3;
    private const int ButtonGap = 1;
    private const int GroupGap = 4;

    private const int DebounceMs = 150;
    private const int LiveCountMs = 400;
    private const int TipDelayMs = 500;
    private const int MaxSeedLength = 200;

    private const int EmSetCueBanner = 0x1501;
    private const int WmSetCursor = 0x0020;
    private const int HtClient = 1;

    private readonly LogScintillaControl _editor;
    private readonly SearchCoordinator _search;
    private readonly ScintillaDocument _document;
    private readonly TextBox _input;
    private readonly FindBarToolTip _tip = new();
    private readonly System.Windows.Forms.Timer _tipDelay = new() { Interval = TipDelayMs };
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = DebounceMs };
    private readonly System.Windows.Forms.Timer _liveCount = new() { Interval = LiveCountMs };
    private readonly Font _textFont = new("Segoe UI", 9f);
    private readonly Font _toggleFont = new("Segoe UI", 8.25f, FontStyle.Bold);
    private readonly Font _underlinedToggleFont = new("Segoe UI", 8.25f, FontStyle.Bold | FontStyle.Underline);
    private readonly Font _glyphFont;
    private readonly Font _smallGlyphFont;
    private readonly Glyphs _glyphs;

    private Palette _palette;
    private float _scale = 1f;
    private Rectangle _card;
    private Rectangle _field;
    private Rectangle _matchCase;
    private Rectangle _wholeWord;
    private Rectangle _regex;
    private Rectangle _count;
    private Rectangle _previous;
    private Rectangle _next;
    private Rectangle _inSelection;
    private Rectangle _close;
    private Part _hover;
    private Part _pressed;

    private CancellationTokenSource? _countCts;
    private int _countedLength = -1;
    private int _total;
    private int _current;
    private SearchOptions _options;
    private bool _inSelectionOn;

    public LogFindBar(LogScintillaControl editor, SearchCoordinator search, ScintillaDocument document, IScintillaTheme theme)
    {
        _editor = editor;
        _search = search;
        _document = document;
        _palette = Palette.From(theme);
        (_glyphs, var glyphFamily) = Glyphs.Resolve();
        _glyphFont = new Font(glyphFamily, 9f);
        _smallGlyphFont = new Font(glyphFamily, 8f);

        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw, true);
        // Clicking a button leaves the focus in the text box, as in an editor's find bar.
        SetStyle(ControlStyles.Selectable, false);
        TabStop = false;
        Visible = false;

        _input = new FindTextBox
        {
            BorderStyle = System.Windows.Forms.BorderStyle.None,
            Font = _textFont,
            BackColor = _palette.Field,
            ForeColor = _palette.Text
        };
        _input.HandleCreated += (_, _) => NativeWindowsHelper.SendMessage(_input.Handle, EmSetCueBanner, 1, "Find");
        _input.TextChanged += OnInputTextChanged;
        _input.KeyDown += (_, e) => editor.HandleCommandKey(e);
        _input.GotFocus += OnInputGotFocus;
        _input.LostFocus += (_, _) => Invalidate();
        Controls.Add(_input);

        _debounce.Tick += (_, _) => ApplyPattern(jump: true);
        _liveCount.Tick += (_, _) => RecountIfDocumentChanged();
        _tipDelay.Tick += OnTipDelayElapsed;
        ApplyTipColors();

        editor.CommandKeyHandler = HandleKey;
        editor.Resize += OnEditorResize;
        editor.UpdateUI += OnEditorUpdateUi;
        document.SearchScopeLost += OnScopeLost;
        editor.Controls.Add(this);
    }

    internal bool CanFindInSelection => _inSelectionOn || HasSelection();

    /// <summary>
    /// Find in selection needs a range the user selected. The bar only moves the caret and never selects,
    /// so any selection is the user's. Read when needed, because the editor reports selection changes late.
    /// </summary>
    private bool HasSelection() =>
        _editor.DirectMessage(LogConstants.SciGetSelectionEnd, IntPtr.Zero, IntPtr.Zero)
        != _editor.DirectMessage(LogConstants.SciGetSelectionStart, IntPtr.Zero, IntPtr.Zero);

    public void ApplyTheme(IScintillaTheme theme)
    {
        _palette = Palette.From(theme);
        _input.BackColor = _palette.Field;
        _input.ForeColor = _palette.Text;
        ApplyTipColors();
        Invalidate();
    }

    private void ApplyTipColors() =>
        _tip.ApplyColors(_palette.Card, _palette.Text, _palette.Muted, _palette.TipBorder);

    // ---- Commands ----------------------------------------------------------------------------------------------

    // Keys is a flags enum with hundreds of values; these switches handle the bar's shortcuts and nothing else.
    private bool HandleKey(Keys keyData)
    {
        // ReSharper disable once SwitchStatementMissingSomeEnumCasesNoDefault
        switch (keyData)
        {
            case Keys.Control | Keys.F:
                Open();
                return true;
            case Keys.F3:
            case Keys.Shift | Keys.F3:
                Open();
                Navigate(forward: keyData == Keys.F3);
                return true;
        }

        if (!Visible)
            return false;

        // ReSharper disable once SwitchStatementHandlesSomeKnownEnumValuesWithDefault
        switch (keyData)
        {
            case Keys.Escape:
                CloseBar();
                return true;
            case Keys.Enter when _input.Focused:
            case Keys.Shift | Keys.Enter when _input.Focused:
                Navigate(forward: keyData == Keys.Enter);
                return true;
            case Keys.Alt | Keys.C:
                ToggleOption(SearchOptions.MatchCase);
                return true;
            case Keys.Alt | Keys.W:
                ToggleOption(SearchOptions.WholeWord);
                return true;
            case Keys.Alt | Keys.R:
                ToggleOption(SearchOptions.Regex);
                return true;
            case Keys.Alt | Keys.L:
                ToggleInSelection();
                return true;
            default:
                return false;
        }
    }

    private void Open()
    {
        var seed = SelectionSeed();
        if (!Visible)
        {
            Arrange();
            Visible = true;
            BringToFront();
            _liveCount.Start();
        }

        Invalidate();
        if (seed is not null && seed != _input.Text)
            _input.Text = seed;
        // Not moving: the selection may be the range the user wants to find in.
        else if (_input.TextLength > 0)
            ApplyPattern(jump: false);

        _input.Focus();
        _input.SelectAll();
    }

    private void CloseBar()
    {
        HideTip();
        Visible = false;
        _debounce.Stop();
        _liveCount.Stop();
        CancelCount();
        if (_inSelectionOn)
        {
            _search.SetScope(false);
            _inSelectionOn = false;
        }

        _search.ClearSearch();
        _editor.Focus();
    }

    /// <summary>A selection of one line is what the user most likely wants to look for.</summary>
    private string? SelectionSeed()
    {
        var text = _editor.SelectedText;
        return text.Length is > 0 and <= MaxSeedLength && text.IndexOfAny(['\r', '\n']) < 0 ? text : null;
    }

    private void ApplyPattern(bool jump)
    {
        _debounce.Stop();
        var text = _input.Text;
        _current = 0;
        if (text.Length == 0)
        {
            CancelCount();
            _search.ClearSearch();
            _total = 0;
            RefreshCount();
            return;
        }

        _search.HighlightSearch(text, _options);
        if (jump)
        {
            // As in an editor: typing moves to the first match from the caret, which sits on the last one found.
            _current = _search.FindNext().Current;
        }

        _total = -1;
        StartCount();
        RefreshCount();
    }

    private void Navigate(bool forward)
    {
        if (_input.TextLength == 0)
            return;
        if (_debounce.Enabled)
            ApplyPattern(jump: false);

        _current = (forward ? _search.FindNext() : _search.FindPrevious()).Current;
        RefreshCount();
    }

    private void ToggleOption(SearchOptions option)
    {
        _options ^= option;
        if (_input.TextLength > 0)
            ApplyPattern(jump: true);
        else
            Invalidate();
    }

    private void ToggleInSelection()
    {
        if (!CanFindInSelection)
            return;

        _inSelectionOn = _search.SetScope(!_inSelectionOn);
        _current = 0;
        if (_input.TextLength > 0)
        {
            _total = -1;
            StartCount();
        }

        RefreshCount();
    }

    private void OnScopeLost()
    {
        _inSelectionOn = false;
        Invalidate();
    }

    // ---- Counting ----------------------------------------------------------------------------------------------

    private async void StartCount()
    {
        try
        {
            await CountAsync();
        }
        catch (Exception)
        {
            // An async void method would rethrow on the host's message loop, which in Revit shows an error dialog.
            // The count is only a hint, so a failed one leaves it unknown.
        }
    }

    private async Task CountAsync()
    {
        CancelCount();
        var cts = new CancellationTokenSource();
        _countCts = cts;
        var length = _document.DocumentLength;
        try
        {
            var total = await _search.CountMatchesAsync(cts.Token);
            if (_countCts != cts || IsDisposed)
                return;

            _total = total;
            _countedLength = length;
            _current = _search.CurrentPosition(total).Current;
            RefreshCount();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // Replaced by a newer count, or the editor went away.
        }
        finally
        {
            if (_countCts == cts)
                _countCts = null;
            cts.Dispose();
        }
    }

    private void CancelCount()
    {
        _countCts?.Cancel();
        _countCts = null;
    }

    /// <summary>Logs keep arriving and leaving while the bar is open, so the count follows them.</summary>
    private void RecountIfDocumentChanged()
    {
        if (_input.TextLength == 0 || _countCts is not null || _debounce.Enabled)
            return;
        if (_document.DocumentLength != _countedLength)
            StartCount();
    }

    private void OnEditorUpdateUi(object? sender, UpdateUIEventArgs e)
    {
        if (!Visible || (e.Change & UpdateChange.Selection) == 0)
            return;

        Invalidate(); // the find-in-selection button follows the selection
        if (_total <= 0 || _countCts is not null)
            return;

        var current = _search.CurrentPosition(_total).Current;
        if (current == _current)
            return;
        _current = current;
        RefreshCount();
    }

    /// <summary>
    /// The editor keeps believing it has the focus when the focus moves into one of its own child windows, and a
    /// thread has only one caret: each match the bar moves to would drag the text box's caret along, pinning it to
    /// the box's right edge. So the editor is told it lost the focus; clicking back into it restores it.
    /// </summary>
    private void OnInputGotFocus(object? sender, EventArgs e)
    {
        _editor.DirectMessage(LogConstants.SciSetFocus, IntPtr.Zero, IntPtr.Zero);
        Invalidate();
    }

    private void OnInputTextChanged(object? sender, EventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    // ---- Layout ------------------------------------------------------------------------------------------------

    private int Scale(int value) => (int)Math.Round(value * _scale);

    private void OnEditorResize(object? sender, EventArgs e)
    {
        if (Visible)
            Arrange();
    }

    /// <summary>Wide enough for the current count, and never narrower than "No results".</summary>
    private int CountWidth()
    {
        const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        var text = TextRenderer.MeasureText(CountText, _textFont, Size.Empty, flags).Width;
        var floor = TextRenderer.MeasureText(NoResults, _textFont, Size.Empty, flags).Width;
        return Math.Max(text, floor) + Scale(CountPadding);
    }

    /// <summary>Repaints after the count changed, laying the bar out again when the count needs another width.</summary>
    private void RefreshCount()
    {
        if (Visible && CountWidth() != _count.Width)
            Arrange();
        else
            Invalidate();
    }

    private void Arrange()
    {
        _scale = _editor.DeviceDpi / 96f;
        var padding = Scale(CardPadding);
        var gap = Scale(GroupGap);
        var button = Scale(ButtonSize);
        var buttonGap = Scale(ButtonGap);
        var countWidth = CountWidth();
        var fixedWidth = 2 * padding + 3 * gap + countWidth + 4 * button + 2 * buttonGap;
        var available = _editor.ClientSize.Width - 2 * Scale(EdgeMargin) - fixedWidth;
        var fieldWidth = Math.Max(Scale(MinFieldWidth), Math.Min(Scale(MaxFieldWidth), available));

        _card = new Rectangle(0, 0, fixedWidth + fieldWidth, Scale(CardHeight));
        // The client area stops at the vertical scroll bar, so the bar never covers it.
        Bounds = new Rectangle(_editor.ClientSize.Width - Scale(EdgeMargin) - _card.Width, Scale(EdgeMargin),
            _card.Width, _card.Height);

        var fieldHeight = Scale(FieldHeight);
        var top = _card.Top + (_card.Height - fieldHeight) / 2;
        var x = _card.Left + padding;
        _field = new Rectangle(x, top, fieldWidth, fieldHeight);

        // Aa, ab and .* sit inside the right end of the text field, as in VS Code.
        var toggle = Scale(ToggleSize);
        var toggleGap = Scale(ToggleGap);
        var toggleTop = _field.Top + (_field.Height - toggle) / 2;
        var toggleX = _field.Right - Scale(ToggleInset) - 3 * toggle - 2 * toggleGap;
        _matchCase = new Rectangle(toggleX, toggleTop, toggle, toggle);
        _wholeWord = new Rectangle(_matchCase.Right + toggleGap, toggleTop, toggle, toggle);
        _regex = new Rectangle(_wholeWord.Right + toggleGap, toggleTop, toggle, toggle);

        x = _field.Right + gap;
        _count = new Rectangle(x, top, countWidth, fieldHeight);
        var buttonTop = _card.Top + (_card.Height - button) / 2;
        x = _count.Right + gap;
        _previous = new Rectangle(x, buttonTop, button, button);
        x = _previous.Right + buttonGap;
        _next = new Rectangle(x, buttonTop, button, button);
        x = _next.Right + gap;
        _inSelection = new Rectangle(x, buttonTop, button, button);
        x = _inSelection.Right + buttonGap;
        _close = new Rectangle(x, buttonTop, button, button);

        var inset = Scale(FieldTextInset);
        var textHeight = _input.PreferredHeight;
        _input.Bounds = new Rectangle(_field.Left + inset, _field.Top + (_field.Height - textHeight) / 2,
            _matchCase.Left - inset / 2 - _field.Left - inset, textHeight);
        Invalidate();
    }

    // ---- Mouse -------------------------------------------------------------------------------------------------

    private static readonly Part[] ClickableParts =
        [Part.MatchCase, Part.WholeWord, Part.Regex, Part.Previous, Part.Next, Part.InSelection, Part.Close];

    /// <summary>The part under <paramref name="point"/>, or <see cref="Part.None"/>, which is the enum's default.</summary>
    private Part HitTest(Point point) => ClickableParts.FirstOrDefault(part => BoundsOf(part).Contains(point));

    private Rectangle BoundsOf(Part part) => part switch
    {
        Part.MatchCase => _matchCase,
        Part.WholeWord => _wholeWord,
        Part.Regex => _regex,
        Part.Previous => _previous,
        Part.Next => _next,
        Part.InSelection => _inSelection,
        Part.Close => _close,
        _ => Rectangle.Empty
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var hover = HitTest(e.Location);
        if (hover == _hover)
            return;

        _hover = hover;
        HideTip();
        if (hover != Part.None)
            _tipDelay.Start();
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = Part.None;
        HideTip();
        Invalidate();
    }

    private (string Title, string Shortcut) TipFor(Part part) => part switch
    {
        Part.MatchCase => ("Match Case", "Alt+C"),
        Part.WholeWord => ("Match Whole Word", "Alt+W"),
        Part.Regex => ("Use Regular Expression", "Alt+R"),
        Part.InSelection when !CanFindInSelection => ("Select a range first to find in selection", "Alt+L"),
        Part.InSelection => ("Find in Selection", "Alt+L"),
        Part.Previous => ("Previous Match", "Shift+Enter"),
        Part.Next => ("Next Match", "Enter"),
        Part.Close => ("Close", "Escape"),
        _ => (string.Empty, string.Empty)
    };

    private void OnTipDelayElapsed(object? sender, EventArgs e)
    {
        _tipDelay.Stop();
        if (!Visible || _hover == Part.None)
            return;

        var (title, shortcut) = TipFor(_hover);
        _tip.ShowNear(RectangleToScreen(BoundsOf(_hover)), title, shortcut, DeviceDpi);
    }

    private void HideTip()
    {
        _tipDelay.Stop();
        if (_tip.Visible)
            _tip.Hide();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        HideTip();
        if (e.Button != MouseButtons.Left)
            return;

        _pressed = HitTest(e.Location);
        if (_pressed == Part.None && _field.Contains(e.Location))
            _input.Focus();
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var pressed = _pressed;
        _pressed = Part.None;
        Invalidate();
        // A click is a press and a release on the same part.
        if (e.Button == MouseButtons.Left && HitTest(e.Location) == pressed)
            Activate(pressed);
    }

    private void Activate(Part part)
    {
        switch (part)
        {
            case Part.MatchCase: ToggleOption(SearchOptions.MatchCase); break;
            case Part.WholeWord: ToggleOption(SearchOptions.WholeWord); break;
            case Part.Regex: ToggleOption(SearchOptions.Regex); break;
            case Part.Previous: Navigate(forward: false); break;
            case Part.Next: Navigate(forward: true); break;
            case Part.InSelection: ToggleInSelection(); break;
            case Part.Close: CloseBar(); break;
            case Part.None:
            default:
                break;
        }
    }

    // ---- Painting ----------------------------------------------------------------------------------------------

    private bool HasNoResults => _input.TextLength > 0 && _total == 0;

    private const string NoResults = "No results";

    private string CountText
    {
        get
        {
            if (_input.TextLength == 0 || _total == 0)
                return NoResults;

            // A negative total is a count still running.
            if (_total < 0)
                return _current > 0 ? $"{_current:n0} of ?" : "…";

            return _current > 0 ? $"{_current:n0} of {_total:n0}" : $"? of {_total:n0}";
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var p = _palette;
        g.Clear(p.Editor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Flat, as in VS Code: the bar stands out from the editor by its background alone.
        using (var card = RoundedRect(_card, Scale(CardRadius)))
        using (var fill = new SolidBrush(p.Card))
            g.FillPath(fill, card);

        PaintField(g);
        PaintButton(g, Part.MatchCase, _matchCase, "Aa", _toggleFont, enabled: true, Has(SearchOptions.MatchCase));
        PaintButton(g, Part.WholeWord, _wholeWord, "ab", _underlinedToggleFont, enabled: true, Has(SearchOptions.WholeWord));
        PaintButton(g, Part.Regex, _regex, ".*", _toggleFont, enabled: true, Has(SearchOptions.Regex));
        TextRenderer.DrawText(g, CountText, _textFont, _count, HasNoResults ? p.Error : p.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var canMove = _input.TextLength > 0 && _total != 0;
        PaintButton(g, Part.Previous, _previous, _glyphs.Up, _glyphFont, canMove, toggled: false);
        PaintButton(g, Part.Next, _next, _glyphs.Down, _glyphFont, canMove, toggled: false);
        PaintButton(g, Part.InSelection, _inSelection, _glyphs.Selection, _glyphFont, CanFindInSelection, _inSelectionOn);
        PaintButton(g, Part.Close, _close, _glyphs.Close, _smallGlyphFont, enabled: true, toggled: false);
    }

    /// <summary>
    /// As in VS Code: no border at rest, the field's own shade against the bar is enough, and a 1px accent
    /// outline with the focus. No results shows in the count's color only.
    /// </summary>
    private void PaintField(Graphics g)
    {
        var p = _palette;
        using var path = RoundedRect(_field, Scale(FieldRadius));
        using (var fill = new SolidBrush(p.Field))
            g.FillPath(fill, path);

        if (!_input.Focused)
            return;

        using var focus = new Pen(p.Accent);
        g.DrawPath(focus, path);
    }

    private bool Has(SearchOptions option) => (_options & option) != 0;

    private void PaintButton(Graphics g, Part part, Rectangle rect, string glyph, Font font, bool enabled, bool toggled)
    {
        var hot = enabled && _hover == part;
        var down = hot && _pressed == part;
        using var path = RoundedRect(rect, Scale(ButtonRadius));

        if (ButtonFill(toggled, down, hot) is { } color)
        {
            using var brush = new SolidBrush(color);
            g.FillPath(brush, path);
        }

        TextRenderer.DrawText(g, glyph, font, rect, ButtonForeground(enabled, toggled),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
            | TextFormatFlags.NoPadding);
    }

    /// <summary>A toggled option keeps its accent; otherwise pressed, then hovered. None at rest.</summary>
    private Color? ButtonFill(bool toggled, bool down, bool hot)
    {
        if (toggled)
            return _palette.AccentSoft;
        if (down)
            return _palette.Pressed;
        return hot ? _palette.Hover : null;
    }

    private Color ButtonForeground(bool enabled, bool toggled)
    {
        if (!enabled)
            return _palette.Disabled;
        return toggled ? _palette.Accent : _palette.Text;
    }

    private static GraphicsPath RoundedRect(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        var d = Math.Max(1, Math.Min(radius * 2, Math.Min(rect.Width, rect.Height)));
        var r = new RectangleF(rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Windows asks the parent first which cursor to show, and the parent here is the editor, which answers
    /// with its own: the margin's right-pointing arrow, or whatever the link hover last set. So the bar answers
    /// for itself and its text box, and the question never reaches the editor.
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmSetCursor && (m.LParam.ToInt64() & 0xFFFF) == HtClient)
        {
            Cursor.Current = m.WParam == _input.Handle ? Cursors.IBeam : Cursors.Default;
            m.Result = new IntPtr(1);
            return;
        }

        base.WndProc(ref m);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            CancelCount();
            _editor.CommandKeyHandler = null;
            _editor.Resize -= OnEditorResize;
            _editor.UpdateUI -= OnEditorUpdateUi;
            _document.SearchScopeLost -= OnScopeLost;
            _debounce.Dispose();
            _liveCount.Dispose();
            _tipDelay.Dispose();
            _tip.Dispose();
            _textFont.Dispose();
            _glyphFont.Dispose();
            _smallGlyphFont.Dispose();
            _toggleFont.Dispose();
            _underlinedToggleFont.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// A text box that asks for every key. A native host that runs IsDialogMessage, as MFC hosts such as Revit do,
    /// would otherwise turn Enter and Escape into the parent's OK and Cancel before the box sees them.
    /// </summary>
    private sealed class FindTextBox : TextBox
    {
        private const int WmGetDlgCode = 0x0087;
        private const int DlgcWantAllKeys = 0x0004;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WmGetDlgCode)
                m.Result = new IntPtr(m.Result.ToInt64() | DlgcWantAllKeys);
        }
    }

    /// <summary>Icons from the Windows icon font, with plain characters where it is missing.</summary>
    private sealed record Glyphs(string Up, string Down, string Selection, string Close)
    {
        private static readonly string[] IconFamilies = ["Segoe Fluent Icons", "Segoe MDL2 Assets"];

        public static (Glyphs Glyphs, string Family) Resolve()
        {
            var family = IconFamilies.FirstOrDefault(IsInstalled);
            return family is not null
                ? (new Glyphs("", "", "", ""), family)
                : (new Glyphs("↑", "↓", "≡", "✕"), "Segoe UI Symbol");
        }

        private static bool IsInstalled(string family)
        {
            try
            {
                using var font = new FontFamily(family);
                return true;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    /// <summary>The bar's colors, worked out from the editor's theme.</summary>
    private sealed record Palette(
        Color Editor, Color Card, Color Field, Color TipBorder, Color Text, Color Muted, Color Disabled, Color Hover,
        Color Pressed, Color Accent, Color AccentSoft, Color Error)
    {
        public static Palette From(IScintillaTheme theme)
        {
            var bg = theme.Background;
            var fg = theme.Foreground;
            // VS Code's contrast: the bar a shade off the editor, the input a shade off the bar.
            var card = Blend(bg, fg, theme.IsDark ? 0.06 : 0.05);
            var accent = theme.IsDark ? Color.FromArgb(106, 149, 246) : Color.FromArgb(50, 108, 243);
            return new Palette(
                Editor: bg,
                Card: card,
                Field: theme.IsDark ? Blend(bg, fg, 0.14) : Color.White,
                TipBorder: Blend(bg, fg, theme.IsDark ? 0.18 : 0.15),
                Text: fg,
                Muted: theme.SecondaryText,
                Disabled: Blend(card, fg, 0.35),
                Hover: Blend(card, fg, theme.IsDark ? 0.10 : 0.07),
                Pressed: Blend(card, fg, theme.IsDark ? 0.05 : 0.04),
                Accent: accent,
                AccentSoft: Blend(card, accent, theme.IsDark ? 0.28 : 0.16),
                Error: theme.LevelError);
        }

        private static Color Blend(Color from, Color to, double amount) => Color.FromArgb(
            (int)Math.Round(from.R + (to.R - from.R) * amount),
            (int)Math.Round(from.G + (to.G - from.G) * amount),
            (int)Math.Round(from.B + (to.B - from.B) * amount));
    }
}
