using System.Text.Json;

string? outputPath = Environment.GetEnvironmentVariable("DOCMANAGER_LAUNCHER_TEST_OUTPUT");
if (string.IsNullOrWhiteSpace(outputPath))
{
    return 91;
}

var result = new
{
    arguments = Environment.GetCommandLineArgs().Skip(1).ToArray(),
    workingDirectory = Environment.CurrentDirectory,
};
File.WriteAllBytes(Path.GetFullPath(outputPath), JsonSerializer.SerializeToUtf8Bytes(result));
return 37;
