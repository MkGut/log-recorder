using System.Runtime.InteropServices;

namespace Log.Windows;

public sealed record WindowsPlatformSupport(
    bool IsWindows,
    bool IsSupportedBuild,
    bool IsX64,
    int ReportedBuild,
    bool IsSupported)
{
    public static WindowsPlatformSupport Inspect()
    {
        var isWindows = OperatingSystem.IsWindows();
        var reportedBuild = isWindows ? Environment.OSVersion.Version.Build : 0;
        var isSupportedBuild = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100);
        var isX64 = RuntimeInformation.OSArchitecture == Architecture.X64
            && RuntimeInformation.ProcessArchitecture == Architecture.X64;
        return new WindowsPlatformSupport(
            isWindows,
            isSupportedBuild,
            isX64,
            reportedBuild,
            isWindows && isSupportedBuild && isX64);
    }
}
