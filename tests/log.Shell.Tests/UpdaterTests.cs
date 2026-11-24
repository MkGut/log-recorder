using System.Security.Cryptography;
using System.Text;
using Log.Shell;
using Xunit;

namespace Log.Shell.Tests;

public sealed class UpdaterTests : IDisposable
{
    static readonly string Hash = new('a', 64);
    readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly string dir = Path.Combine(Path.GetTempPath(), "log-updater-" + Guid.NewGuid().ToString("N"));

    string PublicKey => Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());

    static byte[] Manifest(string version, bool bothFiles = true)
    {
        var files = "\"log.exe\":\"" + Hash + "\"" + (bothFiles ? ",\"log.MediaFoundationBridge.dll\":\"" + Hash + "\"" : "");
        return Encoding.UTF8.GetBytes("{\"version\":\"" + version + "\",\"files\":{" + files + "}}");
    }

    byte[] Sign(byte[] data) => key.SignData(data, HashAlgorithmName.SHA256);

    [Fact]
    public void AcceptsANewerSignedManifest()
    {
        var m = Manifest("0.2.0");
        var parsed = Updater.Verify(m, Sign(m), new Version(0, 1, 0), PublicKey);
        Assert.NotNull(parsed);
        Assert.Equal("0.2.0", parsed.Version);
    }

    [Fact]
    public void RejectsTamperingAndOtherKeys()
    {
        var m = Manifest("0.2.0");
        var sig = Sign(m);
        var tampered = Manifest("9.9.9");
        Assert.Null(Updater.Verify(tampered, sig, new Version(0, 1, 0), PublicKey));

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Null(Updater.Verify(m, other.SignData(m, HashAlgorithmName.SHA256), new Version(0, 1, 0), PublicKey));
    }

    [Fact]
    public void RejectsReplayOfTheSameOrOlderVersion()
    {
        var m = Manifest("0.1.0");
        Assert.Null(Updater.Verify(m, Sign(m), new Version(0, 1, 0), PublicKey));
        var older = Manifest("0.0.9");
        Assert.Null(Updater.Verify(older, Sign(older), new Version(0, 1, 0), PublicKey));
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("../1.2.3")]
    public void RejectsVersionsThatAreNotPlainTriples(string version)
    {
        var m = Manifest(version);
        Assert.Null(Updater.Verify(m, Sign(m), new Version(0, 1, 0), PublicKey));
    }

    [Fact]
    public void RejectsAManifestMissingAFileHash()
    {
        var m = Manifest("0.2.0", bothFiles: false);
        Assert.Null(Updater.Verify(m, Sign(m), new Version(0, 1, 0), PublicKey));
    }

    [Fact]
    public void ShippedKeyIsAValidP256PublicKey()
    {
        using var k = ECDsa.Create();
        k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(Updater.PublicKey), out _);
        Assert.Equal(256, k.KeySize);
    }

    [Fact]
    public void ApplySwapsStagedFilesAndKeepsOldOnesForCleanup()
    {
        Directory.CreateDirectory(dir);
        foreach (var f in Updater.Files)
        {
            File.WriteAllText(Path.Combine(dir, f), "old");
            File.WriteAllText(Path.Combine(dir, f + ".new"), "new");
        }

        Assert.True(Updater.IsStaged(dir));
        Assert.True(Updater.Apply(dir));
        Assert.False(Updater.IsStaged(dir));
        Assert.All(Updater.Files, f => Assert.Equal("new", File.ReadAllText(Path.Combine(dir, f))));

        Updater.CleanUp(dir);
        Assert.All(Updater.Files, f => Assert.False(File.Exists(Path.Combine(dir, f + ".old"))));
    }

    [Fact]
    public void ApplyPutsTheOldFilesBackWhenAStagedFileIsMissing()
    {
        Directory.CreateDirectory(dir);
        foreach (var f in Updater.Files) File.WriteAllText(Path.Combine(dir, f), "old");
        File.WriteAllText(Path.Combine(dir, Updater.Files[0] + ".new"), "new"); // second one missing

        Assert.False(Updater.Apply(dir));
        Assert.All(Updater.Files, f => Assert.Equal("old", File.ReadAllText(Path.Combine(dir, f))));
    }

    [Fact]
    public void HashMatchesComparesSha256()
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "x");
        File.WriteAllText(path, "abc");
        Assert.True(Updater.HashMatches(path, "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"));
        Assert.False(Updater.HashMatches(path, Hash));
    }

    public void Dispose()
    {
        key.Dispose();
        try { Directory.Delete(dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }
}
