using Log.Core.Capture;
using Log.Core.Render;
using Log.Core.Store;
using Log.Core.Timeline;
using Xunit;

namespace Log.Integration.Tests;

public sealed class RandomSingleCaptureTests
{
    private const long Second = TimelineConstants.TicksPerSecond;

    [Fact]
    public async Task RandomCaptureKeepsOneSourceCadenceAndPersistsSparseTargets()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        using var stopController = new CaptureStopController();
        var source = new RandomFrameSource();
        var stopAtTicks = 190 * Second;
        try
        {
            var result = await CaptureCoordinator.RunAsync(
                source,
                new StopAtClock(stopController, stopAtTicks),
                new Log.Testing.RecordingEncoder(),
                root,
                "random-session",
                new CaptureRunOptions(
                    durationTicks: 0,
                    intervalTicks: Second,
                    maxFrameAgeTicks: 10 * Second,
                    sourceMode: "screen",
                    samplingMode: CaptureSamplingMode.Random,
                    randomSeed: 42),
                CaptureTestSupport.StartedSafetyGate(),
                stopController: stopController);

            Assert.True(result.StopTicks >= stopAtTicks);
            Assert.Equal([Second], source.Intervals);

            await using var store = await SessionStore.OpenAsync(root, "random-session");
            Assert.Equal(SessionStatus.Ready, store.Manifest.Status);
            Assert.Equal(SessionManifest.RandomSamplingMode, store.Manifest.SamplingMode);
            Assert.Equal(Second, store.Manifest.CaptureIntervalTicks);
            Assert.NotNull(store.Manifest.RandomSampling);
            Assert.Equal(42, store.Manifest.RandomSampling!.Seed);
            Assert.NotEmpty(store.Manifest.RandomSampling.Windows);
            Assert.NotEmpty(store.Manifest.Slots);
            Assert.Equal(result.SlotCount, store.Manifest.Slots.Count);
            Assert.Equal(
                store.Manifest.Slots.Count,
                store.Manifest.Slots.Select(static slot => slot.TargetTicks).Distinct().Count());
            Assert.All(
                store.Manifest.Slots,
                slot => Assert.StartsWith("screen/slot-r-", slot.AssetReference, StringComparison.Ordinal));
            Assert.All(
                store.Manifest.Slots,
                slot => Assert.Contains(
                    store.Manifest.RandomSampling.Windows,
                    window => window.TargetTicks == slot.TargetTicks));

            var journal = await SessionJournalReader.ReadAsync(
                Path.Combine(store.DirectoryPath, "events.jsonl"));
            Assert.Contains(journal.Records, record => record.Kind == "random-window");
            Assert.DoesNotContain(journal.Records, record => record.Kind == "adaptive-compaction");

            var input = SessionRenderInputBuilder.Build(store.Manifest);
            Assert.Equal(
                store.Manifest.Slots.Select(static slot => slot.TargetTicks),
                input.Slots.Select(static slot => slot.Slot.ActiveTargetTicks));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class RandomFrameSource : IFrameSource, IFrameSourceSamplingControl
    {
        private readonly object sync = new();
        private readonly List<long> intervals = [];
        private long nextSourceTicks;

        public IReadOnlyList<long> Intervals
        {
            get
            {
                lock (sync)
                {
                    return intervals.ToArray();
                }
            }
        }

        public long SampleIntervalTicks
        {
            get
            {
                lock (sync)
                {
                    return intervals.Count == 0 ? 0 : intervals[^1];
                }
            }
        }

        public void SetSampleInterval(long intervalTicks)
        {
            lock (sync)
            {
                if (intervals.Count == 0 || intervals[^1] != intervalTicks)
                {
                    intervals.Add(intervalTicks);
                }
            }
        }

        public async IAsyncEnumerable<FrameSample> ReadFramesAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceTicks = Interlocked.Add(ref nextSourceTicks, Second) - Second;
                yield return new FrameSample(sourceTicks, new byte[] { 0, 0, 0, 255 }, 1, 1);
                await Task.Delay(1, cancellationToken);
            }
        }
    }

    private sealed class StopAtClock(CaptureStopController stopController, long stopAtTicks) : IClock
    {
        public long NowTicks { get; private set; }

        public ValueTask DelayUntilAsync(long targetTicks, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NowTicks = Math.Max(NowTicks, targetTicks);
            if (NowTicks >= stopAtTicks)
            {
                stopController.RequestStop();
            }

            return ValueTask.CompletedTask;
        }
    }
}
