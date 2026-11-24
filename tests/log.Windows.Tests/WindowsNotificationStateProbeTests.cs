using Log.Windows;
using Xunit;

namespace Log.Windows.Tests;

public sealed class WindowsNotificationStateProbeTests
{
    [Fact]
    public void OnlySuccessfulAcceptsNotificationsStateAllowsInvitation()
    {
        var accepting = new WindowsNotificationStateResult(HResult: 0, StateValue: 5);

        Assert.True(accepting.AllowsNonCriticalNotifications);
        Assert.False(accepting.IsSuppressed);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(0, 3)]
    [InlineData(0, 4)]
    [InlineData(0, 6)]
    [InlineData(0, 7)]
    [InlineData(0, 99)]
    [InlineData(1, 5)]
    [InlineData(-2147467259, 5)]
    public void FailedUnknownOrBlockedStatesSuppressInvitation(int hresult, int stateValue)
    {
        var result = new WindowsNotificationStateResult(hresult, stateValue);

        Assert.False(result.AllowsNonCriticalNotifications);
        Assert.True(result.IsSuppressed);
    }

    [Fact]
    public void NativeQueryReturnsASuccessAndDocumentedStateOnThisWindowsHost()
    {
        var result = WindowsNotificationStateProbe.Query();

        Assert.True(
            result.HResult == 0,
            $"SHQueryUserNotificationState failed with HRESULT 0x{result.HResult:X8}.");
        Assert.InRange(result.StateValue, 1, 7);
    }
}