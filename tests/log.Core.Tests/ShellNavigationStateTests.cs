using Log.Core.Shell;
using Xunit;

namespace Log.Core.Tests;

public sealed class ShellNavigationStateTests
{
    [Fact]
    public void StartsOnRecord()
    {
        var state = new ShellNavigationState();

        Assert.Equal(ShellPage.Record, state.CurrentPage);
    }

    [Fact]
    public void NavigatingAcrossDestinationsDoesNotRecreateTheStateObject()
    {
        var state = new ShellNavigationState();

        Assert.True(state.NavigateTo(ShellPage.Settings));
        Assert.Equal(ShellPage.Settings, state.CurrentPage);
        Assert.True(state.NavigateTo(ShellPage.UnfinishedSaves));
        Assert.Equal(ShellPage.UnfinishedSaves, state.CurrentPage);
    }

    [Fact]
    public void RepeatingTheCurrentDestinationIsAStableNoOp()
    {
        var state = new ShellNavigationState();

        Assert.False(state.NavigateTo(ShellPage.Record));
        Assert.Equal(ShellPage.Record, state.CurrentPage);
    }
}
