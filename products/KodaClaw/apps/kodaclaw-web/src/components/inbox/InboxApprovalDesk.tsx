import React, { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../../lib/queryKeys";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import {
  fetchApprovals,
  fetchInbox,
  submitApprovalDecision,
  updateInboxStatus,
} from "../../lib/api";
import { formatRelativeTime, parseChannelDeliveryPayload } from "./inboxUtils";
import { useI18n, useLocaleText } from "../../i18n/I18nProvider";
import { Skeleton } from "../ui/Skeleton";
import { EmptyState } from "../ui/EmptyState";
import { Button } from "../ui/Button";
import { Select } from "../ui/Select";
import { Inbox, Loader2, Zap } from "lucide-react";
import { InboxDeliveryContext } from "./InboxDeliveryContext";
import { InboxAutomationResultPush } from "./InboxAutomationResultPush";
import { InboxLinkedApproval } from "./InboxLinkedApproval";
import {
  inboxTranslations,
  INBOX_STATUS_OPTIONS,
  formatInboxKind,
  formatInboxStatus,
} from "./inboxTranslations";
import type {
  Approval,
  InboxItem,
  InboxItemStatus,
} from "../../types/contracts";
import type { InboxStatusFilter } from "./inboxTranslations";
import "../ControlPlaneDesk.css";

export function InboxApprovalDesk() {
  const { formatDateTime } = useI18n();
  const t = useLocaleText(inboxTranslations);

  const queryClient = useQueryClient();
  const [statusFilter, setStatusFilter] = useState<InboxStatusFilter>("Open");
  const { data: inboxData, isLoading, isFetching, error: inboxQueryError } = useQuery({
    queryKey: queryKeys.inbox(statusFilter === "all" ? undefined : statusFilter),
    queryFn: () => fetchInbox({ limit: 50, status: statusFilter === "all" ? undefined : statusFilter }),
    retry: false,
  });
  const { data: approvalsData } = useQuery({
    queryKey: queryKeys.approvals(),
    queryFn: () => fetchApprovals({ limit: 50 }),
    retry: false,
  });
  const inboxItems: InboxItem[] = inboxData?.items ?? [];
  const approvals: Approval[] = approvalsData?.items ?? [];
  const isRefreshing = isFetching && !isLoading;
  const [error, setError] = useState<string | null>(null);
  const [approvalNotes, setApprovalNotes] = useState<Record<string, string>>({});
  const [pendingApprovalIds, setPendingApprovalIds] = useState<Record<string, boolean>>({});
  const [pendingInboxIds, setPendingInboxIds] = useState<Record<string, boolean>>({});
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [kindFilter, setKindFilter] = useState<"all" | "approvals" | "automations">("all");
  const [approvalOverrides, setApprovalOverrides] = useState<Record<string, Approval>>({});
  const [inboxStatusOverrides, setInboxStatusOverrides] = useState<Record<string, InboxItemStatus>>({});
  const [markingAllRead, setMarkingAllRead] = useState(false);
  const [markAllDone, setMarkAllDone] = useState(0);
  const [markAllTotal, setMarkAllTotal] = useState(0);
  const [searchQuery, setSearchQuery] = useState("");
  const [debouncedQuery, setDebouncedQuery] = useState("");

  // Per-item inline errors (kept separate from the global query-load error)
  const [approvalErrors, setApprovalErrors] = useState<Record<string, string>>({});
  const [statusErrors, setStatusErrors] = useState<Record<string, string>>({});
  const [markAllError, setMarkAllError] = useState<string | null>(null);

  const formatTimestamp = useCallback(
    (value?: string | null) => formatDateTime(value, t.common.none),
    [formatDateTime, t.common.none],
  );

  const formatRelative = useCallback(
    (value?: string | null) => formatRelativeTime(value, t),
    [t],
  );

  const listBodyRef = useRef<HTMLDivElement>(null);

  // ── Derived data ──

  const approvalsMap = useMemo(() => {
    const map: Record<string, Approval> = {};
    for (const a of approvals) map[a.id] = a;
    for (const [id, override] of Object.entries(approvalOverrides)) map[id] = override;
    return map;
  }, [approvals, approvalOverrides]);

  const filteredItems = useMemo(() => {
    let items = inboxItems;
    if (kindFilter === "approvals") items = items.filter((i) => i.kind !== "AutomationResult");
    else if (kindFilter === "automations") items = items.filter((i) => i.kind === "AutomationResult");
    if (debouncedQuery.trim()) {
      const q = debouncedQuery.toLowerCase().trim();
      items = items.filter(
        (i) =>
          i.title.toLowerCase().includes(q) ||
          (i.summary ?? "").toLowerCase().includes(q),
      );
    }
    return items;
  }, [inboxItems, kindFilter, debouncedQuery]);

  // ── Keyboard navigation ──

  const handleListKeyDown = useCallback(
    (e: React.KeyboardEvent) => {
      const idx = filteredItems.findIndex((item) => item.id === selectedId);
      let nextIdx: number;
      if (e.key === "ArrowDown") {
        e.preventDefault();
        nextIdx = idx < filteredItems.length - 1 ? idx + 1 : idx;
      } else if (e.key === "ArrowUp") {
        e.preventDefault();
        nextIdx = idx > 0 ? idx - 1 : idx;
      } else if (e.key === "Enter" || e.key === " ") {
        e.preventDefault();
        const card = listBodyRef.current?.querySelector(
          `[data-testid="inbox-item-${selectedId}"]`,
        ) as HTMLElement | null;
        card?.click();
        return;
      } else {
        return;
      }
      const nextId = filteredItems[nextIdx]?.id;
      if (nextId) setSelectedId(nextId);
    },
    [filteredItems, selectedId],
  );

  // Scroll selected card into view
  useEffect(() => {
    if (!selectedId || !listBodyRef.current) return;
    const card = listBodyRef.current.querySelector(
      `[data-testid="inbox-item-${selectedId}"]`,
    ) as HTMLElement | null;
    card?.scrollIntoView?.({ block: "nearest" });
  }, [selectedId]);

  // ── Data mutations ──

  const handleRefresh = useCallback(async () => {
    await queryClient.invalidateQueries({ queryKey: ['inbox'] });
    await queryClient.invalidateQueries({ queryKey: ['approvals'] });
  }, [queryClient]);

  const handleApprovalDecision = useCallback(
    async (approvalId: string, approve: boolean) => {
      setPendingApprovalIds((cur) => ({ ...cur, [approvalId]: true }));
      try {
        const note = approvalNotes[approvalId] ?? "";
        const updatedApproval = await submitApprovalDecision(approvalId, approve, note);
        setApprovalOverrides((cur) => ({ ...cur, [approvalId]: updatedApproval }));
        void queryClient.invalidateQueries({ queryKey: ['inbox'] });
        void queryClient.invalidateQueries({ queryKey: ['approvals'] });
      } catch (err) {
        setApprovalErrors((cur) => ({
          ...cur,
          [approvalId]: err instanceof Error ? err.message : t.errors.decisionFailed,
        }));
      } finally {
        setPendingApprovalIds((cur) => {
          const next = { ...cur };
          delete next[approvalId];
          return next;
        });
      }
    },
    [approvalNotes, queryClient, t.errors.decisionFailed],
  );

  const handleStatusUpdate = useCallback(
    async (inboxId: string, status: InboxItemStatus) => {
      setPendingInboxIds((cur) => ({ ...cur, [inboxId]: true }));
      try {
        await updateInboxStatus(inboxId, status);
        setInboxStatusOverrides((cur) => ({ ...cur, [inboxId]: status }));
        void queryClient.invalidateQueries({ queryKey: ['inbox'] });
      } catch (err) {
        setStatusErrors((cur) => ({
          ...cur,
          [inboxId]: err instanceof Error ? err.message : t.errors.updateFailed,
        }));
      } finally {
        setPendingInboxIds((cur) => {
          const next = { ...cur };
          delete next[inboxId];
          return next;
        });
      }
    },
    [queryClient, t.errors.updateFailed],
  );

  const handleApprovalNoteChange = useCallback(
    (approvalId: string, value: string) => {
      setApprovalNotes((cur) => ({ ...cur, [approvalId]: value }));
    },
    [],
  );

  const handleMarkAllRead = useCallback(async () => {
    const itemsToMark = filteredItems.filter(
      (item) => item.status !== "Archived" && item.status !== "Resolved",
    );
    if (itemsToMark.length === 0) return;

    setMarkingAllRead(true);
    setMarkAllTotal(itemsToMark.length);
    setMarkAllDone(0);

    // Optimistic: mark all as acknowledged immediately
    const overrides: Record<string, InboxItemStatus> = {};
    for (const item of itemsToMark) {
      overrides[item.id] = "Acknowledged";
    }
    setInboxStatusOverrides((cur) => ({ ...cur, ...overrides }));

    const results = await Promise.allSettled(
      itemsToMark.map((item) =>
        updateInboxStatus(item.id, "Acknowledged").then(() => {
          setMarkAllDone((prev) => prev + 1);
        }),
      ),
    );

    const firstError = results.find((r) => r.status === "rejected") as
      | PromiseRejectedResult
      | undefined;
    const errorMsg =
      firstError?.reason instanceof Error
        ? firstError.reason.message
        : firstError
          ? t.errors.updateFailed
          : null;

    setMarkingAllRead(false);
    if (errorMsg) {
      setMarkAllError(errorMsg);
    }
    void queryClient.invalidateQueries({ queryKey: ['inbox'] });
  }, [filteredItems, queryClient, t.errors.updateFailed]);

  const selectedItem = useMemo(() => {
    const item = inboxItems.find((i) => i.id === selectedId) ?? null;
    if (!item) return null;
    const statusOverride = inboxStatusOverrides[item.id];
    return statusOverride ? { ...item, status: statusOverride } : item;
  }, [inboxItems, selectedId, inboxStatusOverrides]);

  const linkedApproval = useMemo(() => {
    if (!selectedItem?.approvalId) return null;
    return approvalsMap[selectedItem.approvalId] ?? null;
  }, [selectedItem, approvalsMap]);

  const selectedPayload = useMemo(
    () => parseChannelDeliveryPayload(selectedItem?.payloadJson ?? linkedApproval?.payloadJson),
    [selectedItem, linkedApproval],
  );

  // ── Side effects ──

  // Debounce search input
  useEffect(() => {
    const timer = setTimeout(() => setDebouncedQuery(searchQuery), 300);
    return () => clearTimeout(timer);
  }, [searchQuery]);

  useEffect(() => {
    setSelectedId((current) => {
      if (current && inboxItems.some((item) => item.id === current)) return current;
      return inboxItems[0]?.id ?? null;
    });
  }, [inboxItems]);

  useEffect(() => {
    if (inboxQueryError) {
      setError(inboxQueryError instanceof Error ? inboxQueryError.message : t.errors.loadFailed);
    }
  }, [inboxQueryError, t.errors.loadFailed]);

  // ── Render ──

  return (
    <section data-testid="inbox-approval-desk" className="control-plane-stack" style={{ gap: 0 }}>
      <div>
        <h2 className="desk-section-title">{t.title}</h2>
        <p className="desk-section-desc">{t.intro}</p>

        <div className="control-plane-toolbar">
          <Button
            variant="secondary"
            size="control"
            data-testid="inbox-refresh"
            disabled={isLoading || isRefreshing}
            onClick={() => { void handleRefresh(); }}
          >
            {isRefreshing ? t.refreshing : t.refresh}
          </Button>
          <Button
            variant="secondary"
            size="control"
            data-testid="inbox-mark-all-read"
            disabled={isLoading || markingAllRead || filteredItems.every((i) => i.status === "Acknowledged" || i.status === "Resolved" || i.status === "Archived")}
            onClick={() => { void handleMarkAllRead(); }}
          >
            {markingAllRead && markAllTotal > 0
              ? t.markingProgress(markAllDone, markAllTotal)
              : markingAllRead
                ? t.markingAllRead
                : t.markAllRead}
          </Button>
          {markAllError ? (
            <span className="desk-feedback desk-feedback--error">{markAllError}</span>
          ) : null}
          <label className="metric-label" htmlFor="inbox-status-filter">
            {t.statusFilter}
          </label>
          <Select
            id="inbox-status-filter"
            data-testid="inbox-status-filter"
            className="control-plane-filter"
            value={statusFilter}
            onChange={(e) => setStatusFilter(e.target.value as InboxStatusFilter)}
          >
            <option value="all">{t.common.all}</option>
            {INBOX_STATUS_OPTIONS.map((s) => (
              <option key={s} value={s}>{formatInboxStatus(s, t)}</option>
            ))}
          </Select>
          <input
            type="search"
            className="kc-input control-plane-filter"
            data-testid="inbox-search"
            placeholder={t.searchPlaceholder}
            value={searchQuery}
            onChange={(e) => setSearchQuery(e.target.value)}
          />
        </div>
      </div>

      {error ? (
        <section className="status-card status-card--error" data-testid="inbox-approval-error">
          <h3 className="desk-section-title">{t.errorTitle}</h3>
          <p className="desk-section-desc">{error}</p>
        </section>
      ) : null}

      <div className="control-plane-pane-shell inbox-desk__layout">
        {/* LEFT: Inbox list */}
        <div className="control-plane-pane-rail inbox-desk__rail">
          <section className="timeline" data-testid="inbox-list">
            <div className="timeline__header">
              <span className="composer__status">
                {isLoading ? t.common.loading : t.common.items(filteredItems.length)}
              </span>
            </div>

            <div className="inbox-kind-tabs" data-testid="inbox-kind-tabs">
              {(["all", "approvals", "automations"] as const).map((tab) => (
                <button
                  key={tab}
                  type="button"
                  className={`inbox-kind-tab${kindFilter === tab ? " inbox-kind-tab--active" : ""}`}
                  onClick={() => setKindFilter(tab)}
                >
                  {tab === "automations" && <Zap size={12} strokeWidth={1.75} />}
                  {t.kindTabs[tab]}
                </button>
              ))}
            </div>

            <div
              ref={listBodyRef}
              className="timeline__body inbox-desk__list-body"
              role="listbox"
              aria-label={t.title}
              aria-activedescendant={selectedId ? `inbox-item-${selectedId}` : undefined}
              tabIndex={0}
              onKeyDown={handleListKeyDown}
            >
              {isLoading ? <Skeleton height={52} count={3} /> : null}
              {!isLoading && filteredItems.length === 0 ? (
                <EmptyState icon={<Inbox size={28} strokeWidth={1.5} />} title={t.emptyInbox} />
              ) : null}
              {filteredItems.map((item) => {
                const isAutomation = item.kind === "AutomationResult";
                return (
                  <article
                    key={item.id}
                    id={`inbox-item-${item.id}`}
                    role="option"
                    aria-selected={selectedId === item.id}
                    tabIndex={-1}
                    className={`message message--system control-plane-stack control-plane-queue-card${selectedId === item.id ? " control-plane-list-button--selected" : ""}${isAutomation ? " inbox-automation-result-card" : ""}`}
                    data-testid={`inbox-item-${item.id}`}
                    onClick={() => setSelectedId(item.id)}
                  >
                    <div className="message__meta">
                      {isAutomation ? (
                        <span className="message__role inbox-automation-result-kind">
                          <Zap size={12} strokeWidth={1.75} />
                          {formatInboxKind(item.kind, t)}
                        </span>
                      ) : (
                        <span className="message__role">{formatInboxKind(item.kind, t)}</span>
                      )}
                      <time dateTime={item.updatedAt ?? undefined} title={formatTimestamp(item.updatedAt)}>
                        {formatRelative(item.updatedAt)}
                      </time>
                    </div>
                    <strong>{item.title}</strong>
                    <p className="control-plane-compact-copy inbox-card-summary">{item.summary}</p>
                    {item.requiresAction ? (
                      <span className="inbox-action-badge">{t.actionRequired}</span>
                    ) : null}
                  </article>
                );
              })}
            </div>
          </section>
        </div>

        {/* RIGHT: Detail */}
        <div className="control-plane-pane-stage inbox-desk__stage" data-testid="inbox-detail">
          {!selectedItem ? (
            <div className="control-plane-stage-hero">
              <EmptyState icon={<Inbox size={28} strokeWidth={1.5} />} title={t.emptyDetail} />
            </div>
          ) : (
            <div className="status-card status-card--normal control-plane-stage-hero control-plane-stack">
              {/* Hero header */}
              <div className="inbox-detail-hero">
                <div className="control-plane-chip-row">
                  <span className="control-plane-chip">{formatInboxKind(selectedItem.kind, t)}</span>
                  <span className="control-plane-chip">{formatInboxStatus(selectedItem.status, t)}</span>
                  {selectedItem.requiresAction ? (
                    <span className="control-plane-chip control-plane-chip--warning">
                      {t.actionRequired}
                    </span>
                  ) : null}
                </div>
                <h3 className="desk-section-title control-plane-card-title">{selectedItem.title}</h3>
                <div className={`inbox-detail-markdown${selectedItem.kind === "AutomationResult" ? " inbox-detail-markdown--automation" : ""}`}>
                  <ReactMarkdown
                    remarkPlugins={[remarkGfm]}
                    components={{
                      a: ({ href, children }) => (
                        <a href={href} target="_blank" rel="noopener noreferrer">
                          {children}
                        </a>
                      ),
                    }}
                  >
                    {selectedItem.summary ?? ""}
                  </ReactMarkdown>
                </div>
              </div>

              {/* Linked approval section — promoted above fold */}
              {linkedApproval ? (
                <InboxLinkedApproval
                  linkedApproval={linkedApproval}
                  text={t}
                  approvalNotes={approvalNotes}
                  pendingApprovalIds={pendingApprovalIds}
                  inlineError={approvalErrors[linkedApproval.id] ?? null}
                  onApprovalDecision={handleApprovalDecision}
                  onApprovalNoteChange={handleApprovalNoteChange}
                />
              ) : null}

              {/* AutomationResult channel push section */}
              <InboxAutomationResultPush
                item={selectedItem}
                onRefresh={() => { void handleRefresh(); }}
                text={t}
              />

              {/* Metadata grid */}
              <div className="control-plane-summary-grid">
                <div className="metric-item">
                  <span className="metric-label">{t.source}</span>
                  <span className="metric-value metric-value--path">{selectedItem.source}</span>
                </div>
                <div className="metric-item">
                  <span className="metric-label">{t.lastUpdated}</span>
                  <time
                    dateTime={selectedItem.updatedAt ?? undefined}
                    title={formatTimestamp(selectedItem.updatedAt)}
                    className="metric-value"
                  >
                    {formatRelative(selectedItem.updatedAt)}
                  </time>
                </div>
                {selectedItem.sessionId ? (
                  <div className="metric-item">
                    <span className="metric-label">{t.session}</span>
                    <span className="metric-value metric-value--path">{selectedItem.sessionId}</span>
                  </div>
                ) : null}
                {selectedItem.correlationId ? (
                  <div className="metric-item">
                    <span className="metric-label">{t.correlation}</span>
                    <span className="metric-value metric-value--path">{selectedItem.correlationId}</span>
                  </div>
                ) : null}
                {selectedItem.route ? (
                  <div className="metric-item metric-item--full-width">
                    <span className="metric-label">{t.route}</span>
                    <span className="metric-value metric-value--path">{selectedItem.route}</span>
                  </div>
                ) : null}
              </div>

              {/* Channel delivery context */}
              {selectedPayload ? (
                <InboxDeliveryContext payload={selectedPayload} text={t} />
              ) : null}

              {/* Item actions */}
              <div className="control-plane-detail-actions">
                {selectedItem.kind === "AutomationResult" ? (
                  <Button
                    variant="secondary"
                    data-testid={`inbox-mark-read-${selectedItem.id}`}
                    disabled={
                      (pendingInboxIds[selectedItem.id] ?? false) ||
                      selectedItem.status === "Acknowledged"
                    }
                    onClick={() => void handleStatusUpdate(selectedItem.id, "Acknowledged")}
                  >
                    {(pendingInboxIds[selectedItem.id] ?? false) ? (
                      <Loader2 size={14} className="icon-spinning" />
                    ) : null}
                    {t.markRead}
                  </Button>
                ) : (
                  <>
                    <label className="metric-label" htmlFor="inbox-detail-status">
                      {t.status}
                    </label>
                    <Select
                      id="inbox-detail-status"
                      data-testid={`inbox-status-${selectedItem.id}`}
                      value={selectedItem.status}
                      disabled={pendingInboxIds[selectedItem.id] ?? false}
                      onChange={(e) =>
                        void handleStatusUpdate(selectedItem.id, e.target.value as InboxItemStatus)
                      }
                    >
                      {INBOX_STATUS_OPTIONS.map((s) => (
                        <option key={s} value={s}>{formatInboxStatus(s, t)}</option>
                      ))}
                    </Select>
                    {(pendingInboxIds[selectedItem.id] ?? false) ? (
                      <Loader2 size={14} className="icon-spinning" />
                    ) : null}
                  </>
                )}
                {statusErrors[selectedItem.id] ? (
                  <p className="desk-feedback desk-feedback--error" style={{ width: "100%" }}>
                    {statusErrors[selectedItem.id]}
                  </p>
                ) : null}
              </div>
            </div>
          )}
        </div>
      </div>
    </section>
  );
}
