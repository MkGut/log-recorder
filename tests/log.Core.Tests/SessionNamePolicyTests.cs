using Log.Core.Store;
using Xunit;

namespace Log.Core.Tests;

public sealed class SessionNamePolicyTests
{
    [Fact]
    public void DescriptionAllowsNewlinesAndTabsForMultiLineNotes()
    {
        const string notes = "First line.\nSecond line.\tindented";

        Assert.Equal(notes, SessionNamePolicy.NormalizeDescription(notes));
    }

    [Fact]
    public void DescriptionRejectsControlCharactersOtherThanNewlinesAndTabs()
    {
        Assert.Throws<ArgumentException>(() => SessionNamePolicy.NormalizeDescription("vtab\vhere"));
        Assert.Throws<ArgumentException>(() => SessionNamePolicy.NormalizeDescription("has\0null"));
    }

    [Fact]
    public void DescriptionTrimsAndCollapsesEmptyToNull()
    {
        Assert.Null(SessionNamePolicy.NormalizeDescription(null));
        Assert.Null(SessionNamePolicy.NormalizeDescription("   "));
        Assert.Equal("trimmed", SessionNamePolicy.NormalizeDescription("  trimmed  "));
    }

    [Fact]
    public void DescriptionRejectsOverlongText()
    {
        Assert.Throws<ArgumentException>(() => SessionNamePolicy.NormalizeDescription(new string('x', 2001)));
    }

    [Fact]
    public void NameTrimsAndRejectsControlCharactersAndOverlongOrEmptyValues()
    {
        Assert.Equal("Study block", SessionNamePolicy.Normalize("  Study block  "));
        Assert.Throws<ArgumentException>(() => SessionNamePolicy.Normalize("line\nbreak"));
        Assert.Throws<ArgumentException>(() => SessionNamePolicy.Normalize(new string('x', 201)));
        Assert.Throws<ArgumentException>(() => SessionNamePolicy.Normalize("   "));
    }

    [Fact]
    public void AutomaticNameUsesAFileSafeTimestamp()
    {
        var timestamp = new DateTimeOffset(2026, 11, 5, 21, 45, 58, TimeSpan.FromHours(-5));

        var name = SessionNamePolicy.CreateAutomaticName(timestamp);

        Assert.Equal("Recording 2026-11-05 21-45-58", name);
        Assert.Equal(name, SessionNamePolicy.Normalize(name));
        Assert.DoesNotContain(':', name);
    }
}
