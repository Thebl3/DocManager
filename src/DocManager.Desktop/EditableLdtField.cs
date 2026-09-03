using DocManager.Photometry;

namespace DocManager.Desktop;

public sealed class EditableLdtField : IPhotometryViewRow
{
    public int Index { get; init; }
    public string Section { get; init; } = "LDT";
    public string Name { get; init; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
