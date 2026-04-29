using FluentAssertions;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Kode.Agent.Sdk.Core.Types;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

public sealed class SessionSummaryServiceTests
{
    // ── ShouldGenerateSummary ────────────────────────────────────────────────

    [Fact]
    public void ShouldGenerateSummary_ReturnsFalse_WhenAutomationSession()
    {
        var context = new MemorySessionSummaryContext(
            SessionId: "s1",
            SessionType: "automation",
            BindingId: null,
            Messages: CreateUserMessages(10));

        MemorySessionSummaryService.ShouldGenerateSummary(context).Should().BeFalse();
    }

    [Fact]
    public void ShouldGenerateSummary_ReturnsFalse_WhenTooFewMessages()
    {
        var context = new MemorySessionSummaryContext(
            SessionId: "s2",
            SessionType: "chat",
            BindingId: null,
            Messages: CreateUserMessages(4));

        MemorySessionSummaryService.ShouldGenerateSummary(context).Should().BeFalse();
    }

    [Fact]
    public void ShouldGenerateSummary_ReturnsTrue_WhenEnoughMessages()
    {
        var context = new MemorySessionSummaryContext(
            SessionId: "s3",
            SessionType: "chat",
            BindingId: null,
            Messages: CreateUserMessages(5));

        MemorySessionSummaryService.ShouldGenerateSummary(context).Should().BeTrue();
    }

    // ── ParseSummaryJson ─────────────────────────────────────────────────────

    [Fact]
    public void ParseSummaryJson_ValidJson_ReturnsSessionSummary()
    {
        var context = new MemorySessionSummaryContext(
            SessionId: "s4",
            SessionType: "chat",
            BindingId: null,
            Messages: []);

        var json = """
            {
              "topics": ["C# testing", "unit tests"],
              "keywords": ["xUnit", "Moq", "FluentAssertions"],
              "decisions": ["Use FluentAssertions for all asserts"],
              "user_expressions": ["I prefer xUnit over NUnit"],
              "follow_ups": ["Add integration tests"]
            }
            """;

        var summary = MemorySessionSummaryService.ParseSummaryJson(context, json);

        summary.Should().NotBeNull();
        summary!.SessionId.Should().Be("s4");
        summary.SessionType.Should().Be("chat");
        summary.Topics.Should().BeEquivalentTo(["C# testing", "unit tests"]);
        summary.Keywords.Should().BeEquivalentTo(["xUnit", "Moq", "FluentAssertions"]);
        summary.Decisions.Should().BeEquivalentTo(["Use FluentAssertions for all asserts"]);
        summary.UserExpressions.Should().BeEquivalentTo(["I prefer xUnit over NUnit"]);
        summary.FollowUps.Should().BeEquivalentTo(["Add integration tests"]);
    }

    [Fact]
    public void ParseSummaryJson_JsonWithCodeFences_StripsFences()
    {
        var context = new MemorySessionSummaryContext(
            SessionId: "s5",
            SessionType: "channel",
            BindingId: "b1",
            Messages: []);

        var json = """
            ```json
            {
              "topics": ["fenced topic"],
              "keywords": ["fence"],
              "decisions": [],
              "user_expressions": [],
              "follow_ups": []
            }
            ```
            """;

        var summary = MemorySessionSummaryService.ParseSummaryJson(context, json);

        summary.Should().NotBeNull();
        summary!.Topics.Should().BeEquivalentTo(["fenced topic"]);
        summary.Keywords.Should().BeEquivalentTo(["fence"]);
    }

    [Fact]
    public void ParseSummaryJson_InvalidJson_ReturnsNull()
    {
        var context = new MemorySessionSummaryContext(
            SessionId: "s6",
            SessionType: "chat",
            BindingId: null,
            Messages: []);

        var result = MemorySessionSummaryService.ParseSummaryJson(context, "not valid json {{{");

        result.Should().BeNull();
    }

    // ── DetectPrivacyLevel ───────────────────────────────────────────────────

    [Theory]
    [InlineData("请帮我设置密码，不要记录", "private")]
    [InlineData("别记这个，很敏感", "private")]
    [InlineData("keep this private please", "private")]
    [InlineData("今天讨论了架构设计", "normal")]
    public void DetectPrivacyLevel_MatchesExpected(string text, string expected)
    {
        MemorySessionSummaryService.DetectPrivacyLevel(text).Should().Be(expected);
    }

    // ── Resumed session context ───────────────────────────────────────────────

    [Fact]
    public void MemorySessionSummaryContext_IsResumed_DefaultsFalse()
    {
        var context = new MemorySessionSummaryContext(
            SessionId: "s10",
            SessionType: "main",
            BindingId: null,
            Messages: []);

        context.IsResumed.Should().BeFalse();
    }

    [Fact]
    public void MemorySessionSummary_Supersedes_DefaultsNull()
    {
        var summary = new MemorySessionSummary(
            SessionId: "s11",
            Date: "2026-03-26",
            SessionType: "main",
            Topics: ["test"],
            Keywords: ["test"],
            Decisions: [],
            UserExpressions: [],
            FollowUps: []);

        summary.Supersedes.Should().BeNull();
        summary.PrivacyLevel.Should().Be("normal");
    }

    [Fact]
    public void MemorySessionSummary_WithSupersedes_PreservesValue()
    {
        var summary = new MemorySessionSummary(
            SessionId: "s12",
            Date: "2026-03-26",
            SessionType: "main",
            Topics: ["test"],
            Keywords: ["test"],
            Decisions: [],
            UserExpressions: [],
            FollowUps: [],
            Supersedes: "2026-03-26-main-abcd1234-100000.md");

        summary.Supersedes.Should().Be("2026-03-26-main-abcd1234-100000.md");
    }

    // ── ExtractConversationText ────────────────────────────────────────────

    [Fact]
    public void ExtractConversationText_ShortConversation_ReturnsFullText()
    {
        var messages = CreateUserMessages(3);

        var result = MemorySessionSummaryService.ExtractConversationText(messages);

        result.Should().Contain("User message 0");
        result.Should().Contain("User message 2");
        result.Should().NotContain("中间部分省略");
    }

    [Fact]
    public void ExtractConversationText_LongConversation_PreservesHeadAndTail()
    {
        // Generate enough messages to exceed 12,000 chars
        var messages = new List<Message>();
        for (var i = 0; i < 200; i++)
        {
            messages.Add(Message.User($"User message {i}: " + new string('x', 80)));
            messages.Add(Message.Assistant($"Assistant reply {i}: " + new string('y', 80)));
        }

        var result = MemorySessionSummaryService.ExtractConversationText(messages);

        // Should contain head (first messages)
        result.Should().Contain("User message 0:");
        // Should contain tail (last messages)
        result.Should().Contain("User message 199:");
        // Should contain the truncation marker
        result.Should().Contain("中间部分省略");
    }

    [Fact]
    public void ExtractConversationText_SkipsSystemMessages()
    {
        var messages = new List<Message>
        {
            Message.User("Hello"),
            Message.Assistant("Hi there"),
        };

        var result = MemorySessionSummaryService.ExtractConversationText(messages);

        result.Should().Contain("[User]: Hello");
        result.Should().Contain("[Assistant]: Hi there");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<Message> CreateUserMessages(int count)
    {
        var messages = new List<Message>();
        for (var i = 0; i < count; i++)
        {
            messages.Add(Message.User($"User message {i}"));
            messages.Add(Message.Assistant($"Assistant reply {i}"));
        }

        return messages;
    }
}
