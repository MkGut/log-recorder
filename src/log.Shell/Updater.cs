using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Log.Shell;

/// <summary>update.json from a release: the version and the SHA-256 of each shipped file.</summary>
internal sealed record UpdateManifest(string Version, Dictionary<string, string> Files);

[JsonSerializable(typeof(UpdateManifest))]
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
internal sealed partial class UpdateJson : JsonSerializerContext;

/// <summary>
/// Automatic updates for the installed copy. Every release carries update.json and update.json.sig
/// (ECDSA P-256 over the exact manifest bytes, made in CI with a key only the release workflow holds).
/// A manifest is used only if the signature verifies against the key below and its version is newer;
/// each file must then match its hash. Files are swapped in while the app is idle and it restarts.
/// </summary>
internal static unsafe class Updater
{
    public const string Repository = "MkGut/log-recorder";
    public const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEIAsy38nu26kTAUjYrXA6lLlAwJT7G82h98wk0eH4U1ENZQ/DOfAv7TLS+ceh4UEDM7VD+W1DAeeqWL+0CO5arQ==";
    public static readonly string[] Files = ["log.exe", "log.MediaFoundationBridge.dll"];
    const string SettingName = "AutomaticUpdates";

    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Only the per-user installed copy updates itself; portable, winget and dev copies do not.</summary>
    public static bool IsInstalledCopy =>
        Installer.IsInside(Environment.ProcessPath ?? "", Installer.DefaultInstallDir)
        && !File.Exists(Path.Combine(Installer.DefaultInstallDir, "portable.txt"));

    /// <summary>On unless the user turned it off (tray menu) or LOG_NO_UPDATE is set.</summary>
    public static bool Enabled
    {
        get => Reg.GetDword(Installer.DeclineKey, SettingName) != 0
            && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LOG_NO_UPDATE"));
        set => Reg.SetDword(Installer.DeclineKey, SettingName, value ? 1 : 0);
    }

    /// <summary>The manifest if it is signed by <paramref name="publicKey"/>, well formed and newer than <paramref name="current"/>.</summary>
    public static UpdateManifest? Verify(byte[] manifest, byte[] signature, Version current, string publicKey = PublicKey)
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
        if (!key.VerifyData(manifest, signature, HashAlgorithmName.SHA256)) return null;

        UpdateManifest? m;
        try { m = JsonSerializer.Deserialize(manifest, UpdateJson.Default.UpdateManifest); }
        catch (JsonException) { return null; }
        if (m?.Files is null || !IsPlainVersion(m.Version) || Version.Parse(m.Version) <= current) return null;
        foreach (var f in Files)
            if (!m.Files.TryGetValue(f, out var hash) || hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) return null;
        return m;
    }

    static bool IsPlainVersion(string? v) =>
        v is { Length: > 0 and < 32 } && v.Split('.') is { Length: 3 } parts && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit));

    /// <summary>
    /// Checks the latest release and, when it is newer and verified, leaves <c>name.new</c> files beside
    /// the exe. True when an update is staged and ready to apply.
    /// </summary>
    public static bool Stage(string installDir)
    {
        var temp = Path.Combine(installDir, "update");
        Directory.CreateDirectory(temp);
        try
        {
            var nonce = DateTime.UtcNow.Ticks; // keeps the Windows download cache out of the way
            var latest = $"https://github.com/{Repository}/releases/latest/download/";
            var manifestPath = Download(latest + $"update.json?t={nonce}", Path.Combine(temp, "update.json"));
            var sigPath = Download(latest + $"update.json.sig?t={nonce}", Path.Combine(temp, "update.json.sig"));
            if (manifestPath is null || sigPath is null) return false;

            byte[] signature;
            try { signature = Convert.FromBase64String(File.ReadAllText(sigPath).Trim()); }
            catch (FormatException) { return false; }
            var m = Verify(File.ReadAllBytes(manifestPath), signature, Current);
            if (m is null) return false;

            foreach (var f in Files)
            {
                var got = Download($"https://github.com/{Repository}/releases/download/v{m.Version}/{f}", Path.Combine(temp, f));
                if (got is null || !HashMatches(got, m.Files[f])) return false;
            }

            foreach (var f in Files) File.Move(Path.Combine(temp, f), Path.Combine(installDir, f + ".new"), overwrite: true);
            return true;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public static bool HashMatches(string path, string expectedHex)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s)).Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsStaged(string installDir) => Files.All(f => File.Exists(Path.Combine(installDir, f + ".new")));

    /// <summary>
    /// Swaps the staged files in. A running exe or loaded DLL can be renamed (not deleted), so each current
    /// file becomes <c>.old</c> and is removed on the next start. Any failure puts the old files back.
    /// </summary>
    public static bool Apply(string installDir)
    {
        var done = new List<string>();
        try
        {
            foreach (var f in Files)
            {
                var target = Path.Combine(installDir, f);
                if (File.Exists(target)) File.Move(target, target + ".old", overwrite: true);
                File.Move(target + ".new", target);
                done.Add(target);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("update apply", e);
            foreach (var target in done)
            {
                try { File.Move(target, target + ".new", overwrite: true); } catch (IOException) { }
            }
            foreach (var f in Files)
            {
                var target = Path.Combine(installDir, f);
                if (!File.Exists(target) && File.Exists(target + ".old"))
                    try { File.Move(target + ".old", target); } catch (IOException) { }
            }
            return false;
        }
    }

    /// <summary>Removes files left by an earlier update (the old exe could not be deleted while it ran).</summary>
    public static void CleanUp(string installDir)
    {
        foreach (var f in Files)
        {
            try { File.Delete(Path.Combine(installDir, f + ".old")); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Starts the new copy quietly in the tray once this process has let go of the single-instance lock.</summary>
    public static void Relaunch(string installDir) =>
        Process.Start(new ProcessStartInfo(Path.Combine(installDir, "log.exe"), "--after-update") { UseShellExecute = false });

    static string? Download(string url, string path)
    {
        try { File.Delete(path); } catch (IOException) { }
        fixed (char* u = url)
        fixed (char* p = path)
            return URLDownloadToFileW(0, u, p, 0, 0) == 0 && File.Exists(path) ? path : null;
    }

    [DllImport("urlmon", ExactSpelling = true)]
    static extern int URLDownloadToFileW(nint caller, char* url, char* file, uint reserved, nint callback);
}
