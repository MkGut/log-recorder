using Log.Core.Shell;
using Xunit;

namespace Log.Core.Tests;

public sealed class GoodbyeDecisionModelTests
{
    [Fact]
    public void TimerClickOpensDecisionWithoutStoppingCapture()
    {
        var model = new GoodbyeDecisionModel();

        var transition = model.Apply(GoodbyeDecisionAction.TimerClicked);

        Assert.Equal(GoodbyeDecisionPhase.Recording, transition.PreviousPhase);
        Assert.Equal(GoodbyeDecisionPhase.Decision, transition.CurrentPhase);
        Assert.False(transition.ShouldStopCapture);
    }

    [Theory]
    [InlineData(GoodbyeDecisionAction.KeepGoing)]
    [InlineData(GoodbyeDecisionAction.Escape)]
    [InlineData(GoodbyeDecisionAction.WindowClosed)]
    public void DismissingDecisionKeepsCaptureRunning(GoodbyeDecisionAction action)
    {
        var model = new GoodbyeDecisionModel();
        model.Apply(GoodbyeDecisionAction.TimerClicked);

        var transition = model.Apply(action);

        Assert.Equal(GoodbyeDecisionPhase.Recording, transition.CurrentPhase);
        Assert.False(transition.ShouldStopCapture);
    }

    [Fact]
    public void FinishFromDecisionRequestsOneStopAndEntersFinishing()
    {
        var model = new GoodbyeDecisionModel();
        model.Apply(GoodbyeDecisionAction.TimerClicked);

        var finish = model.Apply(GoodbyeDecisionAction.FinishAndPreview);
        var repeatedFinish = model.Apply(GoodbyeDecisionAction.FinishAndPreview);

        Assert.Equal(GoodbyeDecisionPhase.Finishing, finish.CurrentPhase);
        Assert.True(finish.ShouldStopCapture);
        Assert.Equal(GoodbyeDecisionPhase.Finishing, repeatedFinish.CurrentPhase);
        Assert.False(repeatedFinish.ShouldStopCapture);
    }

    [Fact]
    public void ConcurrentFinishClicksCanRequestStopOnlyOnce()
    {
        var model = new GoodbyeDecisionModel();
        model.Apply(GoodbyeDecisionAction.TimerClicked);
        var acceptedStops = 0;

        Parallel.For(0, 64, _ =>
        {
            if (model.Apply(GoodbyeDecisionAction.FinishAndPreview).ShouldStopCapture)
            {
                Interlocked.Increment(ref acceptedStops);
            }
        });

        Assert.Equal(1, acceptedStops);
        Assert.Equal(GoodbyeDecisionPhase.Finishing, model.CurrentPhase);
    }

    [Fact]
    public void FinishActionOutsideOpenDecisionDoesNotStopCapture()
    {
        var model = new GoodbyeDecisionModel();

        var transition = model.Apply(GoodbyeDecisionAction.FinishAndPreview);

        Assert.Equal(GoodbyeDecisionPhase.Recording, transition.CurrentPhase);
        Assert.False(transition.ShouldStopCapture);
    }
}
