namespace KodaClaw.ChannelHub.Common;

/// <summary>
/// Shared text utilities for channel-hub previews. Consolidates the previously
/// duplicated <c>BuildPreview</c> helpers across Delivery/Ingestion/Turn services.
/// </summary>
internal static class ChannelTextExtensions
{
    public const int DefaultPreviewMaxLength = 96;

    /// <summary>
    /// Trim <paramref name="text"/>, optionally collapse newlines into spaces, and
    /// truncate to <paramref name="maxLength"/> with an ellipsis suffix.
    /// </summary>
    /// <param name="text">The source text.</param>
    /// <param name="maxLength">Maximum kept characters before truncation.</param>
    /// <param name="collapseNewlines">
    /// When true, applies <c>ReplaceLineEndings(" ")</c> so multi-line drafts become
    /// single-line previews — used by approval/ingestion paths.
    /// </param>
    public static string Preview(string text, int maxLength = DefaultPreviewMaxLength, bool collapseNewlines = false)
    {
        var normalized = text.Trim();
        if (collapseNewlines)
        {
            normalized = normalized.ReplaceLineEndings(" ");
        }

        return normalized.Length <= maxLength
            ? normalized
            : $"{normalized[..maxLength]}...";
    }
}
