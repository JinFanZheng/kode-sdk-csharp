using FluentAssertions;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;
using KodaClaw.Workspace;
using KodaClaw.Workspace.Heartbeat;
using Xunit;

namespace KodaClaw.ContractTests.Workspace;

public sealed class HeartbeatAutomationCompilerContractTests
{
    private readonly HeartbeatAutomationCompiler _compiler = new();

    [Fact]
    public void Compile_should_support_all_frozen_schedule_grammars()
    {
        var markdown = """
# Heartbeat

## Quick Check
- schedule: every 15m
- prompt: Quick system check.

## Hourly Patrol
- schedule: hourly 2h
- prompt: Scan recent workspace updates.
- enabled: true

## Daily Digest
- schedule: daily 09:00
- prompt: Summarize daily status.

## Weekday Reminder
- schedule: weekdays 09:00
- prompt: Plan weekday priorities.

## Weekly Ops Review
- schedule: weekly mon,wed,fri 18:30
- prompt: Audit ops checklist.
""";

        var definitions = _compiler.Compile(markdown);

        definitions.Should().HaveCount(5);
        definitions[0].CronExpression.Should().Be("*/15 * * * *");
        definitions[1].CronExpression.Should().Be("0 */2 * * *");
        definitions[2].CronExpression.Should().Be("0 9 * * *");
        definitions[3].CronExpression.Should().Be("0 9 * * 1-5");
        definitions[4].CronExpression.Should().Be("30 18 * * 1,3,5");

        definitions.Should().OnlyContain(x => x.Source == AutomationDefinitionSource.Heartbeat);
        definitions.Should().OnlyContain(x => x.SourcePath == "workspace/HEARTBEAT.md");
        definitions.Should().OnlyContain(x => x.CreatedAt == DateTimeOffset.UnixEpoch);
        definitions.Should().OnlyContain(x => x.UpdatedAt == DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Compile_should_support_native_cron_field()
    {
        var markdown = """
## Weekday Morning
- cron: "5 10 * * 1-5"
- prompt: Weekday morning check.
""";

        var definitions = _compiler.Compile(markdown);

        definitions.Should().ContainSingle();
        definitions[0].CronExpression.Should().Be("5 10 * * 1-5");
    }

    [Fact]
    public void Compile_should_generate_deterministic_ids_and_deduplicate_duplicate_titles()
    {
        var markdown = """
## Daily Check
- schedule: daily 09:00
- prompt: First run.

## Daily Check
- schedule: daily 10:00
- prompt: Second run.
""";

        var definitions = _compiler.Compile(markdown);

        definitions.Select(x => x.Id).Should().Equal("daily-check", "daily-check-2");
    }

    [Fact]
    public void Compile_should_parse_disabled_flag()
    {
        var markdown = """
## Disabled Automation
- schedule: daily 09:00
- prompt: Keep disabled by default.
- enabled: false
""";

        var definitions = _compiler.Compile(markdown);

        definitions.Should().ContainSingle();
        definitions[0].Enabled.Should().BeFalse();
    }

    [Fact]
    public void Compile_should_normalize_workspace_relative_inputs()
    {
        var markdown = """
## Input Normalization
- schedule: daily 09:00
- prompt: Normalize paths.
- inputs:
  - .\tasks\open.md
  - workspace/inbox
  - ./memory/facts
""";

        var definitions = _compiler.Compile(markdown);

        definitions[0].InputPaths.Should().Equal("tasks/open.md", "inbox", "memory/facts");
    }

    [Fact]
    public void Compile_should_reject_input_path_traversal()
    {
        var markdown = """
## Path Traversal
- schedule: daily 09:00
- prompt: Should fail.
- inputs:
  - ../secrets.txt
""";

        var action = () => _compiler.Compile(markdown);

        action.Should()
            .Throw<HeartbeatCompilationException>()
            .WithMessage("*traverse outside workspace*");
    }

    [Fact]
    public void Compile_should_throw_for_invalid_markdown()
    {
        var markdown = """
## Invalid Markdown
- schedule: daily 09:00
prompt: missing bullet marker
""";

        var action = () => _compiler.Compile(markdown);

        action.Should()
            .Throw<HeartbeatCompilationException>()
            .WithMessage("*Expected a top-level bullet field*");
    }

    [Fact]
    public void Compile_should_throw_for_invalid_schedule_expression()
    {
        var markdown = """
## Invalid Schedule
- schedule: daily 9am
- prompt: Should fail.
""";

        var action = () => _compiler.Compile(markdown);

        action.Should()
            .Throw<HeartbeatCompilationException>()
            .WithMessage("*Unsupported legacy schedule*");
    }

    [Fact]
    public void Compile_should_throw_for_missing_required_fields()
    {
        var missingScheduleMarkdown = """
## Missing Schedule
- prompt: no schedule.
""";
        var missingPromptMarkdown = """
## Missing Prompt
- schedule: daily 09:00
""";

        var missingSchedule = () => _compiler.Compile(missingScheduleMarkdown);
        var missingPrompt = () => _compiler.Compile(missingPromptMarkdown);

        missingSchedule.Should()
            .Throw<HeartbeatCompilationException>()
            .WithMessage("*missing required schedule*");
        missingPrompt.Should()
            .Throw<HeartbeatCompilationException>()
            .WithMessage("*missing required 'prompt'*");
    }

    [Fact]
    public void Compile_should_throw_for_missing_title()
    {
        var markdown = """
##    
- schedule: daily 09:00
- prompt: missing title.
""";

        var action = () => _compiler.Compile(markdown);

        action.Should()
            .Throw<HeartbeatCompilationException>()
            .WithMessage("*title is required*");
    }

    [Fact]
    public void Compile_should_parse_single_channel_binding_id()
    {
        var markdown = """
## Daily Push
- schedule: daily 09:00
- prompt: Push daily summary.
- channels:
  - tg-main-abc123
""";
        var definitions = _compiler.Compile(markdown);

        definitions[0].NotificationChannels.Should().ContainSingle()
            .Which.Should().Be("tg-main-abc123");
    }

    [Fact]
    public void Compile_should_parse_multiple_channel_binding_ids_in_order()
    {
        var markdown = """
## Multi Push
- schedule: daily 09:00
- prompt: Push to multiple channels.
- channels:
  - tg-main-abc123
  - feishu-ops-xyz456
""";
        var definitions = _compiler.Compile(markdown);

        definitions[0].NotificationChannels.Should().Equal("tg-main-abc123", "feishu-ops-xyz456");
    }

    [Fact]
    public void Compile_should_parse_delivery_mode_auto()
    {
        var markdown = """
## Auto Push
- schedule: daily 09:00
- prompt: Auto push.
- channels:
  - tg-main-abc123
- delivery-mode: auto
""";
        var definitions = _compiler.Compile(markdown);

        definitions[0].NotifyMode.Should().Be(AutomationNotifyMode.Auto);
    }

    [Fact]
    public void Compile_should_parse_delivery_mode_approval()
    {
        var markdown = """
## Approval Push
- schedule: daily 09:00
- prompt: Approval push.
- channels:
  - tg-main-abc123
- delivery-mode: approval
""";
        var definitions = _compiler.Compile(markdown);

        definitions[0].NotifyMode.Should().Be(AutomationNotifyMode.Approval);
    }

    [Fact]
    public void Compile_should_default_notify_mode_to_none_when_field_absent()
    {
        var markdown = """
## No Push
- schedule: daily 09:00
- prompt: No push configured.
""";
        var definitions = _compiler.Compile(markdown);

        definitions[0].NotifyMode.Should().Be(AutomationNotifyMode.None);
        definitions[0].NotificationChannels.Should().BeNull();
    }

    [Fact]
    public void Compile_should_treat_unknown_delivery_mode_value_as_none()
    {
        var markdown = """
## Unknown Mode
- schedule: daily 09:00
- prompt: Unknown mode.
- channels:
  - tg-main-abc123
- delivery-mode: manual
""";
        var definitions = _compiler.Compile(markdown);

        definitions[0].NotifyMode.Should().Be(AutomationNotifyMode.None);
    }

    [Fact]
    public void Compile_should_treat_delivery_mode_as_case_insensitive()
    {
        var markdown = """
## Case Test
- schedule: daily 09:00
- prompt: Case test.
- channels:
  - tg-main-abc123
- delivery-mode: AUTO
""";
        var definitions = _compiler.Compile(markdown);

        definitions[0].NotifyMode.Should().Be(AutomationNotifyMode.Auto);
    }

    [Fact]
    public void Compile_should_return_null_notification_channels_when_channels_field_absent()
    {
        var markdown = """
## No Channels
- schedule: daily 09:00
- prompt: No channels.
""";
        var definitions = _compiler.Compile(markdown);

        definitions[0].NotificationChannels.Should().BeNull();
    }

    [Fact]
    public void Compile_should_preserve_newlines_in_block_scalar_prompt()
    {
        var markdown = """
# Heartbeat

## Multi Stage Task
- cron: "0 23 * * *"
- prompt: >
    Step one: do this.
    Step two: do that.

    Step three: new paragraph.
- enabled: true
""";
        var definitions = _compiler.Compile(markdown);

        definitions[0].Prompt.Should().Be("Step one: do this.\nStep two: do that.\n\nStep three: new paragraph.");
    }

    [Fact]
    public void Compile_should_accept_inline_empty_list_for_inputs_and_channels()
    {
        // Regression: "inputs: []" and "channels: []" previously threw "Unsupported field".
        var markdown = """
# Heartbeat

## Empty Lists
- cron: "0 9 * * *"
- prompt: Check health.
- inputs: []
- channels: []
""";
        var definitions = _compiler.Compile(markdown);

        definitions.Should().ContainSingle();
        definitions[0].InputPaths.Should().BeNullOrEmpty();
        definitions[0].NotificationChannels.Should().BeNullOrEmpty();
    }

    [Fact]
    public void Compile_should_preserve_markdown_formatting_in_block_scalar_prompt()
    {
        var markdown = """
# Heartbeat

## Rich Prompt
- cron: "45 23 * * *"
- prompt: >
    **Stage 1** — Gather sources:
    1. Read file A.
    2. Read file B.

    **Stage 2** — Consolidate:
    - Merge new facts.
    - Remove duplicates.
- enabled: true
""";
        var definitions = _compiler.Compile(markdown);

        definitions[0].Prompt.Should().Contain("**Stage 1**");
        definitions[0].Prompt.Should().Contain("**Stage 2**");
        // Paragraph break between stages
        definitions[0].Prompt.Should().Contain("2. Read file B.\n\n**Stage 2**");
    }
}
