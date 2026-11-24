using Log.Core.Render;
using Xunit;

namespace Log.Core.Tests;

public sealed class ExportPathPolicyTests
{
    [Fact]
    public void SanitizesWindowsNamesAndKeepsMp4Contract()
    {
        var fileName = ExportPathPolicy.CreateFileName(
            "  Study: <week>  ",
            new DateTimeOffset(2026, 9, 3, 12, 34, 56, TimeSpan.Zero));

        Assert.Equal("Study week_20260903-1234.mp4", fileName);
    }

    [Theory]
    [InlineData("Weekly review", "Weekly review.mp4")]
    [InlineData("Weekly review.mp4", "Weekly review.mp4")]
    [InlineData("Study: <week>", "Study week.mp4")]
    public void UsesTheEnteredNameAsTheExportFileName(string enteredName, string expectedFileName)
    {
        Assert.Equal(expectedFileName, ExportPathPolicy.CreateFileNameFromInput(enteredName));
    }

    [Theory]
    [InlineData("CON.txt", "_CON.txt.mp4")]
    [InlineData("NUL.description", "_NUL.description.mp4")]
    public void PrefixesDottedReservedDeviceNames(string enteredName, string expectedFileName)
    {
        Assert.Equal(expectedFileName, ExportPathPolicy.CreateFileNameFromInput(enteredName));
    }

    [Fact]
    public void PrefixesReservedDeviceNamesAndAllocatesCollisionSuffix()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var first = Path.Combine(root, "CON_20260903-1234.mp4");
            File.WriteAllBytes(first, []);

            Assert.Equal("_CON", ExportPathPolicy.SanitizeBaseName("CON"));
            var second = ExportPathPolicy.AllocateUniquePath(root, "CON_20260903-1234.mp4");
            Assert.Equal(Path.Combine(root, "CON_20260903-1234 (2).mp4"), second);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RejectsRootedAndNestedExportFilenames()
    {
        var root = Path.Combine(Path.GetTempPath(), "log-tests", Guid.NewGuid().ToString("N"));

        Assert.Throws<ArgumentException>(() => ExportPathPolicy.AllocateUniquePath(root, "nested\\escape.mp4"));
        Assert.Throws<ArgumentException>(() => ExportPathPolicy.AllocateUniquePath(root, "C:\\escape.mp4"));
    }

}
