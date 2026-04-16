import { FormEvent, useEffect, useMemo, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../lib/queryKeys";
import {
  fetchModelPresets,
  fetchProviderAccounts,
  testModelConnection,
} from "../lib/api";
import { useI18n } from "../i18n/I18nProvider";
import type {
  ModelConnectionTestResponse,
  ModelPreset,
  ModelProviderKind,
  ProviderAccountResponse,
} from "../types/contracts";
import { Modal } from "./ui/Modal";
import { Button } from "./ui/Button";
import { Select } from "./ui/Select";
import { ConfirmModal } from "./ui/ConfirmModal";
import "./ui/Modal.css";
import "./ControlPlaneDesk.css";
import "./models-settings/ModelsSettingsDesk.css";

import { useModelsSettingsText } from "./models-settings/locale";
import { useModelMutations } from "./models-settings/useModelMutations";
import { ProviderSidebar } from "./models-settings/ProviderSidebar";
import { EndpointDetailPanel } from "./models-settings/EndpointDetailPanel";
import { AddModelModal } from "./models-settings/AddModelModal";
import type { CardTestState } from "./models-settings/ModelListItem";
import {
  AccountModelPair,
  ComposerMode,
  DEFAULT_MODEL_DRAFT,
  EndpointDraft,
  ModelDraft,
  KNOWN_DEFAULT_URLS,
  PROVIDER_DEFAULT_BASE_URLS,
  presetMatchesProvider,
  toCreateRequest,
  toDraft,
  toEndpointDraft,
  toOptionalText,
} from "./models-settings/types";

/**
 * Models Atelier — master/detail UI for ProviderAccount + AccountModel.
 *
 * Left sidebar: scrollable list of endpoints with ON/OFF toggles.
 * Right panel: selected endpoint's inline-editable details + model list.
 * Modals: create endpoint, add/edit model, delete confirmations.
 */
export function ModelsSettingsDesk() {
  const { locale } = useI18n();
  const text = useModelsSettingsText();
  const queryClient = useQueryClient();

  const { data: accountsData, isLoading, isFetching, error: queryError } = useQuery({
    queryKey: queryKeys.providerAccounts,
    queryFn: () => fetchProviderAccounts(),
  });

  // ── Selection + inline editing ────────────────────────────────────────────
  const [selectedAccountId, setSelectedAccountId] = useState<string | null>(null);
  const [endpointDraft, setEndpointDraft] = useState<EndpointDraft | null>(null);

  // Auto-select first account when data loads or selectedAccountId becomes stale
  useEffect(() => {
    if (!accountsData?.length) return;
    if (selectedAccountId && accountsData.some((a) => a.id === selectedAccountId)) return;
    setSelectedAccountId(accountsData[0].id);
  }, [accountsData, selectedAccountId]);

  const selectedAccount = useMemo(
    () => accountsData?.find((a) => a.id === selectedAccountId) ?? null,
    [accountsData, selectedAccountId],
  );

  // Flatten accounts → (account, model) pairs for duplicate detection
  const pairs: AccountModelPair[] = useMemo(() => {
    if (!accountsData) return [];
    const result: AccountModelPair[] = [];
    for (const account of accountsData) {
      for (const model of account.models) {
        result.push({ account, model });
      }
    }
    return result;
  }, [accountsData]);

  // ── Composer state (create-endpoint / add-model / edit-model) ─────────────
  const [composerMode, setComposerMode] = useState<ComposerMode | null>(null);
  const [modelDraft, setModelDraft] = useState<ModelDraft>(DEFAULT_MODEL_DRAFT);
  const [presets, setPresets] = useState<ModelPreset[]>([]);
  const [selectedPresetForFill, setSelectedPresetForFill] = useState<ModelPreset | null>(null);

  // Create-endpoint specific state
  const [createTestResult, setCreateTestResult] = useState<ModelConnectionTestResponse | null>(null);
  const [createTesting, setCreateTesting] = useState(false);

  // ── Feedback banners ───────────────────────────────────────────────────────
  const [note, setNote] = useState<string | null>(null);
  const [mutationError, setMutationError] = useState<string | null>(null);
  const [justCreatedId, setJustCreatedId] = useState<string | null>(null);

  // ── Delete confirmations ───────────────────────────────────────────────────
  const [deleteConfirm, setDeleteConfirm] = useState<{ pair: AccountModelPair; name: string } | null>(null);
  const [deleteEndpointConfirm, setDeleteEndpointConfirm] = useState<ProviderAccountResponse | null>(null);

  // ── Per-card quick-test state ──────────────────────────────────────────────
  const [cardTests, setCardTests] = useState<Map<string, CardTestState>>(new Map());

  // ── Mutations ──────────────────────────────────────────────────────────────
  const mutations = useModelMutations();
  const isMutating = mutations.isMutating;
  const error =
    mutationError ??
    mutations.lastError ??
    (queryError instanceof Error ? queryError.message : null);

  useEffect(() => {
    fetchModelPresets().then(setPresets).catch(() => {});
  }, []);

  const isZh = locale === "zh-CN";

  // Duplicate detection for create/add-model
  const isEditingModel = composerMode?.kind === "edit-model";
  const isDuplicate =
    !isEditingModel &&
    composerMode?.kind !== undefined &&
    pairs.some(
      (p) =>
        p.model.modelId === modelDraft.modelId.trim() &&
        (p.account.baseUrl ?? "") === (modelDraft.baseUrl.trim() || ""),
    );

  // ── Helpers ────────────────────────────────────────────────────────────────
  function resetFeedback() {
    setMutationError(null);
    setNote(null);
    setJustCreatedId(null);
  }

  function closeComposer() {
    setComposerMode(null);
    setCreateTestResult(null);
    setSelectedPresetForFill(null);
    setMutationError(null);
  }

  // ── Sidebar callbacks ─────────────────────────────────────────────────────

  function handleSelectAccount(accountId: string) {
    // Cancel any in-progress endpoint editing when switching
    setEndpointDraft(null);
    setSelectedAccountId(accountId);
  }

  async function handleToggleEndpoint(account: ProviderAccountResponse) {
    setNote(null);
    setMutationError(null);
    try {
      await mutations.updateEndpoint.mutateAsync({
        accountId: account.id,
        request: { enabled: !account.enabled },
      });
    } catch (nextError) {
      const detail =
        nextError instanceof Error ? nextError.message : text.errors.updateEndpoint;
      setMutationError(detail);
    }
  }

  // ── Create-endpoint modal ─────────────────────────────────────────────────

  function openCreateModal() {
    resetFeedback();
    setModelDraft(DEFAULT_MODEL_DRAFT);
    setComposerMode({ kind: "create-endpoint" });
  }

  // ── Inline endpoint editing ───────────────────────────────────────────────

  function handleEditEndpoint() {
    if (!selectedAccount) return;
    setEndpointDraft(toEndpointDraft(selectedAccount));
  }

  async function handleSaveEndpoint() {
    if (!selectedAccount || !endpointDraft) return;
    setNote(null);
    setMutationError(null);
    try {
      await mutations.updateEndpoint.mutateAsync({
        accountId: selectedAccount.id,
        request: {
          displayName: endpointDraft.displayName.trim() || null,
          baseUrl: toOptionalText(endpointDraft.baseUrl),
          apiKeyValue: toOptionalText(endpointDraft.apiKeyValue),
          apiKeyEnvironmentVariable: toOptionalText(endpointDraft.apiKeyEnvironmentVariable),
          customHeaders:
            Object.keys(endpointDraft.customHeaders).length > 0
              ? endpointDraft.customHeaders
              : null,
        },
      });
      setNote(text.notes.endpointUpdated);
      setEndpointDraft(null);
    } catch (nextError) {
      const detail =
        nextError instanceof Error ? nextError.message : text.errors.updateEndpoint;
      setMutationError(detail);
    }
  }

  // ── Add/edit model ────────────────────────────────────────────────────────

  function handleAddModelToAccount(account: ProviderAccountResponse) {
    resetFeedback();
    setModelDraft({
      ...DEFAULT_MODEL_DRAFT,
      provider: account.providerKind,
      baseUrl: account.baseUrl ?? PROVIDER_DEFAULT_BASE_URLS[account.providerKind] ?? "",
    });
    setSelectedPresetForFill(null);
    setComposerMode({ kind: "add-model", account });
  }

  function handleEditModel(pair: AccountModelPair) {
    resetFeedback();
    setModelDraft(toDraft(pair));
    setSelectedPresetForFill(null);
    setComposerMode({ kind: "edit-model", pair });
  }

  function handleDuplicateModel(pair: AccountModelPair) {
    resetFeedback();
    const draft = toDraft(pair);
    draft.displayName = `${draft.displayName} (Copy)`;
    setModelDraft(draft);
    setSelectedPresetForFill(null);
    setComposerMode({ kind: "add-model", account: pair.account });
  }

  // ── Submit handler — routes to the right mutation ─────────────────────────
  async function handleComposerSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!composerMode) return;
    setNote(null);
    setMutationError(null);

    try {
      switch (composerMode.kind) {
        case "create-endpoint": {
          const created = await mutations.createEndpoint.mutateAsync(
            toCreateRequest(modelDraft),
          );
          const firstModel = created.models[0];
          if (firstModel) setJustCreatedId(firstModel.id);
          setNote(text.notes.modelCreated);
          setSelectedAccountId(created.id);
          closeComposer();
          break;
        }
        case "add-model": {
          const created = await mutations.createModel.mutateAsync({
            accountId: composerMode.account.id,
            request: {
              displayName: modelDraft.displayName.trim(),
              modelId: modelDraft.modelId.trim(),
              capabilities: modelDraft.capabilities,
              contextWindowSize: modelDraft.contextWindowSize,
              maxOutputTokens: modelDraft.maxOutputTokens,
              isReasoning: modelDraft.isReasoning,
              supportsToolCalling: modelDraft.supportsToolCalling,
              isDefaultForAccount: false,
              isGlobalDefault: false,
            },
          });
          setJustCreatedId(created.id);
          setNote(text.notes.modelCreated);
          closeComposer();
          break;
        }
        case "edit-model": {
          await mutations.updateModel.mutateAsync({
            accountId: composerMode.pair.account.id,
            modelId: composerMode.pair.model.id,
            request: {
              displayName: modelDraft.displayName.trim(),
              modelId: modelDraft.modelId.trim(),
              capabilities: modelDraft.capabilities,
              contextWindowSize: modelDraft.contextWindowSize,
              maxOutputTokens: modelDraft.maxOutputTokens,
              isReasoning: modelDraft.isReasoning,
              supportsToolCalling: modelDraft.supportsToolCalling,
              enabled: modelDraft.enabled,
            },
          });
          setNote(text.notes.modelUpdated);
          closeComposer();
          break;
        }
      }
    } catch (nextError) {
      const fallback =
        composerMode.kind === "create-endpoint" || composerMode.kind === "add-model"
          ? text.errors.createModel
          : text.errors.updateModel;
      const detail = nextError instanceof Error ? nextError.message : fallback;
      setMutationError(detail);
    }
  }

  async function handleSetDefault(pair: AccountModelPair) {
    setNote(null);
    setMutationError(null);
    try {
      await mutations.setDefault.mutateAsync({
        accountId: pair.account.id,
        modelId: pair.model.id,
      });
      setNote(text.notes.modelDefaultSwitched);
    } catch (nextError) {
      const detail =
        nextError instanceof Error ? nextError.message : text.errors.setDefault;
      setMutationError(detail);
    }
  }

  async function handleDeleteModel(pair: AccountModelPair) {
    setNote(null);
    setMutationError(null);
    try {
      await mutations.deleteModel.mutateAsync({
        accountId: pair.account.id,
        modelId: pair.model.id,
      });
      setNote(text.notes.modelDeleted);
    } catch (nextError) {
      const detail =
        nextError instanceof Error ? nextError.message : text.errors.deleteModel;
      setMutationError(detail);
    }
  }

  async function handleDeleteEndpoint(account: ProviderAccountResponse) {
    setNote(null);
    setMutationError(null);
    try {
      await mutations.deleteEndpoint.mutateAsync(account.id);
      setNote(text.notes.endpointDeleted);
      if (selectedAccountId === account.id) {
        setSelectedAccountId(null);
        setEndpointDraft(null);
      }
    } catch (nextError) {
      const detail =
        nextError instanceof Error ? nextError.message : text.errors.deleteEndpoint;
      setMutationError(detail);
    }
  }

  function handleQuickTest(pair: AccountModelPair) {
    const key = pair.model.id;
    setCardTests((prev) => new Map(prev).set(key, { testing: true, result: null }));
    testModelConnection({
      accountId: pair.account.id,
      modelId: pair.model.modelId,
      baseUrl: pair.account.baseUrl ?? undefined,
      apiKey: "",
      provider: pair.account.providerKind,
    })
      .then((result) =>
        setCardTests((prev) => new Map(prev).set(key, { testing: false, result })),
      )
      .catch(() =>
        setCardTests((prev) =>
          new Map(prev).set(key, {
            testing: false,
            result: { ok: false, latencyMs: 0, error: "network_error" },
          }),
        ),
      );
  }

  // ── Create-endpoint modal state helpers ───────────────────────────────────
  const isThirdParty =
    modelDraft.provider === "OpenAICompatible" ||
    modelDraft.provider === "AnthropicCompatible";
  const thirdPartyProtocol: "OpenAI" | "Anthropic" =
    modelDraft.provider === "AnthropicCompatible" ? "Anthropic" : "OpenAI";

  const createSubmitDisabled =
    isMutating ||
    modelDraft.displayName.trim().length === 0 ||
    modelDraft.modelId.trim().length === 0 ||
    !createTestResult?.ok;

  // ── Render ────────────────────────────────────────────────────────────────
  return (
    <section data-testid="models-settings-desk">
      <h2 className="desk-section-title">{text.title}</h2>
      <p className="desk-section-desc">{text.intro}</p>

      {error ? (
        <p className="desk-feedback desk-feedback--error" data-testid="models-settings-error">
          {error}
        </p>
      ) : null}
      {note ? (
        <div className="desk-feedback desk-feedback--success" data-testid="models-settings-note">
          {note}
          {(() => {
            if (!justCreatedId) return null;
            const createdPair = pairs.find((p) => p.model.id === justCreatedId);
            if (!createdPair || createdPair.model.isGlobalDefault) return null;
            return (
              <span style={{ marginLeft: "var(--space-3)" }}>
                {text.composer.setAsDefaultPrompt}{" "}
                <button
                  className="ob-hint-link"
                  style={{ background: "none", border: "none", cursor: "pointer", padding: 0 }}
                  onClick={() => {
                    void handleSetDefault(createdPair);
                    setJustCreatedId(null);
                  }}
                >
                  {text.composer.setAsDefaultDone}
                </button>
              </span>
            );
          })()}
        </div>
      ) : null}

      {/* Master/detail layout */}
      <div className="control-plane-pane-shell models-desk__layout">
        <ProviderSidebar
          accounts={accountsData}
          isLoading={isLoading}
          isFetching={isFetching}
          isMutating={isMutating}
          selectedAccountId={selectedAccountId}
          text={text}
          isZh={isZh}
          onSelect={handleSelectAccount}
          onToggleEnabled={(a) => void handleToggleEndpoint(a)}
          onCreateEndpoint={openCreateModal}
          onRefresh={() =>
            void queryClient.invalidateQueries({ queryKey: queryKeys.providerAccounts })
          }
        />

        <EndpointDetailPanel
          account={selectedAccount}
          endpointDraft={endpointDraft}
          setEndpointDraft={setEndpointDraft}
          isMutating={isMutating}
          text={text}
          isZh={isZh}
          cardTests={cardTests}
          onEditEndpoint={handleEditEndpoint}
          onSaveEndpoint={() => void handleSaveEndpoint()}
          onCancelEdit={() => setEndpointDraft(null)}
          onDeleteEndpoint={(a) => setDeleteEndpointConfirm(a)}
          onAddModel={handleAddModelToAccount}
          onEditModel={handleEditModel}
          onDuplicateModel={handleDuplicateModel}
          onSetDefault={(pair) => void handleSetDefault(pair)}
          onDeleteModel={(pair, name) => setDeleteConfirm({ pair, name })}
          onQuickTest={handleQuickTest}
        />
      </div>

      {/* ── Create Endpoint Modal ──────────────────────────────────────────── */}
      {composerMode?.kind === "create-endpoint" && <Modal
        open
        title={text.sections.modelComposerCreate}
        onClose={closeComposer}
        width={560}
        footer={
          <>
            <Button variant="ghost" onClick={closeComposer} disabled={isMutating}>
              {text.composer.cancel}
            </Button>
            <Button
              type="submit"
              form="create-endpoint-form"
              variant="primary"
              data-testid="model-composer-submit"
              disabled={createSubmitDisabled}
              title={!createTestResult?.ok ? text.composer.createRequiresTest : undefined}
            >
              {isMutating ? text.common.loading : text.composer.create}
            </Button>
          </>
        }
      >
        <div data-testid="model-detail">
          <form id="create-endpoint-form" className="bootstrap-form" onSubmit={handleComposerSubmit}>
            {/* Provider selector */}
            <label className="bootstrap-form__field">
              <span className="bootstrap-form__label">{text.composer.provider}</span>
              <Select
                data-testid="model-provider"
                value={isThirdParty ? "__third__" : modelDraft.provider === "OpenAIResponses" ? "OpenAI" : modelDraft.provider}
                onChange={(e) => {
                  const val = e.target.value;
                  if (val === "__third__") {
                    setModelDraft((c) => ({ ...c, provider: "OpenAICompatible", baseUrl: "" }));
                  } else {
                    const next = val as ModelProviderKind;
                    const defaultUrl = PROVIDER_DEFAULT_BASE_URLS[next] ?? "";
                    setModelDraft((c) => ({
                      ...c,
                      provider: next,
                      baseUrl: c.baseUrl === "" || KNOWN_DEFAULT_URLS.has(c.baseUrl) ? defaultUrl : c.baseUrl,
                    }));
                  }
                  setSelectedPresetForFill(null);
                  setCreateTestResult(null);
                }}
              >
                <option value="OpenAI">{text.providerLabels.OpenAI}</option>
                <option value="Anthropic">{text.providerLabels.Anthropic}</option>
                <option value="__third__">{text.composer.thirdParty}</option>
              </Select>
            </label>

            {/* Protocol toggle (third-party only) */}
            {isThirdParty && (
              <div className="bootstrap-form__field">
                <span className="bootstrap-form__label">{text.composer.protocol}</span>
                <div className="ob-protocol-tabs">
                  {(["OpenAI", "Anthropic"] as const).map((p) => (
                    <button
                      key={p}
                      type="button"
                      className={`ob-protocol-tab ${thirdPartyProtocol === p ? "is-active" : ""}`}
                      onClick={() => {
                        const nextProvider: ModelProviderKind =
                          p === "Anthropic" ? "AnthropicCompatible" : "OpenAICompatible";
                        setModelDraft((c) => {
                          const nextBaseUrl =
                            p === "Anthropic"
                              ? selectedPresetForFill?.anthropicBaseUrl ?? selectedPresetForFill?.baseUrl ?? c.baseUrl
                              : selectedPresetForFill?.baseUrl ?? c.baseUrl;
                          return { ...c, provider: nextProvider, baseUrl: nextBaseUrl ?? "" };
                        });
                        setSelectedPresetForFill(null);
                        setCreateTestResult(null);
                      }}
                    >
                      {p === "OpenAI" ? text.composer.protocolOpenAI : text.composer.protocolAnthropic}
                    </button>
                  ))}
                </div>
              </div>
            )}

            {/* Preset picker */}
            <div className="bootstrap-form__field">
              <span className="bootstrap-form__label">{text.composer.presetSection}</span>
              <Select
                data-testid="preset-model-select"
                value={selectedPresetForFill?.presetId ?? ""}
                onChange={(e) => {
                  const preset = presets.find((p) => p.presetId === e.target.value);
                  if (preset) {
                    setSelectedPresetForFill(preset);
                    const baseUrl =
                      thirdPartyProtocol === "Anthropic" && preset.anthropicBaseUrl
                        ? preset.anthropicBaseUrl
                        : preset.baseUrl ?? PROVIDER_DEFAULT_BASE_URLS[preset.provider as ModelProviderKind] ?? "";
                    setModelDraft((c) => ({
                      ...c,
                      displayName: c.displayName || preset.displayName,
                      provider: preset.provider as ModelProviderKind,
                      modelId: preset.modelId,
                      baseUrl,
                      capabilities: preset.defaultCapabilities,
                      contextWindowSize: preset.contextWindowSize ?? c.contextWindowSize,
                      maxOutputTokens: preset.maxOutputTokens ?? c.maxOutputTokens,
                      isReasoning: preset.isReasoning ?? c.isReasoning,
                      supportsToolCalling: preset.supportsToolCalling ?? c.supportsToolCalling,
                    }));
                    setCreateTestResult(null);
                  } else {
                    setSelectedPresetForFill(null);
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
              {selectedPresetForFill?.description && (
                <span className="ob-hint" style={{ marginTop: "var(--space-1)", display: "block" }}>
                  {selectedPresetForFill.description}
                </span>
              )}
            </div>

            {/* Display name */}
            <label className="bootstrap-form__field">
              <span className="bootstrap-form__label">{text.composer.displayName}</span>
              <input
                data-testid="model-display-name"
                className="kc-input"
                value={modelDraft.displayName}
                onChange={(e) => setModelDraft((c) => ({ ...c, displayName: e.target.value }))}
              />
            </label>

            {/* Model ID */}
            <label className="bootstrap-form__field">
              <span className="bootstrap-form__label">{text.composer.modelId}</span>
              <input
                data-testid="model-id"
                className="kc-input"
                value={modelDraft.modelId}
                onChange={(e) => setModelDraft((c) => ({ ...c, modelId: e.target.value }))}
              />
            </label>

            {/* Base URL */}
            <label className="bootstrap-form__field">
              <span className="bootstrap-form__label">
                {modelDraft.provider.includes("Compatible") ? text.composer.baseUrlRequired : text.composer.baseUrl}
              </span>
              <input
                data-testid="model-base-url"
                className="kc-input"
                value={modelDraft.baseUrl}
                onChange={(e) => setModelDraft((c) => ({ ...c, baseUrl: e.target.value }))}
              />
            </label>

            {/* API Key */}
            <label className="bootstrap-form__field">
              <span className="bootstrap-form__label">{text.composer.apiKey}</span>
              <input
                data-testid="model-api-key-value"
                type="password"
                autoComplete="new-password"
                className="kc-input"
                placeholder={text.composer.apiKeyPlaceholder}
                value={modelDraft.apiKeyValue}
                onChange={(e) => setModelDraft((c) => ({ ...c, apiKeyValue: e.target.value }))}
              />
            </label>

            {/* Duplicate warning */}
            {isDuplicate && (
              <p className="desk-feedback desk-feedback--error" style={{ marginBottom: "var(--space-2)" }}>
                {text.composer.duplicateWarning}
              </p>
            )}

            {/* Test connection */}
            <div className="bootstrap-form__field">
              <Button
                variant="secondary"
                data-testid="test-connection-btn"
                onClick={() => {
                  setCreateTesting(true);
                  setCreateTestResult(null);
                  testModelConnection({
                    presetId: selectedPresetForFill?.presetId,
                    modelId: modelDraft.modelId || undefined,
                    apiKey: modelDraft.apiKeyValue.trim(),
                    baseUrl: modelDraft.baseUrl || undefined,
                    provider: modelDraft.provider,
                  })
                    .then(setCreateTestResult)
                    .catch(() =>
                      setCreateTestResult({ ok: false, latencyMs: 0, error: "network_error" }),
                    )
                    .finally(() => setCreateTesting(false));
                }}
                disabled={createTesting || !modelDraft.modelId}
              >
                {createTesting ? text.composer.testingConnection : text.composer.testConnection}
              </Button>
              {createTestResult && (
                <div
                  className={`desk-feedback ${createTestResult.ok ? "desk-feedback--success" : "desk-feedback--error"}`}
                  data-testid="connection-result"
                >
                  {createTestResult.ok ? (
                    <span>{text.composer.connectionOk}（{createTestResult.latencyMs}ms）</span>
                  ) : (
                    <span>
                      {text.composer.connectionFail}
                      {createTestResult.error
                        ? `：${text.composer.errorCodes[createTestResult.error] ?? createTestResult.error}`
                        : ""}
                    </span>
                  )}
                </div>
              )}
            </div>
          </form>
        </div>
      </Modal>}

      {/* ── Add/Edit Model Modal ───────────────────────────────────────────── */}
      <AddModelModal
        mode={
          composerMode?.kind === "add-model" || composerMode?.kind === "edit-model"
            ? (composerMode as Extract<ComposerMode, { kind: "add-model" | "edit-model" }>)
            : null
        }
        modelDraft={modelDraft}
        setModelDraft={setModelDraft}
        presets={presets}
        selectedPreset={selectedPresetForFill}
        setSelectedPreset={setSelectedPresetForFill}
        isDuplicate={isDuplicate}
        isMutating={isMutating}
        text={text}
        onSubmit={handleComposerSubmit}
        onClose={closeComposer}
      />

      {/* ── Delete Confirmations ───────────────────────────────────────────── */}
      <ConfirmModal
        open={deleteConfirm !== null}
        title={text.modelList.delete}
        description={`确认删除模型 "${deleteConfirm?.name}" 吗？此操作不可撤销。`}
        confirmLabel={text.modelList.delete}
        variant="danger"
        busy={isMutating}
        onConfirm={() => {
          if (deleteConfirm) void handleDeleteModel(deleteConfirm.pair);
          setDeleteConfirm(null);
        }}
        onCancel={() => setDeleteConfirm(null)}
      />

      <ConfirmModal
        open={deleteEndpointConfirm !== null}
        title={text.sections.deleteEndpointButton}
        description={text.sections.deleteEndpointConfirm.replace(
          "{name}",
          deleteEndpointConfirm?.displayName ?? "",
        )}
        confirmLabel={text.sections.deleteEndpointButton}
        variant="danger"
        busy={isMutating}
        onConfirm={() => {
          if (deleteEndpointConfirm) void handleDeleteEndpoint(deleteEndpointConfirm);
          setDeleteEndpointConfirm(null);
        }}
        onCancel={() => setDeleteEndpointConfirm(null)}
      />
    </section>
  );
}
