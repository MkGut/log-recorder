using System.Diagnostics;
using static Log.Shell.Native;

namespace Log.Shell;

internal static class Program
{
    public static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static bool Timing;

    [STAThread]
    static int Main(string[] args)
    {
        Timing = args.Contains("--timing");
        // Portable mode: portable.txt beside the exe keeps all data in .\data and skips the install offer.
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.txt"))
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LOG_DATA_ROOT")))
            Environment.SetEnvironmentVariable("LOG_DATA_ROOT", Path.Combine(AppContext.BaseDirectory, "data"));
        CoInitializeEx(0, 2); // apartment-threaded, for the capture picker and folder dialog
        Skin.Current.Refresh();
        var fx = Array.IndexOf(args, "--render-fixtures");
        if (fx >= 0 && fx + 1 < args.Length) return Fixtures.Render(args[fx + 1]);
        Installer.Quiet = args.Contains("--quiet");
        if (args.Contains("--install")) return Installer.InstallFromCommandLine(args);
        if (args.Contains("--uninstall")) { Installer.Uninstall(args); return 0; }
        if (Installer.OfferOnFirstRun(args)) return 0; // installed copy was started
        var hold = Array.IndexOf(args, "--fixture-window");
        if (hold >= 0 && hold + 1 < args.Length)
            return Fixtures.Hold(args[hold + 1], hold + 2 < args.Length ? args[hold + 2] : null);
        int rc;
        bool restart;
        using (var app = new ShellApp(args))
        {
            rc = app.Start() ? app.Run() : 0;
            restart = app.RestartForUpdate;
        }
        // Disposed first, so the new copy can take the single-instance lock.
        if (restart) Updater.Relaunch(Installer.DefaultInstallDir);
        return rc;
    }
}
