using System.Globalization;

namespace Log.Core.Store;

public static class SessionNamePolicy
{
    public static string CreateAutomaticName(DateTimeOffset timestamp)
    {
        return $"Recording {timestamp.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)}";
    }

    public static string Normalize(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalized = name.Trim();
        if (normalized.Length > 200 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Session names must be 1-200 characters without control characters.", nameof(name));
        }

        return normalized;
    }

    public static string? NormalizeDescription(string? description)
    {
        if (description is null)
        {
            return null;
        }

        var normalized = description.Trim();
        // Allow CR/LF/TAB (multi-line notes); reject only other control characters.
        if (normalized.Length > 2000
            || normalized.Any(character =>
                char.IsControl(character) && character is not ('\r' or '\n' or '\t')))
        {
            throw new ArgumentException(
                "Session descriptions must be at most 2000 characters without control characters other than newlines and tabs.",
                nameof(description));
        }

        return normalized.Length == 0 ? null : normalized;
    }
}
