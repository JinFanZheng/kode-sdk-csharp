using FluentAssertions;
using KodaClaw.Contracts.Jobs;
using Xunit;

namespace KodaClaw.UnitTests.Contracts;

public sealed class JobDefinitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact] public void Name_empty_fails() { var e = ValidOneShot() with { Name = "" }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("name")); }
    [Fact] public void Name_whitespace_fails() { var e = ValidOneShot() with { Name = "   " }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("name")); }
    [Fact] public void Name_exceeds_200_fails() { var e = ValidOneShot() with { Name = new string('x', 201) }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("200")); }
    [Fact] public void Name_at_200_ok() { var e = ValidOneShot() with { Name = new string('x', 200) }; e.ValidateForCreate(Now).Should().NotContain(x => x.Contains("name")); }
    [Fact] public void Type_invalid_fails() { var e = ValidOneShot() with { Type = (JobType)99 }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("type")); }
    [Fact] public void Prompt_empty_fails() { var e = ValidOneShot() with { Prompt = "" }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("prompt")); }
    [Fact] public void Prompt_exceeds_50000_fails() { var e = ValidOneShot() with { Prompt = new string('x', 50001) }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("50000")); }
    [Fact] public void Status_invalid_fails() { var e = ValidOneShot() with { Status = (JobStatus)99 }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("status")); }
    [Fact] public void Status_pending_ok() { var e = ValidOneShot() with { Status = JobStatus.Pending }; e.ValidateForUpdate().Should().NotContain(x => x.Contains("status")); }
    [Fact] public void Status_cancelled_ok() { var e = ValidOneShot() with { Status = JobStatus.Cancelled }; e.ValidateForUpdate().Should().NotContain(x => x.Contains("status")); }
    [Fact] public void Recurring_cron_null_fails() { var e = ValidRecurring() with { Cron = null }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("cron")); }
    [Fact] public void OneShot_next_run_at_null_fails() { var e = ValidOneShot() with { NextRunAt = null }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("next_run_at")); }
    [Fact] public void OneShot_next_run_at_past_fails() { var e = ValidOneShot() with { NextRunAt = Now.AddHours(-1) }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("next_run_at")); }
    [Fact] public void SelfDriven_next_run_at_null_fails() { var e = ValidSelfDriven() with { NextRunAt = null }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("next_run_at")); }
    [Fact] public void FallbackInterval_below_5_fails() { var e = ValidSelfDriven() with { FallbackIntervalMinutes = 3 }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("5-10080")); }
    [Fact] public void FallbackInterval_above_10080_fails() { var e = ValidSelfDriven() with { FallbackIntervalMinutes = 20000 }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("5-10080")); }
    [Fact] public void FallbackInterval_non_selfdriven_fails() { var e = ValidOneShot() with { FallbackIntervalMinutes = 60 }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("fallback")); }
    [Fact] public void Timeout_0_fails() { var e = ValidOneShot() with { TimeoutMinutes = 0 }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("timeout")); }
    [Fact] public void Timeout_1441_fails() { var e = ValidOneShot() with { TimeoutMinutes = 1441 }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("timeout")); }
    [Fact] public void Timeout_1_ok() { var e = ValidOneShot() with { TimeoutMinutes = 1 }; e.ValidateForCreate(Now).Should().NotContain(x => x.Contains("timeout")); }
    [Fact] public void MaxRetries_6_fails() { var e = ValidOneShot() with { MaxRetries = 6 }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("max_retries")); }
    [Fact] public void MaxRetries_5_ok() { var e = ValidOneShot() with { MaxRetries = 5 }; e.ValidateForCreate(Now).Should().NotContain(x => x.Contains("max_retries")); }
    [Fact] public void MaxConsecutiveFailures_0_fails() { var e = ValidOneShot() with { MaxConsecutiveFailures = 0 }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("max_consecutive_failures")); }
    [Fact] public void DeliveryMode_invalid_fails() { var e = ValidOneShot() with { DeliveryMode = (JobDeliveryMode)99 }; e.ValidateForCreate(Now).Should().Contain(x => x.Contains("delivery_mode")); }
    [Fact] public void DeliveryMode_auto_ok() { var e = ValidOneShot() with { DeliveryMode = JobDeliveryMode.Auto }; e.ValidateForCreate(Now).Should().NotContain(x => x.Contains("delivery_mode")); }
    [Fact] public void HappyPath_oneShot_passes() { ValidOneShot().ValidateForCreate(Now).Should().BeEmpty(); }
    [Fact] public void HappyPath_recurring_passes() { ValidRecurring().ValidateForCreate(Now).Should().BeEmpty(); }
    [Fact] public void HappyPath_selfDriven_passes() { ValidSelfDriven().ValidateForCreate(Now).Should().BeEmpty(); }

    private static JobDefinition ValidOneShot() => new() { Name = "Test", Type = JobType.OneShot, Prompt = "Do something", NextRunAt = Now.AddHours(1) };
    private static JobDefinition ValidRecurring() => new() { Name = "Test", Type = JobType.Recurring, Prompt = "Do something", Cron = "0 9 * * *" };
    private static JobDefinition ValidSelfDriven() => new() { Name = "Test", Type = JobType.SelfDriven, Prompt = "Do something", NextRunAt = Now.AddHours(1), FallbackIntervalMinutes = 60 };
}
