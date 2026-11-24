using Log.Shell;
using Xunit;

namespace Log.Shell.Tests;

public sealed class InstallerTests
{
    const string Dir = @"C:\Users\Public\AppData\Local\Programs\log";

    [Fact]
    public void OffersOnlyOnAPlainDoubleClick()
    {
        Assert.True(Installer.ShouldOffer([], @"C:\Downloads\log.exe", Dir, dataRootSet: false, declined: false));
        Assert.False(Installer.ShouldOffer(["--show"], @"C:\Downloads\log.exe", Dir, false, false)); // scripts and tests
        Assert.False(Installer.ShouldOffer([], @"C:\Downloads\log.exe", Dir, dataRootSet: true, declined: false));
        Assert.False(Installer.ShouldOffer([], @"C:\Downloads\log.exe", Dir, false, declined: true));
    }

    [Fact]
    public void NeverOffersFromTheInstalledCopy()
    {
        Assert.False(Installer.ShouldOffer([], Dir + @"\log.exe", Dir, false, false));
        Assert.False(Installer.ShouldOffer([], (Dir + @"\log.exe").ToUpperInvariant(), Dir, false, false));
        Assert.True(Installer.ShouldOffer([], Dir + @"-other\log.exe", Dir, false, false)); // a sibling folder is not inside
    }

    [Fact]
    public void UninstallDeletesOnlyItsOwnFilesAndNeverForcesTheFolder()
    {
        var cmd = Installer.UninstallCommand(Dir);
        Assert.Contains($"\"{Dir}\\log.exe\"", cmd, StringComparison.Ordinal);
        Assert.Contains($"\"{Dir}\\log.MediaFoundationBridge.dll\"", cmd, StringComparison.Ordinal);
        Assert.DoesNotContain("/s", cmd, StringComparison.OrdinalIgnoreCase); // portable data in the folder survives
        Assert.EndsWith($"rmdir \"{Dir}\"", cmd, StringComparison.Ordinal);
    }

    [Fact]
    public void LogLinesCarryNoExceptionMessage()
    {
        var line = AppLog.Line(new DateTime(2026, 1, 2, 3, 4, 5), "save", new IOException(@"D:\private\secret.mp4"));
        Assert.Equal("2026-01-02T03:04:05 save IOException 0x80131620", line);
    }

    [Fact]
    public void UninstallEntryRunsTheInstalledExeWithUninstall()
    {
        var values = Installer.Describe(Dir, "0.1.0", runAtSignIn: false);
        var uninstall = Assert.Single(values, v => v.Name == "UninstallString");
        Assert.Equal($"\"{Dir}\\log.exe\" --uninstall", uninstall.Data);
        Assert.Contains(values, v => v is { Name: "DisplayName", Data: "log" });
        Assert.Contains(values, v => v is { Name: "InstallLocation" } && (string)v.Data == Dir);
        Assert.All(values, v => Assert.StartsWith(@"Software\", v.Key, StringComparison.Ordinal)); // per user only
    }

    [Fact]
    public void SignInLaunchIsWrittenOnlyWhenChosenAndMarksColdSignIn()
    {
        Assert.DoesNotContain(Installer.Describe(Dir, "0.1.0", runAtSignIn: false), v => v.Key == Installer.RunKey);
        var run = Assert.Single(Installer.Describe(Dir, "0.1.0", runAtSignIn: true), v => v.Key == Installer.RunKey);
        Assert.Equal($"\"{Dir}\\log.exe\" --at-sign-in", run.Data);
    }

    [Fact]
    public void OnlyRemovesASignInEntryThatPointsIntoItsOwnFolder()
    {
        Assert.True(Installer.IsOurSignIn($"\"{Dir}\\log.exe\" --at-sign-in", Dir));
        Assert.False(Installer.IsOurSignIn(@"""D:\Apps\other\other.exe"" --at-sign-in", Dir)); // another program's entry
        Assert.False(Installer.IsOurSignIn(null, Dir));
    }
}
