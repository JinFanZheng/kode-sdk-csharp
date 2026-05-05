import { useEffect, useState } from "react";
import { Loader2 } from "lucide-react";
import { saveMcpServers } from "../../lib/api";
import { getRuntimeConfig } from "../../lib/config";
import { Modal } from "../ui/Modal";
import { Button } from "../ui/Button";
import { Select } from "../ui/Select";
import type { WorkspaceMcpConfig, WorkspaceMcpServerEntry } from "../../types/contracts";

export const ALL_SCOPES = ["main", "dm", "group", "automation"] as const;
export type ScopeValue = typeof ALL_SCOPES[number];

export const SCOPE_LABELS: Record<ScopeValue, string> = {
  main: "主会话",
  dm: "私聊",
  group: "群聊",
  automation: "自动化",
};

export interface ServerFormState {
  name: string;
  transport: "stdio" | "streamableHttp" | "sse";
  command: string;
  args: string;
  url: string;
  headers: string;
  sessionScopes: string[];
}

const EMPTY_FORM: ServerFormState = {
  name: "",
  transport: "stdio",
  command: "",
  args: "",
  url: "",
  headers: "",
  sessionScopes: [...ALL_SCOPES],
};

function parseHeadersText(text: string): Record<string, string> | undefined {
  if (!text.trim()) return undefined;
  try {
    return JSON.parse(text) as Record<string, string>;
  } catch {
    return undefined;
  }
}

function isHttpTransport(t: string) {
  return t === "streamableHttp" || t === "sse";
}

function entryToForm(name: string, entry: WorkspaceMcpServerEntry): ServerFormState {
  const isHttp = !!entry.url || isHttpTransport(entry.transport ?? "");
  const scopes = entry.sessionScopes == null || entry.sessionScopes.length === 0
    ? [...ALL_SCOPES]
    : entry.sessionScopes.filter((s): s is ScopeValue => ALL_SCOPES.includes(s as ScopeValue));
  return {
    name,
    transport: isHttp
      ? ((entry.transport ?? "streamableHttp") as ServerFormState["transport"])
      : "stdio",
    command: entry.command ?? "",
    args: (entry.args ?? []).join(" "),
    url: entry.url ?? "",
    headers: entry.headers ? JSON.stringify(entry.headers, null, 2) : "",
    sessionScopes: scopes,
  };
}

interface ServerModalProps {
  open: boolean;
  config: WorkspaceMcpConfig | null;
  editName?: string;
  initialEntry?: WorkspaceMcpServerEntry;
  onClose: () => void;
  onSaved: (config: WorkspaceMcpConfig, oldName?: string) => void;
}

export function McpServerModal({ open, config, editName, initialEntry, onClose, onSaved }: ServerModalProps) {
  const isEdit = !!editName;
  const [form, setForm] = useState<ServerFormState>(EMPTY_FORM);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (!open) return;
    setError(null);
    if (isEdit && editName && initialEntry) {
      setForm(entryToForm(editName, initialEntry));
    } else {
      setForm(EMPTY_FORM);
    }
  }, [open, isEdit, editName, initialEntry]);

  const isHttp = isHttpTransport(form.transport);
  const isWindows = getRuntimeConfig().platform === "win32";
  const showWindowsNodeHint = isWindows && !isHttp &&
    /^(npx|npm|yarn|pnpm|uvx|uv)$/i.test(form.command.trim());

  function setField<K extends keyof ServerFormState>(key: K, value: ServerFormState[K]) {
    setForm(f => ({ ...f, [key]: value }));
    setError(null);
  }

  async function handleSubmit() {
    setError(null);
    const name = form.name.trim();
    if (!name) { setError("服务器名称不能为空"); return; }
    if (config?.mcpServers[name] && name !== editName) {
      setError(`名称 "${name}" 已存在`);
      return;
    }
    if (!isHttp && !form.command.trim()) { setError("stdio 类型必须填写启动命令"); return; }
    if (isHttp && !form.url.trim()) { setError("HTTP 类型必须填写服务地址 URL"); return; }

    const isAllScopes = ALL_SCOPES.every(s => form.sessionScopes.includes(s));
    const scopesField = isAllScopes ? null : form.sessionScopes;

    const entry: WorkspaceMcpServerEntry = isHttp
      ? {
          transport: form.transport,
          url: form.url.trim(),
          headers: parseHeadersText(form.headers),
          ...(isEdit && initialEntry?.enabled !== undefined
            ? { enabled: initialEntry.enabled }
            : {}),
          sessionScopes: scopesField,
        }
      : {
          command: form.command.trim(),
          args: form.args.trim() ? form.args.trim().split(/\s+/) : undefined,
          ...(isEdit && initialEntry?.enabled !== undefined
            ? { enabled: initialEntry.enabled }
            : {}),
          sessionScopes: scopesField,
        };

    const base = { ...(config?.mcpServers ?? {}) };
    if (isEdit && editName && editName !== name) {
      delete base[editName];
    }
    base[name] = entry;

    setSaving(true);
    try {
      const saved = await saveMcpServers({ mcpServers: base });
      onSaved(saved, isEdit && editName !== name ? editName : undefined);
    } catch (e) {
      setError(e instanceof Error ? e.message : "保存失败");
    } finally {
      setSaving(false);
    }
  }

  const footer = (
    <>
      <Button variant="secondary" onClick={onClose} disabled={saving}>
        取消
      </Button>
      <Button
        variant="primary"
        onClick={() => void handleSubmit()}
        disabled={saving}
      >
        {saving ? <Loader2 size={14} className="spin" /> : null}
        {saving ? "保存中…" : "保存"}
      </Button>
    </>
  );

  return (
    <Modal
      open={open}
      title={isEdit ? `编辑 "${editName}"` : "添加 MCP 服务器"}
      onClose={onClose}
      footer={footer}
      width={520}
    >
      <div className="kc-modal-form">
        <div className="kc-modal-form__row">
          <div className="kc-field">
            <label className="kc-field__label" htmlFor="mcp-name">名称</label>
            <input
              id="mcp-name"
              className="kc-input"
              placeholder="e.g. chrome-devtools"
              value={form.name}
              onChange={e => setField("name", e.target.value)}
              autoFocus
            />
          </div>
          <div className="kc-field">
            <label className="kc-field__label" htmlFor="mcp-transport">传输类型</label>
            <Select
              id="mcp-transport"
              value={form.transport}
              onChange={e => setField("transport", e.target.value as ServerFormState["transport"])}
            >
              <option value="stdio">stdio（本地进程）</option>
              <option value="streamableHttp">streamableHttp（远程 HTTP）</option>
              <option value="sse">sse（Server-Sent Events）</option>
            </Select>
          </div>
        </div>

        {!isHttp ? (
          <div className="kc-modal-form__row">
            <div className="kc-field">
              <label className="kc-field__label" htmlFor="mcp-command">启动命令</label>
              <input
                id="mcp-command"
                className="kc-input"
                placeholder="npx"
                value={form.command}
                onChange={e => setField("command", e.target.value)}
              />
              {showWindowsNodeHint && (
                <span className="kc-field__hint kc-field__hint--warn">
                  Windows 提示：<code>{form.command.trim()}</code> 是 .cmd 脚本，
                  无法直接启动。请改为 <code>cmd.exe</code>，
                  并将 <code>/c {form.command.trim()}</code> 填入参数栏。
                </span>
              )}
            </div>
            <div className="kc-field">
              <label className="kc-field__label" htmlFor="mcp-args">参数</label>
              <input
                id="mcp-args"
                className="kc-input"
                placeholder="-y my-server@latest"
                value={form.args}
                onChange={e => setField("args", e.target.value)}
              />
              <span className="kc-field__hint">多个参数以空格分隔</span>
            </div>
          </div>
        ) : (
          <>
            <div className="kc-field">
              <label className="kc-field__label" htmlFor="mcp-url">服务地址</label>
              <input
                id="mcp-url"
                className="kc-input"
                placeholder="https://api.example.com/mcp"
                value={form.url}
                onChange={e => setField("url", e.target.value)}
              />
            </div>
            <div className="kc-field">
              <label className="kc-field__label" htmlFor="mcp-headers">请求头（可选）</label>
              <input
                id="mcp-headers"
                className="kc-input"
                placeholder='{"Authorization": "Bearer sk-..."}'
                value={form.headers}
                onChange={e => setField("headers", e.target.value)}
              />
              <span className="kc-field__hint">JSON 格式，留空跳过</span>
            </div>
          </>
        )}

        <div className="kc-modal-form__row">
          <div className="kc-field">
            <label className="kc-field__label">生效范围</label>
            <div className="kc-scope-checks">
              {ALL_SCOPES.map(scope => {
                const checked = form.sessionScopes.includes(scope);
                return (
                  <label key={scope} className="kc-scope-check">
                    <input
                      type="checkbox"
                      checked={checked}
                      onChange={() => {
                        const next = checked
                          ? form.sessionScopes.filter(s => s !== scope)
                          : [...form.sessionScopes, scope];
                        setField("sessionScopes", next);
                      }}
                    />
                    {SCOPE_LABELS[scope]}
                  </label>
                );
              })}
            </div>
            <span className="kc-field__hint">选择哪些会话类型可以加载此服务器，全选等同于所有</span>
          </div>
        </div>

        {error && <p className="kc-field__error">{error}</p>}
      </div>
    </Modal>
  );
}
