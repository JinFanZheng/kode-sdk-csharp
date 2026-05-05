import { useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../lib/queryKeys";
import {
  CheckCircle2, XCircle, Loader2, Trash2, Plus, RefreshCw,
  Terminal, Globe, Network, Pencil, ChevronDown, ChevronUp,
} from "lucide-react";
import { fetchMcpServers, saveMcpServers, testMcpServerConnection } from "../lib/api";
import type { WorkspaceMcpConfig, WorkspaceMcpServerEntry, McpConnectionTestResult } from "../types/contracts";
import { ConfirmModal } from "./ui/ConfirmModal";
import { Button } from "./ui/Button";
import "./ui/Modal.css";
import "./McpServersDesk.css";
import { McpServerModal, ALL_SCOPES, SCOPE_LABELS } from "./mcp/McpServerModal";
import type { ScopeValue } from "./mcp/McpServerModal";

/* ── Types ─────────────────────────────────────────── */

type TestState = "idle" | "testing" | "ok" | "fail";

interface ServerTestStatus {
  state: TestState;
  result?: McpConnectionTestResult;
}

/* ── Helpers ─────────────────────────────────────────── */

function entryTarget(entry: WorkspaceMcpServerEntry): string {
  return entry.url ?? entry.command ?? "";
}

function entryTransportLabel(entry: WorkspaceMcpServerEntry): string {
  if (entry.transport) return entry.transport;
  return entry.url ? "http" : "stdio";
}

function entryIcon(entry: WorkspaceMcpServerEntry) {
  return entry.url
    ? <Globe size={13} className="mcp-entry__icon mcp-entry__icon--http" />
    : <Terminal size={13} className="mcp-entry__icon mcp-entry__icon--stdio" />;
}

/* ── Main component ─────────────────────────────────── */

export function McpServersDesk() {
  const queryClient = useQueryClient();
  const { data: mcpData, isLoading: loading, error: loadError } = useQuery({
    queryKey: queryKeys.mcpServers,
    queryFn: () => fetchMcpServers(),
  });
  const config: WorkspaceMcpConfig | null = mcpData ?? null;
  const [saveError, setSaveError] = useState<string | null>(loadError instanceof Error ? loadError.message : null);
  const [saving, setSaving] = useState(false);
  const [testStatus, setTestStatus] = useState<Record<string, ServerTestStatus>>({});
  const [expandedPanels, setExpandedPanels] = useState<Set<string>>(new Set());
  // null = closed, "" = adding new, "<name>" = editing existing
  const [modalTarget, setModalTarget] = useState<string | null>(null);
  const [deleteConfirm, setDeleteConfirm] = useState<string | null>(null);

  async function saveConfig(next: WorkspaceMcpConfig) {
    setSaving(true);
    setSaveError(null);
    try {
      await saveMcpServers(next);
      void queryClient.invalidateQueries({ queryKey: queryKeys.mcpServers });
    } catch (e) {
      setSaveError(e instanceof Error ? e.message : "保存失败");
    } finally {
      setSaving(false);
    }
  }

  async function handleToggleEnabled(name: string, entry: WorkspaceMcpServerEntry) {
    if (!config) return;
    await saveConfig({
      mcpServers: {
        ...config.mcpServers,
        [name]: { ...entry, enabled: entry.enabled !== false ? false : true },
      },
    });
  }

  async function handleDelete(name: string) {
    if (!config) return;
    const { [name]: _removed, ...rest } = config.mcpServers;
    await saveConfig({ mcpServers: rest });
    setTestStatus(prev => { const next = { ...prev }; delete next[name]; return next; });
    setDeleteConfirm(null);
  }

  async function handleTestConnection(name: string) {
    setExpandedPanels(prev => { const next = new Set(prev); next.delete(name); return next; });
    setTestStatus(prev => ({ ...prev, [name]: { state: "testing" } }));
    try {
      const result = await testMcpServerConnection(name);
      setTestStatus(prev => ({
        ...prev,
        [name]: { state: result.success ? "ok" : "fail", result },
      }));
    } catch (e) {
      setTestStatus(prev => ({
        ...prev,
        [name]: {
          state: "fail",
          result: {
            success: false, toolCount: 0,
            errorMessage: e instanceof Error ? e.message : "连接失败",
          },
        },
      }));
    }
  }

  function togglePanel(name: string) {
    setExpandedPanels(prev => {
      const next = new Set(prev);
      if (next.has(name)) next.delete(name); else next.add(name);
      return next;
    });
  }

  function handleSaved(_updated: WorkspaceMcpConfig, oldName?: string) {
    void queryClient.invalidateQueries({ queryKey: queryKeys.mcpServers });
    setModalTarget(null);
    if (oldName) {
      setTestStatus(prev => {
        const next = { ...prev };
        delete next[oldName];
        return next;
      });
    }
  }

  const servers = config ? Object.entries(config.mcpServers) : [];
  const editingEntry = modalTarget && config?.mcpServers[modalTarget]
    ? config.mcpServers[modalTarget]
    : null;

  return (
    <div className="mcp-desk">
      {/* ── Toolbar ── */}
      <div className="mcp-desk__toolbar">
        <div className="mcp-desk__toolbar-left">
          <span className="mcp-desk__count">
            {loading ? "加载中…" : `${servers.length} 个服务器`}
          </span>
          {saveError && <span className="mcp-desk__inline-error">{saveError}</span>}
        </div>
        <div className="mcp-desk__toolbar-right">
          <Button
            variant="ghost"
            size="control"
            onClick={() => void queryClient.invalidateQueries({ queryKey: queryKeys.mcpServers })}
            disabled={loading}
            aria-label="刷新"
          >
            <RefreshCw size={14} className={loading ? "spin" : ""} />
            刷新
          </Button>
          <Button
            variant="primary"
            size="control"
            onClick={() => setModalTarget("")}
          >
            <Plus size={14} />
            添加服务器
          </Button>
        </div>
      </div>

      {/* ── Server list ── */}
      {!loading && servers.length === 0 ? (
        <div className="mcp-desk__empty">
          <Network size={36} className="mcp-desk__empty-icon" />
          <p className="mcp-desk__empty-title">暂无 MCP 服务器</p>
          <p className="mcp-desk__empty-sub">添加 MCP 服务器以在对话中扩展工具能力</p>
          <Button
            variant="primary"
            onClick={() => setModalTarget("")}
          >
            <Plus size={14} />
            添加第一个服务器
          </Button>
        </div>
      ) : (
        <div className="mcp-desk__list">
          {servers.map(([name, entry]) => {
            const enabled = entry.enabled !== false;
            const ts = testStatus[name];
            const target = entryTarget(entry);
            const panelExpanded = expandedPanels.has(name);
            const hasError = ts?.state === "fail" && !!ts.result?.errorMessage;
            const hasTools = ts?.state === "ok" && !!ts.result?.toolNames?.length;

            return (
              <div
                key={name}
                className={`mcp-entry ${enabled ? "" : "mcp-entry--disabled"}`}
              >
                <div className="mcp-entry__row">
                  <div className="mcp-entry__icon-wrap">{entryIcon(entry)}</div>

                  <div className="mcp-entry__meta">
                    <div className="mcp-entry__name">
                      {name}
                      <span className="mcp-entry__transport">
                        {entryTransportLabel(entry)}
                      </span>
                      {!enabled && (
                        <span className="mcp-entry__badge mcp-entry__badge--disabled">已禁用</span>
                      )}
                    </div>
                    <div className="mcp-entry__target" title={target}>{target}</div>
                    {entry.sessionScopes != null &&
                      entry.sessionScopes.length > 0 &&
                      !ALL_SCOPES.every(s => entry.sessionScopes!.includes(s)) && (
                      <div className="mcp-entry__scopes">
                        {entry.sessionScopes.map(s => (
                          <span key={s} className="mcp-entry__badge mcp-entry__badge--scope">
                            {SCOPE_LABELS[s as ScopeValue] ?? s}
                          </span>
                        ))}
                      </div>
                    )}
                  </div>

                  <div className="mcp-entry__status">
                    <TestStatusBadge ts={ts} />
                    {(hasError || hasTools) && (
                      <Button
                        variant="ghost"
                        size="sm"
                        className="mcp-entry__log-toggle"
                        onClick={() => togglePanel(name)}
                        title={panelExpanded ? "收起" : hasTools ? "查看工具列表" : "查看错误日志"}
                      >
                        {panelExpanded
                          ? <ChevronUp size={12} />
                          : <ChevronDown size={12} />}
                        {panelExpanded ? "收起" : hasTools ? "工具" : "日志"}
                      </Button>
                    )}
                  </div>

                  <div className="mcp-entry__actions">
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => void handleTestConnection(name)}
                      disabled={ts?.state === "testing" || saving}
                      title="测试连接"
                    >
                      {ts?.state === "testing"
                        ? <Loader2 size={13} className="spin" />
                        : null}
                      测试
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => setModalTarget(name)}
                      disabled={saving}
                      title="编辑"
                      aria-label={`编辑 ${name}`}
                    >
                      <Pencil size={13} />
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      onClick={() => void handleToggleEnabled(name, entry)}
                      disabled={saving}
                    >
                      {enabled ? "禁用" : "启用"}
                    </Button>
                    <Button
                      variant="ghost"
                      size="sm"
                      className="mcp-entry__delete"
                      onClick={() => setDeleteConfirm(name)}
                      disabled={saving}
                      title="删除"
                      aria-label={`删除 ${name}`}
                    >
                      <Trash2 size={13} />
                    </Button>
                  </div>
                </div>

                {hasTools && panelExpanded && (
                  <div className="mcp-entry__tools-panel">
                    <div className="mcp-entry__tools-panel-label">
                      可用工具（{ts!.result!.toolNames!.length}）
                    </div>
                    <div className="mcp-entry__tools-list">
                      {ts!.result!.toolNames!.map(n => (
                        <span key={n} className="mcp-tool-chip">{n}</span>
                      ))}
                    </div>
                  </div>
                )}

                {hasError && panelExpanded && (
                  <div className="mcp-entry__error-log">
                    <div className="mcp-entry__error-log-label">错误详情</div>
                    <pre className="mcp-entry__error-log-body">
                      {ts!.result!.errorMessage}
                    </pre>
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}

      <McpServerModal
        open={modalTarget !== null}
        config={config}
        editName={modalTarget ?? undefined}
        initialEntry={editingEntry ?? undefined}
        onClose={() => setModalTarget(null)}
        onSaved={handleSaved}
      />

      <ConfirmModal
        open={deleteConfirm !== null}
        title="删除 MCP Server"
        description={`确认删除 "${deleteConfirm}" 吗？此操作不可撤销。`}
        confirmLabel="删除"
        variant="danger"
        busy={saving}
        onConfirm={() => { if (deleteConfirm) void handleDelete(deleteConfirm); }}
        onCancel={() => setDeleteConfirm(null)}
      />
    </div>
  );
}

/* ── Test status badge ──────────────────────────────── */

function TestStatusBadge({ ts }: { ts: ServerTestStatus | undefined }) {
  if (!ts || ts.state === "idle") return null;

  if (ts.state === "testing") {
    return (
      <span className="mcp-badge mcp-badge--testing">
        <Loader2 size={11} className="spin" />
        测试中
      </span>
    );
  }
  if (ts.state === "ok") {
    return (
      <span className="mcp-badge mcp-badge--ok">
        <CheckCircle2 size={11} />
        {ts.result?.toolCount ?? 0} 工具
      </span>
    );
  }
  return (
    <span className="mcp-badge mcp-badge--fail">
      <XCircle size={11} />
      连接失败
    </span>
  );
}
