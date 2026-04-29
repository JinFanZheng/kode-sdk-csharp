using Cronos;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Automations;

namespace KodaClaw.Automation;

internal static class AutomationValidation
{
    public static void ValidateDefinition(AutomationDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ValidateRequired(definition.Id, nameof(definition.Id));
        ValidateRequired(definition.Title, nameof(definition.Title));
        ValidateRequired(definition.Prompt, nameof(definition.Prompt));
        ValidateCronExpression(definition.CronExpression, nameof(definition.CronExpression));

        if (definition.CreatedAt == default)
        {
            throw new ArgumentException("CreatedAt is required.", nameof(definition.CreatedAt));
        }

        if (definition.UpdatedAt == default)
        {
            throw new ArgumentException("UpdatedAt is required.", nameof(definition.UpdatedAt));
        }
    }

    public static void ValidateRunRecord(AutomationRunRecord runRecord)
    {
        ArgumentNullException.ThrowIfNull(runRecord);
        ValidateRequired(runRecord.RunId, nameof(runRecord.RunId));
        ValidateRequired(runRecord.AutomationId, nameof(runRecord.AutomationId));
        ValidateRequired(runRecord.Trigger, nameof(runRecord.Trigger));

        if (runRecord.Attempt < 1)
        {
            throw new ArgumentException("Attempt must be greater than or equal to 1.", nameof(runRecord.Attempt));
        }

        if (runRecord.StartedAt == default)
        {
            throw new ArgumentException("StartedAt is required.", nameof(runRecord.StartedAt));
        }

        if (runRecord.CompletedAt is { } completedAt && completedAt < runRecord.StartedAt)
        {
            throw new ArgumentException(
                "CompletedAt must be greater than or equal to StartedAt.",
                nameof(runRecord.CompletedAt));
        }
    }

    public static void ValidateId(string? id, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Id is required.", parameterName);
        }
    }

    public static int NormalizeLimit(int limit, int defaultLimit = 50, int maxLimit = 200)
    {
        if (limit <= 0)
        {
            return defaultLimit;
        }

        return Math.Min(limit, maxLimit);
    }

    private static void ValidateRequired(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{parameterName} is required.", parameterName);
        }
    }

    private static void ValidateCronExpression(string? cronExpression, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(cronExpression))
        {
            throw new ArgumentException("CronExpression is required.", parameterName);
        }

        try
        {
            CronExpression.Parse(cronExpression.Trim());
        }
        catch (CronFormatException ex)
        {
            throw new ArgumentException($"Invalid cron expression '{cronExpression}': {ex.Message}", parameterName);
        }
    }
}
