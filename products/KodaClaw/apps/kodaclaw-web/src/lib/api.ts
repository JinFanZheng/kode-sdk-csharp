import {
  type Approval,
  type ApprovalDecisionRequest,
  type ApprovalKind,
  type ApprovalQueryResponse,
  type ApprovalStatus,
  type AutomationDefinition,
  type AutomationDefinitionsQueryResponse,
  type AutomationDefinitionSource,
  type AutomationRunRecord,
  type AutomationRunsQueryResponse,
  type BootstrapCompletionRequest,
  type BootstrapCompletionResult,
  type BootstrapDraftRequest,
  type BootstrapDraftResult,
  type BootstrapStateResponse,
  type CanvasArtifact,
  type CanvasArtifactKind,
  type ChannelAccount,
  type ChannelAccountState,
  type ChannelAuditEntry,
  type ChannelConnectorDescriptor,
  type ChannelConnectorKind,
  type ChannelThreadDetail,
  type ChannelThreadType,
  type ChannelsQueryResponse,
  type CanvasEntryResponse,
  type CanvasQueryResponse,
  type ChatStreamEvent,
  type ChatStreamRequest,
  type CreateProviderAccountRequest,
  type CreateAccountModelRequest,
  type DiagnosticBundleExportRequest,
  type DiagnosticBundleExportResponse,
  type DiagnosticEvent,
  type DiagnosticsQueryResponse,
  type GatewayHealthResponse,
  type InboxItem,
  type InboxItemKind,
  type InboxItemStatus,
  type InboxQueryResponse,
  type InboxStatusUpdateRequest,
  type KodaClawSettings,
  type AccountModelResponse,
  type ProviderAccountResponse,
  type PluginDetail,
  type PluginLogEntry,
  type SandboxRiskOverviewResponse,
  type PluginRuntimeState,
  type PluginsQueryResponse,
  type PluginTrustState,
  type PluginType,
  type RotateSessionResponse,
  type ResumeSessionResponse,
  type SessionDetail,
  type SessionsQueryResponse,
  type StorageUsageResponse,
  type UpdateAutomationDefinitionRequest,
  type UpdateThreadSettingsRequest,
  type TriggerAutomationResponse,
  type TestTelegramTokenRequest,
  type TestTelegramTokenResponse,
  type TestFeishuCredentialsRequest,
  type TestFeishuCredentialsResponse,
  type WeChatQrCodeResult,
  type WeChatQrCodeStatus,
  type TestWeChatCredentialsResponse,
  type CreateChannelAccountRequest,
  type PatchChannelAccountRequest,
  type UpdateCheckRequest,
  type UpdateProviderAccountRequest,
  type UpdateAccountModelRequest,
  type UpdateStateResponse,
  type UpsertCanvasArtifactRequest,
  type InstallLocalPluginRequest,
  type ModelPreset,
  type ModelConnectionTestRequest,
  type ModelConnectionTestResponse,
  type PersonaPreset,
  type OnboardingState,
  type ApplyPersonaRequest,
  type WorkspaceMcpConfig,
  type McpConnectionTestResult,
  type SessionMessagesResponse,
  type MediaMeta,
  type PushToChannelResponse,
  type WorkspaceGitLogResponse,
  type WorkspaceGitRevertFileRequest,
  type WorkspaceGitRevertFileResponse,
  type SkillDescriptor,
  type DiagnosticsStatsResponse,
} from "../types/contracts";
import { getGatewayToken, resolveGatewayPath } from "./config";

export interface ParsedSseFrame {
  eventName: string;
  data: string;
  id?: string;
}

type QueryValue = string | number | boolean | null | undefined | string[];

export function buildHeaders(json = false): HeadersInit {
  const headers: Record<string, string> = {};
  const gatewayToken = getGatewayToken();

  if (gatewayToken) {
    headers.Authorization = `Bearer ${gatewayToken}`;
  }

  if (json) {
    headers["Content-Type"] = "application/json";
  }

  return headers;
}

async function readJson<T>(response: Response): Promise<T> {
  return (await response.json()) as T;
}

async function readErrorDetail(response: Response): Promise<string> {
  try {
    const body = (await response.json()) as { message?: string };
    if (body?.message) {
      return body.message;
    }
  } catch {
    // fall through to text
  }

  try {
    const bodyText = await response.text();
    if (bodyText.trim()) {
      return bodyText.trim();
    }
  } catch {
    // ignore
  }

  return `${response.status} ${response.statusText}`.trim();
}

function buildQueryString(query?: Record<string, QueryValue>): string {
  if (!query) {
    return "";
  }

  const params = new URLSearchParams();
  for (const [key, value] of Object.entries(query)) {
    if (value === null || value === undefined || value === "") {
      continue;
    }

    if (Array.isArray(value)) {
      for (const item of value) {
        params.append(key, item);
      }
    } else {
      params.set(key, String(value));
    }
  }

  const encoded = params.toString();
  return encoded ? `?${encoded}` : "";
}

async function requestJson<TResponse>(
  path: string,
  init?: RequestInit,
): Promise<TResponse> {
  const response = await fetch(resolveGatewayPath(path), init);

  if (!response.ok) {
    throw new Error(await readErrorDetail(response));
  }

  return readJson<TResponse>(response);
}

async function requestVoid(path: string, init?: RequestInit): Promise<void> {
  const response = await fetch(resolveGatewayPath(path), init);

  if (!response.ok) {
    throw new Error(await readErrorDetail(response));
  }
}

function encodePathSegments(path: string): string {
  return path
    .replace(/\\/g, "/")
    .replace(/^\/+/, "")
    .split("/")
    .filter(Boolean)
    .map((segment) => encodeURIComponent(segment))
    .join("/");
}

export function buildCanvasEntryUrl(entryPath: string): string {
  return resolveGatewayPath(`/api/canvas/fs/${encodePathSegments(entryPath)}`);
}

function isAbsoluteUrl(value: string): boolean {
  return /^https?:\/\//i.test(value);
}

function normalizeCanvasEntryResponse(payload: CanvasEntryResponse): CanvasEntryResponse {
  const entryUrl = payload.entryUrl.trim();
  return {
    ...payload,
    entryUrl: entryUrl
      ? (isAbsoluteUrl(entryUrl) ? entryUrl : resolveGatewayPath(entryUrl))
      : buildCanvasEntryUrl(payload.entryPath),
  };
}

export async function fetchGatewayHealth(signal?: AbortSignal): Promise<GatewayHealthResponse> {
  return requestJson<GatewayHealthResponse>("/api/system/health", {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchBootstrapState(signal?: AbortSignal): Promise<BootstrapStateResponse> {
  return requestJson<BootstrapStateResponse>("/api/system/bootstrap-state", {
    headers: buildHeaders(),
    signal,
  });
}

export async function completeBootstrap(
  request: BootstrapCompletionRequest,
  signal?: AbortSignal,
): Promise<BootstrapCompletionResult> {
  return requestJson<BootstrapCompletionResult>("/api/system/bootstrap-complete", {
    method: "POST",
    headers: buildHeaders(true),
    body: JSON.stringify(request),
    signal,
  });
}

export async function generateBootstrapDraft(
  request: BootstrapDraftRequest,
  signal?: AbortSignal,
): Promise<BootstrapDraftResult> {
  return requestJson<BootstrapDraftResult>("/api/system/bootstrap-draft", {
    method: "POST",
    headers: buildHeaders(true),
    body: JSON.stringify(request),
    signal,
  });
}

export async function fetchInbox(
  query?: {
    limit?: number;
    status?: InboxItemStatus;
    kind?: InboxItemKind;
    requiresAction?: boolean;
    sessionId?: string | null;
  },
  signal?: AbortSignal,
): Promise<InboxQueryResponse> {
  return requestJson<InboxQueryResponse>(`/api/inbox${buildQueryString(query)}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchInboxItem(id: string, signal?: AbortSignal): Promise<InboxItem> {
  return requestJson<InboxItem>(`/api/inbox/${id}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function updateInboxStatus(
  id: string,
  status: InboxItemStatus,
  signal?: AbortSignal,
): Promise<InboxItem> {
  const payload: InboxStatusUpdateRequest = { status };

  return requestJson<InboxItem>(`/api/inbox/${id}/status`, {
    method: "PATCH",
    headers: buildHeaders(true),
    body: JSON.stringify(payload),
    signal,
  });
}

export async function fetchApprovals(
  query?: {
    limit?: number;
    status?: ApprovalStatus;
    kind?: ApprovalKind;
    sessionId?: string | null;
  },
  signal?: AbortSignal,
): Promise<ApprovalQueryResponse> {
  return requestJson<ApprovalQueryResponse>(`/api/approvals${buildQueryString(query)}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchAutomations(
  query?: {
    limit?: number;
    enabled?: boolean;
    source?: AutomationDefinitionSource;
  },
  signal?: AbortSignal,
): Promise<AutomationDefinitionsQueryResponse> {
  return requestJson<AutomationDefinitionsQueryResponse>(`/api/automations${buildQueryString(query)}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchAutomation(id: string, signal?: AbortSignal): Promise<AutomationDefinition> {
  return requestJson<AutomationDefinition>(`/api/automations/${id}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchAutomationRuns(
  automationId: string,
  query?: {
    limit?: number;
    status?: string | null;
  },
  signal?: AbortSignal,
): Promise<AutomationRunsQueryResponse> {
  return requestJson<AutomationRunsQueryResponse>(
    `/api/automations/${automationId}/runs${buildQueryString(query)}`,
    {
      headers: buildHeaders(),
      signal,
    },
  );
}

export async function updateAutomationDefinition(
  id: string,
  enabled: boolean,
  signal?: AbortSignal,
): Promise<AutomationDefinition> {
  const payload: UpdateAutomationDefinitionRequest = { enabled };

  return requestJson<AutomationDefinition>(`/api/automations/${id}`, {
    method: "PATCH",
    headers: buildHeaders(true),
    body: JSON.stringify(payload),
    signal,
  });
}

export async function triggerAutomation(
  id: string,
  signal?: AbortSignal,
): Promise<TriggerAutomationResponse> {
  return requestJson<TriggerAutomationResponse>(`/api/automations/${encodeURIComponent(id)}/trigger`, {
    method: "POST",
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchPlugins(
  query?: {
    type?: PluginType;
    trustState?: PluginTrustState;
    enabled?: boolean;
    runtimeState?: PluginRuntimeState;
    limit?: number;
  },
  signal?: AbortSignal,
): Promise<PluginsQueryResponse> {
  return requestJson<PluginsQueryResponse>(`/api/plugins${buildQueryString(query)}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchPlugin(id: string, signal?: AbortSignal): Promise<PluginDetail> {
  return requestJson<PluginDetail>(`/api/plugins/${id}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function installLocalPlugin(
  request: InstallLocalPluginRequest,
  signal?: AbortSignal,
): Promise<PluginDetail> {
  return requestJson<PluginDetail>("/api/plugins/install-local", {
    method: "POST",
    headers: buildHeaders(true),
    body: JSON.stringify(request),
    signal,
  });
}

export async function discoverPlugins(signal?: AbortSignal): Promise<PluginsQueryResponse> {
  return requestJson<PluginsQueryResponse>("/api/plugins/discover", {
    method: "POST",
    headers: buildHeaders(),
    signal,
  });
}

export async function trustPlugin(id: string, signal?: AbortSignal): Promise<PluginDetail> {
  return requestJson<PluginDetail>(`/api/plugins/${id}/trust`, {
    method: "POST",
    headers: buildHeaders(),
    signal,
  });
}

export async function enablePlugin(id: string, signal?: AbortSignal): Promise<PluginDetail> {
  return requestJson<PluginDetail>(`/api/plugins/${id}/enable`, {
    method: "POST",
    headers: buildHeaders(),
    signal,
  });
}

export async function disablePlugin(id: string, signal?: AbortSignal): Promise<PluginDetail> {
  return requestJson<PluginDetail>(`/api/plugins/${id}/disable`, {
    method: "POST",
    headers: buildHeaders(),
    signal,
  });
}

export async function startPlugin(id: string, signal?: AbortSignal): Promise<PluginDetail> {
  return requestJson<PluginDetail>(`/api/plugins/${id}/start`, {
    method: "POST",
    headers: buildHeaders(),
    signal,
  });
}

export async function stopPlugin(id: string, signal?: AbortSignal): Promise<PluginDetail> {
  return requestJson<PluginDetail>(`/api/plugins/${id}/stop`, {
    method: "POST",
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchPluginLogs(
  id: string,
  query?: { limit?: number },
  signal?: AbortSignal,
): Promise<PluginLogEntry[]> {
  return requestJson<PluginLogEntry[]>(`/api/plugins/${id}/logs${buildQueryString(query)}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchChannelConnectors(
  signal?: AbortSignal,
): Promise<ChannelConnectorDescriptor[]> {
  return requestJson<ChannelConnectorDescriptor[]>("/api/channels/connectors", {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchChannelAccounts(
  query?: {
    connectorKind?: ChannelConnectorKind;
    state?: ChannelAccountState;
    limit?: number;
  },
  signal?: AbortSignal,
): Promise<ChannelAccount[]> {
  return requestJson<ChannelAccount[]>(`/api/channels/accounts${buildQueryString(query)}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchChannelThreads(
  query?: {
    connectorKind?: ChannelConnectorKind;
    accountId?: string | null;
    threadType?: ChannelThreadType;
    sessionKind?: SessionDetail["sessionKind"];
    sessionId?: string | null;
    limit?: number;
  },
  signal?: AbortSignal,
): Promise<ChannelsQueryResponse> {
  return requestJson<ChannelsQueryResponse>(`/api/channels/threads${buildQueryString(query)}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchChannelThreadDetail(
  bindingId: string,
  signal?: AbortSignal,
): Promise<ChannelThreadDetail> {
  return requestJson<ChannelThreadDetail>(`/api/channels/threads/${bindingId}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchChannelThreadAudit(
  bindingId: string,
  query?: { limit?: number },
  signal?: AbortSignal,
): Promise<ChannelAuditEntry[]> {
  return requestJson<ChannelAuditEntry[]>(
    `/api/channels/threads/${bindingId}/audit${buildQueryString(query)}`,
    {
      headers: buildHeaders(),
      signal,
    },
  );
}

export async function updateThreadSettings(
  bindingId: string,
  request: UpdateThreadSettingsRequest,
  signal?: AbortSignal,
): Promise<void> {
  return requestVoid(`/api/channels/threads/${bindingId}/settings`, {
    method: "PATCH",
    headers: buildHeaders(true),
    body: JSON.stringify(request),
    signal,
  });
}

export async function createChannelAccount(
  request: CreateChannelAccountRequest,
  signal?: AbortSignal,
): Promise<ChannelAccount> {
  return requestJson<ChannelAccount>("/api/channels/accounts", {
    method: "POST",
    headers: buildHeaders(true),
    body: JSON.stringify(request),
    signal,
  });
}

export async function updateChannelAccount(
  id: string,
  request: PatchChannelAccountRequest,
  signal?: AbortSignal,
): Promise<ChannelAccount> {
  return requestJson<ChannelAccount>(`/api/channels/accounts/${encodeURIComponent(id)}`, {
    method: "PATCH",
    headers: buildHeaders(true),
    body: JSON.stringify(request),
    signal,
  });
}

export async function deleteChannelAccount(
  id: string,
  signal?: AbortSignal,
): Promise<void> {
  return requestVoid(`/api/channels/accounts/${encodeURIComponent(id)}`, {
    method: "DELETE",
    headers: buildHeaders(),
    signal,
  });
}

export async function testTelegramToken(
  botToken: string,
  signal?: AbortSignal,
): Promise<TestTelegramTokenResponse> {
  const payload: TestTelegramTokenRequest = { botToken };
  return requestJson<TestTelegramTokenResponse>("/api/channels/test-telegram-token", {
    method: "POST",
    headers: buildHeaders(true),
    body: JSON.stringify(payload),
    signal,
  });
}

export async function testFeishuCredentials(
  appId: string,
  appSecret: string,
  signal?: AbortSignal,
): Promise<TestFeishuCredentialsResponse> {
  const payload: TestFeishuCredentialsRequest = { appId, appSecret };
  return requestJson<TestFeishuCredentialsResponse>("/api/channels/test-feishu-credentials", {
    method: "POST",
    headers: buildHeaders(true),
    body: JSON.stringify(payload),
    signal,
  });
}

export async function getWeChatQrCode(signal?: AbortSignal): Promise<WeChatQrCodeResult> {
  return requestJson<WeChatQrCodeResult>("/api/channels/wechat/get-qrcode", {
    method: "POST",
    headers: buildHeaders(true),
    body: JSON.stringify({}),
    signal,
  });
}

export async function pollWeChatQrStatus(
  qrcode: string,
  signal?: AbortSignal,
): Promise<WeChatQrCodeStatus> {
  return requestJson<WeChatQrCodeStatus>(
    `/api/channels/wechat/qrcode-status?qrcode=${encodeURIComponent(qrcode)}`,
    { headers: buildHeaders(), signal },
  );
}

export async function testWeChatCredentials(
  botToken: string,
  signal?: AbortSignal,
): Promise<TestWeChatCredentialsResponse> {
  return requestJson<TestWeChatCredentialsResponse>("/api/channels/wechat/test-credentials", {
    method: "POST",
    headers: buildHeaders(true),
    body: JSON.stringify({ botToken }),
    signal,
  });
}

export async function fetchApproval(id: string, signal?: AbortSignal): Promise<Approval> {
  return requestJson<Approval>(`/api/approvals/${id}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function submitApprovalDecision(
  id: string,
  approve: boolean,
  note?: string | null,
  signal?: AbortSignal,
): Promise<Approval> {
  const payload: ApprovalDecisionRequest = {
    note: note?.trim() ? note.trim() : null,
  };

  return requestJson<Approval>(`/api/approvals/${id}/${approve ? "approve" : "reject"}`, {
    method: "POST",
    headers: buildHeaders(true),
    body: JSON.stringify(payload),
    signal,
  });
}

export async function fetchSessions(limit = 20, signal?: AbortSignal): Promise<SessionsQueryResponse> {
  return requestJson<SessionsQueryResponse>(`/api/sessions${buildQueryString({ limit })}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchSessionDetail(id: string, signal?: AbortSignal): Promise<SessionDetail> {
  return requestJson<SessionDetail>(`/api/sessions/${id}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function uploadMedia(file: File, signal?: AbortSignal): Promise<MediaMeta> {
  const form = new FormData();
  form.append("file", file);
  const response = await fetch(resolveGatewayPath("/api/media/upload"), {
    method: "POST",
    headers: buildHeaders(),
    body: form,
    signal,
  });
  if (!response.ok) {
    throw new Error(`Media upload failed: ${response.status}`);
  }
  return response.json() as Promise<MediaMeta>;
}

export async function rotateSession(signal?: AbortSignal): Promise<RotateSessionResponse> {
  return requestJson<RotateSessionResponse>("/api/sessions/rotate", {
    method: "POST",
    headers: buildHeaders(),
    signal,
  });
}

export async function resumeSession(id: string, signal?: AbortSignal): Promise<ResumeSessionResponse> {
  return requestJson<ResumeSessionResponse>(`/api/sessions/${id}/resume`, {
    method: "POST",
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchSessionMessages(
  id: string,
  limit = 20,
  skip = 0,
  signal?: AbortSignal,
): Promise<SessionMessagesResponse> {
  return requestJson<SessionMessagesResponse>(
    `/api/sessions/${id}/messages${buildQueryString({ limit, skip })}`,
    { headers: buildHeaders(), signal },
  );
}

export async function fetchDiagnosticsRecent(
  query?: {
    limit?: number;
    correlationId?: string | null;
    sessionId?: string | null;
    source?: string | null;
    eventType?: string | null;
    levels?: string[] | null;
  },
  signal?: AbortSignal,
): Promise<DiagnosticsQueryResponse> {
  return requestJson<DiagnosticsQueryResponse>(
    `/api/diagnostics/recent${buildQueryString(query)}`,
    {
      headers: buildHeaders(),
      signal,
    },
  );
}

export async function fetchDiagnosticsTimeline(
  query?: {
    limit?: number;
    correlationId?: string | null;
    sessionId?: string | null;
    source?: string | null;
    eventType?: string | null;
    levels?: string[] | null;
  },
  signal?: AbortSignal,
): Promise<DiagnosticsQueryResponse> {
  return requestJson<DiagnosticsQueryResponse>(
    `/api/diagnostics/timeline${buildQueryString(query)}`,
    {
      headers: buildHeaders(),
      signal,
    },
  );
}

export async function fetchDiagnosticsStats(
  params?: { since?: string },
  signal?: AbortSignal,
): Promise<DiagnosticsStatsResponse> {
  const qs = params?.since ? `?since=${encodeURIComponent(params.since)}` : '';
  return requestJson<DiagnosticsStatsResponse>(`/api/diagnostics/stats${qs}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function clearDiagnostics(
  before?: string | null,
  signal?: AbortSignal,
): Promise<void> {
  const qs = before ? `?before=${encodeURIComponent(before)}` : "";
  await requestJson<void>(`/api/diagnostics/${qs}`, {
    method: "DELETE",
    headers: buildHeaders(),
    signal,
  });
}

export function openDiagnosticsStream(
  onEvent: (evt: DiagnosticEvent) => void,
  signal?: AbortSignal,
): void {
  void openSseStreamWithRetry(
    "/api/diagnostics/stream",
    (frame) => {
      if (frame.eventName === "message" || frame.eventName === "") {
        try { onEvent(JSON.parse(frame.data) as DiagnosticEvent); } catch { /* ignore malformed */ }
      }
    },
    signal,
  );
}

export type SystemStatsEvent = {
  errorCount: number;
  warningCount: number;
  inboxUnreadCount: number;
};

export function openSystemEventsStream(
  onStats: (evt: SystemStatsEvent) => void,
  signal?: AbortSignal,
): void {
  void openSseStreamWithRetry(
    "/api/events/stream",
    (frame) => {
      if (frame.eventName === "system.stats") {
        try { onStats(JSON.parse(frame.data) as SystemStatsEvent); } catch { /* ignore malformed */ }
      }
    },
    signal,
  );
}

async function openSseStreamWithRetry(
  path: string,
  onFrame: (frame: ParsedSseFrame) => void,
  signal?: AbortSignal,
): Promise<void> {
  let lastEventId: string | undefined;
  let delayMs = 1_000;
  const maxDelayMs = 30_000;

  while (!signal?.aborted) {
    try {
      const headers: Record<string, string> = { ...buildHeaders() as Record<string, string> };
      if (lastEventId !== undefined) {
        headers["Last-Event-ID"] = lastEventId;
      }
      const response = await fetch(resolveGatewayPath(path), { headers, signal });
      if (!response.ok || !response.body) {
        await delay(delayMs, signal);
        delayMs = Math.min(delayMs * 2, maxDelayMs);
        continue;
      }

      delayMs = 1_000; // reset backoff on successful connect
      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = "";

      try {
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;
          buffer += decoder.decode(value, { stream: true });
          const parsed = parseSseFrames(buffer);
          buffer = parsed.rest;
          for (const frame of parsed.frames) {
            if (frame.id !== undefined) lastEventId = frame.id;
            onFrame(frame);
          }
        }
      } finally {
        reader.releaseLock();
      }
    } catch (err) {
      if (signal?.aborted || (err instanceof Error && err.name === "AbortError")) return;
    }

    // stream ended or errored — wait before reconnecting
    await delay(delayMs, signal);
    delayMs = Math.min(delayMs * 2, maxDelayMs);
  }
}

function delay(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve, reject) => {
    if (signal?.aborted) { reject(new DOMException("Aborted", "AbortError")); return; }
    const id = setTimeout(resolve, ms);
    signal?.addEventListener("abort", () => { clearTimeout(id); reject(new DOMException("Aborted", "AbortError")); }, { once: true });
  });
}

export async function exportDiagnosticBundle(
  request?: DiagnosticBundleExportRequest,
  signal?: AbortSignal,
): Promise<DiagnosticBundleExportResponse> {
  return requestJson<DiagnosticBundleExportResponse>("/api/diagnostics/bundle-export", {
    method: "POST",
    headers: buildHeaders(true),
    body: JSON.stringify(request ?? {}),
    signal,
  });
}

export async function fetchCanvasArtifacts(
  query?: {
    kind?: CanvasArtifactKind;
    source?: string | null;
    sessionId?: string | null;
    limit?: number;
  },
  signal?: AbortSignal,
): Promise<CanvasQueryResponse> {
  return requestJson<CanvasQueryResponse>(`/api/canvas${buildQueryString(query)}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchCanvasArtifact(id: string, signal?: AbortSignal): Promise<CanvasArtifact> {
  return requestJson<CanvasArtifact>(`/api/canvas/${id}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchCanvasDefaultEntry(signal?: AbortSignal): Promise<CanvasEntryResponse> {
  const payload = await requestJson<CanvasEntryResponse>("/api/canvas/default", {
    headers: buildHeaders(),
    signal,
  });

  return normalizeCanvasEntryResponse(payload);
}

export async function fetchCanvasArtifactEntry(
  id: string,
  signal?: AbortSignal,
): Promise<CanvasEntryResponse> {
  const payload = await requestJson<CanvasEntryResponse>(`/api/canvas/${id}/entry`, {
    headers: buildHeaders(),
    signal,
  });

  return normalizeCanvasEntryResponse(payload);
}

export async function publishCanvasArtifact(
  request: UpsertCanvasArtifactRequest,
  signal?: AbortSignal,
): Promise<CanvasArtifact> {
  return requestJson<CanvasArtifact>("/api/canvas", {
    method: "POST",
    headers: buildHeaders(true),
    body: JSON.stringify(request),
    signal,
  });
}

// ── Provider Accounts API ─────────────────────────────────────

export async function fetchProviderAccounts(signal?: AbortSignal): Promise<ProviderAccountResponse[]> {
  return requestJson<ProviderAccountResponse[]>("/api/provider-accounts", {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchProviderAccount(id: string, signal?: AbortSignal): Promise<ProviderAccountResponse> {
  return requestJson<ProviderAccountResponse>(`/api/provider-accounts/${encodeURIComponent(id)}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function createProviderAccount(
  request: CreateProviderAccountRequest,
  signal?: AbortSignal,
): Promise<ProviderAccountResponse> {
  return requestJson<ProviderAccountResponse>("/api/provider-accounts", {
    method: "POST",
    headers: buildHeaders(true),
    body: JSON.stringify(request),
    signal,
  });
}

export async function updateProviderAccount(
  id: string,
  request: UpdateProviderAccountRequest,
  signal?: AbortSignal,
): Promise<ProviderAccountResponse> {
  return requestJson<ProviderAccountResponse>(`/api/provider-accounts/${encodeURIComponent(id)}`, {
    method: "PUT",
    headers: buildHeaders(true),
    body: JSON.stringify(request),
    signal,
  });
}

export async function deleteProviderAccount(id: string, signal?: AbortSignal): Promise<void> {
  return requestVoid(`/api/provider-accounts/${encodeURIComponent(id)}`, {
    method: "DELETE",
    headers: buildHeaders(),
    signal,
  });
}

// ── Account Models API ─────────────────────────────────────────

export async function fetchAccountModels(signal?: AbortSignal): Promise<AccountModelResponse[]> {
  return requestJson<AccountModelResponse[]>("/api/models", {
    headers: buildHeaders(),
    signal,
  });
}

export async function createAccountModel(
  accountId: string,
  request: CreateAccountModelRequest,
  signal?: AbortSignal,
): Promise<AccountModelResponse> {
  return requestJson<AccountModelResponse>(
    `/api/provider-accounts/${encodeURIComponent(accountId)}/models`,
    {
      method: "POST",
      headers: buildHeaders(true),
      body: JSON.stringify(request),
      signal,
    },
  );
}

export async function updateAccountModel(
  accountId: string,
  modelId: string,
  request: UpdateAccountModelRequest,
  signal?: AbortSignal,
): Promise<AccountModelResponse> {
  return requestJson<AccountModelResponse>(
    `/api/provider-accounts/${encodeURIComponent(accountId)}/models/${encodeURIComponent(modelId)}`,
    {
      method: "PUT",
      headers: buildHeaders(true),
      body: JSON.stringify(request),
      signal,
    },
  );
}

export async function deleteAccountModel(
  accountId: string,
  modelId: string,
  signal?: AbortSignal,
): Promise<void> {
  return requestVoid(
    `/api/provider-accounts/${encodeURIComponent(accountId)}/models/${encodeURIComponent(modelId)}`,
    {
      method: "DELETE",
      headers: buildHeaders(),
      signal,
    },
  );
}

export async function setDefaultAccountModel(
  accountId: string,
  modelId: string,
  signal?: AbortSignal,
): Promise<AccountModelResponse> {
  return requestJson<AccountModelResponse>(
    `/api/provider-accounts/${encodeURIComponent(accountId)}/models/${encodeURIComponent(modelId)}/default`,
    {
      method: "POST",
      headers: buildHeaders(),
      signal,
    },
  );
}

export async function fetchSettings(signal?: AbortSignal): Promise<KodaClawSettings> {
  return requestJson<KodaClawSettings>("/api/settings", {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchSandboxRiskOverview(signal?: AbortSignal): Promise<SandboxRiskOverviewResponse> {
  return requestJson<SandboxRiskOverviewResponse>("/api/settings/sandbox-risk", {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchUpdateState(signal?: AbortSignal): Promise<UpdateStateResponse> {
  return requestJson<UpdateStateResponse>("/api/system/update-state", {
    headers: buildHeaders(),
    signal,
  });
}

export async function runUpdateCheck(
  request?: UpdateCheckRequest,
  signal?: AbortSignal,
): Promise<UpdateStateResponse> {
  return requestJson<UpdateStateResponse>("/api/system/update-check", {
    method: "POST",
    headers: buildHeaders(true),
    body: JSON.stringify(request ?? {}),
    signal,
  });
}

export async function saveSettings(
  settings: KodaClawSettings,
  signal?: AbortSignal,
): Promise<KodaClawSettings> {
  return requestJson<KodaClawSettings>("/api/settings", {
    method: "PUT",
    headers: buildHeaders(true),
    body: JSON.stringify(settings),
    signal,
  });
}

export async function setAutomationsEnabled(
  enabled: boolean,
  signal?: AbortSignal,
): Promise<KodaClawSettings> {
  const current = await fetchSettings(signal);
  return saveSettings({ ...current, automationsEnabled: enabled }, signal);
}

export type ParseSseFramesOptions = {
  flushTrailing?: boolean;
};

export function parseSseFrames(
  buffer: string,
  { flushTrailing = false }: ParseSseFramesOptions = {},
): {
  frames: ParsedSseFrame[];
  rest: string;
} {
  const frames: ParsedSseFrame[] = [];
  let cursor = buffer;

  while (true) {
    const boundary = cursor.indexOf("\n\n");
    if (boundary < 0) {
      break;
    }

    const frameText = cursor.slice(0, boundary);
    cursor = cursor.slice(boundary + 2);

    const parsed = parseFrameText(frameText);
    if (parsed) {
      frames.push(parsed);
    }
  }

  if (flushTrailing && cursor.trim()) {
    const trailing = parseFrameText(cursor);
    if (trailing) {
      frames.push(trailing);
      cursor = "";
    }
  }

  return { frames, rest: cursor };
}

function parseFrameText(frameText: string): ParsedSseFrame | null {
  const trimmed = frameText.trim();
  if (!trimmed) {
    return null;
  }

  let eventName = "message";
  let id: string | undefined;
  const dataParts: string[] = [];

  for (const line of trimmed.split("\n")) {
    if (line.startsWith("event:")) {
      eventName = line.slice("event:".length).trim() || eventName;
    } else if (line.startsWith("id:")) {
      id = line.slice("id:".length).trim();
    } else if (line.startsWith("data:")) {
      dataParts.push(line.slice("data:".length).trim());
    }
  }

  if (dataParts.length === 0) {
    return null;
  }

  return {
    eventName,
    data: dataParts.join("\n"),
    ...(id !== undefined ? { id } : {}),
  };
}

// ===== 模型预设 API =====

export async function fetchModelPresets(signal?: AbortSignal): Promise<ModelPreset[]> {
  return requestJson<ModelPreset[]>("/api/models/presets", {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchModelPreset(presetId: string, signal?: AbortSignal): Promise<ModelPreset> {
  return requestJson<ModelPreset>(`/api/models/presets/${encodeURIComponent(presetId)}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function testModelConnection(
  request: ModelConnectionTestRequest,
  signal?: AbortSignal,
): Promise<ModelConnectionTestResponse> {
  return requestJson<ModelConnectionTestResponse>("/api/models/test-connection", {
    method: "POST",
    headers: buildHeaders(true),
    body: JSON.stringify(request),
    signal,
  });
}

// ===== Persona 预设 API =====

export async function fetchPersonaPresets(signal?: AbortSignal): Promise<PersonaPreset[]> {
  return requestJson<PersonaPreset[]>("/api/workspace/persona-presets", {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchPersonaPreset(presetId: string, signal?: AbortSignal): Promise<PersonaPreset> {
  return requestJson<PersonaPreset>(`/api/workspace/persona-presets/${encodeURIComponent(presetId)}`, {
    headers: buildHeaders(),
    signal,
  });
}

// ===== Onboarding API =====

export async function fetchOnboardingState(signal?: AbortSignal): Promise<OnboardingState> {
  return requestJson<OnboardingState>("/api/onboarding/state", {
    headers: buildHeaders(),
    signal,
  });
}

export async function updateOnboardingState(
  state: Partial<OnboardingState>,
  signal?: AbortSignal,
): Promise<OnboardingState> {
  return requestJson<OnboardingState>("/api/onboarding/state", {
    method: "PUT",
    headers: buildHeaders(true),
    body: JSON.stringify(state),
    signal,
  });
}

export async function completeOnboarding(signal?: AbortSignal): Promise<OnboardingState> {
  return requestJson<OnboardingState>("/api/onboarding/complete", {
    method: "POST",
    headers: buildHeaders(),
    signal,
  });
}

export async function resetOnboarding(signal?: AbortSignal): Promise<OnboardingState> {
  return requestJson<OnboardingState>("/api/onboarding/reset", {
    method: "POST",
    headers: buildHeaders(),
    signal,
  });
}

// ===== Workspace File API =====

export type WorkspaceFileTarget = 'identity' | 'soul' | 'user' | 'memory' | 'heartbeat';

export async function fetchWorkspaceFile(
  target: WorkspaceFileTarget,
  signal?: AbortSignal,
): Promise<{ target: string; content: string }> {
  return requestJson<{ target: string; content: string }>(
    `/api/workspace/file?target=${encodeURIComponent(target)}`,
    { headers: buildHeaders(), signal },
  );
}

export async function updateWorkspaceFile(
  target: WorkspaceFileTarget,
  content: string,
  signal?: AbortSignal,
): Promise<{ target: string; content: string }> {
  return requestJson<{ target: string; content: string }>(`/api/workspace/file?target=${encodeURIComponent(target)}`, {
    method: "PUT",
    headers: buildHeaders(true),
    body: JSON.stringify({ content }),
    signal,
  });
}

export async function applyPersonaPreset(
  presetId: string,
  signal?: AbortSignal,
): Promise<{ ok: boolean }> {
  const payload: ApplyPersonaRequest = { presetId };
  return requestJson<{ ok: boolean }>("/api/onboarding/apply-persona", {
    method: "POST",
    headers: buildHeaders(true),
    body: JSON.stringify(payload),
    signal,
  });
}

function toChatStreamEvent(frame: ParsedSseFrame, request: ChatStreamRequest): ChatStreamEvent {
  const payload = JSON.parse(frame.data) as Partial<ChatStreamEvent>;
  const type = (payload.type ?? frame.eventName) as ChatStreamEvent["type"];

  return {
    type,
    sessionId: payload.sessionId ?? request.sessionId ?? "main-unavailable",
    step: payload.step ?? null,
    sequence: payload.sequence ?? null,
    timestamp: payload.timestamp ?? null,
    delta: payload.delta ?? null,
    reason: payload.reason ?? null,
    error: payload.error ?? null,
    approvalId: payload.approvalId ?? null,
    callId: payload.callId ?? null,
    toolName: payload.toolName ?? null,
    inputPreview: payload.inputPreview ?? null,
    decision: payload.decision ?? null,
    durationMs: payload.durationMs ?? null,
    subAgentId: payload.subAgentId ?? null,
    label: payload.label ?? null,
    subAgentToolName: payload.subAgentToolName ?? null,
    thinkingDelta: payload.thinkingDelta ?? null,
  };
}

export async function* streamChatEvents(
  request: ChatStreamRequest,
  signal?: AbortSignal,
): AsyncGenerator<ChatStreamEvent, void, void> {
  const response = await fetch(resolveGatewayPath("/api/chat/stream"), {
    method: "POST",
    headers: buildHeaders(true),
    body: JSON.stringify(request),
    signal,
  });

  if (!response.ok) {
    throw new Error(`Chat stream failed: ${await readErrorDetail(response)}`);
  }

  if (!response.body) {
    throw new Error("Chat stream body is empty.");
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder("utf-8");
  let buffer = "";

  try {
    while (true) {
      const { value, done } = await reader.read();
      if (done) {
        break;
      }

      buffer += decoder.decode(value, { stream: true });
      const parsed = parseSseFrames(buffer);
      buffer = parsed.rest;

      for (const frame of parsed.frames) {
        yield toChatStreamEvent(frame, request);
      }
    }

    if (buffer.trim()) {
      const trailing = parseSseFrames(buffer, { flushTrailing: true });
      for (const frame of trailing.frames) {
        yield toChatStreamEvent(frame, request);
      }
    }
  } finally {
    reader.releaseLock();
  }
}

export async function fetchMcpServers(): Promise<WorkspaceMcpConfig> {
  const response = await fetch(resolveGatewayPath("/api/mcp-servers"), {
    headers: buildHeaders(),
  });
  if (!response.ok) {
    throw new Error(`Failed to fetch MCP servers: ${await readErrorDetail(response)}`);
  }
  return readJson<WorkspaceMcpConfig>(response);
}

export async function saveMcpServers(config: WorkspaceMcpConfig): Promise<WorkspaceMcpConfig> {
  const response = await fetch(resolveGatewayPath("/api/mcp-servers"), {
    method: "PUT",
    headers: buildHeaders(true),
    body: JSON.stringify(config),
  });
  if (!response.ok) {
    throw new Error(`Failed to save MCP servers: ${await readErrorDetail(response)}`);
  }
  return readJson<WorkspaceMcpConfig>(response);
}

export async function testMcpServerConnection(name: string): Promise<McpConnectionTestResult> {
  const response = await fetch(
    resolveGatewayPath(`/api/mcp-servers/${encodeURIComponent(name)}/test-connection`),
    {
      method: "POST",
      headers: buildHeaders(),
    },
  );
  if (!response.ok) {
    throw new Error(`Failed to test MCP server: ${await readErrorDetail(response)}`);
  }
  return readJson<McpConnectionTestResult>(response);
}

// ===== Inbox Channel Push API =====

export async function pushAutomationResultToChannel(
  inboxId: string,
  bindingIds?: string[],
): Promise<PushToChannelResponse> {
  return requestJson<PushToChannelResponse>(
    `/api/inbox/${encodeURIComponent(inboxId)}/push-to-channel`,
    {
      method: "POST",
      headers: buildHeaders(true),
      body: JSON.stringify({ bindingIds: bindingIds ?? [] }),
    },
  );
}

export async function fetchStorageUsage(signal?: AbortSignal): Promise<StorageUsageResponse> {
  return requestJson<StorageUsageResponse>("/api/system/storage-usage", {
    headers: buildHeaders(),
    signal,
  });
}

export async function deleteMainSession(sessionId: string): Promise<void> {
  const response = await fetch(
    resolveGatewayPath(`/api/sessions/main/${encodeURIComponent(sessionId)}`),
    { method: "DELETE", headers: buildHeaders() },
  );
  if (!response.ok) {
    const err = await response.json().catch(() => ({})) as { message?: string };
    throw new Error(err.message ?? `Delete failed: ${response.status}`);
  }
}

export async function fetchWorkspaceGitLog(limit = 10, skip = 0): Promise<WorkspaceGitLogResponse> {
  const response = await fetch(
    resolveGatewayPath(`/api/workspace/git/log?limit=${limit}&skip=${skip}`),
    { headers: buildHeaders() },
  );
  if (!response.ok) throw new Error(`Git log failed: ${response.status}`);
  return readJson<WorkspaceGitLogResponse>(response);
}

export async function fetchWorkspaceGitDiff(hash: string): Promise<string> {
  const response = await fetch(
    resolveGatewayPath(`/api/workspace/git/diff/${encodeURIComponent(hash)}`),
    { headers: buildHeaders() },
  );
  if (!response.ok) throw new Error(`Git diff failed: ${response.status}`);
  return response.text();
}

export async function revertWorkspaceFile(
  req: WorkspaceGitRevertFileRequest,
): Promise<WorkspaceGitRevertFileResponse> {
  const response = await fetch(
    resolveGatewayPath("/api/workspace/git/revert-file"),
    {
      method: "POST",
      headers: { ...buildHeaders(), "Content-Type": "application/json" },
      body: JSON.stringify(req),
    },
  );
  if (!response.ok) {
    const err = await response.json().catch(() => ({})) as { message?: string };
    throw new Error(err.message ?? `Revert failed: ${response.status}`);
  }
  return readJson<WorkspaceGitRevertFileResponse>(response);
}

export async function fetchSkills(signal?: AbortSignal): Promise<SkillDescriptor[]> {
  const response = await fetch(resolveGatewayPath("/api/skills"), {
    signal,
    headers: buildHeaders(),
  });
  if (!response.ok) {
    throw new Error(`Failed to load skills: ${response.status} ${response.statusText}`);
  }
  return readJson<SkillDescriptor[]>(response);
}

// --- Memory API ---

export interface MemoryStats {
  activeCount: number;
  dormantCount: number;
  archivedCount: number;
  topicsCount: number;
  sessionsCount: number;
}

export interface MemoryEntryItem {
  key: string;
  title: string;
  priority: string;
  status: string;
  created: string | null;
  sourcePath: string;
  tags: string[] | null;
}

export interface MemoryEntriesResponse {
  count: number;
  entries: MemoryEntryItem[];
}

export async function fetchMemoryStats(signal?: AbortSignal): Promise<MemoryStats> {
  return requestJson<MemoryStats>("/api/memory/stats", {
    headers: buildHeaders(),
    signal,
  });
}

export async function fetchMemoryEntries(
  status?: string,
  limit?: number,
  signal?: AbortSignal,
): Promise<MemoryEntriesResponse> {
  const params = new URLSearchParams();
  if (status) params.set("status", status);
  if (limit) params.set("limit", String(limit));
  const qs = params.toString();
  return requestJson<MemoryEntriesResponse>(`/api/memory/entries${qs ? `?${qs}` : ""}`, {
    headers: buildHeaders(),
    signal,
  });
}

export async function promoteMemoryEntry(key: string): Promise<{ key: string; status: string; message: string }> {
  return requestJson(`/api/memory/entries/${encodeURIComponent(key)}/promote`, {
    method: "POST",
    headers: buildHeaders(),
  });
}
