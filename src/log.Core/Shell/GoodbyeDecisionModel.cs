namespace Log.Core.Shell;

public enum GoodbyeDecisionPhase
{
    Recording,
    Decision,
    Finishing
}

public enum GoodbyeDecisionAction
{
    TimerClicked,
    KeepGoing,
    FinishAndPreview,
    Escape,
    WindowClosed
}

public sealed record GoodbyeDecisionTransition(
    GoodbyeDecisionPhase PreviousPhase,
    GoodbyeDecisionPhase CurrentPhase,
    bool ShouldStopCapture);

/// <summary>
/// Serializes the goodbye decision for one capture. Only an explicit Finish
/// action from the open decision can request capture stop.
/// </summary>
public sealed class GoodbyeDecisionModel
{
    private readonly object gate = new();
    private GoodbyeDecisionPhase phase = GoodbyeDecisionPhase.Recording;

    public GoodbyeDecisionPhase CurrentPhase
    {
        get
        {
            lock (gate)
            {
                return phase;
            }
        }
    }

    public GoodbyeDecisionTransition Apply(GoodbyeDecisionAction action)
    {
        if (!Enum.IsDefined(action))
        {
            throw new ArgumentOutOfRangeException(nameof(action));
        }

        lock (gate)
        {
            var previous = phase;
            var shouldStopCapture = false;
            switch (phase, action)
            {
                case (GoodbyeDecisionPhase.Recording, GoodbyeDecisionAction.TimerClicked):
                    phase = GoodbyeDecisionPhase.Decision;
                    break;
                case (GoodbyeDecisionPhase.Decision, GoodbyeDecisionAction.KeepGoing or GoodbyeDecisionAction.Escape or GoodbyeDecisionAction.WindowClosed):
                    phase = GoodbyeDecisionPhase.Recording;
                    break;
                case (GoodbyeDecisionPhase.Decision, GoodbyeDecisionAction.FinishAndPreview):
                    phase = GoodbyeDecisionPhase.Finishing;
                    shouldStopCapture = true;
                    break;
            }

            return new GoodbyeDecisionTransition(previous, phase, shouldStopCapture);
        }
    }
}
