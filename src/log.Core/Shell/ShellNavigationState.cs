namespace Log.Core.Shell;

public enum ShellPage
{
    UnfinishedSaves,
    Record,
    Settings
}

/// <summary>
/// State-only destination selection. The app shell keeps capture, review, and
/// export lifetimes outside this value so changing destinations cannot reset them.
/// </summary>
public sealed class ShellNavigationState
{
    public ShellPage CurrentPage { get; private set; } = ShellPage.Record;

    public bool NavigateTo(ShellPage page)
    {
        if (CurrentPage == page)
        {
            return false;
        }

        CurrentPage = page;
        return true;
    }
}
