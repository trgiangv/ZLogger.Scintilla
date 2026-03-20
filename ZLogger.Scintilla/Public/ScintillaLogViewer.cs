using System;
namespace ZLogger.Scintilla.Public;

public sealed class ScintillaLogViewer(Action<ScintillaOptions>? configure = null) : ScintillaLogViewerBase(configure)
{
    public override object HostElement => ScintillaControl;

    protected override void DisposeHostResources() => ScintillaControl.Dispose();
}
