using System;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Public;

namespace ZLogger.Scintilla.Demo;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
#if NET8_0_OR_GREATER
        ApplicationConfiguration.Initialize();
#else
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
#endif

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<MainForm>();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        builder.Logging.AddZLoggerScintillaWinForms(
            v => v
                .Channel(capacity: 50_000, flushMs: 50, maxBatch: 800)
                .Display(maxLines: 50_000)
                .History(maxEntries: 50_000)
                .SetTheme(ScintillaThemes.Dark)
                .WithColorized(new DemoRevitColorizer())
                .WithLinkify(new DemoRevitLinkifier()));

        using var host = builder.Build();
        var mainForm = host.Services.GetRequiredService<MainForm>();
        if (args.Any(static x => string.Equals(x, "--smoke", StringComparison.OrdinalIgnoreCase)))
            mainForm.EnableSmokeMode();
        Application.Run(mainForm);
    }
}
