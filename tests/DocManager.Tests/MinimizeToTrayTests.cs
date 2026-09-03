namespace DocManager.Tests;

public class MinimizeToTrayTests
{
    [Fact]
    public void MainWindow_wires_StateChanged_handler_for_minimize_to_tray()
    {
        var root = RepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "DocManager.Desktop", "MainWindow.xaml"));

        Assert.Contains("StateChanged=\"MainWindow_StateChanged\"", xaml);
    }

    [Fact]
    public void MainWindow_has_StateChanged_handler_and_NotifyIcon_usage()
    {
        var root = RepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DocManager.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("private void MainWindow_StateChanged", codeBehind);
        Assert.Contains("private NotifyIcon? _trayIcon;", codeBehind);
        Assert.Contains("System.Windows.Forms.NotifyIcon", codeBehind);
    }

    [Fact]
    public void Minimize_does_not_cancel_operations()
    {
        var root = RepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DocManager.Desktop", "MainWindow.xaml.cs"));

        // Extract MainWindow_StateChanged method
        var stateChangedStart = codeBehind.IndexOf("private void MainWindow_StateChanged", StringComparison.Ordinal);
        var nextMethodStart = codeBehind.IndexOf("private void ", stateChangedStart + 1);
        var stateChangedMethod = codeBehind.Substring(stateChangedStart, nextMethodStart - stateChangedStart);

        // Verify it doesn't set _isClosing
        Assert.DoesNotContain("_isClosing = true", stateChangedMethod);
        // Verify it doesn't call _cancellation?.Cancel()
        Assert.DoesNotContain("_cancellation?.Cancel()", stateChangedMethod);
    }

    [Fact]
    public void MainWindow_Closing_still_has_dirty_LDT_guard_and_settings_save()
    {
        var root = RepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DocManager.Desktop", "MainWindow.xaml.cs"));

        // Extract MainWindow_Closing method
        var closingStart = codeBehind.IndexOf("private void MainWindow_Closing", StringComparison.Ordinal);
        var nextMethodStart = codeBehind.IndexOf("private void ", closingStart + 1);
        var closingMethod = codeBehind.Substring(closingStart, nextMethodStart - closingStart);

        // Verify dirty LDT guard is still there
        Assert.Contains("_ldtDirty", closingMethod);
        Assert.Contains("SaveWindowState()", closingMethod);
        // Verify _isClosing is still set
        Assert.Contains("_isClosing = true;", closingMethod);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DocManager.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Không tìm thấy thư mục mã nguồn DocManager.");
    }
}
