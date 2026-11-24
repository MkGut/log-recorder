using Log.Core.Store;

namespace Log.Core.Render;

public static class ExportPathPolicy
{
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string SanitizeBaseName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var characters = name
            .Where(character => !char.IsControl(character) && !invalidCharacters.Contains(character))
            .ToArray();
        var sanitized = new string(characters).Trim().TrimEnd('.', ' ');
        if (sanitized.Length == 0)
        {
            sanitized = "log";
        }

        var firstDot = sanitized.IndexOf('.');
        var deviceName = (firstDot < 0 ? sanitized : sanitized[..firstDot]).TrimEnd(' ');
        if (ReservedDeviceNames.Contains(deviceName))
        {
            sanitized = $"_{sanitized}";
        }

        return sanitized.Length <= 100 ? sanitized : sanitized[..100].TrimEnd('.', ' ');
    }

    public static string CreateFileName(string sessionName, DateTimeOffset timestamp)
    {
        return $"{SanitizeBaseName(sessionName)}_{timestamp:yyyyMMdd-HHmm}.mp4";
    }

    public static string CreateFileNameFromInput(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var trimmed = name.Trim();
        var extension = Path.GetExtension(trimmed);
        var baseName = string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileNameWithoutExtension(trimmed)
            : trimmed;
        var sanitized = SanitizeBaseName(baseName);
        return $"{sanitized}.mp4";
    }

    public static string GetDefaultExportDirectory()
    {
        if (LogPaths.IsDataRootConfigured)
        {
            return LogPaths.DefaultExportDirectory;
        }

        var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        if (string.IsNullOrWhiteSpace(videos))
        {
            throw new InvalidOperationException("The per-user Videos directory is unavailable.");
        }

        return Path.Combine(videos, "log");
    }

    public static string AllocateUniquePath(string directory, string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!string.Equals(Path.GetExtension(fileName), ".mp4", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Export filenames must use the .mp4 extension.", nameof(fileName));
        }

        if (Path.IsPathRooted(fileName)
            || !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal))
        {
            throw new ArgumentException("Export filenames must not contain directory components.", nameof(fileName));
        }

        var fullDirectory = Path.GetFullPath(directory);
        Directory.CreateDirectory(fullDirectory);
        var candidate = Path.Combine(fullDirectory, fileName);
        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var suffix = 2; suffix <= 10_000; suffix++)
        {
            candidate = Path.Combine(fullDirectory, $"{stem} ({suffix}).mp4");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Unable to allocate a unique export filename.");
    }
}
