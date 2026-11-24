using Log.Core.Shell;
using Xunit;

namespace Log.Core.Tests;

public sealed class TrayMenuModelTests
{
    [Theory]
    [InlineData("idle", false, TrayStatus.Idle, "log · Idle", false)]
    [InlineData("ready", false, TrayStatus.Idle, "log · Idle", false)]
    [InlineData("awaitingconsent", false, TrayStatus.Idle, "log · Idle", false)]
    [InlineData("recording", true, TrayStatus.Recording, "log · Recording", true)]
    [InlineData("recording", false, TrayStatus.Recording, "log · Recording", false)]
    [InlineData("paused", true, TrayStatus.Paused, "log · Paused", true)]
    [InlineData("stopping", true, TrayStatus.Finishing, "log · Finishing", false)]
    [InlineData("saving", false, TrayStatus.Finishing, "log · Finishing", false)]
    [InlineData("error", true, TrayStatus.Error, "log · Error", false)]
    [InlineData("unexpected", false, TrayStatus.Error, "log · Error", false)]
    public void PublishedStateMapsToTruthfulTrayActions(
        string engineState,
        bool canFinish,
        TrayStatus expectedStatus,
        string expectedToolTip,
        bool expectedFinishEnabled)
    {
        var presentation = TrayMenuModel.Create(engineState, canFinish);

        Assert.Equal(expectedStatus, presentation.Status);
        Assert.Equal(expectedToolTip, presentation.ToolTip);
        var expectedActions = expectedFinishEnabled
            ? new[] { TrayMenuAction.OpenLog, TrayMenuAction.FinishRecording, TrayMenuAction.Settings, TrayMenuAction.UnfinishedSaves, TrayMenuAction.Exit }
            : new[] { TrayMenuAction.OpenLog, TrayMenuAction.Settings, TrayMenuAction.UnfinishedSaves, TrayMenuAction.Exit };
        Assert.Equal(expectedActions, presentation.Items.Select(static item => item.Action));
        Assert.True(Assert.Single(presentation.Items, static item => item.Action == TrayMenuAction.OpenLog).IsEnabled);
        if (expectedFinishEnabled)
            Assert.True(Assert.Single(presentation.Items, static item => item.Action == TrayMenuAction.FinishRecording).IsEnabled);
        Assert.True(Assert.Single(presentation.Items, static item => item.Action == TrayMenuAction.Settings).IsEnabled);
        Assert.True(Assert.Single(presentation.Items, static item => item.Action == TrayMenuAction.UnfinishedSaves).IsEnabled);
        Assert.True(Assert.Single(presentation.Items, static item => item.Action == TrayMenuAction.Exit).IsEnabled);
    }

    [Fact]
    public void EmptyStateIsRejectedInsteadOfBeingReportedAsIdle()
    {
        Assert.Throws<ArgumentException>(() => TrayMenuModel.Create(" ", canFinish: false));
    }
}
