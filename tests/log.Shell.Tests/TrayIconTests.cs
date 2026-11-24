using Log.Core.Shell;
using Log.Shell;
using Xunit;

namespace Log.Shell.Tests;

public sealed class TrayIconTests
{
    // Header plus one 16-byte entry per width (0 means 256, as in a real .ico).
    static byte[] Ico(params int[] widths)
    {
        var ico = new byte[6 + widths.Length * 16];
        ico[2] = 1;
        ico[4] = (byte)widths.Length;
        for (var i = 0; i < widths.Length; i++) ico[6 + i * 16] = (byte)(widths[i] == 256 ? 0 : widths[i]);
        return ico;
    }

    [Theory]
    [InlineData(16, 0)]  // exact
    [InlineData(20, 1)]  // 125 %
    [InlineData(22, 2)]  // nothing exact: next size up
    [InlineData(100, 4)] // 256 is stored as 0
    [InlineData(300, 4)] // nothing big enough: the widest
    public void PicksSmallestImageThatCoversTheTraySize(int want, int expected) =>
        Assert.Equal(expected, TrayIcon.PickIconEntry(Ico(16, 20, 24, 48, 256), want));

    [Fact]
    public void EntryOrderDoesNotMatter() =>
        Assert.Equal(1, TrayIcon.PickIconEntry(Ico(256, 24, 16, 48), 20));

    [Fact]
    public void BrokenFilesGiveNoEntry()
    {
        Assert.Equal(-1, TrayIcon.PickIconEntry([], 16));
        Assert.Equal(-1, TrayIcon.PickIconEntry(Ico(), 16));
        Assert.Equal(-1, TrayIcon.PickIconEntry(Ico(16, 32).AsSpan(0, 30), 16)); // cut off mid-entry
    }

    // Every status icon the tray asks for is really in log.exe, with images down to 16 px.
    [Theory]
    [InlineData(TrayStatus.Recording)]
    [InlineData(TrayStatus.Paused)]
    [InlineData(TrayStatus.Finishing)]
    [InlineData(TrayStatus.Error)]
    public void StatusIconsAreEmbedded(TrayStatus status)
    {
        using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream(TrayIcon.IconFor(status));
        Assert.NotNull(stream);
        var ico = new byte[stream.Length];
        stream.ReadExactly(ico);

        var entry = TrayIcon.PickIconEntry(ico, 16);
        Assert.True(entry >= 0);
        Assert.Equal(16, ico[6 + entry * 16]);
        var size = BitConverter.ToUInt32(ico, 6 + entry * 16 + 8);
        var offset = BitConverter.ToUInt32(ico, 6 + entry * 16 + 12);
        Assert.True(offset + size <= ico.Length);
    }

    [Fact]
    public void IdleUsesThePlainAppIcon() => Assert.Equal("", TrayIcon.IconFor(TrayStatus.Idle));
}
