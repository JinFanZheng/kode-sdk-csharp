using System.Globalization;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Channels;
using KodaClaw.Contracts.Sessions;

namespace KodaClaw.ChannelHub;

internal static class ChannelHubValidation
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 200;

    public static void ValidateAccount(ChannelAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);

        RequireNonEmpty(account.Id, nameof(account.Id));
        RequireNonEmpty(account.DisplayName, nameof(account.DisplayName));

        if (account.CreatedAt == default)
        {
            throw new ArgumentException("Created timestamp is required.", nameof(account));
        }

        if (account.UpdatedAt == default)
        {
            throw new ArgumentException("Updated timestamp is required.", nameof(account));
        }
    }

    public static void ValidateBinding(ThreadBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        RequireNonEmpty(binding.Id, nameof(binding.Id));
        RequireNonEmpty(binding.AccountId, nameof(binding.AccountId));
        RequireNonEmpty(binding.ExternalThreadId, nameof(binding.ExternalThreadId));
        RequireNonEmpty(binding.SessionId, nameof(binding.SessionId));
        RequireNonEmpty(binding.PolicyId, nameof(binding.PolicyId));
        RequireNonEmpty(binding.DeliveryRuleId, nameof(binding.DeliveryRuleId));

        if (binding.ChannelIdentity is null)
        {
            throw new ArgumentException("Channel identity is required.", nameof(binding));
        }

        RequireNonEmpty(binding.ChannelIdentity.Id, nameof(binding.ChannelIdentity.Id));

        if (binding.CreatedAt == default)
        {
            throw new ArgumentException("Created timestamp is required.", nameof(binding));
        }

        if (binding.UpdatedAt == default)
        {
            throw new ArgumentException("Updated timestamp is required.", nameof(binding));
        }

        if (binding.SessionKind is SessionKind.Main or SessionKind.Automation or SessionKind.Plugin)
        {
            throw new ArgumentException("Thread bindings must target isolated channel sessions.", nameof(binding));
        }

        var expectedSessionKind = binding.ThreadType switch
        {
            ChannelThreadType.DirectMessage => SessionKind.ChannelDirectMessage,
            ChannelThreadType.Group => SessionKind.ChannelGroup,
            _ => throw new ArgumentOutOfRangeException(nameof(binding.ThreadType), binding.ThreadType, null),
        };

        if (binding.SessionKind != expectedSessionKind)
        {
            throw new ArgumentException(
                $"Thread type '{binding.ThreadType}' must map to session kind '{expectedSessionKind}'.",
                nameof(binding));
        }
    }

    public static void RequireNonEmpty(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A non-empty value is required.", paramName);
        }
    }

    public static int NormalizeLimit(int limit)
    {
        if (limit <= 0)
        {
            return DefaultLimit;
        }

        return Math.Min(limit, MaxLimit);
    }

    public static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    public static string? FormatTimestampOrNull(DateTimeOffset? value)
    {
        return value is null ? null : FormatTimestamp(value.Value);
    }

    public static DateTimeOffset ParseTimestamp(string value)
    {
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    public static TEnum ParseEnum<TEnum>(string value)
        where TEnum : struct, Enum
    {
        return Enum.Parse<TEnum>(value, ignoreCase: false);
    }

    public static string? NormalizeNullableText(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length == 0 ? null : normalized;
    }
}
