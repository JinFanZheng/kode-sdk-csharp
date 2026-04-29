using System.Reflection;
using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Workspace;

namespace KodaClaw.Gateway;

public sealed class PersonaPresetService
{
    private readonly IReadOnlyList<PersonaPreset> _presets;

    public PersonaPresetService()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("KodaClaw.Gateway.Resources.persona-presets.json")
            ?? throw new InvalidOperationException("persona-presets.json embedded resource not found");
        _presets = JsonSerializer.Deserialize<List<PersonaPreset>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize persona presets");
    }

    public IReadOnlyList<PersonaPreset> GetAll() => _presets;

    public PersonaPreset? GetById(string presetId) =>
        _presets.FirstOrDefault(p => p.PresetId == presetId);
}
