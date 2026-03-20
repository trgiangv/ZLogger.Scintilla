using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ZLogger.Scintilla.Public;

namespace ZLogger.Scintilla.Wpf.Demo;

public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSingleton<MainWindow>();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);

        builder.Logging.AddZLoggerScintillaWpf(
            v => v
                .Channel(capacity: 50_000, flushMs: 50, maxBatch: 800)
                .Display(maxLines: 50_000)
                .History(maxEntries: 50_000)
                .SetTheme(ScintillaThemes.Dark)
                .WithColorized(new DemoRevitColorizer())
                .WithLinkify(new DemoRevitLinkifier()),
            z =>
            {
                z.IncludeScopes = true;
                z.UsePlainTextFormatter(formatter =>
                {
                    formatter.SetPrefixFormatter(
                        $"[{0:local-timeonly} {1:short}] ",
                        (in template, in info) => template.Format(info.Timestamp, info.LogLevel));
                });
            });

        _host = builder.Build();
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        _host = null;
        base.OnExit(e);
    }
}
