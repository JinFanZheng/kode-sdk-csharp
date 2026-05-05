namespace Kode.Agent.Sdk.Core.Types;

/// <summary>
/// Centralised, extensible registry of model capabilities.
/// <br/>
/// Resolves <see cref="ModelCapabilities"/> for a given model ID through a three-tier lookup:
/// <list type="number">
///   <item>User-registered overrides (exact match)</item>
///   <item>Built-in known-model entries (exact match)</item>
///   <item>Prefix-based heuristics (longest matching prefix)</item>
/// </list>
/// <br/>
/// Users can register additional models via <see cref="Register"/> or
/// <see cref="RegisterPrefix"/> at startup without rebuilding the SDK.
/// Providers should consult this registry before falling back to their own legacy
/// hardcoded tables.
/// </summary>
public class ModelCapabilitiesRegistry
{
    // Tier 1: user overrides (registered at startup via DI / options).
    private readonly Dictionary<string, ModelCapabilities> _userOverrides = new(StringComparer.OrdinalIgnoreCase);

    // Tier 1b: user prefix overrides, ordered by descending length so longest match wins.
    private readonly List<(string Prefix, ModelCapabilities Caps)> _userPrefixes = new();

    // Tier 2: built-in known models (shipped with SDK).
    private static readonly Dictionary<string, ModelCapabilities> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        // ── OpenAI Chat Completions ──────────────────────────────────────────
        // GPT-4o family
        ["gpt-4o"] = new ModelCapabilities { ContextWindow = 128_000 },
        ["gpt-4o-mini"] = new ModelCapabilities { ContextWindow = 128_000 },

        // GPT-4.1 family — 1M context with automatic prompt prefix caching
        ["gpt-4.1"] = new ModelCapabilities
        {
            ContextWindow = 1_000_000,
            SupportsPrefixCache = true,
            CacheControlType = CacheControlType.PromptPrefix,
        },
        ["gpt-4.1-mini"] = new ModelCapabilities
        {
            ContextWindow = 1_000_000,
            SupportsPrefixCache = true,
            CacheControlType = CacheControlType.PromptPrefix,
        },
        ["gpt-4.1-nano"] = new ModelCapabilities
        {
            ContextWindow = 1_000_000,
            SupportsPrefixCache = true,
            CacheControlType = CacheControlType.PromptPrefix,
        },

        // GPT-5 family — 256K context with automatic prompt prefix caching
        // (conservative estimate; actual window may be larger)
        ["gpt-5"] = new ModelCapabilities
        {
            ContextWindow = 256_000,
            SupportsPrefixCache = true,
            CacheControlType = CacheControlType.PromptPrefix,
        },
        ["gpt-5-mini"] = new ModelCapabilities
        {
            ContextWindow = 256_000,
            SupportsPrefixCache = true,
            CacheControlType = CacheControlType.PromptPrefix,
        },
        ["gpt-5-nano"] = new ModelCapabilities
        {
            ContextWindow = 128_000,
            SupportsPrefixCache = true,
            CacheControlType = CacheControlType.PromptPrefix,
        },

        // OpenAI reasoning models
        ["o1"] = new ModelCapabilities { ContextWindow = 200_000 },
        ["o1-mini"] = new ModelCapabilities { ContextWindow = 200_000 },
        ["o3-mini"] = new ModelCapabilities { ContextWindow = 200_000 },
        ["o3"] = new ModelCapabilities { ContextWindow = 200_000 },
        ["o4-mini"] = new ModelCapabilities { ContextWindow = 200_000 },

        // ── DeepSeek ─────────────────────────────────────────────────────────
        // V4 family: 1M context, transparent prefix cache, supports cache-aligned summaries
        ["deepseek-v4-pro"] = new ModelCapabilities
        {
            ContextWindow = 1_000_000,
            SupportsPrefixCache = true,
            CacheControlType = CacheControlType.Ephemeral,
            SupportsCacheAlignedSummary = true,
        },
        ["deepseek-v4-flash"] = new ModelCapabilities
        {
            ContextWindow = 1_000_000,
            SupportsPrefixCache = true,
            CacheControlType = CacheControlType.Ephemeral,
            SupportsCacheAlignedSummary = true,
        },

        // Legacy DeepSeek models: 128K context
        ["deepseek-chat"] = new ModelCapabilities
        {
            ContextWindow = 128_000,
            SupportsPrefixCache = true,
            CacheControlType = CacheControlType.Ephemeral,
        },
        ["deepseek-reasoner"] = new ModelCapabilities
        {
            ContextWindow = 128_000,
            SupportsPrefixCache = true,
            CacheControlType = CacheControlType.Ephemeral,
        },
    };

    // Tier 3: prefix-based heuristics (ordered by specificity, longest first).
    // These apply when neither user overrides nor built-in exact entries match.
    private static readonly (string Prefix, ModelCapabilities Caps)[] HeuristicPrefixes =
    [
        // GPT-5 family (before generic "gpt-" so 5.x takes precedence)
        ("gpt-5", new ModelCapabilities
        {
            ContextWindow = 256_000,
            SupportsPrefixCache = true,
            CacheControlType = CacheControlType.PromptPrefix,
        }),

        // GPT-4.1 family
        ("gpt-4.1", new ModelCapabilities
        {
            ContextWindow = 1_000_000,
            SupportsPrefixCache = true,
            CacheControlType = CacheControlType.PromptPrefix,
        }),

        // Generic GPT-4
        ("gpt-4", new ModelCapabilities { ContextWindow = 128_000 }),

        // Generic GPT-3.5 (legacy)
        ("gpt-3.5", new ModelCapabilities { ContextWindow = 16_385 }),

        // OpenAI reasoning models
        ("o4", new ModelCapabilities { ContextWindow = 200_000 }),
        ("o3", new ModelCapabilities { ContextWindow = 200_000 }),
        ("o1", new ModelCapabilities { ContextWindow = 200_000 }),

        // DeepSeek V4
        ("deepseek-v4", new ModelCapabilities
        {
            ContextWindow = 1_000_000,
            SupportsPrefixCache = true,
            CacheControlType = CacheControlType.Ephemeral,
            SupportsCacheAlignedSummary = true,
        }),

        // Generic DeepSeek (legacy fallback)
        ("deepseek-", new ModelCapabilities
        {
            ContextWindow = 128_000,
            SupportsPrefixCache = true,
            CacheControlType = CacheControlType.Ephemeral,
        }),

        // Claude (all models: 200K, ephemeral prompt cache)
        ("claude-", new ModelCapabilities
        {
            ContextWindow = 200_000,
            SupportsPrefixCache = true,
            CacheControlType = CacheControlType.Ephemeral,
        }),
    ];

    // ═════════════════════════════════════════════════════════════════════════
    // Public API
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Register an exact model capability override. Takes precedence over
    /// built-in entries and prefix heuristics. Call at startup (e.g. in DI
    /// configuration) to add new models without an SDK upgrade.
    /// </summary>
    public void Register(string modelId, ModelCapabilities caps)
    {
        ArgumentNullException.ThrowIfNull(modelId);
        ArgumentNullException.ThrowIfNull(caps);
        _userOverrides[modelId] = caps;
    }

    /// <summary>
    /// Register a prefix-based capability override. Longer prefixes take
    /// precedence. Overrides built-in prefix heuristics but not exact
    /// <see cref="Register"/> entries.
    /// </summary>
    public void RegisterPrefix(string prefix, ModelCapabilities caps)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentNullException.ThrowIfNull(caps);
        _userPrefixes.Add((prefix, caps));
        // Keep sorted by descending length so longest match wins on lookup.
        _userPrefixes.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));
    }

    /// <summary>
    /// Look up capabilities for a model ID.
    /// Resolution order: user overrides → built-in → prefix heuristics → null.
    /// </summary>
    /// <param name="modelId">The model identifier (e.g., "gpt-5", "deepseek-v4-pro").</param>
    /// <returns>Capabilities or null if the model is completely unknown.</returns>
    public ModelCapabilities? Get(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;

        // Tier 1: user exact overrides
        if (_userOverrides.TryGetValue(modelId, out var caps))
            return caps;

        // Tier 2: built-in exact match
        if (BuiltIn.TryGetValue(modelId, out caps))
            return caps;

        // Tier 3: user prefix overrides (longest match first)
        foreach (var (prefix, prefixCaps) in _userPrefixes)
        {
            if (modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return prefixCaps;
        }

        // Tier 4: built-in prefix heuristics
        foreach (var (prefix, heuristicCaps) in HeuristicPrefixes)
        {
            if (modelId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return heuristicCaps;
        }

        return null;
    }

    // ═════════════════════════════════════════════════════════════════════════
    // Static convenience
    // ═════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A shared default instance with built-in entries and prefix heuristics only.
    /// Providers that don't need per-instance customisation can use this directly.
    /// </summary>
    public static ModelCapabilitiesRegistry Default { get; } = new();
}
