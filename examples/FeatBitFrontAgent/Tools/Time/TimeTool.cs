namespace Kode.Agent.WebApiAssistant.Tools.Time;

/// <summary>
/// 时间工具
/// </summary>
public class TimeTool
{
    private readonly ILogger<TimeTool> _logger;
    private readonly TimeZoneInfo? _localTimeZone;

    public TimeTool(ILogger<TimeTool> logger)
    {
        _logger = logger;
        try
        {
            _localTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Shanghai");
        }
        catch
        {
            _localTimeZone = TimeZoneInfo.Local;
        }
    }

    /// <summary>
    /// 获取当前时间
    /// </summary>
    public string GetCurrentTime()
    {
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _localTimeZone!)
            .ToString("yyyy-MM-dd HH:mm:ss");
    }

    /// <summary>
    /// 获取当前日期
    /// </summary>
    public string GetCurrentDate()
    {
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _localTimeZone!)
            .ToString("yyyy-MM-dd");
    }

    /// <summary>
    /// 获取时间戳
    /// </summary>
    public long GetTimestamp()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>
    /// 解析 ISO 时间
    /// </summary>
    public DateTime? ParseIsoTime(string isoString)
    {
        if (DateTime.TryParse(isoString, out var result))
        {
            return result;
        }
        return null;
    }

    /// <summary>
    /// 格式化时间为 ISO 字符串
    /// </summary>
    public string FormatIsoTime(DateTime dateTime)
    {
        return dateTime.ToString("yyyy-MM-ddTHH:mm:ss");
    }

    /// <summary>
    /// 计算时间差
    /// </summary>
    public string GetTimeDifference(DateTime from, DateTime to)
    {
        var diff = to - from;
        if (diff.TotalDays >= 1)
        {
            return $"{(int)diff.TotalDays} 天";
        }
        if (diff.TotalHours >= 1)
        {
            return $"{(int)diff.TotalHours} 小时";
        }
        if (diff.TotalMinutes >= 1)
        {
            return $"{(int)diff.TotalMinutes} 分钟";
        }
        return $"{(int)diff.TotalSeconds} 秒";
    }

    /// <summary>
    /// 判断是否为工作日
    /// </summary>
    public bool IsWeekday(DateTime date)
    {
        return date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday;
    }

    /// <summary>
    /// 获取本周日期范围
    /// </summary>
    public (DateTime Start, DateTime End) GetThisWeek()
    {
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _localTimeZone!);
        var start = now.Date.AddDays(-(int)now.DayOfWeek);
        var end = start.AddDays(7);
        return (start, end);
    }
}
