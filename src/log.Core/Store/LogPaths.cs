using System.Security.Cryptography;
using System.Text;

namespace Log.Core.Store;

public static class LogPaths
{
    public const string DataRootEnvironmentVariable = "LOG_DATA_ROOT";

    public static bool IsDataRootConfigured =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DataRootEnvironmentVariable));

    public static string SingleInstanceId
    {
        get
        {
            if (!IsDataRootConfigured)
            {
                return "log";
            }

            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DefaultRootDirectory))
                .ToUpperInvariant();
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)));
            return $"log-{hash[..16]}";
        }
    }

    public static string DefaultRootDirectory
    {
        get
        {
            var configuredRoot = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configuredRoot))
            {
                return Path.GetFullPath(configuredRoot);
            }

            var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                throw new InvalidOperationException("The per-user local application-data directory is unavailable.");
            }

            return Path.Combine(localApplicationData, "log");
        }
    }

    public static string SettingsPath => Path.Combine(DefaultRootDirectory, "settings.json");

    public static string HotkeySettingsPath => Path.Combine(DefaultRootDirectory, "hotkey.json");

    public static string DefaultExportDirectory => IsDataRootConfigured
        ? Path.Combine(DefaultRootDirectory, "exports")
        : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "log",
            "exports");

    public static string CatalogPath(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        return Path.Combine(rootDirectory, "catalog");
    }

    public static string SessionsDirectory(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        return Path.Combine(rootDirectory, "sessions");
    }

    public static string GroupsDirectory(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        return Path.Combine(rootDirectory, "groups");
    }

    public static string GroupDirectory(string rootDirectory, string groupId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        SessionIdPolicy.Validate(groupId);
        return Path.Combine(GroupsDirectory(rootDirectory), groupId);
    }

    public static string SessionDirectory(string rootDirectory, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        SessionIdPolicy.Validate(sessionId);
        return Path.Combine(SessionsDirectory(rootDirectory), sessionId);
    }
}
