using System.Text.Json;
using System.Linq;
using FluentAssertions;
using KodaClaw.BrowserHub;
using KodaClaw.BrowserHub.Models;
using KodaClaw.BrowserHub.Tools;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Browser;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Tools;
using Moq;
using Xunit;

namespace KodaClaw.UnitTests.Browser;

public sealed class BrowserActionToolTests
{
    private readonly Mock<IBrowserHubService> _serviceMock = new();
    private readonly BrowserActionTool _tool;

    public BrowserActionToolTests()
    {
        _tool = new BrowserActionTool(_serviceMock.Object);
    }

    [Fact]
    public async Task Execute_list_tabs_returns_follow_up_note_with_tabs()
    {
        _serviceMock
            .Setup(s => s.ListTabsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserResult<IReadOnlyList<TabInfo>>(
                Ok: true,
                Data:
                [
                    new TabInfo("11", "https://a.example", "A", true, "device-a"),
                    new TabInfo("22", "https://b.example", "B", false, "device-b"),
                ]));

        var result = await ExecuteAsync(new BrowserActionArgs { Action = "list_tabs" });

        result.Success.Should().BeTrue();
        var json = ToJson(result.Value);
        json.GetProperty("note").GetString().Should().Contain("deviceId");
        json.GetProperty("tabs").GetArrayLength().Should().Be(2);
        json.GetProperty("tabs")[0].GetProperty("deviceId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Execute_navigate_returns_reuse_guidance_for_tab_and_device()
    {
        ArrangeSingleConnectedDevice();
        _serviceMock
            .Setup(s => s.NavigateAsync("https://example.com", null, "device-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserResult<NavigateResult>(
                Ok: true,
                Data: new NavigateResult("https://example.com", "tab-7", "device-1")));

        var result = await ExecuteAsync(new BrowserActionArgs
        {
            Action = "navigate",
            DeviceId = "device-1",
            Params = new Dictionary<string, object?> { ["url"] = "https://example.com" },
        });

        result.Success.Should().BeTrue();
        var json = ToJson(result.Value);
        json.GetProperty("tabId").GetString().Should().Be("tab-7");
        json.GetProperty("deviceId").GetString().Should().Be("device-1");
        json.GetProperty("note").GetString().Should().Contain("Reuse this `tabId`");
    }

    [Fact]
    public async Task Execute_screenshot_returns_reference_instead_of_inline_payload()
    {
        ArrangeSingleConnectedDevice();
        _serviceMock
            .Setup(s => s.ScreenshotAsync("tab-9", ScreenshotFormat.Jpeg, 80, "device-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserResult<string>(
                Ok: true,
                Data: "/api/browser/screenshot/file-123"));

        var result = await ExecuteAsync(new BrowserActionArgs
        {
            Action = "screenshot",
            TabId = "tab-9",
            DeviceId = "device-1",
        });

        result.Success.Should().BeTrue();
        var json = ToJson(result.Value);
        json.GetProperty("stored").GetBoolean().Should().BeTrue();
        json.GetProperty("path").GetString().Should().Be("/api/browser/screenshot/file-123");
        json.GetProperty("note").GetString().Should().Contain("instead of requesting inline base64");
    }

    [Fact]
    public async Task Execute_snapshot_truncates_large_output_and_suggests_selector_retry_template()
    {
        ArrangeSingleConnectedDevice();
        var largeHtml = "<html>" + new string('x', 20_000) + "</html>";
        _serviceMock
            .Setup(s => s.SnapshotAsync("tab-42", null, null, "device-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserResult<string>(Ok: true, Data: largeHtml));

        var result = await ExecuteAsync(new BrowserActionArgs
        {
            Action = "snapshot",
            TabId = "tab-42",
            DeviceId = "device-1",
        });

        result.Success.Should().BeTrue();
        var json = ToJson(result.Value);
        json.GetProperty("truncated").GetBoolean().Should().BeTrue();
        json.GetProperty("originalChars").GetInt32().Should().BeGreaterThan(12_000);
        json.GetProperty("note").GetString().Should().Contain("params.selector").And.Contain("evaluate_dom");
    }

    [Fact]
    public async Task Execute_snapshot_parses_structured_snapshot_json_into_object_result()
    {
        ArrangeSingleConnectedDevice();
        const string snapshotJson = """
            {
              "kind": "dom_snapshot",
              "url": "https://example.com/search",
              "title": "Example Search",
              "elements": [
                { "index": 0, "tag": "a", "label": "First result", "href": "https://example.com/r1" }
              ],
              "text": "Search results",
              "elementCount": 1,
              "totalInteractiveElements": 1,
              "scope": "document"
            }
            """;

        _serviceMock
            .Setup(s => s.SnapshotAsync("tab-42", null, null, "device-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserResult<string>(Ok: true, Data: snapshotJson));

        var result = await ExecuteAsync(new BrowserActionArgs
        {
            Action = "snapshot",
            TabId = "tab-42",
            DeviceId = "device-1",
        });

        result.Success.Should().BeTrue();
        var json = ToJson(result.Value);
        json.GetProperty("kind").GetString().Should().Be("dom_snapshot");
        json.GetProperty("elements").GetArrayLength().Should().Be(1);
        json.GetProperty("elements")[0].GetProperty("label").GetString().Should().Be("First result");
    }

    [Fact]
    public async Task Execute_requires_device_id_when_multiple_devices_are_connected()
    {
        _serviceMock
            .Setup(s => s.GetConnectedDevicesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new BrowserDevice("device-a", "A", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow, true),
                new BrowserDevice("device-b", "B", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow, true),
            ]);

        var result = await ExecuteAsync(new BrowserActionArgs
        {
            Action = "get_url",
            TabId = "tab-1",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("First call `list_tabs`").And.Contain("deviceId");
    }

    [Fact]
    public async Task Execute_adds_retry_guidance_for_timeout_failures()
    {
        ArrangeSingleConnectedDevice();
        _serviceMock
            .Setup(s => s.SnapshotAsync("tab-5", null, null, "device-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserResult<string>(
                Ok: false,
                Data: null,
                Error: "Request timed out.",
                ErrorCode: "BRIDGE_004"));

        var result = await ExecuteAsync(new BrowserActionArgs
        {
            Action = "snapshot",
            TabId = "tab-5",
            DeviceId = "device-1",
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Retry once").And.Contain("params.selector");
    }

    [Fact]
    public async Task Execute_evaluate_dom_routes_live_dom_read_scripts()
    {
        ArrangeSingleConnectedDevice();
        _serviceMock
            .Setup(s => s.EvaluateDomAsync("device-1", "tab-9", "Array.from(document.links).length", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserResult<object?>(Ok: true, Data: 12));

        var result = await ExecuteAsync(new BrowserActionArgs
        {
            Action = "evaluate_dom",
            TabId = "tab-9",
            DeviceId = "device-1",
            Params = new Dictionary<string, object?> { ["script"] = "Array.from(document.links).length" },
        });

        result.Success.Should().BeTrue();
        ToJson(result.Value).GetInt32().Should().Be(12);
    }

    [Fact]
    public async Task Execute_extract_results_adds_wait_guidance_for_navigation_in_progress_errors()
    {
        ArrangeSingleConnectedDevice();
        _serviceMock
            .Setup(s => s.ExtractResultsAsync("device-1", "tab-4", null, null, null, null, null, "auto", 10, false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserResult<ResultExtractionResult>(
                Ok: false,
                Data: null,
                Error: "Execution context was destroyed.",
                ErrorCode: "BRIDGE_008"));

        var result = await ExecuteAsync(new BrowserActionArgs
        {
            Action = "extract_results",
            TabId = "tab-4",
            DeviceId = "device-1",
            Params = new Dictionary<string, object?>
            {
                ["strategy"] = "auto",
                ["limit"] = 10,
            },
        });

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("Call `wait`").And.Contain("retry");
    }

    [Fact]
    public async Task Execute_extract_links_routes_high_level_link_extraction()
    {
        ArrangeSingleConnectedDevice();
        _serviceMock
            .Setup(s => s.ExtractLinksAsync("device-1", "tab-3", "main", "a[href]", 5, false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserResult<LinkExtractionResult>(
                Ok: true,
                Data: new LinkExtractionResult(
                    Kind: "link_extract",
                    Url: "https://example.com",
                    Title: "Example",
                    Selector: "main",
                    LinkSelector: "a[href]",
                    TotalMatches: 2,
                    ReturnedCount: 2,
                    SameOriginOnly: false,
                    Links:
                    [
                        new ExtractedLink("Docs", "https://example.com/docs", null, "example.com", true),
                        new ExtractedLink("GitHub", "https://github.com/example", null, "github.com", false),
                    ],
                    Note: null)));

        var result = await ExecuteAsync(new BrowserActionArgs
        {
            Action = "extract_links",
            TabId = "tab-3",
            DeviceId = "device-1",
            Params = new Dictionary<string, object?>
            {
                ["selector"] = "main",
                ["linkSelector"] = "a[href]",
                ["limit"] = 5,
            },
        });

        result.Success.Should().BeTrue();
        var json = ToJson(result.Value);
        json.GetProperty("kind").GetString().Should().Be("link_extract");
        json.GetProperty("links").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Execute_extract_results_routes_high_level_result_extraction()
    {
        ArrangeSingleConnectedDevice();
        _serviceMock
            .Setup(s => s.ExtractResultsAsync("device-1", "tab-4", null, null, null, null, null, "auto", 10, false, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserResult<ResultExtractionResult>(
                Ok: true,
                Data: new ResultExtractionResult(
                    Kind: "result_extract",
                    Url: "https://google.com/search?q=openclaw",
                    Title: "openclaw - Google Search",
                    Strategy: "google",
                    Selector: "#search",
                    ItemSelector: "div.g",
                    TotalMatches: 1,
                    ReturnedCount: 1,
                    SameOriginOnly: false,
                    Results:
                    [
                        new ExtractedResultItem(0, "OpenClaw", "https://github.com/openclaw", "OpenClaw on GitHub", "github.com"),
                    ],
                    Note: null)));

        var result = await ExecuteAsync(new BrowserActionArgs
        {
            Action = "extract_results",
            TabId = "tab-4",
            DeviceId = "device-1",
            Params = new Dictionary<string, object?>
            {
                ["strategy"] = "auto",
                ["limit"] = 10,
            },
        });

        result.Success.Should().BeTrue();
        var json = ToJson(result.Value);
        json.GetProperty("kind").GetString().Should().Be("result_extract");
        json.GetProperty("results")[0].GetProperty("title").GetString().Should().Be("OpenClaw");
    }

    [Fact]
    public async Task Execute_snapshot_routes_same_origin_frame_selector_as_frame_path()
    {
        ArrangeSingleConnectedDevice();
        _serviceMock
            .Setup(s => s.SnapshotAsync(
                "tab-55",
                "main",
                It.Is<IReadOnlyList<string>?>(path => path != null && path.SequenceEqual(new[] { "iframe[name='content']" })),
                "device-1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BrowserResult<string>(Ok: true, Data: "{\"kind\":\"dom_snapshot\",\"scope\":\"main\"}"));

        var result = await ExecuteAsync(new BrowserActionArgs
        {
            Action = "snapshot",
            TabId = "tab-55",
            DeviceId = "device-1",
            Params = new Dictionary<string, object?>
            {
                ["selector"] = "main",
                ["frameSelector"] = "iframe[name='content']",
            },
        });

        result.Success.Should().BeTrue();
        ToJson(result.Value).GetProperty("kind").GetString().Should().Be("dom_snapshot");
    }

    private void ArrangeSingleConnectedDevice()
    {
        _serviceMock
            .Setup(s => s.GetConnectedDevicesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new BrowserDevice("device-1", "Primary", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow, true),
            ]);
    }

    private async Task<ToolResult> ExecuteAsync(BrowserActionArgs args)
    {
        var context = new ToolContext
        {
            AgentId = "test-agent",
            CallId = "test-call",
            Sandbox = new Mock<ISandbox>().Object,
            ContextPressure = 0,
        };

        return await _tool.ExecuteAsync((object)args, context, CancellationToken.None);
    }

    private static JsonElement ToJson(object? value)
        => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}
