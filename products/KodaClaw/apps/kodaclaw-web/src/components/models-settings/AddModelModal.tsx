import { FormEvent } from "react";
import { Modal } from "../ui/Modal";
import { Button } from "../ui/Button";
import { Select } from "../ui/Select";
import type { ModelPreset, ModelProviderKind } from "../../types/contracts";
import type { ModelsSettingsText } from "./locale";
import {
  CAP_AUDIO,
  CAP_FILE,
  CAP_IMAGE,
  CAP_TEXT,
  CAP_VIDEO,
  PROVIDER_DEFAULT_BASE_URLS,
  presetMatchesProvider,
  type ComposerMode,
  type ModelDraft,
} from "./types";

export interface AddModelModalProps {
  mode: Extract<ComposerMode, { kind: "add-model" | "edit-model" }> | null;
  modelDraft: ModelDraft;
  setModelDraft: React.Dispatch<React.SetStateAction<ModelDraft>>;
  presets: ModelPreset[];
  selectedPreset: ModelPreset | null;
  setSelectedPreset: (preset: ModelPreset | null) => void;
  isDuplicate: boolean;
  isMutating: boolean;
  text: ModelsSettingsText;
  onSubmit: (e: FormEvent<HTMLFormElement>) => void;
  onClose: () => void;
}

export function AddModelModal({
  mode,
  modelDraft,
  setModelDraft,
  presets,
  selectedPreset,
  setSelectedPreset,
  isDuplicate,
  isMutating,
  text,
  onSubmit,
  onClose,
}: AddModelModalProps) {
  if (!mode) return null;

  const isEditing = mode.kind === "edit-model";
  const account = mode.kind === "add-model" ? mode.account : mode.pair.account;

  const modalTitle = isEditing
    ? `${text.sections.modelComposerEditModel} · ${mode.pair.model.displayName}`
    : `${text.sections.modelComposerAddToAccount} · ${text.providerLabels[account.providerKind]}`;

  const submitLabel = isMutating
    ? text.common.loading
    : isEditing
      ? text.composer.save
      : text.sections.addModelButton;

  const submitDisabled =
    isMutating ||
    modelDraft.displayName.trim().length === 0 ||
    modelDraft.modelId.trim().length === 0;

  // Determine protocol for filtering presets with anthropicBaseUrl
  const thirdPartyProtocol: "OpenAI" | "Anthropic" =
    modelDraft.provider === "AnthropicCompatible" ? "Anthropic" : "OpenAI";

  return (
    <Modal
      open
      title={modalTitle}
      onClose={onClose}
      width={480}
      footer={
        <>
          <Button variant="ghost" onClick={onClose} disabled={isMutating}>
            {text.composer.cancel}
          </Button>
          <Button
            type="submit"
            form="add-model-form"
            variant="primary"
            data-testid="model-composer-submit"
            disabled={submitDisabled}
          >
            {submitLabel}
          </Button>
        </>
      }
    >
      <form id="add-model-form" className="bootstrap-form" onSubmit={onSubmit}>
        {/* Add-model hint */}
        {!isEditing && (
          <p
            className="desk-feedback"
            data-testid="add-model-hint"
            style={{ marginBottom: "var(--space-3)" }}
          >
            {text.sections.addModelHint}
          </p>
        )}

        {/* Preset picker (filtered by account provider) */}
        <div className="bootstrap-form__field">
          <span className="bootstrap-form__label">{text.composer.presetSection}</span>
          <Select
            data-testid="preset-model-select"
            value={selectedPreset?.presetId ?? ""}
            onChange={(e) => {
              const preset = presets.find((p) => p.presetId === e.target.value);
              if (preset) {
                setSelectedPreset(preset);
                const baseUrl =
                  thirdPartyProtocol === "Anthropic" && preset.anthropicBaseUrl
                    ? preset.anthropicBaseUrl
                    : preset.baseUrl ??
                      PROVIDER_DEFAULT_BASE_URLS[preset.provider as ModelProviderKind] ??
                      "";
                setModelDraft((current) => ({
                  ...current,
                  displayName: current.displayName || preset.displayName,
                  provider: preset.provider as ModelProviderKind,
                  modelId: preset.modelId,
                  baseUrl,
                  capabilities: preset.defaultCapabilities,
                  contextWindowSize: preset.contextWindowSize ?? current.contextWindowSize,
                  maxOutputTokens: preset.maxOutputTokens ?? current.maxOutputTokens,
                  isReasoning: preset.isReasoning ?? current.isReasoning,
                  supportsToolCalling:
                    preset.supportsToolCalling ?? current.supportsToolCalling,
                }));
              } else {
                setSelectedPreset(null);
              }
            }}
          >
            <option value="">{text.composer.presetPlaceholder}</option>
            {presets
              .filter((p) => presetMatchesProvider(p.provider, modelDraft.provider))
              .map((p) => (
                <option key={p.presetId} value={p.presetId}>
                  {p.displayName} ({p.tier})
                </option>
              ))}
          </Select>
          {selectedPreset?.description && (
            <span className="ob-hint" style={{ marginTop: "var(--space-1)", display: "block" }}>
              {selectedPreset.description}
            </span>
          )}
        </div>

        {/* Model ID */}
        <label className="bootstrap-form__field">
          <span className="bootstrap-form__label">{text.composer.modelId}</span>
          <input
            data-testid="model-id"
            className="kc-input"
            value={modelDraft.modelId}
            readOnly={isEditing}
            onChange={(e) =>
              setModelDraft((c) => ({ ...c, modelId: e.target.value }))
            }
          />
        </label>

        {/* Display name */}
        <label className="bootstrap-form__field">
          <span className="bootstrap-form__label">{text.composer.displayName}</span>
          <input
            data-testid="model-display-name"
            className="kc-input"
            value={modelDraft.displayName}
            onChange={(e) =>
              setModelDraft((c) => ({ ...c, displayName: e.target.value }))
            }
          />
        </label>

        {/* Duplicate warning */}
        {isDuplicate && (
          <p
            className="desk-feedback desk-feedback--error"
            style={{ marginBottom: "var(--space-2)" }}
          >
            {text.composer.duplicateWarning}
          </p>
        )}

        {/* More settings fold */}
        <details className="bootstrap-form__advanced">
          <summary className="bootstrap-form__advanced-toggle">
            {text.detailPanel.moreSettings}
          </summary>

          {/* Capabilities */}
          <div className="bootstrap-form__field" style={{ marginTop: "var(--space-2)" }}>
            <span className="bootstrap-form__label">{text.composer.capabilitiesTitle}</span>
            {(
              [
                [CAP_TEXT, text.composer.capText],
                [CAP_IMAGE, text.composer.capImage],
                [CAP_VIDEO, text.composer.capVideo],
                [CAP_FILE, text.composer.capFile],
                [CAP_AUDIO, text.composer.capAudio],
              ] as [number, string][]
            ).map(([flag, label]) => (
              <label key={flag} className="bootstrap-form__toggle">
                <input
                  type="checkbox"
                  checked={Boolean(modelDraft.capabilities & flag)}
                  onChange={(e) =>
                    setModelDraft((c) => ({
                      ...c,
                      capabilities: e.target.checked
                        ? c.capabilities | flag
                        : c.capabilities & ~flag,
                    }))
                  }
                />
                <span>{label}</span>
              </label>
            ))}
          </div>

          {/* Context window + max output */}
          <label className="bootstrap-form__field">
            <span className="bootstrap-form__label">{text.composer.contextWindowSize}</span>
            <input
              data-testid="model-context-window-size"
              type="number"
              min={1}
              className="kc-input"
              value={modelDraft.contextWindowSize}
              onChange={(e) =>
                setModelDraft((c) => ({
                  ...c,
                  contextWindowSize: Number(e.target.value) || c.contextWindowSize,
                }))
              }
            />
          </label>
          <label className="bootstrap-form__field">
            <span className="bootstrap-form__label">{text.composer.maxOutputTokens}</span>
            <input
              data-testid="model-max-output-tokens"
              type="number"
              min={1}
              className="kc-input"
              value={modelDraft.maxOutputTokens}
              onChange={(e) =>
                setModelDraft((c) => ({
                  ...c,
                  maxOutputTokens: Number(e.target.value) || c.maxOutputTokens,
                }))
              }
            />
          </label>

          {/* Reasoning + tool calling toggles */}
          <label className="bootstrap-form__toggle" style={{ marginTop: "var(--space-1)" }}>
            <input
              data-testid="model-is-reasoning"
              type="checkbox"
              checked={modelDraft.isReasoning}
              onChange={(e) =>
                setModelDraft((c) => ({ ...c, isReasoning: e.target.checked }))
              }
            />
            <span>{text.composer.isReasoning}</span>
          </label>
          <label className="bootstrap-form__toggle">
            <input
              data-testid="model-supports-tool-calling"
              type="checkbox"
              checked={modelDraft.supportsToolCalling}
              onChange={(e) =>
                setModelDraft((c) => ({ ...c, supportsToolCalling: e.target.checked }))
              }
            />
            <span>{text.composer.supportsToolCalling}</span>
          </label>

          {/* Enabled toggle (edit-model only) */}
          {isEditing && (
            <label className="bootstrap-form__toggle">
              <input
                type="checkbox"
                checked={modelDraft.enabled}
                onChange={(e) =>
                  setModelDraft((c) => ({ ...c, enabled: e.target.checked }))
                }
              />
              <span>{text.composer.enabled}</span>
            </label>
          )}
        </details>
      </form>
    </Modal>
  );
}
