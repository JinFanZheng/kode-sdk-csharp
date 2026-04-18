namespace Kode.Agent.Sdk.Core.Agent;

/// <summary>
/// Tools whose output must be preserved verbatim because subsequent tool calls
/// reference it character-for-character (for example <c>fs_read</c> → <c>fs_edit</c>).
/// Compressors consult this policy before eliding or offloading results.
/// </summary>
/// <remarks>
/// This is a SDK-level contract: any application that exposes the built-in file
/// tools must respect it. Hosts may extend the default set via
/// <see cref="ToolResultCompressionOptions.VerbatimTools"/> but should rarely
/// remove entries — doing so risks breaking the edit-after-read contract.
/// </remarks>
public static class VerbatimToolPolicy
{
    /// <summary>
    /// Built-in file tools whose output is consumed verbatim by follow-up calls.
    /// </summary>
    public static readonly IReadOnlyCollection<string> DefaultVerbatimTools =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "fs_read",
            "fs_grep",
            "fs_list",
            "fs_glob",
            "fs_edit",
            "fs_write",
        };

    /// <summary>
    /// Returns true if <paramref name="toolName"/> must not be compressed or offloaded.
    /// </summary>
    /// <param name="toolName">Tool name (case-insensitive).</param>
    /// <param name="overrides">
    /// Optional application-level additions. When non-null, the effective set is the
    /// union of <see cref="DefaultVerbatimTools"/> and <paramref name="overrides"/>.
    /// </param>
    public static bool IsVerbatim(string toolName, IReadOnlyCollection<string>? overrides = null)
    {
        if (string.IsNullOrEmpty(toolName))
            return false;

        if (DefaultVerbatimTools.Contains(toolName))
            return true;

        if (overrides is null || overrides.Count == 0)
            return false;

        foreach (var name in overrides)
        {
            if (string.Equals(name, toolName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
