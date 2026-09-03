using Xunit;

namespace DocManager.Tests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RequiresSymbolicLinkFactAttribute : FactAttribute
{
    private static readonly string? SkipReason = ProbeSymbolicLinkSupport();

    public RequiresSymbolicLinkFactAttribute()
    {
        Skip = SkipReason;
    }

    private static string? ProbeSymbolicLinkSupport()
    {
        if (!OperatingSystem.IsWindows())
            return "Requires Windows symbolic-link support.";

        var root = Path.Combine(Path.GetTempPath(), $"DocManagerSymbolicLinkProbe-{Guid.NewGuid():N}");
        try
        {
            var targetDirectory = Path.Combine(root, "target-directory");
            var directoryLink = Path.Combine(root, "directory-link");
            var targetFile = Path.Combine(root, "target-file.txt");
            var fileLink = Path.Combine(root, "file-link.txt");
            Directory.CreateDirectory(targetDirectory);
            File.WriteAllText(targetFile, "probe");
            Directory.CreateSymbolicLink(directoryLink, targetDirectory);
            File.CreateSymbolicLink(fileLink, targetFile);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            return $"Requires symbolic-link permission: {exception.Message}";
        }
        catch (PlatformNotSupportedException exception)
        {
            return $"Requires symbolic-link support: {exception.Message}";
        }
        catch (IOException exception)
        {
            return $"Requires symbolic-link creation support: {exception.Message}";
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Probe cleanup must not make symbolic-link-dependent tests run.
            }
        }
    }
}
