using System.Diagnostics.CodeAnalysis;

namespace KodaClaw.Contracts.Secrets;

public sealed record SecretRef
{
    public SecretRef(
        string provider,
        string scope,
        string key,
        string? displayName = null)
    {
        Provider = NormalizeRequired(provider, nameof(provider));
        Scope = NormalizeRequired(scope, nameof(scope));
        Key = NormalizeRequired(key, nameof(key));
        DisplayName = NormalizeOptional(displayName);
    }

    public string Provider { get; }

    public string Scope { get; }

    public string Key { get; }

    public string? DisplayName { get; }

    public string ToReferenceString()
    {
        return $"{Provider}:{Scope}:{Key}";
    }

    public override string ToString()
    {
        return ToReferenceString();
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out SecretRef? secretRef)
    {
        secretRef = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split(':', 3, StringSplitOptions.TrimEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]) ||
            string.IsNullOrWhiteSpace(parts[2]))
        {
            return false;
        }

        secretRef = new SecretRef(parts[0], parts[1], parts[2]);
        return true;
    }

    private static string NormalizeRequired(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value.Trim();
    }

    private static string? NormalizeOptional(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
