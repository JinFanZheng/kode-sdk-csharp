using System.Globalization;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Settings;
using KodaClaw.Storage;

namespace KodaClaw.Storage.Json.Repositories;

/// <summary>
/// KodaClawSettings 的 JSON 文件存储实现。
/// 路径：{workspaceRoot}/config/settings.json
/// </summary>
public sealed class JsonSettingsRepository : JsonStoreBase, ISettingsRepository
{
    private readonly string _path;

    public JsonSettingsRepository(string workspaceRoot)
    {
        _path = Path.Combine(workspaceRoot, "config", "settings.json");
    }

    public async Task<KodaClawSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        return await ReadEntityAsync<KodaClawSettings>(_path, cancellationToken)
               ?? KodaClawSettings.Default;
    }

    public Task SaveAsync(KodaClawSettings settings, CancellationToken cancellationToken = default)
    {
        ValidateQuietHoursTime(settings.QuietHoursStartLocalTime, nameof(settings.QuietHoursStartLocalTime));
        ValidateQuietHoursTime(settings.QuietHoursEndLocalTime, nameof(settings.QuietHoursEndLocalTime));
        return WriteEntityAsync(_path, settings, cancellationToken);
    }

    private static void ValidateQuietHoursTime(string? time, string paramName)
    {
        if (string.IsNullOrWhiteSpace(time))
        {
            return;
        }

        if (!TimeOnly.TryParseExact(time, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            throw new ArgumentException(
                $"Quiet hours time '{time}' is not a valid HH:mm time value.", paramName);
        }
    }
}
