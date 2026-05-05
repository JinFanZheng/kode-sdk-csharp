using FluentAssertions;
using Kode.Agent.Sdk.Core.Context;
using Kode.Agent.Sdk.Core.Types;
using Xunit;

namespace Kode.Agent.Tests.Unit.Context;

/// <summary>
/// Tests for semantic message pinning in ContextManager compression.
/// </summary>
public sealed class ContextManagerSemanticPinTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Message UserMsg(string text) => Message.User(text);
    private static Message AssistantMsg(string text) => Message.Assistant(text);

    private static Message ToolUseMsg(string id, string name, object input) =>
        new()
        {
            Role = MessageRole.Assistant,
            Content = [new ToolUseContent { Id = id, Name = name, Input = input }]
        };

    private static Message ToolResultMsg(string toolUseId, string content) =>
        new()
        {
            Role = MessageRole.User,
            Content = [new ToolResultContent { ToolUseId = toolUseId, Content = content }]
        };

    // ── Error marker detection ───────────────────────────────────────────────

    [Fact]
    public void PinByError_ContainsErrorMessage_IsPinned()
    {
        var messages = new List<Message>
        {
            UserMsg("normal message"),
            AssistantMsg("error: something went wrong"),
            UserMsg("another message"),
        };

        var pinned = ContextManager.DerivePinnedIndices(messages, null, null);

        pinned.Should().Contain(1, "messages containing 'error:' should be pinned");
    }

    [Fact]
    public void PinByError_ContainsFailed_IsPinned()
    {
        var messages = new List<Message>
        {
            AssistantMsg("step failed with exit code 1"),
            UserMsg("continue"),
        };

        var pinned = ContextManager.DerivePinnedIndices(messages, null, null);

        pinned.Should().Contain(0, "messages containing 'failed' should be pinned");
    }

    [Fact]
    public void PinByError_ContainsStackTrace_IsPinned()
    {
        var messages = new List<Message>
        {
            UserMsg("check this"),
            AssistantMsg("traceback: at line 42..."),
        };

        var pinned = ContextManager.DerivePinnedIndices(messages, null, null);

        pinned.Should().Contain(1, "messages containing 'traceback' should be pinned");
    }

    // ── Patch/diff marker detection ──────────────────────────────────────────

    [Theory]
    [InlineData("diff --git a/file.cs b/file.cs")]
    [InlineData("+++ b/src/main.cs")]
    [InlineData("--- a/src/main.cs")]
    [InlineData("apply_patch applied successfully")]
    public void PinByPatch_ContainsPatchMarker_IsPinned(string text)
    {
        var messages = new List<Message>
        {
            UserMsg("unrelated"),
            AssistantMsg(text),
        };

        var pinned = ContextManager.DerivePinnedIndices(messages, null, null);

        pinned.Should().Contain(1, $"messages containing '{text}' should be pinned");
    }

    // ── Working set path detection ───────────────────────────────────────────

    [Fact]
    public void PinByPath_MessageMentioningWorkingSetPath_IsPinned()
    {
        var messages = new List<Message>
        {
            UserMsg("unrelated"),
            AssistantMsg("I modified src/Program.cs to add logging"),
            UserMsg("also unrelated"),
        };

        var workingSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/Program.cs",
            "src/Utils.cs"
        };

        var pinned = ContextManager.DerivePinnedIndices(messages, workingSet, null);

        pinned.Should().Contain(1, "message mentioning working set path should be pinned");
        pinned.Should().NotContain(0);
        pinned.Should().NotContain(2);
    }

    [Fact]
    public void PinByPath_ToolUseInputWithWorkingSetPath_IsPinned()
    {
        var messages = new List<Message>
        {
            UserMsg("edit the file"),
            ToolUseMsg("t1", "fs_write", new { path = "src/Config.cs", content = "x" }),
            ToolResultMsg("t1", "ok"),
        };

        var workingSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/Config.cs"
        };

        var pinned = ContextManager.DerivePinnedIndices(messages, workingSet, null);

        // The tool_use mentions the path; both tool_use and its result should be pinned
        pinned.Should().Contain(1, "tool_use referencing working set path should be pinned");
    }

    [Fact]
    public void PinByPath_NoWorkingSet_PathMentionsNotPinned()
    {
        var messages = new List<Message>
        {
            AssistantMsg("I modified src/Program.cs"),
        };

        var pinned = ContextManager.DerivePinnedIndices(messages, null, null);

        // Without a working set, path mentions alone don't trigger pinning
        // (recent message protection handles tail anyway)
        pinned.Should().NotContain(0, "without working set, path mentions are not sufficient for pinning");
    }

    // ── Combined pinning ─────────────────────────────────────────────────────

    [Fact]
    public void PinBySemantics_Combined_AllRelevantPinned()
    {
        var messages = new List<Message>
        {
            UserMsg("start the task"),
            AssistantMsg("error: compilation failed in src/App.cs"),
            UserMsg("fix it"),
            AssistantMsg("diff --git a/src/App.cs b/src/App.cs\n-foo\n+bar"),
            ToolResultMsg("t1", "ok"),
        };

        var workingSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "src/App.cs"
        };

        var pinned = ContextManager.DerivePinnedIndices(messages, workingSet, null);

        // index 1: contains both "error:" and working set path
        pinned.Should().Contain(1);
        // index 3: contains diff marker
        pinned.Should().Contain(3);
    }

    // ── External pin integration ─────────────────────────────────────────────

    [Fact]
    public void PinBySemantics_ExternalPins_ArePreserved()
    {
        var messages = new List<Message>
        {
            UserMsg("msg 0"),
            UserMsg("msg 1"),
            UserMsg("msg 2"),
            UserMsg("msg 3"),
        };

        var externalPins = new[] { 1, 2 };

        var pinned = ContextManager.DerivePinnedIndices(messages, null, externalPins);

        pinned.Should().Contain(1, "external pins should always be preserved");
        pinned.Should().Contain(2);
    }
}
