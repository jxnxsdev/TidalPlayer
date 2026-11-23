using System.IO.Compression;
using System.Reflection;

namespace TidalMpvPlayer;

public sealed class MpvRuntime
{
    public static string ExtractBundledRuntime()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith("mpv-runtime.zip", StringComparison.OrdinalIgnoreCase));

        var executableName = OperatingSystem.IsWindows() ? "mpv.exe" : "mpv";
        var runtimeDirectory = Path.Combine(
            Path.GetTempPath(),
            "TidalMpvPlayer",
            "runtime-" + (resourceName is null ? "external" : GetResourceIdentity(assembly, resourceName)));

        var mpvPath = Path.Combine(runtimeDirectory, executableName);
        if (File.Exists(mpvPath))
            return mpvPath;

        if (resourceName is null)
            throw new FileNotFoundException(
                "No bundled MPV runtime is present. Supply mpv-runtime.zip when publishing.");

        Directory.CreateDirectory(runtimeDirectory);
        using var archiveStream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Could not open bundled MPV runtime.");
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        archive.ExtractToDirectory(runtimeDirectory, overwriteFiles: true);

        if (!File.Exists(mpvPath))
            throw new FileNotFoundException($"Bundled MPV runtime has no {executableName}", mpvPath);

        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(
                mpvPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        return mpvPath;
    }

    private static string GetResourceIdentity(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return "unknown";

        using var sha = System.Security.Cryptography.SHA256.Create();
        var digest = sha.ComputeHash(stream);
        return Convert.ToHexString(digest)[..12].ToLowerInvariant();
    }
}
