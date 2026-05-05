using System.Reflection;
using System.Text.Json;
using KodaClaw.Contracts;
using KodaClaw.Contracts.Models;
using Kode.Agent.Sdk.Core.Abstractions;
using Kode.Agent.Sdk.Core.Types;

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

        RegisterCapabilitiesFromPresets();
    }

    public IReadOnlyList<ModelPreset> GetAll() => _presets;

    public ModelPreset? GetById(string presetId) =>
        _presets.FirstOrDefault(p => p.PresetId == presetId);

    /// <summary>
    /// Seeds <see cref="ModelCapabilitiesRegistry.Default"/> with context-window
    /// info from every preset whose model is not already known to the built-in
    /// registry. This gives third-party models (GLM, Kimi, MiniMax, MiMo, etc.)
    /// accurate <see cref="ModelCapabilities"/> at startup without an SDK upgrade.
    /// Built-in entries (gpt-*, deepseek-*, claude-*) are left untouched so their
    /// richer metadata (cache control type, cache-aligned summary support) is
    /// preserved.
    /// </summary>
    private void RegisterCapabilitiesFromPresets()
    {
        var registry = ModelCapabilitiesRegistry.Default;
        foreach (var preset in _presets)
        {
            if (string.IsNullOrWhiteSpace(preset.ModelId)) continue;
            if (preset.ContextWindowSize <= 0) continue;

            // Don't override built-in entries — they carry richer metadata
            // (CacheControlType, SupportsCacheAlignedSummary, etc.)
            if (registry.Get(preset.ModelId) is not null) continue;

            registry.Register(preset.ModelId, new ModelCapabilities
            {
                ContextWindow = preset.ContextWindowSize,
            });
        }
    }
}
