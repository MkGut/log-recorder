using System.Diagnostics;
using System.Runtime.InteropServices;
using static Log.Shell.Native;

namespace Log.Shell;

/// <summary>One registry value to write under HKEY_CURRENT_USER.</summary>
internal readonly record struct RegValue(string Key, string Name, object Data);

/// <summary>
/// Per-user, no-admin install of log.exe: copy to %LOCALAPPDATA%\Programs\log, Start-menu shortcut,
/// optional run at sign-in, and an "Installed apps" entry that runs <c>--uninstall</c>.
/// What to write is decided by pure functions so it can be tested; the rest is Win32.
/// </summary>
internal static unsafe class Installer
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\log";
    public const string DeclineKey = @"Software\log";
    const string ExeName = "log.exe";
    const string BridgeName = "log.MediaFoundationBridge.dll";

    public static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "log");

    public static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "log.lnk");

    /// <summary>
    /// Offer to install only on a plain double-click: no arguments, no test data root, not already
    /// the installed copy, and not declined before.
    /// </summary>
    public static bool ShouldOffer(string[] args, string exePath, string installDir, bool dataRootSet, bool declined)
    {
        if (args.Length > 0 || dataRootSet || declined) return false;
        return !IsInside(exePath, installDir);
    }

    public static bool IsInside(string path, string dir) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Everything the installer writes to the registry for an install at <paramref name="installDir"/>.</summary>
    public static IReadOnlyList<RegValue> Describe(string installDir, string version, bool runAtSignIn)
    {
        var exe = Path.Combine(installDir, ExeName);
        var list = new List<RegValue>
        {
            new(UninstallKey, "DisplayName", "log"),
            new(UninstallKey, "DisplayVersion", version),
            new(UninstallKey, "Publisher", "log"),
            new(UninstallKey, "InstallLocation", installDir),
            new(UninstallKey, "DisplayIcon", exe),
            new(UninstallKey, "UninstallString", $"\"{exe}\" --uninstall"),
            new(UninstallKey, "NoModify", 1),
            new(UninstallKey, "NoRepair", 1),
        };
        // The sign-in launch passes --at-sign-in so the wake reminder treats it as a cold sign-in.
        if (runAtSignIn) list.Add(new RegValue(RunKey, "log", $"\"{exe}\" --at-sign-in"));
        return list;
    }

    // --- First run ---
    /// <summary>
    /// Asks once whether to install. Returns true when the installed copy was started and this
    /// process should exit; false to carry on running from where it is.
    /// </summary>
    public static bool OfferOnFirstRun(string[] args)
    {
        var exePath = Environment.ProcessPath ?? "";
        var dir = DefaultInstallDir;
        var dataRootSet = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("LOG_DATA_ROOT"));
        if (exePath.Length == 0 || !ShouldOffer(args, exePath, dir, dataRootSet, Reg.GetDword(DeclineKey, "InstallDeclined") == 1))
            return false;

        if (Ask("Install log for this user?\n\nlog will be copied to your Programs folder, get a Start menu shortcut and an entry in Installed apps. No administrator rights are needed.\n\nThe installed copy checks this project's GitHub releases for signed updates about twice a day and installs them while log is idle. Nothing about you or your recordings is sent. Turn this off any time from the tray menu (Automatic updates).", 0x24) != 6)
        {
            Reg.SetDword(DeclineKey, "InstallDeclined", 1); // do not ask again; run from here
            return false;
        }

        var signIn = Ask("Start log when you sign in to Windows?\n\nIt stays in the tray and offers a gentle invitation. You can remove it from Settings > Apps > Startup.", 0x24) == 6;
        try
        {
            Install(exePath, dir, signIn);
            Process.Start(new ProcessStartInfo(Path.Combine(dir, ExeName)) { UseShellExecute = false });
            return true;
        }
        catch (Exception e)
        {
            AppLog.Write("install", e);
            Ask("log could not be installed: " + e.Message + "\n\nIf log is already running, exit it from the tray and try again. This copy will run from where it is.", 0x10);
            return false;
        }
    }

    /// <summary>Silent mode (<c>--quiet</c>): every question is answered yes, nothing is shown.</summary>
    public static bool Quiet { get; set; }

    /// <summary><c>log.exe --install [--sign-in]</c>: install without questions, then exit.</summary>
    public static int InstallFromCommandLine(string[] args)
    {
        try
        {
            Install(Environment.ProcessPath!, DefaultInstallDir, runAtSignIn: args.Contains("--sign-in"));
            return 0;
        }
        catch (Exception e)
        {
            AppLog.Write("install", e);
            return 1;
        }
    }

    static int Ask(string text, uint flags)
    {
        if (Quiet) return 6;
        fixed (char* t = text)
        fixed (char* c = "log")
            return MessageBoxW(0, t, c, flags);
    }

    // --- Install / uninstall ---
    public static void Install(string sourceExe, string installDir, bool runAtSignIn)
    {
        Directory.CreateDirectory(installDir);
        var from = Path.GetDirectoryName(sourceExe)!;
        File.Copy(sourceExe, Path.Combine(installDir, ExeName), overwrite: true);
        var bridge = Path.Combine(from, BridgeName);
        if (File.Exists(bridge)) File.Copy(bridge, Path.Combine(installDir, BridgeName), overwrite: true);

        var version = typeof(Installer).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        foreach (var v in Describe(installDir, version, runAtSignIn))
        {
            if (v.Data is int i) Reg.SetDword(v.Key, v.Name, i); else Reg.SetString(v.Key, v.Name, (string)v.Data);
        }

        if (!runAtSignIn) RemoveSignInIfOurs(installDir); // never remove an entry another program wrote
        Reg.SetDword(DeclineKey, "InstallDeclined", 0);
        CreateShortcut(ShortcutPath, Path.Combine(installDir, ExeName));
    }

    /// <summary>True when a sign-in command line points into <paramref name="installDir"/> (so it is ours to remove).</summary>
    public static bool IsOurSignIn(string? command, string installDir) =>
        !string.IsNullOrEmpty(command) && command.Contains(Path.TrimEndingDirectorySeparator(installDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    static void RemoveSignInIfOurs(string installDir)
    {
        if (IsOurSignIn(Reg.GetString(RunKey, "log"), installDir)) Reg.DeleteValue(RunKey, "log");
    }

    /// <summary>Removes the app, shortcut and registry entries. Recordings and settings are kept.</summary>
    public static void Uninstall(string[] args)
    {
        var exe = Environment.ProcessPath ?? "";
        var dir = DefaultInstallDir;
        if (Ask("Remove log from this PC?\n\nYour recordings and saved videos are kept.", 0x24) != 6) return;

        RemoveSignInIfOurs(dir);
        Reg.DeleteTree(UninstallKey);
        Reg.DeleteTree(DeclineKey);
        try { File.Delete(ShortcutPath); }
        catch (IOException) { /* shortcut already gone or in use */ }

        // The running exe cannot delete itself; a hidden shell does it a moment after we exit.
        if (IsInside(exe, dir))
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), UninstallCommand(dir))
            { UseShellExecute = false, CreateNoWindow = true });
        Ask("log was removed. Your recordings were kept.", 0x40);
    }

    /// <summary>
    /// cmd.exe arguments that delete only what install and update put in <paramref name="dir"/>. The final
    /// rmdir has no /s, so a folder still holding anything else (portable recordings, say) is left alone.
    /// </summary>
    public static string UninstallCommand(string dir)
    {
        var files = string.Join(" ", new[] { ExeName, BridgeName, "*.old", "*.new" }.Select(f => $"\"{Path.Combine(dir, f)}\""));
        return $"/c ping -n 4 127.0.0.1 >nul & del /f /q {files} & rmdir \"{dir}\"";
    }

    // --- Start menu shortcut (IShellLinkW through raw COM) ---
    static void CreateShortcut(string lnkPath, string target)
    {
        var clsid = new Guid("00021401-0000-0000-C000-000000000046");
        var iidLink = new Guid("000214F9-0000-0000-C000-000000000046");
        var iidFile = new Guid("0000010b-0000-0000-C000-000000000046");
        nint link;
        Marshal.ThrowExceptionForHR(CoCreateInstance(&clsid, 0, 1, &iidLink, &link));
        try
        {
            static void* Slot(nint o, int i) => (*(void***)o)[i];
            fixed (char* t = target)
            fixed (char* dir = Path.GetDirectoryName(target))
            fixed (char* desc = "log")
            {
                ((delegate* unmanaged[Stdcall]<nint, char*, int>)Slot(link, 20))(link, t);          // SetPath
                ((delegate* unmanaged[Stdcall]<nint, char*, int>)Slot(link, 9))(link, dir);         // SetWorkingDirectory
                ((delegate* unmanaged[Stdcall]<nint, char*, int>)Slot(link, 7))(link, desc);        // SetDescription
                ((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Slot(link, 17))(link, t, 0);  // SetIconLocation
            }

            nint file;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(link, 0))(link, &iidFile, &file));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
                fixed (char* p = lnkPath)
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)Slot(file, 6))(file, p, 1)); // Save
            }
            finally { Gfx.Release(file); }
        }
        finally { Gfx.Release(link); }
    }

    [DllImport("ole32", ExactSpelling = true)]
    static extern int CoCreateInstance(Guid* clsid, nint outer, uint ctx, Guid* iid, nint* obj);
}

/// <summary>Minimal HKEY_CURRENT_USER helpers.</summary>
internal static unsafe class Reg
{
    static readonly nint HkeyCurrentUser = unchecked((nint)0x80000001);

    [DllImport("advapi32", ExactSpelling = true)] static extern int RegCreateKeyExW(nint key, char* sub, uint res, char* cls, uint opts, uint sam, void* sa, nint* result, uint* disp);
    [DllImport("advapi32", ExactSpelling = true)] static extern int RegSetValueExW(nint key, char* name, uint res, uint type, void* data, uint size);
    [DllImport("advapi32", ExactSpelling = true)] static extern int RegDeleteValueW(nint key, char* name);
    [DllImport("advapi32", ExactSpelling = true)] static extern int RegDeleteTreeW(nint key, char* sub);
    [DllImport("advapi32", ExactSpelling = true)] static extern int RegCloseKey(nint key);
    [DllImport("advapi32", ExactSpelling = true)] static extern int RegGetValueW(nint key, char* sub, char* name, uint flags, uint* type, void* data, uint* size);

    public static void SetString(string subKey, string name, string value)
    {
        var key = Open(subKey);
        try
        {
            fixed (char* n = name)
            fixed (char* v = value)
                Check(RegSetValueExW(key, n, 0, 1, v, (uint)((value.Length + 1) * 2))); // REG_SZ
        }
        finally { RegCloseKey(key); }
    }

    public static void SetDword(string subKey, string name, int value)
    {
        var key = Open(subKey);
        try
        {
            fixed (char* n = name)
                Check(RegSetValueExW(key, n, 0, 4, &value, 4)); // REG_DWORD
        }
        finally { RegCloseKey(key); }
    }

    public static int GetDword(string subKey, string name)
    {
        uint data = 0, size = 4;
        fixed (char* s = subKey)
        fixed (char* n = name)
            return RegGetValueW(HkeyCurrentUser, s, n, 0x10, null, &data, &size) == 0 ? (int)data : -1;
    }

    public static string? GetString(string subKey, string name)
    {
        var buffer = stackalloc char[1024];
        uint size = 2048;
        fixed (char* s = subKey)
        fixed (char* n = name)
            return RegGetValueW(HkeyCurrentUser, s, n, 0x2, null, buffer, &size) == 0 ? new string(buffer) : null; // REG_SZ
    }

    public static void DeleteValue(string subKey, string name)
    {
        nint key;
        fixed (char* s = subKey)
            if (RegCreateKeyExW(HkeyCurrentUser, s, 0, null, 0, 0x20006, null, &key, null) != 0) return; // KEY_WRITE
        try { fixed (char* n = name) RegDeleteValueW(key, n); }
        finally { RegCloseKey(key); }
    }

    public static void DeleteTree(string subKey)
    {
        fixed (char* s = subKey) RegDeleteTreeW(HkeyCurrentUser, s);
    }

    static nint Open(string subKey)
    {
        nint key;
        fixed (char* s = subKey) Check(RegCreateKeyExW(HkeyCurrentUser, s, 0, null, 0, 0x20006, null, &key, null));
        return key;
    }

    static void Check(int rc) { if (rc != 0) throw new InvalidOperationException($"Registry write failed ({rc})."); }
}
