using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Browser;

[JsonConverter(typeof(JsonStringEnumConverter<ScreenshotFormat>))]
public enum ScreenshotFormat
{
    Jpeg,
    Png
}
