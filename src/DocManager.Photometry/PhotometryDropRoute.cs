namespace DocManager.Photometry;

public enum PhotometryDropKind
{
    Invalid,
    Ldt,
    Ies
}

public sealed record PhotometryDropRoute(
    PhotometryDropKind Kind,
    string? DocumentPath,
    string? CataloguePdfPath,
    string Message)
{
    public bool IsValid => Kind != PhotometryDropKind.Invalid;

    public static PhotometryDropRoute Resolve(IEnumerable<string>? paths)
    {
        string[] files;
        try
        {
            files = (paths ?? [])
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return Invalid("Chỉ chấp nhận tệp hiện có.");
        }
        if (files.Length == 0) return Invalid("Không có tệp được thả.");
        if (files.Any(path => !File.Exists(path))) return Invalid("Chỉ chấp nhận tệp hiện có.");

        var ies = files.Where(path => Extension(path, ".ies")).ToArray();
        var ldt = files.Where(path => Extension(path, ".ldt")).ToArray();
        var pdf = files.Where(path => Extension(path, ".pdf")).ToArray();
        if (files.Length != ies.Length + ldt.Length + pdf.Length)
            return Invalid("Chỉ chấp nhận .ies, .ldt và một catalogue .pdf đi kèm IES.");
        if (ies.Length == 1 && ldt.Length == 0 && pdf.Length <= 1)
            return new(PhotometryDropKind.Ies, ies[0], pdf.SingleOrDefault(), string.Empty);
        if (ldt.Length == 1 && ies.Length == 0 && pdf.Length == 0 && files.Length == 1)
            return new(PhotometryDropKind.Ldt, ldt[0], null, string.Empty);
        return Invalid("Chỉ thả một LDT, hoặc một IES kèm tối đa một PDF.");
    }

    private static bool Extension(string path, string extension) =>
        Path.GetExtension(path).Equals(extension, StringComparison.OrdinalIgnoreCase);

    private static PhotometryDropRoute Invalid(string message) => new(PhotometryDropKind.Invalid, null, null, message);
}
