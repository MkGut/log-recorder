using Log.Shell;
using Xunit;

namespace Log.Shell.Tests;

public sealed class LayoutTests
{
    // Fixed-size text so layout runs without DirectWrite.
    static (float W, float H) Fake(TextRole role, string text, float width) => (Math.Min(width, text.Length * 8), 20);

    static readonly ViewData Data = new()
    {
        Source = "Entire display",
        Elapsed = new TimeSpan(0, 24, 18),
        SavedPath = "Videos / log / a.mp4",
        ErrorDetail = "The folder is not available.",
    };

    [Fact]
    public void ClockIsAlwaysHoursMinutesSeconds()
    {
        Assert.Equal("00:24:18", Format.Clock(new TimeSpan(0, 24, 18)));
        Assert.Equal("01:02:03", Format.Clock(new TimeSpan(1, 2, 3)));
        Assert.Equal("00:00:00", Format.Clock(TimeSpan.FromSeconds(-5)));
    }

    [Theory]
    [InlineData(5, "less than a minute")]
    [InlineData(60, "1 minute")]
    [InlineData(24 * 60 + 18, "24 minutes")]
    [InlineData(3600 + 5 * 60, "1 hour 5 minutes")]
    public void SpokenDurationHasMinuteGranularity(int seconds, string expected) =>
        Assert.Equal(expected, Format.Spoken(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void PreviewLineUsesTheRealDuration() =>
        Assert.Equal("24 minutes of focus. Ready to take with you.", Format.Focus(new TimeSpan(0, 24, 18)));

    [Fact]
    public void SavedPathShowsVideosCrumbs()
    {
        Assert.Equal("Videos / log / a.mp4", Format.Crumbs(@"C:\Users\Public\Videos\log\a.mp4", @"C:\Users\Public\Videos"));
        Assert.Equal("D: / clips / a.mp4", Format.Crumbs(@"D:\clips\a.mp4", @"C:\Users\Public\Videos"));
    }

    [Fact]
    public void TimerButtonIsNamedWithSpokenElapsed()
    {
        var scene = Layout.Build(ScreenId.Recording, Data, 510, Fake);
        var timer = Assert.Single(scene.Items, i => i.Id == Cmd.Timer);
        Assert.Equal("Finish recording, elapsed 24 minutes", timer.Name);
        Assert.Equal("00:24:18", timer.Text);
    }

    [Theory]
    [InlineData("Invitation", "Wanna lock in?", "A little time for what matters.")]
    [InlineData("Source", "Your time starts here.", "Windows will ask which screen or window to share. Recording starts after you confirm.")]
    [InlineData("Goodbye", "Sure you wanna say goodbye?", "Keep this session as a timelapse.")]
    [InlineData("Finishing", "Finishing your session…", null)]
    [InlineData("Preview", "A little time, kept.", null)]
    [InlineData("Saved", "All yours.", "Your timelapse is saved.")]
    [InlineData("ConsentCancelled", "No rush.", "Nothing was recorded. Choose a source whenever you're ready.")]
    [InlineData("SaveError", "Couldn't save there.", "Your recording is still on this device.")]
    public void ScreensUseTheExactCopy(string screen, string heading, string? body)
    {
        var id = Enum.Parse<ScreenId>(screen);
        var scene = Layout.Build(id, Data, 510, Fake);
        Assert.Contains(scene.Items, i => i.Text == heading);
        if (body is not null) Assert.Contains(scene.Items, i => i.Text == body);
    }

    [Fact]
    public void SourceLostTitleNamesTheReason()
    {
        var scene = Layout.Build(ScreenId.SourceLost, Data with { PauseText = "source unavailable" }, 510, Fake);
        Assert.Contains(scene.Items, i => i.Text == "Paused · source unavailable");
        Assert.Contains(scene.Items, i => i.Text == "Your session is safe.");
    }

    [Theory]
    [InlineData(510)]
    [InlineData(395)]
    [InlineData(320)]
    public void EveryItemStaysInsideTheWindow(float width)
    {
        foreach (var id in Enum.GetValues<ScreenId>())
        {
            var scene = Layout.Build(id, Data, width, Fake);
            Assert.True(scene.Height > Skin.InsetTop);
            foreach (var item in scene.Items)
            {
                Assert.True(item.Rect.L >= 0 && item.Rect.R <= width + .01f, $"{id}/{item.Text} horizontal");
                Assert.True(item.Rect.T >= 0 && item.Rect.B <= scene.Height, $"{id}/{item.Text} vertical");
            }
        }
    }

    [Fact]
    public void NarrowRecoveredPreviewFitsAllThreeActionLabels()
    {
        var scene = Layout.Build(ScreenId.Preview, Data with { Recovered = true }, 320, Fake);
        var actions = scene.Items.Where(i => i.Id is Cmd.Discard or Cmd.KeepLater or Cmd.SaveTimelapse).ToArray();
        Assert.Equal(3, actions.Length);
        Assert.All(actions, action => Assert.True(action.Rect.W >= Fake(TextRole.Button, action.Text, 1000).W + 16));
        Assert.True(actions[0].Rect.B <= actions[1].Rect.T && actions[1].Rect.B <= actions[2].Rect.T);
    }

    [Fact]
    public void ControlsDoNotOverlap()
    {
        foreach (var id in Enum.GetValues<ScreenId>())
        {
            var items = Layout.Build(id, Data, 320, Fake).Items.Where(i => i.Kind is ItemKind.Button or ItemKind.Text).ToList();
            for (var i = 0; i < items.Count; i++)
                for (var j = i + 1; j < items.Count; j++)
                {
                    var a = items[i].Rect; var b = items[j].Rect;
                    var apart = a.R <= b.L || b.R <= a.L || a.B <= b.T || b.B <= a.T;
                    Assert.True(apart, $"{id}: '{items[i].Text}' overlaps '{items[j].Text}'");
                }
        }
    }

    [Fact]
    public void OpenFileIsOnlyOfferedWhenSupported()
    {
        Assert.Contains(Layout.Build(ScreenId.Saved, Data, 510, Fake).Items, i => i.Id == Cmd.OpenFile);
        Assert.DoesNotContain(Layout.Build(ScreenId.Saved, Data with { CanOpenFile = false }, 510, Fake).Items, i => i.Id == Cmd.OpenFile);
    }

    [Fact]
    public void RoundedRegionStaysInsideTheWindow()
    {
        var pts = SurfaceWindow.Outline(510, 440, 1, Skin.Casing(510));
        Assert.All(pts, p => Assert.True(p.x >= 0 && p.x < 510 && p.y >= 0 && p.y < 440));
    }
}
