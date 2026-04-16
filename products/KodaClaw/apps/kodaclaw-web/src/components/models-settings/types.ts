import type {
  AccountModelResponse,
  ModelProviderKind,
  ProviderAccountResponse,
} from "../../types/contracts";

// ── ModelCapabilitySet bitmask constants (mirrors C# enum) ───────────────────
export const CAP_TEXT = 1 << 0; // 1
export const CAP_IMAGE = 1 << 1; // 2
export const CAP_VIDEO = 1 << 2; // 4
export const CAP_FILE = 1 << 3; // 8
export const CAP_AUDIO = 1 << 4; // 16

// ── Provider-specific default base URLs ─────────────────────────────────────
export const PROVIDER_DEFAULT_BASE_URLS: Partial<Record<ModelProviderKind, string>> = {
  OpenAI: "https://api.openai.com/v1",
  OpenAIResponses: "https://api.openai.com/v1",
  Anthropic: "https://api.anthropic.com",
};

export const KNOWN_DEFAULT_URLS = new Set(
  Object.values(PROVIDER_DEFAULT_BASE_URLS).filter(Boolean) as string[],
);

/**
 * Check whether a preset's provider matches the current draft provider.
 * Compatible providers also match their base protocol presets:
 *   OpenAICompatible  → OpenAI, OpenAIResponses
 *   AnthropicCompatible → Anthropic
 */
/**
 * Provider family grouping for preset matching.
 * Anthropic ↔ AnthropicCompatible are interchangeable.
 * OpenAI ↔ OpenAIResponses ↔ OpenAICompatible are interchangeable.
 */
function providerFamily(provider: string): "openai" | "anthropic" | "other" {
  switch (provider) {
    case "OpenAI":
    case "OpenAIResponses":
    case "OpenAICompatible":
      return "openai";
    case "Anthropic":
    case "AnthropicCompatible":
      return "anthropic";
    default:
      return "other";
  }
}

export function presetMatchesProvider(presetProvider: string, draftProvider: ModelProviderKind): boolean {
  return providerFamily(presetProvider) === providerFamily(draftProvider);
}

// ── Draft type for the composer form ─────────────────────────────────────────
export type ModelDraft = {
  displayName: string;
  provider: ModelProviderKind;
  modelId: string;
  baseUrl: string;
  apiKeyEnvironmentVariable: string;
  apiKeyValue: string;
  enabled: boolean;
  capabilities: number;
  contextWindowSize: number;
  maxOutputTokens: number;
  isReasoning: boolean;
  supportsToolCalling: boolean;
  customHeaders: Record<string, string>;
};

export const DEFAULT_MODEL_DRAFT: ModelDraft = {
  displayName: "",
  provider: "OpenAI",
  modelId: "",
  baseUrl: PROVIDER_DEFAULT_BASE_URLS["OpenAI"] ?? "",
  apiKeyEnvironmentVariable: "",
  apiKeyValue: "",
  enabled: true,
  capabilities: CAP_TEXT,
  contextWindowSize: 128000,
  maxOutputTokens: 8192,
  isReasoning: false,
  supportsToolCalling: true,
  customHeaders: {},
};

/** Flattened view of account + one of its models for card display. */
export type AccountModelPair = {
  account: ProviderAccountResponse;
  model: AccountModelResponse;
};

/**
 * Discriminated union of what a composer modal is currently doing.
 * - `create-endpoint`: brand-new ProviderAccount + its first AccountModel (atomic)
 * - `add-model`:       append a new AccountModel under an existing account
 * - `edit-model`:      update an existing AccountModel
 *
 * `edit-endpoint` has been removed — endpoint editing is now inline in the
 * detail panel, controlled by `EndpointDraft` state.
 */
export type ComposerMode =
  | { kind: "create-endpoint" }
  | { kind: "add-model"; account: ProviderAccountResponse }
  | { kind: "edit-model"; pair: AccountModelPair };

/** True when the mode edits model-level fields (modelId, capabilities, …). */
export function showsModelFields(mode: ComposerMode | null): boolean {
  return (
    mode?.kind === "create-endpoint" ||
    mode?.kind === "add-model" ||
    mode?.kind === "edit-model"
  );
}

// ── Endpoint inline-editing draft ───────────────────────────────────────────

/** Draft state for inline endpoint editing in the detail panel. */
export type EndpointDraft = {
  displayName: string;
  baseUrl: string;
  apiKeyValue: string;
  apiKeyEnvironmentVariable: string;
  customHeaders: Record<string, string>;
};

/** Seed an EndpointDraft from an existing ProviderAccount. */
export function toEndpointDraft(account: ProviderAccountResponse): EndpointDraft {
  return {
    displayName: account.displayName,
    baseUrl: account.baseUrl ?? PROVIDER_DEFAULT_BASE_URLS[account.providerKind] ?? "",
    apiKeyValue: "",
    apiKeyEnvironmentVariable: account.apiKeyEnvironmentVariable ?? "",
    customHeaders: account.customHeaders ? { ...account.customHeaders } : {},
  };
}

// ── Helpers ──────────────────────────────────────────────────────────────────

export function toOptionalText(value: string): string | null {
  const next = value.trim();
  return next.length > 0 ? next : null;
}

/** Format large token counts as "128K" / "200K". */
export function fmtK(n: number): string {
  return n >= 1000 ? `${Math.round(n / 1000)}K` : String(n);
}

/** Strip protocol and trailing slash for compact display. */
export function shortBaseUrl(url: string | null | undefined, fallback: string): string {
  if (!url) return fallback;
  return url.replace(/^https?:\/\//, "").replace(/\/$/, "");
}

/** Convert a ModelDraft into a CreateProviderAccountRequest payload. */
export function toCreateRequest(draft: ModelDraft) {
  return {
    displayName: draft.displayName.trim(),
    providerKind: draft.provider,
    baseUrl: toOptionalText(draft.baseUrl),
    apiKeyValue: toOptionalText(draft.apiKeyValue),
    apiKeyEnvironmentVariable: toOptionalText(draft.apiKeyEnvironmentVariable),
    customHeaders:
      Object.keys(draft.customHeaders).length > 0 ? draft.customHeaders : null,
    models: [
      {
        displayName: draft.displayName.trim(),
        modelId: draft.modelId.trim(),
        capabilities: draft.capabilities,
        contextWindowSize: draft.contextWindowSize,
        maxOutputTokens: draft.maxOutputTokens,
        isReasoning: draft.isReasoning,
        supportsToolCalling: draft.supportsToolCalling,
        isDefaultForAccount: true,
        isGlobalDefault: false,
      },
    ],
  };
}

/** Seed a ModelDraft from an existing (account, model) pair for edit-model mode. */
export function toDraft(pair: AccountModelPair): ModelDraft {
  return {
    displayName: pair.model.displayName,
    provider: pair.account.providerKind,
    modelId: pair.model.modelId,
    baseUrl: pair.account.baseUrl ?? "",
    apiKeyEnvironmentVariable: "",
    apiKeyValue: "",
    enabled: pair.model.enabled,
    capabilities: pair.model.capabilities,
    contextWindowSize: pair.model.contextWindowSize,
    maxOutputTokens: pair.model.maxOutputTokens,
    isReasoning: pair.model.isReasoning,
    supportsToolCalling: pair.model.supportsToolCalling,
    customHeaders: {},
  };
}
