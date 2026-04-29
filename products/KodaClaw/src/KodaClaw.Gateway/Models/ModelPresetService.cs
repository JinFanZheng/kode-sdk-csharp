using System.Reflection;
using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Models;

namespace KodaClaw.Gateway;

public sealed class ModelPresetService
{
    private readonly IReadOnlyList<ModelPreset> _presets;

    public ModelPresetService()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("KodaClaw.Gateway.Resources.model-presets.json")
            ?? throw new InvalidOperationException("model-presets.json embedded resource not found");
        _presets = JsonSerializer.Deserialize<List<ModelPreset>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Failed to deserialize model presets");
    }

    public IReadOnlyList<ModelPreset> GetAll() => _presets;

    public ModelPreset? GetById(string presetId) =>
        _presets.FirstOrDefault(p => p.PresetId == presetId);
}
