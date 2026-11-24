using Log.Core.Shell;
using Xunit;

namespace Log.Core.Tests;

public sealed class RecordingSurfaceModelTests
{
    [Fact]
    public void NotStartedDoesNotExposeATimerOrStaleCaptureMetadata()
    {
        var presentation = RecordingSurfaceModel.Create(
            RecordingSurfaceStatus.NotStarted,
            TimeSpan.FromMinutes(4),
            "Old display",
            "Old error");

        Assert.False(presentation.HasTimer);
        Assert.False(presentation.TimerAdvancing);
        Assert.Equal(TimeSpan.Zero, presentation.Elapsed);
        Assert.Null(presentation.SourceIdentity);
        Assert.Null(presentation.ErrorMessage);
    }

    [Theory]
    [InlineData(RecordingSurfaceStatus.Recording, true, true)]
    [InlineData(RecordingSurfaceStatus.Paused, true, false)]
    [InlineData(RecordingSurfaceStatus.Error, true, false)]
    public void StartedStateUsesEngineElapsedAndAdapterSourceIdentity(
        RecordingSurfaceStatus status,
        bool expectedHasTimer,
        bool expectedTimerAdvancing)
    {
        var elapsed = TimeSpan.FromMinutes(2) + TimeSpan.FromSeconds(17);
        const string sourceIdentity = "Display 1 (entire display)";
        const string errorMessage = "The capture source stopped.";

        var presentation = RecordingSurfaceModel.Create(
            status,
            elapsed,
            sourceIdentity,
            errorMessage);

        Assert.Equal(status, presentation.Status);
        Assert.Equal(expectedHasTimer, presentation.HasTimer);
        Assert.Equal(expectedTimerAdvancing, presentation.TimerAdvancing);
        Assert.Equal(elapsed, presentation.Elapsed);
        Assert.Equal(sourceIdentity, presentation.SourceIdentity);
        Assert.Equal(
            status == RecordingSurfaceStatus.Error ? errorMessage : null,
            presentation.ErrorMessage);
    }

    [Fact]
    public void ElapsedTicksDoNotCreateRepeatedStatusAnnouncements()
    {
        var first = RecordingSurfaceModel.Create(
            RecordingSurfaceStatus.Recording,
            TimeSpan.FromSeconds(1),
            "Display 1");
        var nextTick = RecordingSurfaceModel.Create(
            RecordingSurfaceStatus.Recording,
            TimeSpan.FromSeconds(2),
            "Display 1");

        Assert.False(RecordingSurfaceModel.ShouldAnnounceTransition(first, nextTick));
    }

    [Fact]
    public void StatusSourceAndErrorChangesAreAnnounced()
    {
        var recording = RecordingSurfaceModel.Create(
            RecordingSurfaceStatus.Recording,
            TimeSpan.FromSeconds(1),
            "Display 1");
        var paused = RecordingSurfaceModel.Create(
            RecordingSurfaceStatus.Paused,
            TimeSpan.FromSeconds(1),
            "Display 1");
        var changedSource = RecordingSurfaceModel.Create(
            RecordingSurfaceStatus.Recording,
            TimeSpan.FromSeconds(1),
            "Display 2");
        var error = RecordingSurfaceModel.Create(
            RecordingSurfaceStatus.Error,
            TimeSpan.FromSeconds(1),
            "Display 1",
            "Capture failed.");

        Assert.True(RecordingSurfaceModel.ShouldAnnounceTransition(recording, paused));
        Assert.True(RecordingSurfaceModel.ShouldAnnounceTransition(recording, changedSource));
        Assert.True(RecordingSurfaceModel.ShouldAnnounceTransition(recording, error));
    }

    [Fact]
    public void NegativeEngineElapsedIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RecordingSurfaceModel.Create(
            RecordingSurfaceStatus.Recording,
            TimeSpan.FromSeconds(-1),
            "Display 1"));
    }
}
