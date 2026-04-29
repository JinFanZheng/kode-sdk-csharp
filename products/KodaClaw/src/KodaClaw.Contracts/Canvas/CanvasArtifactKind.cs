using System.Text.Json.Serialization;

namespace KodaClaw.Contracts.Canvas;

[JsonConverter(typeof(JsonStringEnumConverter<CanvasArtifactKind>))]
public enum CanvasArtifactKind
{
    Report = 0,
    Dashboard = 1,
    Board = 2,
    TaskList = 3,
    PluginPanel = 4,
    Html = 5,
    Image = 6,
}
