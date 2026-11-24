using Log.Core.Capture;

namespace Log.Integration.Tests;

internal static class CaptureTestSupport
{
    public static CaptureSafetyGate StartedSafetyGate()
    {
        var gate = new CaptureSafetyGate();
        gate.Start(indicatorsVerified: true);
        return gate;
    }
}
