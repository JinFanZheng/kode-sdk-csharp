namespace KodaClaw.Contracts.Browser;

public sealed record DomElement(
    int Index,
    string Tag,
    string? Text = null,
    string? ClassName = null)
{
    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> Attributes { get; init; } = EmptyAttributes;
}
