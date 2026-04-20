import React, { useState } from "react";
import { Edit3, Inbox, Plus, Trash2 } from "lucide-react";
import { Button } from "../ui/Button";
import { EmptyState } from "../ui/EmptyState";
import { testModelConnection } from "../../lib/api";
import type {
  ModelConnectionTestResponse,
  ProviderAccountResponse,
} from "../../types/contracts";
import type { ModelsSettingsText } from "./locale";
import { ModelListItem, type CardTestState } from "./ModelListItem";
import type { AccountModelPair, EndpointDraft } from "./types";

export interface EndpointDetailPanelProps {
  account: ProviderAccountResponse | null;
  endpointDraft: EndpointDraft | null;
  setEndpointDraft: React.Dispatch<React.SetStateAction<EndpointDraft | null>>;
  isMutating: boolean;
  text: ModelsSettingsText;
  isZh: boolean;
  cardTests: Map<string, CardTestState>;

  onEditEndpoint: () => void;
  onSaveEndpoint: () => void;
  onCancelEdit: () => void;
  onDeleteEndpoint: (account: ProviderAccountResponse) => void;

  onAddModel: (account: ProviderAccountResponse) => void;
  onEditModel: (pair: AccountModelPair) => void;
  onDuplicateModel: (pair: AccountModelPair) => void;
  onSetDefault: (pair: AccountModelPair) => void;
  onDeleteModel: (pair: AccountModelPair, name: string) => void;
  onQuickTest: (pair: AccountModelPair) => void;
}

export function EndpointDetailPanel({
  account,
  endpointDraft,
  setEndpointDraft,
  isMutating,
  text,
  isZh,
  cardTests,
  onEditEndpoint,
  onSaveEndpoint,
  onCancelEdit,
  onDeleteEndpoint,
  onAddModel,
  onEditModel,
  onDuplicateModel,
  onSetDefault,
  onDeleteModel,
  onQuickTest,
}: EndpointDetailPanelProps) {
  const [testResult, setTestResult] = useState<ModelConnectionTestResponse | null>(null);
  const [testingConnection, setTestingConnection] = useState(false);

  if (!account) {
    return (
      <div className="control-plane-pane-stage models-detail" data-testid="models-detail-empty">
        <div className="control-plane-stage-hero">
          <EmptyState
            icon={<Inbox size={28} strokeWidth={1.5} />}
            title={text.detailPanel.emptySelect}
          />
        </div>
      </div>
    );
  }

  const isEditing = endpointDraft !== null;
  const providerLabel = text.providerLabels[account.providerKind];

  function handleTestConnection() {
    if (!endpointDraft || !account) return;
    setTestingConnection(true);
    setTestResult(null);
    testModelConnection({
      modelId: account.models[0]?.modelId ?? "",
      apiKey: endpointDraft.apiKeyValue.trim(),
      baseUrl: endpointDraft.baseUrl || undefined,
      provider: account.providerKind,
    })
      .then(setTestResult)
      .catch(() =>
        setTestResult({ ok: false, latencyMs: 0, error: "network_error" }),
      )
      .finally(() => setTestingConnection(false));
  }

  return (
    <div className="control-plane-pane-stage models-detail" data-testid="models-detail">
      {/* Header */}
      <div className="models-detail__header">
        <div className="models-detail__header-left">
          {isEditing ? (
            <input
              className="kc-input models-detail__header-name"
              value={endpointDraft.displayName}
              onChange={(e) =>
                setEndpointDraft((d) => d && { ...d, displayName: e.target.value })
              }
            />
          ) : (
            <span className="models-detail__header-name">{account.displayName}</span>
          )}
          <span className="models-sidebar-item__provider-tag">{providerLabel}</span>
          {!account.enabled && (
            <span className="control-plane-chip control-plane-chip--inactive">
              {text.detail.disabled}
            </span>
          )}
        </div>
        {!isEditing && (
          <div className="models-detail__header-actions">
            <Button
              variant="ghost"
              size="sm"
              data-testid={`edit-endpoint-${account.id}`}
              onClick={onEditEndpoint}
              disabled={isMutating}
              style={{ display: "inline-flex", alignItems: "center", gap: "var(--space-1)" }}
            >
              <Edit3 size={12} strokeWidth={2.5} />
              {text.detailPanel.editInline}
            </Button>
            <Button
              variant="ghost"
              size="sm"
              data-testid={`delete-endpoint-${account.id}`}
              onClick={() => onDeleteEndpoint(account)}
              disabled={isMutating}
              className="btn--danger-text"
              style={{ display: "inline-flex", alignItems: "center", gap: "var(--space-1)" }}
            >
              <Trash2 size={12} strokeWidth={2.5} />
              {text.sections.deleteEndpointButton}
            </Button>
          </div>
        )}
      </div>

      {/* Endpoint info (read-only or editing) */}
      {isEditing ? (
        <div className="models-detail__edit-form">
          <label className="bootstrap-form__field">
            <span className="bootstrap-form__label">
              {account.providerKind.includes("Compatible")
                ? text.composer.baseUrlRequired
                : text.composer.baseUrl}
            </span>
            <input
              data-testid="model-base-url"
              className="kc-input"
              value={endpointDraft.baseUrl}
              onChange={(e) =>
                setEndpointDraft((d) => d && { ...d, baseUrl: e.target.value })
              }
            />
          </label>

          <label className="bootstrap-form__field">
            <span className="bootstrap-form__label">{text.detailPanel.apiKeyStatus}</span>
            {account.hasApiKey && !endpointDraft.apiKeyValue ? (
              <div className="api-key-configured-row">
                <span className="api-key-configured-badge" data-testid="model-api-key-configured">
                  {text.composer.apiKeyConfigured}
                </span>
                <Button
                  variant="link"
                  onClick={() =>
                    setEndpointDraft((d) => d && { ...d, apiKeyValue: " " })
                  }
                >
                  {text.composer.replaceKey}
                </Button>
              </div>
            ) : (
              <input
                data-testid="model-api-key-value"
                type="password"
                autoComplete="new-password"
                className="kc-input"
                placeholder={text.composer.apiKeyPlaceholder}
                value={endpointDraft.apiKeyValue}
                onChange={(e) =>
                  setEndpointDraft((d) => d && { ...d, apiKeyValue: e.target.value })
                }
              />
            )}
          </label>

          {/* Advanced: env var + custom headers */}
          <details className="bootstrap-form__advanced">
            <summary className="bootstrap-form__advanced-toggle">
              {text.composer.advancedOptions}
            </summary>
            <label className="bootstrap-form__field" style={{ marginTop: "var(--space-2)" }}>
              <span className="bootstrap-form__label">{text.composer.apiKeyEnv}</span>
              <input
                data-testid="model-api-key-env"
                className="kc-input"
                value={endpointDraft.apiKeyEnvironmentVariable}
                onChange={(e) =>
                  setEndpointDraft((d) =>
                    d && { ...d, apiKeyEnvironmentVariable: e.target.value },
                  )
                }
              />
            </label>
            <div className="bootstrap-form__field" style={{ marginTop: "var(--space-2)" }}>
              <span className="bootstrap-form__label">{text.composer.customHeadersLabel}</span>
              {Object.entries(endpointDraft.customHeaders).map(([key, value]) => (
                <div
                  key={key}
                  style={{
                    display: "flex",
                    gap: "var(--space-1)",
                    marginBottom: "var(--space-1)",
                  }}
                >
                  <input
                    className="kc-input"
                    style={{ flex: 1 }}
                    placeholder={text.composer.customHeadersKeyPlaceholder}
                    value={key}
                    onChange={(e) => {
                      const newKey = e.target.value;
                      setEndpointDraft((d) => {
                        if (!d) return d;
                        const next = { ...d.customHeaders };
                        delete next[key];
                        if (newKey) next[newKey] = value;
                        return { ...d, customHeaders: next };
                      });
                    }}
                  />
                  <input
                    className="kc-input"
                    style={{ flex: 1 }}
                    placeholder={text.composer.customHeadersValuePlaceholder}
                    value={value}
                    onChange={(e) => {
                      setEndpointDraft((d) =>
                        d && {
                          ...d,
                          customHeaders: { ...d.customHeaders, [key]: e.target.value },
                        },
                      );
                    }}
                  />
                  <Button
                    variant="ghost"
                    size="sm"
                    onClick={() => {
                      setEndpointDraft((d) => {
                        if (!d) return d;
                        const next = { ...d.customHeaders };
                        delete next[key];
                        return { ...d, customHeaders: next };
                      });
                    }}
                  >
                    {text.composer.removeHeader}
                  </Button>
                </div>
              ))}
              <Button
                variant="ghost"
                size="sm"
                data-testid="add-custom-header"
                onClick={() =>
                  setEndpointDraft((d) =>
                    d && { ...d, customHeaders: { ...d.customHeaders, "": "" } },
                  )
                }
              >
                {text.composer.addHeader}
              </Button>
            </div>
          </details>

          {/* Test connection */}
          <div className="bootstrap-form__field">
            <Button
              variant="secondary"
              data-testid="test-connection-btn"
              onClick={handleTestConnection}
              disabled={testingConnection}
            >
              {testingConnection ? text.composer.testingConnection : text.composer.testConnection}
            </Button>
            {testResult && (
              <div
                className={`desk-feedback ${testResult.ok ? "desk-feedback--success" : "desk-feedback--error"}`}
                data-testid="connection-result"
              >
                {testResult.ok ? (
                  <span>{text.composer.connectionOk}（{testResult.latencyMs}ms）</span>
                ) : (
                  <span>
                    {text.composer.connectionFail}
                    {testResult.error
                      ? `：${text.composer.errorCodes[testResult.error] ?? testResult.error}`
                      : ""}
                  </span>
                )}
              </div>
            )}
          </div>

          {/* Save / Cancel */}
          <div className="models-detail__edit-actions">
            <Button
              variant="primary"
              onClick={onSaveEndpoint}
              disabled={isMutating}
            >
              {text.detailPanel.saveInline}
            </Button>
            <Button variant="ghost" onClick={onCancelEdit} disabled={isMutating}>
              {text.detailPanel.cancelInline}
            </Button>
          </div>
        </div>
      ) : (
        <div className="models-detail__info-grid">
          <span className="models-detail__info-label">Base URL</span>
          <span className="models-detail__info-value">
            <code>{account.baseUrl || "—"}</code>
          </span>
          <span className="models-detail__info-label">{text.detailPanel.apiKeyStatus}</span>
          <span className="models-detail__info-value">
            {account.hasApiKey ? (
              <span className="api-key-configured-badge">{text.composer.apiKeyConfigured}</span>
            ) : (
              <span style={{ color: "var(--text-tertiary)" }}>{text.detailPanel.notConfigured}</span>
            )}
          </span>
          {account.apiKeyEnvironmentVariable && (
            <>
              <span className="models-detail__info-label">{text.composer.apiKeyEnv}</span>
              <span className="models-detail__info-value">
                <code>{account.apiKeyEnvironmentVariable}</code>
              </span>
            </>
          )}
          {account.customHeaders && Object.keys(account.customHeaders).length > 0 && (
            <>
              <span className="models-detail__info-label">{text.composer.customHeadersLabel}</span>
              <span className="models-detail__info-value">
                {Object.keys(account.customHeaders).length}{" "}
                {text.detailPanel.headerCount}
              </span>
            </>
          )}
        </div>
      )}

      {/* Model section */}
      <div className="models-detail__model-section">
        <div className="models-detail__model-header">
          <span className="models-detail__model-header-title">
            {text.detailPanel.modelSectionTitle} ({account.models.length})
          </span>
          <Button
            variant="ghost"
            size="sm"
            data-testid={`add-model-to-${account.id}`}
            onClick={() => onAddModel(account)}
            disabled={isMutating || isEditing}
            style={{ display: "inline-flex", alignItems: "center", gap: "var(--space-1)" }}
          >
            <Plus size={12} strokeWidth={2.5} />
            {text.sections.addModelButton}
          </Button>
        </div>

        <ul className="models-detail__model-list">
          {account.models.map((model) => {
            const pair: AccountModelPair = { account, model };
            return (
              <ModelListItem
                key={model.id}
                pair={pair}
                text={text}
                isZh={isZh}
                testState={cardTests.get(model.id)}
                isMutating={isMutating}
                onEdit={() => onEditModel(pair)}
                onDuplicate={() => onDuplicateModel(pair)}
                onSetDefault={() => onSetDefault(pair)}
                onDelete={() => onDeleteModel(pair, model.displayName)}
                onQuickTest={() => onQuickTest(pair)}
              />
            );
          })}
        </ul>
      </div>

    </div>
  );
}
