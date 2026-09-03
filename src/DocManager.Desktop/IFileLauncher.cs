using System.Diagnostics;

namespace DocManager.Desktop;

public interface IFileLauncher
{
    void Open(string path);
}

public sealed class ShellFileLauncher : IFileLauncher
{
    public void Open(string path) =>
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
}
