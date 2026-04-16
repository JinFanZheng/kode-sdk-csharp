import { Plus, RefreshCw } from "lucide-react";
import { Button } from "../ui/Button";
import { Toggle } from "../ui/Toggle";
import { Skeleton } from "../ui/Skeleton";
import type { ProviderAccountResponse } from "../../types/contracts";
import type { ModelsSettingsText } from "./locale";
import { shortBaseUrl, PROVIDER_DEFAULT_BASE_URLS } from "./types";

export interface ProviderSidebarProps {
  accounts: ProviderAccountResponse[] | undefined;
  isLoading: boolean;
  isFetching: boolean;
  isMutating: boolean;
  selectedAccountId: string | null;
  text: ModelsSettingsText;
  isZh: boolean;
  onSelect: (accountId: string) => void;
  onToggleEnabled: (account: ProviderAccountResponse) => void;
  onCreateEndpoint: () => void;
  onRefresh: () => void;
}

export function ProviderSidebar({
  accounts,
  isLoading,
  isFetching,
  isMutating,
  selectedAccountId,
  text,
  isZh,
  onSelect,
  onToggleEnabled,
  onCreateEndpoint,
  onRefresh,
}: ProviderSidebarProps) {
  return (
    <div className="control-plane-pane-rail models-sidebar">
      <div className="models-sidebar__header">
        <span className="desk-section-title" style={{ margin: 0, fontSize: "var(--font-size-sm)" }}>
          {text.sidebar.title}
        </span>
        <div className="models-sidebar__header-actions">
          <Button
            variant="ghost"
            size="sm"
            data-testid="model-create"
            onClick={onCreateEndpoint}
            disabled={isLoading}
            title={text.sections.modelComposerCreate}
            style={{ display: "inline-flex", alignItems: "center" }}
          >
            <Plus size={14} strokeWidth={2.5} />
          </Button>
          <Button
            variant="ghost"
            size="sm"
            data-testid="models-settings-refresh"
            onClick={onRefresh}
            disabled={isFetching || isMutating}
            title={text.common.refresh}
            style={{ display: "inline-flex", alignItems: "center" }}
          >
            <RefreshCw
              size={13}
              strokeWidth={2}
              className={isFetching ? "icon-spinning" : ""}
            />
          </Button>
        </div>
      </div>

      <ul className="models-sidebar__list" data-testid="models-list">
        {isLoading ? <Skeleton height={48} count={3} /> : null}

        {!isLoading && accounts?.map((account) => {
          const isSelected = account.id === selectedAccountId;
          const providerLabel = text.providerLabels[account.providerKind];
          const baseUrlShort = shortBaseUrl(
            account.baseUrl,
            shortBaseUrl(PROVIDER_DEFAULT_BASE_URLS[account.providerKind] ?? null, ""),
          );

          return (
            <li key={account.id}>
              <button
                type="button"
                className={[
                  "models-sidebar-item",
                  isSelected && "models-sidebar-item--selected",
                  !account.enabled && "models-sidebar-item--disabled",
                ]
                  .filter(Boolean)
                  .join(" ")}
                onClick={() => onSelect(account.id)}
                data-testid={`sidebar-item-${account.id}`}
              >
                <div className="models-sidebar-item__info">
                  <span className="models-sidebar-item__name">
                    {account.displayName}
                  </span>
                  <span className="models-sidebar-item__meta">
                    <span className="models-sidebar-item__provider-tag">
                      {providerLabel}
                    </span>
                    <span>
                      {account.models.length}{" "}
                      {isZh ? text.sidebar.models : account.models.length === 1 ? "model" : "models"}
                    </span>
                    {baseUrlShort && (
                      <span style={{ opacity: 0.6, fontSize: "var(--font-size-xs)" }}>
                        {baseUrlShort}
                      </span>
                    )}
                  </span>
                </div>
                <Toggle
                  checked={account.enabled}
                  disabled={isMutating}
                  onChange={(e) => {
                    e.stopPropagation();
                    onToggleEnabled(account);
                  }}
                  onClick={(e) => e.stopPropagation()}
                  aria-label={`${account.displayName} ${text.composer.enabled}`}
                />
              </button>
            </li>
          );
        })}
      </ul>

    </div>
  );
}
