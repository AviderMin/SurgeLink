using System.Runtime.ExceptionServices;
using Microsoft.UI.Reactor;
using Microsoft.UI.Reactor.Core;
using SurgeLink.App.Services;
using SurgeLink.App.Shell;

namespace SurgeLink.App;

/// <summary>Application entry point. Reactor owns the WinUI application bootstrap.</summary>
public static class Program
{
    /// <summary>Starts the shell window.</summary>
    public static void Main()
    {
        // WinUI terminates the process when an exception escapes a UI callback.
        // Record it first so a crash is diagnosable from the log folder.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write("TaskScheduler", args.Exception);
            args.SetObserved();
        };

        ReactorApp.Run<MainShell>(
            new WindowSpec
            {
                Title = "SurgeLink",
                Width = 1440,
                Height = 900,
                MinWidth = 960,
                MinHeight = 600,
                StartPosition = WindowStartPosition.CenterOnCurrent,
                Backdrop = BackdropChoice.Of(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? BackdropKind.Mica : BackdropKind.None),
                ExtendsContentIntoTitleBar = true,
                PersistPlacement = true,
                PersistenceId = "surge-link-main",
            },
            configure: _ =>
            {
                // Runs on the UI thread after WinUI starts and before the root
                // mounts. Service composition and the settings file read happen
                // here once, never inside Render().
                try
                {
                    AppServices.Initialize();
                }
                catch (Exception ex)
                {
                    CrashLog.Write("Startup", ex);
                    throw;
                }
            });

        AppServices.Current?.Agent.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
