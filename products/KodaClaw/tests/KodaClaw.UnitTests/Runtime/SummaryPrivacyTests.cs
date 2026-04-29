using FluentAssertions;
using KodaClaw.Runtime;
using KodaClaw.Runtime.Sessions;
using Xunit;

namespace KodaClaw.UnitTests.Runtime;

public sealed class SummaryPrivacyTests
{
    [Fact]
    public void DetectPrivacyLevel_NormalConversation_ReturnsNormal()
    {
        var text = "[User]: Can you help me write a unit test?\n[Assistant]: Sure, here is an example.";

        MemorySessionSummaryService.DetectPrivacyLevel(text).Should().Be("normal");
    }

    [Fact]
    public void DetectPrivacyLevel_Chinese_不要记录_ReturnsPrivate()
    {
        var text = "[User]: 这个不要记录，我只是随便问问。\n[Assistant]: 好的。";

        MemorySessionSummaryService.DetectPrivacyLevel(text).Should().Be("private");
    }

    [Fact]
    public void DetectPrivacyLevel_Chinese_别记这个_ReturnsPrivate()
    {
        var text = "[User]: 别记这个对话。\n[Assistant]: 了解。";

        MemorySessionSummaryService.DetectPrivacyLevel(text).Should().Be("private");
    }

    [Fact]
    public void DetectPrivacyLevel_EnglishPrivateSession_ReturnsPrivate()
    {
        var text = "[User]: This is a private session, please don't log anything.\n[Assistant]: Understood.";

        MemorySessionSummaryService.DetectPrivacyLevel(text).Should().Be("private");
    }
}
