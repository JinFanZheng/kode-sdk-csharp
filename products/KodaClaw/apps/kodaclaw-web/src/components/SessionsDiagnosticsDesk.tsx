import { useEffect, useMemo, useRef, useState } from "react";
import {
  exportDiagnosticBundle,
  fetchDiagnosticsTimeline,
  fetchSessionDetail,
  fetchSessions,
} from "../lib/api";
import { useI18n, useLocaleText } from "../i18n/I18nProvider";
import { Skeleton } from "./ui/Skeleton";
import { EmptyState } from "./ui/EmptyState";
import { Button } from "./ui/Button";
import { Search } from "lucide-react";
import { getRuntimeConfig } from "../lib/config";
import { CollapsibleSection } from "./ui/CollapsibleSection";
import { MetricRow } from "./ui/MetricRow";
import type {
  DiagnosticBundleExportRequest,
  DiagnosticBundleExportResponse,
  DiagnosticEvent,
  SessionDetail,
  SessionSummary,
} from "../types/contracts";
import "./ControlPlaneDesk.css";
import { RiskSection } from "./settings/RiskSection";

type SessionsDiagnosticsDeskProps = {
  defaultLimit?: number;
  focusRequest?: {
    sessionId: string;
    requestId: number;
  } | null;
  onFocusRequestConsumed?: () => void;
};

const DiagnosticBundleTimelineLimit = 120;

function buildDiagnosticBundleRequest(
  sessionId: string | null,
): DiagnosticBundleExportRequest {
  const runtimeConfig = getRuntimeConfig();
  const request: DiagnosticBundleExportRequest = {
    sessionId,
    timelineLimit: DiagnosticBundleTimelineLimit,
  };

  if (runtimeConfig.desktopMode) {
    request.desktopContext = {
      desktopMode: true,
      platform: runtimeConfig.platform || "unknown",
      appVersion: runtimeConfig.appVersion || "unknown",
      releaseChannel: runtimeConfig.releaseChannel,
      gatewayLifecycleMode: runtimeConfig.gatewayLifecycleMode,
    };
  }

  return request;
}

export function SessionsDiagnosticsDesk({
  defaultLimit = 20,
  focusRequest = null,
  onFocusRequestConsumed,
}: SessionsDiagnosticsDeskProps) {
  const { formatDateTime } = useI18n();
  const text = useLocaleText({
    zh: {
      common: {
        none: "暂无",
        loading: "加载中...",
        refreshing: "正在刷新...",
        items: (count: number) => `${count} 个会话`,
      },
      title: "会话 / 诊断台",
      summary: {
        loading: "正在汇总活跃与最近会话。",
        empty: "还没有可检查的会话，先触发一次聊天或自动化运行。",
        ready: (count: number) => `已加载 ${count} 个会话。选择一条轨迹查看诊断详情或导出脱敏证据包。`,
      },
      refresh: "刷新会话",
      sections: {
        sessionIndex: "会话索引",
        sessionDetail: "会话详情",
        promptOverview: "提示词概况",
        contextFiles: "上下文文件",
        recentChanges: "最近变化",
        recentBuilds: "历史版本",
        systemPrompt: "系统提示词",
        diagnosticsTimeline: "诊断时间线",
        diagnosticBundle: "诊断证据包",
      },
      sessionDetail: {
        loading: "正在加载所选会话详情...",
        empty: "选择一个会话以检查详情与时间线。",
        session: "会话",
        sessionFocus: "当前焦点",
        kind: "类型",
        createdAt: "创建",
        messagesAndApprovals: "消息 / 待审批",
        messageBreakdown: "用户 / 助手 / 工具",
        approvals: "待审批调用",
        traceIndex: "最后 SFP",
        breakpoint: "断点",
        lastEvent: "最近事件",
        promptProfile: "提示词画像",
        promptSize: "大小",
        promptSizeValue: (count: number) => `${count} 字符`,
        promptBudget: "预算",
        promptBudgetValue: (used: number, budget: number, remaining: number) =>
          `${used} / ${budget} 字符，剩余 ${remaining}`,
        promptGeneratedAt: "最近生成",
        loadedContextFiles: "已加载文件",
        truncationState: "裁剪状态",
        truncationOn: "已裁剪",
        truncationOff: "未裁剪",
        truncatedContextFiles: "被裁剪文件",
        truncationNotes: "裁剪说明",
        promptDelta: "最近变化",
        previousPromptGeneratedAt: "上一版生成",
        charDelta: "字符变化",
        charDeltaValue: (delta: number) => `${delta >= 0 ? "+" : ""}${delta} 字符`,
        truncationChanged: "裁剪状态已变化",
        addedContextFiles: "新增上下文",
        removedContextFiles: "移除上下文",
        noPromptDelta: "当前没有上一版提示词可供比较。",
        promptHistory: "最近版本",
        promptPreview: "系统提示词",
        promptUnavailable: "当前会话还没有可用的提示词报告。",
        none: "无",
      },
      sessionList: {
        activeMain: "主链路",
        pendingApprovals: (count: number) => `${count} 个待审批`,
        messages: (count: number) => `${count} 条消息`,
      },
      timeline: {
        loading: "正在加载时间线...",
        empty: "当前会话没有可显示的诊断事件。",
      },
      bundle: {
        requestedSession: "请求范围",
        crossSession: "跨会话诊断快照",
        timelineWindow: "时间线窗口",
        timelineWindowValue: `${DiagnosticBundleTimelineLimit} 条事件上限，默认脱敏`,
        export: "导出诊断包",
        exporting: "正在导出...",
        exportedFor: (sessionId: string) => `诊断包已为 ${sessionId} 导出。`,
        exportedGeneric: "诊断包已导出。",
        exportFailed: "导出诊断包失败。",
        bundlePath: "包路径",
        workspaceRoot: "工作区根目录",
        manifest: "Manifest",
        redactionPosture: "脱敏姿态",
        included: "包含",
        excluded: "排除",
        rawSecrets: "原始 Secrets",
        messageBodies: "消息正文",
      },
      riskSectionTitle: "沙箱与风险简报",
      errors: {
        sessions: "拉取会话列表失败。",
        sessionDetail: "拉取会话详情失败。",
      },
      sessionKindLabels: {
        Main: "主会话",
        Automation: "自动化",
        ChannelDirectMessage: "渠道私信",
        ChannelGroup: "渠道群聊",
        Plugin: "插件",
      },
    },
    en: {
      common: {
        none: "n/a",
        loading: "Loading...",
        refreshing: "Refreshing...",
        items: (count: number) => `${count} sessions`,
      },
      title: "Sessions & Diagnostics",
      summary: {
        loading: "Collecting active and recent sessions.",
        empty: "No sessions available yet. Trigger a chat run first.",
        ready: (count: number) => `${count} sessions loaded. Pick one to inspect diagnostics or export a redacted bundle.`,
      },
      refresh: "Refresh sessions",
      sections: {
        sessionIndex: "Session index",
        sessionDetail: "Session detail",
        promptOverview: "Prompt overview",
        contextFiles: "Context files",
        recentChanges: "Recent changes",
        recentBuilds: "Recent builds",
        systemPrompt: "System prompt",
        diagnosticsTimeline: "Diagnostics timeline",
        diagnosticBundle: "Diagnostic bundle",
      },
      sessionDetail: {
        loading: "Loading selected session detail...",
        empty: "Select a session to inspect detail and timeline.",
        session: "Session",
        sessionFocus: "Current focus",
        kind: "Kind",
        createdAt: "Created",
        messagesAndApprovals: "Messages / Pending approvals",
        messageBreakdown: "User / Assistant / Tools",
        approvals: "Pending approval calls",
        traceIndex: "Last SFP",
        breakpoint: "Breakpoint",
        lastEvent: "Last event",
        promptProfile: "Profile",
        promptSize: "Size",
        promptSizeValue: (count: number) => `${count} chars`,
        promptBudget: "Budget",
        promptBudgetValue: (used: number, budget: number, remaining: number) =>
          `${used} / ${budget} chars, ${remaining} remaining`,
        promptGeneratedAt: "Last generated",
        loadedContextFiles: "Loaded files",
        truncationState: "Truncation",
        truncationOn: "Truncated",
        truncationOff: "Not truncated",
        truncatedContextFiles: "Truncated files",
        truncationNotes: "Truncation notes",
        promptDelta: "Recent changes",
        previousPromptGeneratedAt: "Previous build",
        charDelta: "Character delta",
        charDeltaValue: (delta: number) => `${delta >= 0 ? "+" : ""}${delta} chars`,
        truncationChanged: "Truncation state changed",
        addedContextFiles: "Added context",
        removedContextFiles: "Removed context",
        noPromptDelta: "No previous prompt build is available for comparison.",
        promptHistory: "Recent builds",
        promptPreview: "System prompt",
        promptUnavailable: "No prompt report is available for this session yet.",
        none: "none",
      },
      sessionList: {
        activeMain: "Main line",
        pendingApprovals: (count: number) => `${count} pending approvals`,
        messages: (count: number) => `${count} messages`,
      },
      timeline: {
        loading: "Loading timeline...",
        empty: "No diagnostics events found for the selected session.",
      },
      bundle: {
        requestedSession: "Requested session",
        crossSession: "Cross-session diagnostics snapshot",
        timelineWindow: "Timeline window",
        timelineWindowValue: `${DiagnosticBundleTimelineLimit} events max, redacted by default`,
        export: "Export diagnostic bundle",
        exporting: "Exporting...",
        exportedFor: (sessionId: string) => `Diagnostic bundle exported for ${sessionId}.`,
        exportedGeneric: "Diagnostic bundle exported.",
        exportFailed: "Failed to export diagnostic bundle.",
        bundlePath: "Bundle path",
        workspaceRoot: "Workspace root",
        manifest: "Manifest",
        redactionPosture: "Redaction posture",
        included: "included",
        excluded: "excluded",
        rawSecrets: "Raw secrets",
        messageBodies: "Message bodies",
      },
      riskSectionTitle: "Sandbox & Risk Briefing",
      errors: {
        sessions: "Failed to fetch sessions.",
        sessionDetail: "Failed to fetch session detail.",
      },
      sessionKindLabels: {
        Main: "Main",
        Automation: "Automation",
        ChannelDirectMessage: "Channel direct message",
        ChannelGroup: "Channel group",
        Plugin: "Plugin",
      },
    },
  });

  const [sessions, setSessions] = useState<SessionSummary[]>([]);
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(null);
  const [sessionDetail, setSessionDetail] = useState<SessionDetail | null>(null);
  const [timeline, setTimeline] = useState<DiagnosticEvent[]>([]);
  const [isLoadingList, setIsLoadingList] = useState(true);
  const [isLoadingDetail, setIsLoadingDetail] = useState(false);
  const [isExportingBundle, setIsExportingBundle] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [bundleError, setBundleError] = useState<string | null>(null);
  const [bundleNote, setBundleNote] = useState<string | null>(null);
  const [bundleExport, setBundleExport] =
    useState<DiagnosticBundleExportResponse | null>(null);
  const [refreshToken, setRefreshToken] = useState(0);

  const selectedSessionIdRef = useRef<string | null>(null);
  selectedSessionIdRef.current = selectedSessionId;

  function truncateId(id: string): string {
    return id.length > 12 ? `${id.slice(0, 8)}…` : id;
  }

  const formatTimestamp = (value?: string | null): string =>
    formatDateTime(value, text.common.none);

  const formatSessionKind = (kind: string): string =>
    text.sessionKindLabels[kind as keyof typeof text.sessionKindLabels] ?? kind;

  const formatPromptSize = (count?: number | null): string =>
    count && count > 0 ? text.sessionDetail.promptSizeValue(count) : text.sessionDetail.none;

  const formatPromptBudget = (
    used?: number | null,
    budget?: number | null,
    remaining?: number | null,
  ): string =>
    used && budget && budget > 0
      ? text.sessionDetail.promptBudgetValue(used, budget, Math.max(remaining ?? 0, 0))
      : text.sessionDetail.none;

  const formatPromptDelta = (delta?: number | null): string =>
    delta != null ? text.sessionDetail.charDeltaValue(delta) : text.sessionDetail.none;

  useEffect(() => {
    if (!focusRequest?.sessionId) return;
    setSelectedSessionId(focusRequest.sessionId);
    onFocusRequestConsumed?.();
  }, [focusRequest, onFocusRequestConsumed]);

  useEffect(() => {
    let isDisposed = false;

    async function loadList() {
      setIsLoadingList(true);
      setError(null);
      try {
        const payload = await fetchSessions(defaultLimit);
        if (isDisposed) return;
        setSessions(payload.sessions);
        const preferredSessionId = focusRequest?.sessionId ?? selectedSessionIdRef.current;
        const hasPreferred = preferredSessionId
          ? payload.sessions.some((s) => s.sessionId === preferredSessionId)
          : false;
        setSelectedSessionId(
          hasPreferred && preferredSessionId
            ? preferredSessionId
            : payload.sessions[0]?.sessionId ?? null,
        );
      } catch (nextError) {
        if (isDisposed) return;
        setError(nextError instanceof Error ? nextError.message : text.errors.sessions);
        setSessions([]);
        setSelectedSessionId(null);
      } finally {
        if (!isDisposed) setIsLoadingList(false);
      }
    }

    void loadList();
    return () => { isDisposed = true; };
  }, [defaultLimit, focusRequest?.sessionId, refreshToken, text.errors.sessions]);

  useEffect(() => {
    let isDisposed = false;

    async function loadDetailAndTimeline(sessionId: string) {
      setIsLoadingDetail(true);
      setError(null);
      try {
        const [detail, timelinePayload] = await Promise.all([
          fetchSessionDetail(sessionId),
          fetchDiagnosticsTimeline({ sessionId, limit: 60 }),
        ]);
        if (isDisposed) return;
        setSessionDetail(detail);
        setTimeline(timelinePayload.events);
      } catch (nextError) {
        if (isDisposed) return;
        setError(nextError instanceof Error ? nextError.message : text.errors.sessionDetail);
        setSessionDetail(null);
        setTimeline([]);
      } finally {
        if (!isDisposed) setIsLoadingDetail(false);
      }
    }

    if (!selectedSessionId) {
      setSessionDetail(null);
      setTimeline([]);
      return () => { isDisposed = true; };
    }
    void loadDetailAndTimeline(selectedSessionId);
    return () => { isDisposed = true; };
  }, [refreshToken, selectedSessionId, text.errors.sessionDetail]);

  const summaryLine = useMemo(() => {
    if (isLoadingList) return text.summary.loading;
    if (!sessions.length) return text.summary.empty;
    return text.summary.ready(sessions.length);
  }, [isLoadingList, sessions.length, text.summary]);

  const selectedSessionSummary = useMemo(
    () => sessions.find((s) => s.sessionId === selectedSessionId) ?? null,
    [selectedSessionId, sessions],
  );
  const hasFreshSessionDetail =
    selectedSessionId !== null && sessionDetail?.sessionId === selectedSessionId;
  const isHydratingSelection =
    selectedSessionId !== null && !error && (isLoadingDetail || !hasFreshSessionDetail);
  const visibleTimeline = hasFreshSessionDetail ? timeline : [];

  async function handleExportBundle() {
    setBundleError(null);
    setBundleNote(null);
    setIsExportingBundle(true);
    try {
      const exported = await exportDiagnosticBundle(
        buildDiagnosticBundleRequest(selectedSessionId),
      );
      setBundleExport(exported);
      setBundleNote(
        selectedSessionId
          ? text.bundle.exportedFor(selectedSessionId)
          : text.bundle.exportedGeneric,
      );
    } catch (nextError) {
      setBundleError(
        nextError instanceof Error ? nextError.message : text.bundle.exportFailed,
      );
    } finally {
      setIsExportingBundle(false);
    }
  }

  return (
    <section className="control-plane-stack" data-testid="sessions-diagnostics-desk">
      <div>
        <h2 className="desk-section-title">{text.title}</h2>
        <p className="desk-section-desc">{summaryLine}</p>
        <div className="control-plane-toolbar">
        <Button
          variant="secondary"
          data-testid="sessions-refresh"
          onClick={() => setRefreshToken((v) => v + 1)}
          disabled={isLoadingList || isLoadingDetail}
        >
          {isLoadingList || isLoadingDetail ? text.common.refreshing : text.refresh}
        </Button>
        </div>
      </div>

      {error ? (
        <p className="desk-feedback desk-feedback--error" role="alert">{error}</p>
      ) : null}

      <div className="control-plane-pane-shell">
        {/* ── Left rail: session list ── */}
        <section className="timeline control-plane-pane-rail" data-testid="sessions-list">
          <div className="timeline__header">
            <h3 className="desk-section-title">{text.sections.sessionIndex}</h3>
            <span className="composer__status">
              {isLoadingList ? text.common.loading : text.common.items(sessions.length)}
            </span>
          </div>
          <div className="timeline__body control-plane-session-list">
            {isLoadingList ? <Skeleton height={52} count={3} /> : null}
            {!isLoadingList && sessions.length === 0 ? (
              <EmptyState icon={<Search size={28} strokeWidth={1.5} />} title={text.summary.empty} />
            ) : null}
            {sessions.map((session) => (
              <button
                key={session.sessionId}
                type="button"
                className={`control-plane-session-card control-plane-list-button ${
                  selectedSessionId === session.sessionId ? "control-plane-list-button--selected" : ""
                }`}
                data-testid={`session-select-${session.sessionId}`}
                onClick={() => setSelectedSessionId(session.sessionId)}
                aria-pressed={selectedSessionId === session.sessionId}
              >
                <div className="control-plane-session-card__topline">
                  <span className="metric-label">{formatSessionKind(session.sessionKind)}</span>
                  {session.status.isActiveMainSession ? (
                    <span className="control-plane-chip control-plane-chip--active">
                      {text.sessionList.activeMain}
                    </span>
                  ) : null}
                </div>
                <strong className="control-plane-session-card__title" title={session.sessionId}>{truncateId(session.sessionId)}</strong>
                <div className="control-plane-session-card__meta">
                  <span>{text.sessionDetail.breakpoint}: {session.status.breakpointState ?? text.sessionDetail.none}</span>
                  <span>{text.sessionDetail.lastEvent}: {formatTimestamp(session.lastEventAt)}</span>
                </div>
                <div className="control-plane-chip-row">
                  <span className="control-plane-chip">{text.sessionList.messages(session.status.messageCount)}</span>
                  {session.status.pendingApprovalCount > 0 ? (
                    <span className="control-plane-chip control-plane-chip--warning">
                      {text.sessionList.pendingApprovals(session.status.pendingApprovalCount)}
                    </span>
                  ) : null}
                </div>
              </button>
            ))}
          </div>
        </section>

        {/* ── Right stage: detail + timeline + bundle ── */}
        <div className="control-plane-pane-stage">

          {/* Session detail card */}
          <section className="status-card status-card--normal control-plane-stage-hero" data-testid="session-detail">
            {isHydratingSelection ? (
              <Skeleton height={52} count={3} />
            ) : sessionDetail && hasFreshSessionDetail ? (
              <div className="control-plane-stack">
                {/* Header */}
                <div className="control-plane-stage-hero__header">
                  <div>
                    <h3 className="desk-section-title" title={sessionDetail.sessionId}>{truncateId(sessionDetail.sessionId)}</h3>
                    <p className="desk-section-desc">
                      {text.sessionDetail.sessionFocus} · {formatSessionKind(sessionDetail.sessionKind)}
                    </p>
                  </div>
                  <div className="control-plane-chip-row">
                    <span className="control-plane-chip">
                      {text.sessionDetail.breakpoint}: {sessionDetail.status.breakpointState ?? text.sessionDetail.none}
                    </span>
                    {sessionDetail.status.pendingApprovalCount > 0 ? (
                      <span className="control-plane-chip control-plane-chip--warning">
                        {text.sessionList.pendingApprovals(sessionDetail.status.pendingApprovalCount)}
                      </span>
                    ) : null}
                  </div>
                </div>

                {/* Session metrics grid */}
                <div className="control-plane-summary-grid">
                  <MetricRow label={text.sessionDetail.session}>
                    <span className="metric-value--path">{sessionDetail.sessionId}</span>
                  </MetricRow>
                  <MetricRow label={text.sessionDetail.kind}>
                    {formatSessionKind(sessionDetail.sessionKind)}
                  </MetricRow>
                  <MetricRow label={text.sessionDetail.createdAt}>
                    {formatTimestamp(sessionDetail.createdAt)}
                  </MetricRow>
                  <MetricRow label={text.sessionDetail.lastEvent}>
                    {formatTimestamp(sessionDetail.lastEventAt)}
                  </MetricRow>
                  <MetricRow label={text.sessionDetail.messagesAndApprovals}>
                    {sessionDetail.status.messageCount} / {sessionDetail.status.pendingApprovalCount}
                  </MetricRow>
                  <MetricRow label={text.sessionDetail.messageBreakdown}>
                    {sessionDetail.userMessageCount} / {sessionDetail.assistantMessageCount} / {sessionDetail.toolCallCount}
                  </MetricRow>
                  <MetricRow label={text.sessionDetail.approvals}>
                    {sessionDetail.pendingApprovalCallIds.length}
                  </MetricRow>
                  <MetricRow label={text.sessionDetail.traceIndex}>
                    {sessionDetail.lastSfpIndex}
                  </MetricRow>
                </div>

                {/* Prompt report — collapsible sections */}
                <div data-testid="session-prompt-report">
                  {sessionDetail.promptReport ? (
                    <>
                      {/* Overview */}
                      <CollapsibleSection title={text.sections.promptOverview}>
                        <div className="control-plane-summary-grid">
                          <MetricRow label={text.sessionDetail.promptProfile}>
                            {sessionDetail.promptReport.profileId}
                          </MetricRow>
                          <MetricRow label={text.sessionDetail.promptSize}>
                            {formatPromptSize(sessionDetail.promptReport.characterCount)}
                          </MetricRow>
                          <MetricRow label={text.sessionDetail.promptBudget}>
                            {formatPromptBudget(
                              sessionDetail.promptReport.characterCount,
                              sessionDetail.promptReport.characterBudget,
                              sessionDetail.promptReport.remainingCharacterBudget,
                            )}
                          </MetricRow>
                          <MetricRow label={text.sessionDetail.promptGeneratedAt}>
                            {formatTimestamp(sessionDetail.promptReport.generatedAt)}
                          </MetricRow>
                          <MetricRow label={text.sessionDetail.truncationState}>
                            {sessionDetail.promptReport.wasTruncated
                              ? text.sessionDetail.truncationOn
                              : text.sessionDetail.truncationOff}
                          </MetricRow>
                        </div>
                      </CollapsibleSection>

                      {/* Context files */}
                      <CollapsibleSection title={text.sections.contextFiles}>
                        <div className="control-plane-summary-grid">
                          <MetricRow label={text.sessionDetail.loadedContextFiles}>
                            {sessionDetail.promptReport.loadedContextFiles.length > 0
                              ? sessionDetail.promptReport.loadedContextFiles.map((p) => (
                                  <span key={p} className="metric-value metric-value--path diag-path-item">{p}</span>
                                ))
                              : text.sessionDetail.none}
                          </MetricRow>
                          {sessionDetail.promptReport.wasTruncated && (
                            <>
                              <MetricRow label={text.sessionDetail.truncatedContextFiles}>
                                {(sessionDetail.promptReport.truncatedContextFiles?.length ?? 0) > 0
                                  ? sessionDetail.promptReport.truncatedContextFiles!.map((p) => (
                                      <span key={p} className="metric-value metric-value--path diag-path-item">{p}</span>
                                    ))
                                  : text.sessionDetail.none}
                              </MetricRow>
                              <MetricRow label={text.sessionDetail.truncationNotes}>
                                {(sessionDetail.promptReport.truncationNotes?.length ?? 0) > 0
                                  ? sessionDetail.promptReport.truncationNotes!.map((note) => (
                                      <span key={note} className="metric-value diag-path-item">{note}</span>
                                    ))
                                  : text.sessionDetail.none}
                              </MetricRow>
                            </>
                          )}
                        </div>
                      </CollapsibleSection>

                      {/* Recent changes / delta */}
                      <CollapsibleSection title={text.sections.recentChanges} defaultOpen={true}>
                        {sessionDetail.promptReportDelta ? (
                          <div className="control-plane-summary-grid">
                            <MetricRow label={text.sessionDetail.previousPromptGeneratedAt}>
                              {formatTimestamp(sessionDetail.promptReportDelta.previousGeneratedAt)}
                            </MetricRow>
                            <MetricRow label={text.sessionDetail.charDelta}>
                              {formatPromptDelta(sessionDetail.promptReportDelta.characterCountDelta)}
                            </MetricRow>
                            {sessionDetail.promptReportDelta.truncationStateChanged ? (
                              <MetricRow label={text.sessionDetail.truncationChanged}>—</MetricRow>
                            ) : null}
                            <MetricRow label={text.sessionDetail.addedContextFiles}>
                              {sessionDetail.promptReportDelta.addedContextFiles.length > 0
                                ? sessionDetail.promptReportDelta.addedContextFiles.map((p) => (
                                    <span key={p} className="metric-value metric-value--path diag-path-item">{p}</span>
                                  ))
                                : text.sessionDetail.none}
                            </MetricRow>
                            <MetricRow label={text.sessionDetail.removedContextFiles}>
                              {sessionDetail.promptReportDelta.removedContextFiles.length > 0
                                ? sessionDetail.promptReportDelta.removedContextFiles.map((p) => (
                                    <span key={p} className="metric-value metric-value--path diag-path-item">{p}</span>
                                  ))
                                : text.sessionDetail.none}
                            </MetricRow>
                          </div>
                        ) : (
                          <p className="desk-section-desc desk-section-desc--flush">
                            {text.sessionDetail.noPromptDelta}
                          </p>
                        )}
                      </CollapsibleSection>

                      {/* Recent builds */}
                      <CollapsibleSection title={text.sessionDetail.promptHistory} defaultOpen={false}>
                        {(sessionDetail.recentPromptReports?.length ?? 0) > 0 ? (
                          <div className="control-plane-summary-grid">
                            {sessionDetail.recentPromptReports!.map((report, index) => (
                              <MetricRow key={`${report.generatedAt}-${index}`} label={report.profileId}>
                                {formatPromptSize(report.characterCount)} · {formatTimestamp(report.generatedAt)}
                              </MetricRow>
                            ))}
                          </div>
                        ) : (
                          <p className="desk-section-desc desk-section-desc--flush">{text.sessionDetail.none}</p>
                        )}
                      </CollapsibleSection>

                      {/* System prompt */}
                      <CollapsibleSection title={text.sections.systemPrompt} defaultOpen={true}>
                        <pre className="diag-system-prompt">{sessionDetail.promptReport.systemPrompt}</pre>
                      </CollapsibleSection>
                    </>
                  ) : (
                    <p className="desk-section-desc">{text.sessionDetail.promptUnavailable}</p>
                  )}
                </div>
              </div>
            ) : selectedSessionSummary ? (
              <div className="control-plane-summary-grid">
                <MetricRow label={text.sessionDetail.session}>
                  <span className="metric-value--path">{selectedSessionSummary.sessionId}</span>
                </MetricRow>
                <MetricRow label={text.sessionDetail.kind}>
                  {formatSessionKind(selectedSessionSummary.sessionKind)}
                </MetricRow>
              </div>
            ) : (
              <EmptyState icon={<Search size={28} strokeWidth={1.5} />} title={text.sessionDetail.empty} />
            )}
          </section>

          {/* Bundle export (top) + Timeline (bottom) */}
          <section className="status-card status-card--normal control-plane-stage-panel" data-testid="diagnostic-bundle-export">
            <div className="control-plane-summary-grid">
              <MetricRow label={text.bundle.requestedSession}>
                {selectedSessionId ?? text.bundle.crossSession}
              </MetricRow>
              <MetricRow label={text.bundle.timelineWindow}>
                {text.bundle.timelineWindowValue}
              </MetricRow>
            </div>

            <div className="diag-bundle-action">
              <Button
                variant="secondary"
                data-testid="diagnostic-bundle-export-button"
                onClick={() => void handleExportBundle()}
                disabled={isLoadingList || isHydratingSelection || isExportingBundle}
              >
                {isExportingBundle ? text.bundle.exporting : text.bundle.export}
              </Button>
            </div>

            {bundleError ? (
              <p className="desk-feedback desk-feedback--error" data-testid="diagnostic-bundle-export-error">
                {bundleError}
              </p>
            ) : null}

            {bundleNote ? (
              <p className="desk-feedback desk-feedback--success" data-testid="diagnostic-bundle-export-note">
                {bundleNote}
              </p>
            ) : null}

            {bundleExport ? (
              <div className="control-plane-stack diag-bundle-result" data-testid="diagnostic-bundle-export-result">
                <div className="control-plane-summary-grid">
                  <MetricRow label={text.bundle.bundlePath}>
                    <span className="metric-value--path">{bundleExport.bundlePath}</span>
                  </MetricRow>
                  <MetricRow label={text.bundle.workspaceRoot}>
                    <span className="metric-value--path">{bundleExport.workspaceRootPath}</span>
                  </MetricRow>
                  <MetricRow label={text.bundle.manifest}>
                    {bundleExport.manifest.entries.length} entries · {formatTimestamp(bundleExport.generatedAt)}
                  </MetricRow>
                  <MetricRow label={text.bundle.redactionPosture}>
                    {text.bundle.rawSecrets}: {bundleExport.manifest.redactionSummary.includesRawSecrets ? text.bundle.included : text.bundle.excluded}
                    {" · "}
                    {text.bundle.messageBodies}: {bundleExport.manifest.redactionSummary.includesMessageBodies ? text.bundle.included : text.bundle.excluded}
                  </MetricRow>
                </div>
                <ul data-testid="diagnostic-bundle-redaction-notes" className="risk-briefing__list">
                  {bundleExport.manifest.redactionSummary.notes.map((note) => (
                    <li key={note} className="desk-section-desc">{note}</li>
                  ))}
                </ul>
              </div>
            ) : null}
          </section>

          <section className="timeline control-plane-stage-panel" data-testid="diagnostics-timeline">
            <div className="timeline__header">
              <h3 className="desk-section-title">{text.sections.diagnosticsTimeline}</h3>
              <span className="composer__status">{visibleTimeline.length}</span>
            </div>
            <div className="timeline__body">
              {isHydratingSelection ? (
                <Skeleton height={36} count={5} />
              ) : visibleTimeline.length > 0 ? (
                <div className="diag-event-log">
                  {visibleTimeline.map((event) => (
                    <div key={event.id} className={`diag-event diag-event--${event.level.toLowerCase()}`}>
                      <span className="diag-event__time">{formatTimestamp(event.timestamp)}</span>
                      <span className={`diag-event__level diag-event__level--${event.level.toLowerCase()}`}>{event.level}</span>
                      <span className="diag-event__source">{event.source}</span>
                      <span className="diag-event__msg">{event.message}</span>
                    </div>
                  ))}
                </div>
              ) : (
                <p className="desk-section-desc">{text.timeline.empty}</p>
              )}
            </div>
          </section>
        </div>
      </div>
      <CollapsibleSection title={text.riskSectionTitle} defaultOpen={false}>
        <RiskSection />
      </CollapsibleSection>
    </section>
  );
}
