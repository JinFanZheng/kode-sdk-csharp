using KodaClaw.BrowserHub;
using KodaClaw.BrowserHub.Models;
using KodaClaw.BrowserHub.Connection;
using KodaClaw.BrowserHub.Device;
using KodaClaw.BrowserHub.Screenshot;
using KodaClaw.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text.Json;
using KodaClaw.Contracts.Browser;
using KodaClaw.Contracts.Diagnostics;

public static partial class GatewayApp
{
    private static void MapBrowserEndpoints(WebApplication app)
    {
        var browser = app.MapGroup("/api/browser");

        browser.MapGet("/status", async (
            HttpContext context,
            IConfiguration configuration,
            IBrowserHubService browserHubService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var status = await browserHubService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(status);
        });

        browser.MapGet("/devices", async (
            HttpContext context,
            IConfiguration configuration,
            IBrowserHubService browserHubService,
            IDiagnosticsService diagnosticsService,
            CancellationToken cancellationToken) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var devices = await browserHubService.GetConnectedDevicesAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(devices);
        });

        browser.MapPost("/screenshot/upload", async (
            HttpContext context,
            ScreenshotUploadService screenshotUploadService,
            CancellationToken cancellationToken) =>
        {
            var authHeader = context.Request.Headers.Authorization.ToString();
            const string bearerPrefix = "Bearer ";
            if (!authHeader.StartsWith(bearerPrefix, StringComparison.OrdinalIgnoreCase))
                return Results.Unauthorized();

            var token = authHeader[bearerPrefix.Length..].Trim();
            var filePath = screenshotUploadService.ValidateAndConsume(token);
            if (filePath is null)
                return Results.Unauthorized();

            await using var fileStream = File.Create(filePath);
            await context.Request.Body.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);

            return Results.Ok(new { fileId = Path.GetFileNameWithoutExtension(filePath) });
        });

        browser.MapGet("/screenshot/{fileId}", (
            string fileId,
            HttpContext context,
            IConfiguration configuration,
            ScreenshotUploadService screenshotUploadService,
            IDiagnosticsService diagnosticsService) =>
        {
            if (!TryAuthorize(context, configuration, diagnosticsService))
                return Results.Unauthorized();

            var filePath = screenshotUploadService.GetFilePath(fileId);
            if (filePath is null)
                return Results.NotFound();

            var contentType = Path.GetExtension(filePath).Equals(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png"
                : "image/jpeg";

            return Results.File(filePath, contentType);
        });

        browser.MapGet("/pairing/token", (
            DevicePairingService pairingService) =>
        {
            var (tokenValue, _) = pairingService.GeneratePairingToken();
            return Results.Ok(new { token = tokenValue, expiresIn = 300 });
        });

        browser.MapPost("/pairing/challenge", async (
            HttpContext context,
            DevicePairingService pairingService) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var bodyJson = await reader.ReadToEndAsync();
            string? token = null;
            if (!string.IsNullOrWhiteSpace(bodyJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(bodyJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("token", out var t) || root.TryGetProperty("Token", out t))
                        token = t.GetString();
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "Invalid JSON body" });
                }
            }

            if (string.IsNullOrWhiteSpace(token))
                return Results.BadRequest(new { error = "Token is required" });

            try
            {
                var data = pairingService.GetPairingChallenge(token);
                return Results.Ok(new
                {
                    challenge = data.Challenge,
                    gwEcdsaPubKey = data.GwEcdsaPubKey,
                    gwEcdhPubKey = data.GwEcdhPubKey,
                    gwSignature = data.GwSignature,
                });
            }
            catch (InvalidOperationException)
            {
                return Results.NotFound(new { error = "Invalid or expired pairing token" });
            }
            catch (Exception ex)
            {
                return Results.Problem(detail: ex.ToString(), statusCode: 500);
            }
        });

        browser.MapPost("/pairing/complete", async (
            HttpContext context,
            DevicePairingService pairingService,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var bodyJson = await reader.ReadToEndAsync();
            string? token = null;
            string? extEcdsaPubKey = null;
            string? extSignature = null;
            string? extEcdhPubKey = null;
            string? deviceId = null;
            string? label = null;
            if (!string.IsNullOrWhiteSpace(bodyJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(bodyJson);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("token", out var t) || root.TryGetProperty("Token", out t))
                        token = t.GetString();
                    if (root.TryGetProperty("extEcdsaPubKey", out var v) || root.TryGetProperty("ExtEcdsaPubKey", out v))
                        extEcdsaPubKey = v.GetString();
                    if (root.TryGetProperty("extSignature", out v) || root.TryGetProperty("ExtSignature", out v))
                        extSignature = v.GetString();
                    if (root.TryGetProperty("extEcdhPubKey", out v) || root.TryGetProperty("ExtEcdhPubKey", out v))
                        extEcdhPubKey = v.GetString();
                    if (root.TryGetProperty("deviceId", out v) || root.TryGetProperty("DeviceId", out v))
                        deviceId = v.GetString();
                    if (root.TryGetProperty("label", out v) || root.TryGetProperty("Label", out v))
                        label = v.GetString();
                }
                catch (JsonException)
                {
                    return Results.BadRequest(new { error = "Invalid JSON body" });
                }
            }

            if (string.IsNullOrWhiteSpace(token))
                return Results.BadRequest(new { error = "Token is required" });

            try
            {
                var result = await pairingService.CompletePairingAsync(
                    pairingToken: token,
                    extensionPublicKey: extEcdsaPubKey!,
                    challengeSignature: extSignature!,
                    extensionEcdhPublicKey: extEcdhPubKey!,
                    deviceId: deviceId!,
                    label: label,
                    cancellationToken: cancellationToken).ConfigureAwait(false);

                return Results.Ok(new { sessionToken = result.SessionToken });
            }
            catch (Exception ex) when (ex is InvalidOperationException or CryptographicException or ArgumentException)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });


        // TEMPORARY: Test endpoint for browser actions (will be removed after verification)
        browser.MapPost("/test/action", async (
            HttpContext context,
            IBrowserHubService browserHubService,
            BridgeConnectionManager connectionManager,
            CancellationToken cancellationToken) =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
                return Results.BadRequest(new { error = "Empty body" });

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            var action = root.GetProperty("action").GetString()!;
            var tabId = root.TryGetProperty("tabId", out var tid) ? tid.GetString() : null;
            var requestedDeviceId = root.TryGetProperty("deviceId", out var did) ? did.GetString() : null;
            var framePath = root.TryGetProperty("framePath", out var framePathElement) && framePathElement.ValueKind == JsonValueKind.Array
                ? framePathElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item!.Trim())
                    .ToArray()
                : root.TryGetProperty("frameSelector", out var frameSelectorElement) && frameSelectorElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(frameSelectorElement.GetString())
                    ? [frameSelectorElement.GetString()!.Trim()]
                    : null;
            var deviceIds = connectionManager.ConnectedDeviceIds;
            if (deviceIds.Count == 0)
                return Results.Ok(new { ok = false, error = "No connected devices" });
            var deviceId = !string.IsNullOrWhiteSpace(requestedDeviceId) ? requestedDeviceId : deviceIds.First();

            try
            {
                object? result = action switch
                {
                    "list_tabs" => await browserHubService.ListTabsAsync(cancellationToken),
                    "navigate" => await browserHubService.NavigateAsync(root.GetProperty("url").GetString()!, tabId, deviceId, cancellationToken),
                    "get_url" => await browserHubService.GetUrlAsync(tabId!, deviceId, cancellationToken),
                    "snapshot" => await browserHubService.SnapshotAsync(tabId!, null, framePath, deviceId, cancellationToken),
                    "screenshot" => await browserHubService.ScreenshotAsync(tabId!, ScreenshotFormat.Jpeg, 80, deviceId, cancellationToken),
                    "click" => await browserHubService.ClickAsync(deviceId, tabId, root.GetProperty("elementIndex").GetInt32(), framePath, null, null, cancellationToken),
                    "type" => await browserHubService.TypeAsync(deviceId, tabId, root.GetProperty("elementIndex").GetInt32(), root.GetProperty("text").GetString()!, framePath, false, 0, cancellationToken),
                    "scroll" => await browserHubService.ScrollAsync(deviceId, tabId, Enum.Parse<ScrollDirection>(root.GetProperty("direction").GetString()!, ignoreCase: true), null, null, framePath, cancellationToken),
                    "key_press" => await browserHubService.KeyPressAsync(deviceId, tabId, root.GetProperty("key").GetString()!, cancellationToken),
                    "go_back" => await browserHubService.GoBackAsync(deviceId, tabId, cancellationToken),
                    "close_tab" => await browserHubService.CloseTabAsync(deviceId, tabId!, cancellationToken),
                    "switch_tab" => await browserHubService.SwitchTabAsync(deviceId, tabId!, cancellationToken),
                    "evaluate" => await browserHubService.EvaluateAsync(deviceId, tabId, root.GetProperty("script").GetString()!, true, cancellationToken),
                    "evaluate_dom" => await browserHubService.EvaluateDomAsync(deviceId, tabId, root.GetProperty("script").GetString()!, framePath, cancellationToken),
                    "extract_links" => await browserHubService.ExtractLinksAsync(
                        deviceId,
                        tabId,
                        root.TryGetProperty("selector", out var linkSelectorRoot) ? linkSelectorRoot.GetString() : null,
                        root.TryGetProperty("linkSelector", out var linkSelector) ? linkSelector.GetString() : null,
                        root.TryGetProperty("limit", out var linkLimit) ? linkLimit.GetInt32() : 20,
                        root.TryGetProperty("sameOriginOnly", out var sameOriginLinks) && sameOriginLinks.GetBoolean(),
                        framePath,
                        cancellationToken),
                    "extract_results" => await browserHubService.ExtractResultsAsync(
                        deviceId,
                        tabId,
                        root.TryGetProperty("selector", out var resultSelector) ? resultSelector.GetString() : null,
                        root.TryGetProperty("itemSelector", out var itemSelector) ? itemSelector.GetString() : null,
                        root.TryGetProperty("titleSelector", out var titleSelector) ? titleSelector.GetString() : null,
                        root.TryGetProperty("linkSelector", out var resultLinkSelector) ? resultLinkSelector.GetString() : null,
                        root.TryGetProperty("snippetSelector", out var snippetSelector) ? snippetSelector.GetString() : null,
                        root.TryGetProperty("strategy", out var strategy) ? strategy.GetString() : null,
                        root.TryGetProperty("limit", out var resultLimit) ? resultLimit.GetInt32() : 10,
                        root.TryGetProperty("sameOriginOnly", out var sameOriginResults) && sameOriginResults.GetBoolean(),
                        framePath,
                        cancellationToken),
                    "evaluate_write" => await browserHubService.EvaluateWriteAsync(deviceId, tabId, root.GetProperty("script").GetString()!, framePath, cancellationToken),
                    "cookies" => await browserHubService.GetCookiesAsync(deviceId, tabId, null, cancellationToken),
                    "form_state" => await browserHubService.GetFormStateAsync(deviceId, tabId, framePath, cancellationToken),
                    "console" => await browserHubService.GetConsoleMessagesAsync(deviceId, tabId, null, cancellationToken),
                    "upload_file" => await browserHubService.UploadFileAsync(deviceId, tabId, root.GetProperty("elementIndex").GetInt32(), root.GetProperty("filePath").GetString()!, framePath, cancellationToken),
                    "wait" => await browserHubService.WaitAsync(
                        deviceId,
                        tabId,
                        root.TryGetProperty("durationMs", out var durationMs) ? durationMs.GetInt32() : null,
                        root.TryGetProperty("timeoutMs", out var timeoutMs) ? timeoutMs.GetInt32() : null,
                        root.TryGetProperty("waitForSelector", out var waitForSelector) ? waitForSelector.GetString() : null,
                        root.TryGetProperty("waitUntil", out var waitUntil) ? waitUntil.GetString() : null,
                        framePath,
                        cancellationToken),
                    "intercept" => await browserHubService.StartInterceptAsync(deviceId, tabId, null, null, false, false, cancellationToken),
                    "intercept_clear" => await browserHubService.StopInterceptAsync(deviceId, tabId, cancellationToken),
                    "intercept_result" => await browserHubService.GetInterceptedRequestsAsync(deviceId, tabId, null, null, null, 50, cancellationToken),
                    _ => null
                };

                if (result is null)
                    return Results.BadRequest(new { error = $"Unknown action: {action}" });

                var okProp = result.GetType().GetProperty("Ok");
                var dataProp = result.GetType().GetProperty("Data");
                var errorProp = result.GetType().GetProperty("Error");
                return Results.Ok(new
                {
                    ok = okProp?.GetValue(result),
                    data = dataProp?.GetValue(result),
                    error = errorProp?.GetValue(result), errorCode = result.GetType().GetProperty("ErrorCode")?.GetValue(result)
                });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, error = ex.Message, errorCode = "TEST_ERROR" });
            }
        });


        app.Map("/ws/bridge", async (
            HttpContext context,
            BridgeConnectionManager connectionManager,
            BridgeConnectionHandler _) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var deviceId = context.Request.Query["deviceId"].FirstOrDefault()
                ?? context.Request.Headers["X-Device-Id"].FirstOrDefault()
                ?? Guid.NewGuid().ToString("N");

            var subprotocol = context.WebSockets.WebSocketRequestedProtocols.FirstOrDefault();
            var webSocket = await context.WebSockets.AcceptWebSocketAsync(subprotocol).ConfigureAwait(false);
            await connectionManager.RegisterConnectionAsync(deviceId, webSocket, context.RequestAborted).ConfigureAwait(false);
            await connectionManager.RunReceiveLoopAsync(deviceId, webSocket, context.RequestAborted).ConfigureAwait(false);
        });

    }

    private static IResult ToBrowserOperationResult<T>(BrowserResult<T> result)
    {
        if (result.Ok)
        {
            return Results.Ok(result.Data);
        }

        var (statusCode, category, retryable) = MapBrowserError(result.ErrorCode);
        return Results.Json(
            new
            {
                ok = false,
                error = result.Error,
                errorCode = result.ErrorCode,
                category,
                retryable,
            },
            statusCode: statusCode);
    }

    private static (int StatusCode, string Category, bool Retryable) MapBrowserError(string? errorCode)
        => errorCode switch
        {
            "BRIDGE_002" => (StatusCodes.Status404NotFound, "tab_not_found", true),
            "BRIDGE_001" => (StatusCodes.Status409Conflict, "device_unavailable", true),
            "BRIDGE_004" => (StatusCodes.Status408RequestTimeout, "timeout", true),
            "BRIDGE_007" => (StatusCodes.Status502BadGateway, "bridge_send_failed", true),
            _ => (StatusCodes.Status400BadRequest, "browser_operation_failed", false),
        };
}

internal sealed record PairingChallengeRequest(string Token);

internal sealed record PairingCompleteRequest(
    string Token,
    string ExtEcdsaPubKey,
    string ExtSignature,
    string ExtEcdhPubKey,
    string DeviceId,
    string? Label = null);

