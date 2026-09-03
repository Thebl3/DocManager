namespace DocManager.Core;

public static class AtomicFile
{
    public static void Replace(string temporaryPath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var fullTemporary = Path.GetFullPath(temporaryPath);
        var fullDestination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDestination)!);

        if (File.Exists(fullDestination))
        {
            File.Move(fullTemporary, fullDestination, true);
        }
        else
        {
            File.Move(fullTemporary, fullDestination);
        }
    }

    public static string CreateSiblingTemporaryPath(string destinationPath) =>
        $"{Path.GetFullPath(destinationPath)}.{Guid.NewGuid():N}.part";
}
