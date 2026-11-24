using System.Diagnostics;
using Log.Engine;
using Log.Shell;
using Xunit;

namespace Log.Shell.Tests;

/// <summary>The model rules for offering a recording found on disk after a restart.</summary>
public sealed class UnfinishedModelTests
{
    [Fact]
    public void AFoundPreviewOpensThePreviewAndCanBeDiscardedOnlyAfterAsking()
    {
        var m = new ShellModel();
        Assert.True(m.UnfinishedFound("p.mp4", TimeSpan.FromMinutes(7), "Kept from earlier."));
        Assert.Equal(ScreenId.Preview, m.Screen);
        Assert.True(m.Recovered);
        Assert.Equal("Kept from earlier.", m.Notice);

        Assert.False(m.DiscardStarted()); // nothing is deleted without the question
        Assert.True(m.DiscardAsked());
        Assert.Equal(ScreenId.Discard, m.Screen);
        Assert.True(m.KeepIt());
        Assert.Equal(ScreenId.Preview, m.Screen);

        Assert.True(m.DiscardAsked());
        Assert.True(m.DiscardStarted());
        Assert.True(m.ToView().Busy); // duplicate clicks are blocked
        Assert.True(m.Discarded());
        Assert.Equal(ScreenId.Invitation, m.Screen);
        Assert.False(m.Recovered);
        Assert.Null(m.PreviewPath);
    }

    [Fact]
    public void ARecordingWithoutAPreviewGoesThroughFinishingAndKeepsItsNotice()
    {
        var m = new ShellModel();
        Assert.True(m.UnfinishedFound(null, TimeSpan.FromMinutes(3), "log closed before this finished."));
        Assert.Equal(ScreenId.Finishing, m.Screen);
        Assert.True(m.PreviewReady("p.mp4", TimeSpan.FromMinutes(3)));
        Assert.Equal(ScreenId.Preview, m.Screen);
        Assert.Equal("log closed before this finished.", m.Notice);
    }

    [Fact]
    public void AFoundRecordingNeverReplacesWhatTheUserIsDoing()
    {
        var m = new ShellModel();
        m.LetsGo();
        m.BeginConsent();
        Assert.False(m.CanOfferUnfinished); // the picker is open
        Assert.False(m.UnfinishedFound("p.mp4", TimeSpan.Zero, ""));
        Assert.Equal(ScreenId.Source, m.Screen);

        m.EngineStarted("Entire display");
        Assert.False(m.UnfinishedFound("p.mp4", TimeSpan.Zero, "")); // recording
        Assert.Equal(ScreenId.Recording, m.Screen);
    }

    [Fact]
    public void AFailedDeleteReturnsToThePreviewWithAReason()
    {
        var m = new ShellModel();
        m.UnfinishedFound("p.mp4", TimeSpan.FromMinutes(1), "");
        m.DiscardAsked();
        m.DiscardStarted();
        Assert.True(m.DiscardFailed("Couldn't delete it."));
        Assert.Equal(ScreenId.Preview, m.Screen);
        Assert.Equal("Couldn't delete it.", m.Notice);
        Assert.False(m.ToView().Busy);
    }

    [Fact]
    public void ANormalPreviewHasNoDiscard()
    {
        var m = new ShellModel();
        m.LetsGo();
        m.BeginConsent();
        m.EngineStarted("Entire display");
        m.TimerClicked();
        m.FinishConfirmed();
        m.CaptureStopped();
        m.PreviewReady("p.mp4", TimeSpan.FromMinutes(5));
        Assert.False(m.DiscardAsked());
    }

    [Fact]
    public void OnlyAFoundPreviewOffersDiscard()
    {
        static Scene Build(bool recovered) => Layout.Build(ScreenId.Preview,
            new ViewData { Recovered = recovered, Elapsed = TimeSpan.FromMinutes(5) }, 320, (_, t, _) => (t.Length * 8, 20));
        Assert.Contains(Build(true).Items, i => i.Id == Cmd.Discard);
        Assert.DoesNotContain(Build(false).Items, i => i.Id == Cmd.Discard);
    }
}

/// <summary>
/// Recordings left on disk by an earlier run, found by a fresh engine on the same data root: a preview that was
/// kept for later, and a capture that stopped dead mid-recording. Real engine, real store, real render and save;
/// only the frames are generated, so no picker is involved.
/// </summary>
[Collection("ui")]
public sealed class UnfinishedSavesTests : IDisposable
{
    readonly List<string> roots = [];
    readonly string? previousRoot = Environment.GetEnvironmentVariable("LOG_DATA_ROOT");

    public UnfinishedSavesTests() => Ui.Init(0);

    string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-shell-unfinished-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        roots.Add(root);
        // The export folder follows the data root, which keeps saved files out of Videos\log.
        Environment.SetEnvironmentVariable("LOG_DATA_ROOT", root);
        return root;
    }

    static ShellController Open(string root) => new(
        b => LogEngine.CreateDefault(new SnapshotEncoderFactory(), b, new RendererAdapter(root), root),
        new RealEngineFlowTests.GeneratedConsent(), root,
        changed: () => { }, showWindow: () => { }, hideWindow: () => { },
        updateTray: (_, _) => true, verifyWindowVisible: () => true, pickFolder: () => null);

    static async Task Until(Func<bool> condition, int seconds = 90)
    {
        var end = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() < end)
        {
            Ui.Drain();
            if (condition()) return;
            await Task.Delay(20);
        }

        throw new TimeoutException("condition not reached");
    }

    static async Task RecordForAWhile(ShellController c)
    {
        c.LetsGo();
        c.ChooseSource();
        await Until(() => c.Model.Screen == ScreenId.Recording);
        await Task.Delay(TimeSpan.FromSeconds(5)); // a few one-second samples land
        Ui.Drain();
    }

    static async Task AssertSavedMp4(ShellController c, string root)
    {
        c.Save();
        await Until(() => c.Model.Screen is ScreenId.Saved or ScreenId.SaveError);
        Assert.Equal(ScreenId.Saved, c.Model.Screen);
        var saved = c.Model.SavedPath!;
        Assert.StartsWith(root, saved, StringComparison.OrdinalIgnoreCase); // the normal output folder
        Assert.True(new FileInfo(saved).Length > 1000);
        var head = new byte[12];
        await using (var f = File.OpenRead(saved)) _ = await f.ReadAsync(head);
        Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(head, 4, 4));
    }

    // Copies a folder while another thread may still be writing to its files.
    static void CopyLive(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            // A frame still being written (or just renamed into place) is lost in a real crash too; skip it.
            FileStream source;
            try { source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (IOException) when (file.Contains($"{Path.DirectorySeparatorChar}.tmp{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) { continue; }
            catch (FileNotFoundException) { continue; }
            using (source)
            using (var target = File.Create(Path.Combine(to, Path.GetRelativePath(from, file))))
                source.CopyTo(target);
        }
    }

    [Fact]
    public async Task APreviewKeptForLaterIsOfferedAgainAfterARestartAndCanBeSaved()
    {
        var root = NewRoot();
        await using (var first = Open(root))
        {
            await RecordForAWhile(first);
            first.TimerClicked();
            first.FinishConfirmed();
            await Until(() => first.Model.Screen == ScreenId.Preview);
            first.KeepForLater();
        } // log.exe exits here

        await using (var second = Open(root))
        {
            second.CheckUnfinished(show: true);
            await Until(() => second.Model.Screen == ScreenId.Preview);
            Assert.True(second.Model.Recovered);
            Assert.True(File.Exists(second.Model.PreviewPath));
            Assert.True(second.Model.Elapsed > TimeSpan.FromSeconds(2), $"elapsed {second.Model.Elapsed}");
            await AssertSavedMp4(second, root);
        }

        // Saved means it is no longer waiting: a third start finds nothing.
        await using var third = Open(root);
        third.CheckUnfinished(show: true);
        await Task.Delay(1500);
        Ui.Drain();
        Assert.Equal(ScreenId.Invitation, third.Model.Screen);
    }

    [Fact]
    public async Task ACaptureKilledMidRecordingIsOfferedAfterARestartAndCanBeSaved()
    {
        var live = NewRoot();
        var after = Path.Combine(Path.GetTempPath(), "log-shell-unfinished-" + Guid.NewGuid().ToString("N"));
        roots.Add(after);
        await using (var victim = Open(live))
        {
            await RecordForAWhile(victim);
            // What the disk holds at the instant the process dies: a manifest still saying "recording" plus the journal.
            CopyLive(Path.Combine(live, "sessions"), Path.Combine(after, "sessions"));
            var manifest = Directory.EnumerateFiles(Path.Combine(after, "sessions"), "manifest.json", SearchOption.AllDirectories).Single();
            Assert.Contains("\"status\": \"Recording\"", File.ReadAllText(manifest));
        }

        Environment.SetEnvironmentVariable("LOG_DATA_ROOT", after);
        await using var restarted = Open(after);
        restarted.CheckUnfinished(show: true);
        await Until(() => restarted.Model.Screen == ScreenId.Preview);
        Assert.True(restarted.Model.Recovered);
        Assert.Contains("captured", restarted.Model.Notice);
        Assert.True(File.Exists(restarted.Model.PreviewPath));
        Assert.True(restarted.Model.Elapsed > TimeSpan.FromSeconds(2), $"elapsed {restarted.Model.Elapsed}");

        await AssertSavedMp4(restarted, after);
    }

    [Fact]
    public async Task DiscardingARecoveredRecordingDeletesItForGood()
    {
        var root = NewRoot();
        string keptPreview;
        await using (var first = Open(root))
        {
            await RecordForAWhile(first);
            first.TimerClicked();
            first.FinishConfirmed();
            await Until(() => first.Model.Screen == ScreenId.Preview);
            keptPreview = first.Model.PreviewPath!;
            first.KeepForLater();
        }

        await using (var second = Open(root))
        {
            second.CheckUnfinished(show: true);
            await Until(() => second.Model.Screen == ScreenId.Preview);

            second.Discard();
            Assert.Equal(ScreenId.Discard, second.Model.Screen); // asks first; nothing is deleted yet
            Assert.True(File.Exists(keptPreview));
            second.DiscardConfirmed();
            await Until(() => second.Model.Screen == ScreenId.Invitation);
        }

        Assert.False(File.Exists(keptPreview));
        await using var third = Open(root);
        third.CheckUnfinished(show: true);
        await Task.Delay(1500);
        Ui.Drain();
        Assert.Equal(ScreenId.Invitation, third.Model.Screen);
    }

    [Fact]
    public async Task AnEmptyDataRootOffersNothing()
    {
        await using var c = Open(NewRoot());
        c.CheckUnfinished(show: true);
        await Task.Delay(1500);
        Ui.Drain();
        Assert.Equal(ScreenId.Invitation, c.Model.Screen);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("LOG_DATA_ROOT", previousRoot);
        foreach (var root in roots)
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { /* temp folder */ }
            catch (UnauthorizedAccessException) { /* temp folder */ }
        }
    }
}
