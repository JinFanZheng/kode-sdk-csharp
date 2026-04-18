using FluentAssertions;
using Kode.Agent.Sdk.Core.Agent;
using Kode.Agent.Sdk.Core.Types;
using Xunit;

namespace Kode.Agent.Tests.Unit;

public sealed class MessageQueueDedupTests
{
    private static MessageQueueOptions BuildOptions(List<Message> sink)
    {
        return new MessageQueueOptions
        {
            WrapReminder = (content, _) => $"<system-reminder>\n{content}\n</system-reminder>",
            AddMessageAsync = (msg, _, _) =>
            {
                sink.Add(msg);
                return Task.CompletedTask;
            },
            PersistAsync = _ => Task.CompletedTask,
            EnsureProcessing = () => { }
        };
    }

    [Fact]
    public void Send_WithDedupKey_DropsDuplicatesStillPending()
    {
        var sink = new List<Message>();
        var queue = new MessageQueue(BuildOptions(sink));

        var id1 = queue.Send("a", new SendOptions
        {
            Kind = PendingKind.Reminder,
            DedupKey = "file-change:/foo",
            Reminder = new ReminderOptions { Category = "file" }
        });
        var id2 = queue.Send("a", new SendOptions
        {
            Kind = PendingKind.Reminder,
            DedupKey = "file-change:/foo",
            Reminder = new ReminderOptions { Category = "file" }
        });
        var id3 = queue.Send("a", new SendOptions
        {
            Kind = PendingKind.Reminder,
            DedupKey = "file-change:/foo",
            Reminder = new ReminderOptions { Category = "file" }
        });

        queue.PendingCount.Should().Be(1);
        id2.Should().Be(id1);
        id3.Should().Be(id1);
    }

    [Fact]
    public void Send_DifferentDedupKeys_DoNotCollide()
    {
        var sink = new List<Message>();
        var queue = new MessageQueue(BuildOptions(sink));

        queue.Send("a", new SendOptions { Kind = PendingKind.Reminder, DedupKey = "file-change:/foo", Reminder = new ReminderOptions { Category = "file" } });
        queue.Send("b", new SendOptions { Kind = PendingKind.Reminder, DedupKey = "file-change:/bar", Reminder = new ReminderOptions { Category = "file" } });

        queue.PendingCount.Should().Be(2);
    }

    [Fact]
    public async Task Send_AfterFlush_AllowsNewReminderForSameKey()
    {
        var sink = new List<Message>();
        var queue = new MessageQueue(BuildOptions(sink));

        queue.Send("a", new SendOptions { Kind = PendingKind.Reminder, DedupKey = "file-change:/foo", Reminder = new ReminderOptions { Category = "file" } });
        await queue.FlushAsync();
        queue.PendingCount.Should().Be(0);
        sink.Should().HaveCount(1);

        queue.Send("a", new SendOptions { Kind = PendingKind.Reminder, DedupKey = "file-change:/foo", Reminder = new ReminderOptions { Category = "file" } });
        queue.PendingCount.Should().Be(1);
    }

    [Fact]
    public void Send_WithoutDedupKey_PreservesCurrentBehavior()
    {
        var sink = new List<Message>();
        var queue = new MessageQueue(BuildOptions(sink));

        queue.Send("user 1");
        queue.Send("user 2");
        queue.Send("user 3");

        queue.PendingCount.Should().Be(3);
    }
}
