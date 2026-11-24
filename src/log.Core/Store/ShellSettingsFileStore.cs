using System.Text.Json;

namespace Log.Core.Store;

/// <summary>
/// Result of reading the unpackaged fallback settings file. The original bytes
/// are never replaced automatically when they cannot be parsed.
/// </summary>
public sealed record ShellSettingsFileLoadResult(
    ShellSettings Settings,
    string? Error,
    bool PreservedOriginalBytes)
{
    public bool RequiresExplicitOverwrite => PreservedOriginalBytes;
}

/// <summary>
/// Durable fallback-file semantics shared by the shell and deterministic tests.
/// Package-local settings remain owned by the WinUI shell.
/// </summary>
public static class ShellSettingsFileStore
{

    public static ShellSettingsFileLoadResult Load(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            return new(ShellSettings.Defaults, null, false);
        }

        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            var settings = JsonSerializer.Deserialize(bytes, PlainJson.Default.ShellSettings);
            if (settings is null)
            {
                throw new InvalidDataException("The settings JSON value was null.");
            }

            return new(
                settings.Validate(),
                null,
                false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return new(
                ShellSettings.Defaults,
                $"The fallback settings file is unreadable ({exception.Message}). The original bytes were preserved at '{fullPath}'.",
                true);
        }
    }

    public static bool TrySave(
        string filePath,
        ShellSettings settings,
        bool allowReplacingPreservedBytes,
        out string? error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(settings);
        var fullPath = Path.GetFullPath(filePath);

        if (!allowReplacingPreservedBytes && File.Exists(fullPath))
        {
            var load = Load(fullPath);
            if (load.RequiresExplicitOverwrite)
            {
                error = load.Error;
                return false;
            }
        }

        var temporaryPath = fullPath + ".tmp";
        try
        {
            var directory = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("The local settings directory could not be resolved.");
            Directory.CreateDirectory(directory);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(settings.Validate(), PlainJson.Default.ShellSettings);
            using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            error = $"The local settings write failed ({exception.Message}).";
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch
            {
                // Preserve the primary write diagnostic; a later successful save
                // is allowed to replace the known temporary path.
            }

            return false;
        }
    }
}
