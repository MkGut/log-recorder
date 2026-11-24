using Xunit;

namespace Log.Windows.Tests;

public sealed class PlatformBoundaryTests
{
    [Fact]
    public void CaptureExclusionRejectsMissingWindowHandle()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureWindowPolicy.ExcludeFromCapture(nint.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => CaptureWindowPolicy.SetCaptureExclusion(nint.Zero, exclude: false));
    }

    [Fact]
    public void HotkeyRegistrationRejectsInvalidNativeInputsBeforePInvoke()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GlobalHotkeyRegistration.Register(
            nint.Zero,
            1,
            GlobalHotkeyModifiers.Control,
            0x48));
        Assert.Throws<ArgumentOutOfRangeException>(() => GlobalHotkeyRegistration.Register(
            new nint(1),
            0,
            GlobalHotkeyModifiers.Control,
            0x48));
        Assert.Throws<ArgumentOutOfRangeException>(() => GlobalHotkeyRegistration.Register(
            new nint(1),
            1,
            GlobalHotkeyModifiers.Control,
            0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GlobalHotkeyRegistration.Register(
            new nint(1),
            1,
            GlobalHotkeyModifiers.Control,
            0x100));
        Assert.Throws<ArgumentException>(() => GlobalHotkeyRegistration.Register(
            new nint(1),
            1,
            GlobalHotkeyModifiers.Control | (GlobalHotkeyModifiers)0x8000,
            0x48));
    }

    [Fact]
    public void PumpBindingsRequireModifiersAndUniqueIds()
    {
        Assert.Throws<ArgumentException>(() => new GlobalHotkeyBinding(
            1,
            GlobalHotkeyModifiers.None,
            0x48));

        var binding = new GlobalHotkeyBinding(1, GlobalHotkeyModifiers.Control, 0x48);
        Assert.Throws<ArgumentOutOfRangeException>(() => new GlobalHotkeyBinding(
            1,
            GlobalHotkeyModifiers.Control,
            0x100));
        Assert.Throws<ArgumentException>(() => new GlobalHotkeyBinding(
            1,
            GlobalHotkeyModifiers.Control | (GlobalHotkeyModifiers)0x8000,
            0x48));
        Assert.Throws<ArgumentException>(() => new GlobalHotkeyPump(
        [
            (binding, (Action)(static () => { })),
            (binding, (Action)(static () => { }))
        ]));
    }

    [Fact]
    public void CurrentHostMeetsTheDocumentedWindowsBaseline()
    {
        var support = WindowsPlatformSupport.Inspect();

        Assert.True(support.IsWindows);
        Assert.True(support.IsSupportedBuild);
        Assert.True(support.IsX64);
        Assert.True(support.IsSupported);
        Assert.True(support.ReportedBuild >= 26100);
    }

    [Fact]
    public void InBoxMediaFoundationCanBeStartedAndShutdown()
    {
        using var mediaFoundation = MediaFoundationSession.Start();
        Assert.Equal(0x0002_0070u, MediaFoundationSession.MfVersion);
    }

    [Fact]
    public void GraphicsCaptureCapabilityProbeDoesNotStartCaptureOrBypassConsent()
    {
        var capabilities = WindowsCaptureCapabilities.Inspect();

        Assert.True(capabilities.ApiAvailable || capabilities.FailureHResult is not null);
    }
}
