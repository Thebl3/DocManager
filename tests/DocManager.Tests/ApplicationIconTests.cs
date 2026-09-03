namespace DocManager.Tests;

public class ApplicationIconTests
{
    [Fact]
    public void Icon_file_exists_and_is_valid()
    {
        var root = RepositoryRoot();
        var iconPath = Path.Combine(root, "src", "DocManager.Desktop", "Assets", "app.ico");

        Assert.True(File.Exists(iconPath), "Icon file must exist at Assets/app.ico");

        var fileInfo = new FileInfo(iconPath);
        Assert.Equal(30531, fileInfo.Length);

        // Verify it's a valid .ico file by checking magic header
        var header = new byte[4];
        using (var fs = File.OpenRead(iconPath))
        {
            fs.Read(header, 0, 4);
        }

        // .ico magic header is: 0x00 0x00 0x01 0x00
        Assert.Equal(0x00, header[0]);
        Assert.Equal(0x00, header[1]);
        Assert.Equal(0x01, header[2]);
        Assert.Equal(0x00, header[3]);
    }

    [Fact]
    public void Csproj_contains_ApplicationIcon_and_Resource_include()
    {
        var root = RepositoryRoot();
        var csproj = File.ReadAllText(Path.Combine(root, "src", "DocManager.Desktop", "DocManager.Desktop.csproj"));

        Assert.Contains("<ApplicationIcon>Assets\\app.ico</ApplicationIcon>", csproj);
        Assert.Contains("<Resource Include=\"Assets\\app.ico\" />", csproj);
    }

    [Fact]
    public void MainWindow_xaml_has_Icon_attribute()
    {
        var root = RepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "DocManager.Desktop", "MainWindow.xaml"));

        Assert.Contains("Icon=\"pack://application:,,,/Assets/app.ico\"", xaml);
    }

    [Fact]
    public void Launcher_csproj_has_ApplicationIcon()
    {
        var root = RepositoryRoot();
        var csproj = File.ReadAllText(Path.Combine(root, "tools", "DocManager.Launcher", "DocManager.Launcher.csproj"));

        Assert.Contains("<ApplicationIcon>", csproj);
        Assert.Contains("app.ico", csproj);
    }

    [Fact]
    public void GetApplicationIcon_contains_pack_uri_fallback()
    {
        var root = RepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DocManager.Desktop", "MainWindow.xaml.cs"));

        Assert.Contains("pack://application:,,,/Assets/app.ico", codeBehind);
        Assert.Contains("System.Windows.Application.GetResourceStream", codeBehind);
    }

    [Fact]
    public void GetApplicationIcon_contains_extract_associated_icon_fallback()
    {
        var root = RepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DocManager.Desktop", "MainWindow.xaml.cs"));

        // Extract GetApplicationIcon method
        var methodStart = codeBehind.IndexOf("private static System.Drawing.Icon GetApplicationIcon()", StringComparison.Ordinal);
        var nextMethodStart = codeBehind.IndexOf("private ", methodStart + 1);
        var method = codeBehind.Substring(methodStart, nextMethodStart - methodStart);

        Assert.Contains("System.Drawing.Icon.ExtractAssociatedIcon", method);
        Assert.Contains("Environment.ProcessPath", method);
    }

    [Fact]
    public void GetApplicationIcon_contains_system_icons_fallback()
    {
        var root = RepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DocManager.Desktop", "MainWindow.xaml.cs"));

        // Extract GetApplicationIcon method
        var methodStart = codeBehind.IndexOf("private static System.Drawing.Icon GetApplicationIcon()", StringComparison.Ordinal);
        var nextMethodStart = codeBehind.IndexOf("private ", methodStart + 1);
        var method = codeBehind.Substring(methodStart, nextMethodStart - methodStart);

        Assert.Contains("System.Drawing.SystemIcons.Application", method);
    }

    [Fact]
    public void GetApplicationIcon_uses_small_icon_size_for_tray()
    {
        var root = RepositoryRoot();
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "DocManager.Desktop", "MainWindow.xaml.cs"));

        // Extract GetApplicationIcon method
        var methodStart = codeBehind.IndexOf("private static System.Drawing.Icon GetApplicationIcon()", StringComparison.Ordinal);
        var nextMethodStart = codeBehind.IndexOf("private ", methodStart + 1);
        var method = codeBehind.Substring(methodStart, nextMethodStart - methodStart);

        Assert.Contains("System.Windows.Forms.SystemInformation.SmallIconSize", method);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "DocManager.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Không tìm thấy thư mục mã nguồn DocManager.");
    }
}
