using KodaClaw.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KodaClaw.Contracts.Canvas;
using KodaClaw.Contracts.Diagnostics;
using KodaClaw.Contracts.System;
using KodaClaw.Contracts.Workspace;

public static partial class GatewayApp
{
    private const string CanvasPreviewUnauthorizedCode = "canvas.preview_unauthorized";
    private static readonly TimeSpan CanvasPreviewTokenLifetime = TimeSpan.FromHours(12);

    private static async Task<IResult> ServeCanvasFileAsync(
        HttpContext context,
        IConfiguration configuration,
        IWorkspaceService workspaceService,
        IDiagnosticsService diagnosticsService,
        string? path,
        CancellationToken cancellationToken)
    {
        if (!TryAuthorize(context, configuration))
        {
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.auth",
                eventType: "gateway.auth.failed",
                level: "warning",
                message: "Unauthorized access to canvas filesystem endpoint.");
            return Results.Unauthorized();
        }

        return await ServeCanvasFileCoreAsync(
            context,
            workspaceService,
            diagnosticsService,
            path,
            allowedRootPath: GetCanvasRootPath(),
            fallbackEntryPath: DefaultCanvasEntryPath,
            cancellationToken);
    }

    private static async Task<IResult> ServeCanvasPreviewAsync(
        HttpContext context,
        IConfiguration configuration,
        IWorkspaceService workspaceService,
        IDiagnosticsService diagnosticsService,
        string previewToken,
        string? path,
        CancellationToken cancellationToken)
    {
        if (!TryValidateCanvasPreviewToken(
                context,
                configuration,
                previewToken,
                out var allowedRootPath,
                out var fallbackEntryPath,
                out var validationError))
        {
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.canvas",
                eventType: "gateway.canvas.preview_unauthorized",
                level: "warning",
                message: validationError?.Message ?? "Unauthorized canvas preview request.",
                attributes: new Dictionary<string, string?>
                {
                    ["requestedPath"] = path,
                });
            return Results.Unauthorized();
        }

        return await ServeCanvasFileCoreAsync(
            context,
            workspaceService,
            diagnosticsService,
            path,
            allowedRootPath,
            fallbackEntryPath,
            cancellationToken);
    }

    private static async Task<IResult> ServeCanvasFileCoreAsync(
        HttpContext context,
        IWorkspaceService workspaceService,
        IDiagnosticsService diagnosticsService,
        string? path,
        string allowedRootPath,
        string fallbackEntryPath,
        CancellationToken cancellationToken)
    {
        await workspaceService.EnsureInitializedAsync(cancellationToken);

        if (!TryNormalizeCanvasPath(path, fallbackEntryPath, out var normalizedPath, out var validationError))
        {
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.canvas",
                eventType: "gateway.canvas.invalid_request",
                level: "warning",
                message: validationError!.Message,
                attributes: new Dictionary<string, string?>
                {
                    ["requestedPath"] = path,
                });
            return Results.BadRequest(validationError);
        }

        if (!TryNormalizeCanvasPath(allowedRootPath, out var normalizedAllowedRootPath, out validationError))
        {
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.canvas",
                eventType: "gateway.canvas.invalid_request",
                level: "warning",
                message: validationError!.Message,
                attributes: new Dictionary<string, string?>
                {
                    ["allowedRootPath"] = allowedRootPath,
                });
            return Results.BadRequest(validationError);
        }

        if (!TryNormalizeCanvasPath(fallbackEntryPath, out var normalizedFallbackEntryPath, out validationError))
        {
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.canvas",
                eventType: "gateway.canvas.invalid_request",
                level: "warning",
                message: validationError!.Message,
                attributes: new Dictionary<string, string?>
                {
                    ["fallbackEntryPath"] = fallbackEntryPath,
                });
            return Results.BadRequest(validationError);
        }

        if (!TryResolveCanvasFilePath(workspaceService.RootPath, normalizedPath, out var resolvedPath, out validationError))
        {
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.canvas",
                eventType: "gateway.canvas.invalid_request",
                level: "warning",
                message: validationError!.Message,
                attributes: new Dictionary<string, string?>
                {
                    ["requestedPath"] = normalizedPath,
                });
            return Results.BadRequest(validationError);
        }

        if (!TryResolveCanvasFilePath(
                workspaceService.RootPath,
                normalizedAllowedRootPath,
                out var resolvedAllowedRootPath,
                out validationError))
        {
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.canvas",
                eventType: "gateway.canvas.invalid_request",
                level: "warning",
                message: validationError!.Message,
                attributes: new Dictionary<string, string?>
                {
                    ["allowedRootPath"] = normalizedAllowedRootPath,
                });
            return Results.BadRequest(validationError);
        }

        if (!TryResolveCanvasFilePath(
                workspaceService.RootPath,
                normalizedFallbackEntryPath,
                out var resolvedFallbackEntryPath,
                out validationError))
        {
            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.canvas",
                eventType: "gateway.canvas.invalid_request",
                level: "warning",
                message: validationError!.Message,
                attributes: new Dictionary<string, string?>
                {
                    ["fallbackEntryPath"] = normalizedFallbackEntryPath,
                });
            return Results.BadRequest(validationError);
        }

        if (!IsPathUnderRoot(resolvedPath, resolvedAllowedRootPath))
        {
            var error = new ErrorResponse(
                Code: "validation.canvas_path_invalid",
                Message: "Canvas path must stay within the authorized preview root.");

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.canvas",
                eventType: "gateway.canvas.invalid_request",
                level: "warning",
                message: error.Message,
                attributes: new Dictionary<string, string?>
                {
                    ["requestedPath"] = normalizedPath,
                    ["allowedRootPath"] = normalizedAllowedRootPath,
                });
            return Results.BadRequest(error);
        }

        if (!IsPathUnderRoot(resolvedFallbackEntryPath, resolvedAllowedRootPath))
        {
            var error = new ErrorResponse(
                Code: "validation.canvas_path_invalid",
                Message: "Canvas fallback path must stay within the authorized preview root.");

            RecordDiagnosticEvent(
                diagnosticsService,
                context,
                source: "gateway.canvas",
                eventType: "gateway.canvas.invalid_request",
                level: "warning",
                message: error.Message,
                attributes: new Dictionary<string, string?>
                {
                    ["fallbackEntryPath"] = normalizedFallbackEntryPath,
                    ["allowedRootPath"] = normalizedAllowedRootPath,
                });
            return Results.BadRequest(error);
        }

        var usedFallback = false;
        var servedPath = resolvedPath;
        if (!File.Exists(servedPath))
        {
            if (!ShouldFallbackToCanvasEntry(normalizedPath) || !File.Exists(resolvedFallbackEntryPath))
            {
                return Results.NotFound(new ErrorResponse(
                    Code: "canvas.file_not_found",
                    Message: "Canvas file was not found."));
            }

            servedPath = resolvedFallbackEntryPath;
            usedFallback = true;
        }

        if (!File.Exists(servedPath))
        {
            return Results.NotFound(new ErrorResponse(
                Code: "canvas.file_not_found",
                Message: "Canvas file was not found."));
        }

        RecordDiagnosticEvent(
            diagnosticsService,
            context,
            source: "gateway.canvas",
            eventType: "gateway.canvas.fs_served",
            level: "info",
            message: "Served canvas filesystem asset.",
            attributes: new Dictionary<string, string?>
            {
                ["requestedPath"] = normalizedPath,
                ["servedPath"] = usedFallback ? normalizedFallbackEntryPath : normalizedPath,
                ["fallback"] = usedFallback.ToString(),
            });

        return Results.File(servedPath, ResolveCanvasContentType(servedPath));
    }

    private static bool TryNormalizeCanvasPath(
        string? rawPath,
        string defaultPath,
        out string normalizedPath,
        out ErrorResponse? error)
    {
        normalizedPath = string.IsNullOrWhiteSpace(rawPath)
            ? defaultPath
            : rawPath.Trim().Replace('\\', '/');
        error = null;

        if (normalizedPath.StartsWith("/", StringComparison.Ordinal) || Path.IsPathRooted(normalizedPath))
        {
            error = new ErrorResponse(
                Code: "validation.canvas_path_invalid",
                Message: "Canvas path must be workspace-relative under workspace/canvas.");
            return false;
        }

        var segments = normalizedPath.Split('/', StringSplitOptions.None);
        if (segments.Length < 2)
        {
            error = new ErrorResponse(
                Code: "validation.canvas_path_invalid",
                Message: "Canvas path must be under workspace/canvas.");
            return false;
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                error = new ErrorResponse(
                    Code: "validation.canvas_path_invalid",
                    Message: "Canvas path contains invalid path segments.");
                return false;
            }
        }

        var hasWorkspacePrefix =
            string.Equals(segments[0], KodaClawWorkspaceLayout.WorkspaceDirectory, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(segments[1], "canvas", StringComparison.OrdinalIgnoreCase);
        if (!hasWorkspacePrefix)
        {
            error = new ErrorResponse(
                Code: "validation.canvas_path_invalid",
                Message: "Canvas path must be under workspace/canvas.");
            return false;
        }

        normalizedPath = string.Join('/', segments);
        return true;
    }

    private static bool TryNormalizeCanvasPath(
        string? rawPath,
        out string normalizedPath,
        out ErrorResponse? error)
    {
        return TryNormalizeCanvasPath(rawPath, DefaultCanvasEntryPath, out normalizedPath, out error);
    }

    private static bool TryResolveCanvasFilePath(
        string workspaceRoot,
        string normalizedPath,
        out string resolvedPath,
        out ErrorResponse? error)
    {
        error = null;
        var segments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        resolvedPath = Path.GetFullPath(Path.Combine(workspaceRoot, Path.Combine(segments)));

        var canvasRoot = Path.GetFullPath(Path.Combine(
            workspaceRoot,
            KodaClawWorkspaceLayout.WorkspaceDirectory,
            "canvas"));
        if (!IsPathUnderRoot(resolvedPath, canvasRoot))
        {
            error = new ErrorResponse(
                Code: "validation.canvas_path_invalid",
                Message: "Canvas path must stay within workspace/canvas.");
            return false;
        }

        return true;
    }

    private static bool IsPathUnderRoot(string candidatePath, string rootPath)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var normalizedRoot = rootPath.EndsWith(Path.DirectorySeparatorChar)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(normalizedRoot, comparison) ||
               string.Equals(candidatePath, rootPath, comparison);
    }

    private static bool ShouldFallbackToCanvasEntry(string normalizedPath)
    {
        var extension = Path.GetExtension(normalizedPath);
        return string.IsNullOrEmpty(extension) ||
               string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCanvasRootPath()
    {
        return $"{KodaClawWorkspaceLayout.WorkspaceDirectory}/canvas";
    }

    private static CanvasEntryResponse BuildCanvasEntryResponse(
        HttpContext context,
        IConfiguration configuration,
        CanvasArtifact? artifact)
    {
        var entryPath = artifact?.EntryPath ?? DefaultCanvasEntryPath;
        var assetRootPath = artifact?.AssetDirectory ?? GetCanvasRootPath();

        return new CanvasEntryResponse(
            EntryUrl: BuildCanvasEntryUrl(context, configuration, entryPath, assetRootPath),
            EntryPath: entryPath,
            ArtifactId: artifact?.Id,
            Route: artifact?.Route,
            Title: artifact?.Title);
    }

    private static string BuildCanvasEntryUrl(
        HttpContext context,
        IConfiguration configuration,
        string entryPath,
        string assetRootPath)
    {
        return TryBuildCanvasPreviewUrl(
                context,
                configuration,
                entryPath,
                assetRootPath,
                out var previewUrl)
            ? previewUrl
            : BuildCanvasEntryUrl(entryPath);
    }

    private static bool TryBuildCanvasPreviewUrl(
        HttpContext context,
        IConfiguration configuration,
        string entryPath,
        string assetRootPath,
        out string previewUrl)
    {
        previewUrl = string.Empty;
        if (!TryCreateCanvasPreviewToken(
                context,
                configuration,
                assetRootPath,
                entryPath,
                out var previewToken))
        {
            return false;
        }

        previewUrl = "/api/canvas/preview/" + previewToken + "/" + EncodeCanvasPath(entryPath);
        return true;
    }

    private static bool TryCreateCanvasPreviewToken(
        HttpContext context,
        IConfiguration configuration,
        string allowedRootPath,
        string fallbackEntryPath,
        out string previewToken)
    {
        previewToken = string.Empty;

        var configuredToken = GetConfiguredGatewayToken(context, configuration);
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return false;
        }

        if (!TryNormalizeCanvasPath(allowedRootPath, out var normalizedAllowedRootPath, out _))
        {
            return false;
        }

        if (!TryNormalizeCanvasPath(fallbackEntryPath, out var normalizedFallbackEntryPath, out _))
        {
            return false;
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(new CanvasPreviewGrant(
            RootPath: normalizedAllowedRootPath,
            EntryPath: normalizedFallbackEntryPath,
            ExpiresAtUnixSeconds: DateTimeOffset.UtcNow
                .Add(CanvasPreviewTokenLifetime)
                .ToUnixTimeSeconds()));

        var encodedPayload = Base64UrlEncode(payload);
        var signature = SignCanvasPreviewPayload(encodedPayload, configuredToken);
        previewToken = encodedPayload + "." + signature;
        return true;
    }

    private static bool TryValidateCanvasPreviewToken(
        HttpContext context,
        IConfiguration configuration,
        string previewToken,
        out string allowedRootPath,
        out string fallbackEntryPath,
        out ErrorResponse? error)
    {
        allowedRootPath = string.Empty;
        fallbackEntryPath = string.Empty;
        error = null;

        var configuredToken = GetConfiguredGatewayToken(context, configuration);
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            error = new ErrorResponse(
                Code: CanvasPreviewUnauthorizedCode,
                Message: "Canvas preview is unavailable because gateway authentication is not configured.");
            return false;
        }

        var separatorIndex = previewToken.IndexOf('.', StringComparison.Ordinal);
        if (separatorIndex <= 0 || separatorIndex == previewToken.Length - 1)
        {
            error = new ErrorResponse(
                Code: CanvasPreviewUnauthorizedCode,
                Message: "Canvas preview token is invalid.");
            return false;
        }

        var encodedPayload = previewToken[..separatorIndex];
        var encodedSignature = previewToken[(separatorIndex + 1)..];
        byte[] providedSignature;
        try
        {
            providedSignature = Base64UrlDecode(encodedSignature);
        }
        catch (FormatException)
        {
            error = new ErrorResponse(
                Code: CanvasPreviewUnauthorizedCode,
                Message: "Canvas preview token is invalid.");
            return false;
        }

        var expectedSignature = SignCanvasPreviewPayloadBytes(encodedPayload, configuredToken);
        if (!CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
        {
            error = new ErrorResponse(
                Code: CanvasPreviewUnauthorizedCode,
                Message: "Canvas preview token is invalid.");
            return false;
        }

        CanvasPreviewGrant? grant;
        try
        {
            var payload = Base64UrlDecode(encodedPayload);
            grant = JsonSerializer.Deserialize<CanvasPreviewGrant>(payload);
        }
        catch (Exception) when (error is null)
        {
            grant = null;
        }

        if (grant is null)
        {
            error = new ErrorResponse(
                Code: CanvasPreviewUnauthorizedCode,
                Message: "Canvas preview token is invalid.");
            return false;
        }

        if (grant.ExpiresAtUnixSeconds <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
        {
            error = new ErrorResponse(
                Code: CanvasPreviewUnauthorizedCode,
                Message: "Canvas preview token has expired.");
            return false;
        }

        if (!TryNormalizeCanvasPath(grant.RootPath, out allowedRootPath, out error))
        {
            error = new ErrorResponse(
                Code: CanvasPreviewUnauthorizedCode,
                Message: "Canvas preview token is invalid.");
            return false;
        }

        if (!TryNormalizeCanvasPath(grant.EntryPath, out fallbackEntryPath, out error))
        {
            error = new ErrorResponse(
                Code: CanvasPreviewUnauthorizedCode,
                Message: "Canvas preview token is invalid.");
            return false;
        }

        return true;
    }

    private static string SignCanvasPreviewPayload(string encodedPayload, string configuredToken)
    {
        return Base64UrlEncode(SignCanvasPreviewPayloadBytes(encodedPayload, configuredToken));
    }

    private static byte[] SignCanvasPreviewPayloadBytes(string encodedPayload, string configuredToken)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(configuredToken));
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(encodedPayload));
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value
            .Replace('-', '+')
            .Replace('_', '/');

        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => throw new FormatException("Invalid base64url payload.")
        };

        return Convert.FromBase64String(padded);
    }

    private static string EncodeCanvasPath(string path)
    {
        return string.Join(
            '/',
            path.Trim()
                .Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
    }

    private static string BuildCanvasEntryUrl(string entryPath)
    {
        return "/api/canvas/fs/" + EncodeCanvasPath(entryPath);
    }

    private static string ResolveCanvasContentType(string filePath)
    {
        return CanvasContentTypeProvider.TryGetContentType(filePath, out var contentType)
            ? contentType
            : "text/plain; charset=utf-8";
    }

    private static FileExtensionContentTypeProvider CreateCanvasContentTypeProvider()
    {
        var provider = new FileExtensionContentTypeProvider();
        provider.Mappings[".html"] = "text/html; charset=utf-8";
        provider.Mappings[".css"] = "text/css; charset=utf-8";
        provider.Mappings[".js"] = "application/javascript; charset=utf-8";
        provider.Mappings[".json"] = "application/json; charset=utf-8";
        provider.Mappings[".txt"] = "text/plain; charset=utf-8";
        return provider;
    }

    private sealed record CanvasPreviewGrant(
        string RootPath,
        string EntryPath,
        long ExpiresAtUnixSeconds);
}
