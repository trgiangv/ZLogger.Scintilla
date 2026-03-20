using System;
using System.Windows.Forms.Integration;
namespace ZLogger.Scintilla.Public;

public sealed class ScintillaLogViewerWpf : ScintillaLogViewerBase
{
    private readonly WindowsFormsHost _wpfHost;

    public ScintillaLogViewerWpf(Action<ScintillaOptions>? configure = null) : base(configure)
    {
        _wpfHost = new WindowsFormsHost { Child = ScintillaControl };
    }

    public override object HostElement => _wpfHost;

    protected override void DisposeHostResources()
    {
        _wpfHost.Child = null;
        _wpfHost.Dispose();
    }
}
