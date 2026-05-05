import { useEffect, useMemo, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../lib/queryKeys";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import {
  fetchAutomationRuns,
  fetchAutomations,
  fetchSessionDetail,
  fetchSettings,
  setAutomationsEnabled,
  triggerAutomation,
  updateAutomationDefinition,
} from "../lib/api";
import { useI18n, useLocaleText } from "../i18n/I18nProvider";
import { Skeleton } from "./ui/Skeleton";
import { EmptyState } from "./ui/EmptyState";
import { ConfirmModal } from "./ui/ConfirmModal";
import { Button } from "./ui/Button";
import { Select } from "./ui/Select";
import { Zap } from "lucide-react";
import { parseCronHuman } from "../lib/cron";
import { stripXml } from "../lib/textUtils";
import type {
  AutomationDefinition,
  AutomationDefinitionSource,
  AutomationRunRecord,
  SessionDetail,
} from "../types/contracts";

type EnabledFilter = "all" | "enabled";
type SourceFilter = "all" | AutomationDefinitionSource;
type ConfirmState =
  | { kind: "disable"; automation: AutomationDefinition }
  | { kind: "trigger"; automationId: string; title: string }
  | null;

const SOURCE_FILTER_OPTIONS: AutomationDefinitionSource[] = ["Manual", "Heartbeat"];

function resolveChannelLabel(bindingId: string): string {
  if (bindingId.startsWith("tg-")) return `Telegram · ${bindingId.slice(3)}`;
  if (bindingId.startsWith("feishu-")) return `飞书 · ${bindingId.slice(7)}`;
  return bindingId;
}

export function AutomationsDesk() {
  const { formatDateTime } = useI18n();
  const text = useLocaleText({
    zh: {
      eyebrow: "控制平面",
      title: "自动化编务台",
      copy:
        "统筹周期任务、核对即将到来的调度，并在切换开关前把最近一次运行叙事保留在视野里。",
      refresh: "刷新工作台",
      refreshing: "刷新中...",
      filters: {
        scope: "范围",
        source: "来源",
      },
      enabledFilter: {
        all: "全部自动化",
        enabled: "仅看已启用",
      },
      sourceFilterAll: "全部来源",
      indexTitle: "自动化索引",
      loaded: (count: number) => `已加载 ${count} 条`,
      loadingList: "正在加载自动化...",
      emptyList: "当前筛选条件下暂无自动化。",
      detailEyebrow: "自动化详情",
      detailSubtitle: (schedule: string, source: string) => `${schedule} · 来源 ${source}`,
      inspectDetail: "查看详情",
      inspecting: "查看中",
      states: {
        enabled: "已启用",
        disabled: "已停用",
      },
      model: "模型",
      modelDefault: "默认模型",
      channels: "渠道",
      notifyMode: "推送方式",
      notifyModeAuto: "自动",
      notifyModeApproval: "审批后推送",
      nextRun: "下次运行",
      lastRunStatus: "上次运行状态",
      toggleEnable: "启用自动化",
      toggleDisable: "停用自动化",
      saving: "保存中...",
      nextRunHint: (value: string) => `下次运行 ${value}`,
      promptSummary: "提示词",
      inputPaths: "输入路径",
      noInputPaths: "未配置显式输入路径。",
      lastError: "最近错误",
      noRecentError: "最近没有自动化错误。",
      recentRuns: "最近运行",
      loadingRuns: "正在加载最近运行...",
      emptyRuns: "这个自动化还没有最近运行记录。",
      promptDiagnostics: "最近运行提示诊断",
      loadingPromptDiagnostics: "正在加载最近一次运行的提示诊断...",
      emptyPromptDiagnostics: "最近运行没有可用的提示诊断。",
      loadPromptDiagnosticsError: "加载最近运行的提示诊断失败。",
      latestRunSession: "最近运行会话",
      promptProfile: "提示词档案",
      promptSize: "提示词大小",
      promptBudget: "提示词预算",
      promptGeneratedAt: "生成时间",
      loadedContextFiles: "已加载上下文",
      noLoadedContextFiles: "最近运行未记录已加载上下文。",
      truncationState: "截断状态",
      truncationOn: "已截断",
      truncationOff: "未截断",
      truncatedContextFiles: "被截断的文件",
      truncationNotes: "截断说明",
      memoryBoundary: "记忆边界",
      memoryBoundaryDefault:
        "本轮默认不读取长期记忆；只有基线协议文件和显式输入路径会进入自动化提示词。",
      memoryBoundaryWithMemory:
        "本轮提示词显式加载了 MEMORY.md；这属于主动纳入的上下文，而不是默认记忆回忆。",
      promptSizeValue: (count: number) => `${count.toLocaleString()} 字符`,
      promptBudgetValue: (count: number, budget: number, remaining: number | null | undefined) =>
        `${count.toLocaleString()} / ${budget.toLocaleString()}${remaining === null || remaining === undefined ? "" : `（剩余 ${remaining.toLocaleString()}）`}`,
      promptBudgetUnavailable: "未配置字符预算",
      emptyDetail: "选择一个自动化以查看提示词、运行记录与开关状态。",
      sourceLabels: {
        Manual: "手动",
        Heartbeat: "心跳",
      },
      runStatus: {
        Succeeded: "成功",
        Failed: "失败",
        Running: "运行中",
        Queued: "排队中",
        Pending: "待处理",
        Canceled: "已取消",
      },
      triggerLabels: {
        schedule: "计划触发",
        manual: "手动触发",
        heartbeat: "心跳触发",
      },
      schedule: {
        everyMinutes: (interval: number) => `每 ${interval} 分钟`,
        hourly: "每小时",
        everyHours: (interval: number) => `每 ${interval} 小时`,
        daily: "每天",
        dailyAt: (localTime: string) => `每天 ${localTime}`,
        weekdays: (localTime: string) => `工作日 ${localTime}`,
        weekly: (days: string, localTime?: string | null) =>
          `每周 ${days}${localTime ? ` ${localTime}` : ""}`,
        selectedDays: "指定日期",
      },
      scheduleLabel: "调度计划",
      cronRaw: "Cron",
      nextRunAt: "下次运行",
      nextRunAbsolute: (abs: string) => `（${abs}）`,
      nextRunToday: (t: string) => `今天 ${t}`,
      nextRunTomorrow: (t: string) => `明天 ${t}`,
      dayNames: {
        Monday: "周一",
        Tuesday: "周二",
        Wednesday: "周三",
        Thursday: "周四",
        Friday: "周五",
        Saturday: "周六",
        Sunday: "周日",
      },
      runAttempt: (attempt: number) => `第 ${attempt} 次尝试`,
      unavailable: "暂无",
      loadRunsError: "加载自动化运行记录失败。",
      loadAutomationsError: "加载自动化列表失败。",
      updateAutomationError: "更新自动化状态失败。",
      triggerNow: "立即执行",
      triggering: "执行中...",
      triggerError: "触发自动化失败。",
      engineDisabledBanner: "自动化引擎当前已关闭。请在「设置」中启用「自动化引擎」，已启用的自动化才会按计划执行。",
      engineEnabled: "引擎已启用",
      engineDisabled: "引擎已关闭",
      confirmDisableTitle: "停用此自动化？",
      confirmDisableDesc: "停用后，该自动化将不再按计划执行，直到重新启用。",
      confirmDisableLabel: "停用",
      confirmTriggerTitle: (title: string) => `立即触发「${title}」？`,
      confirmTriggerDesc: "将立即创建一次手动执行。如有渠道推送配置，完成后会按设置发送。",
      confirmTriggerLabel: "立即执行",
      cancel: "取消",
      durationSecs: (s: number) => `${s}s`,
      durationMins: (m: number, s: number) => `${m}m ${s}s`,
      relativeNow: "刚刚",
      relativeSoon: "即将",
      relativeMinutesAgo: (n: number) => `${n} 分钟前`,
      relativeMinutesLater: (n: number) => `${n} 分钟后`,
      relativeHoursAgo: (n: number) => `${n} 小时前`,
      relativeHoursLater: (n: number) => `${n} 小时后`,
      relativeDaysAgo: (n: number) => `${n} 天前`,
      relativeDaysLater: (n: number) => `${n} 天后`,
    },
    en: {
      eyebrow: "Control Plane",
      title: "Automations Editorial Desk",
      copy:
        "Curate recurring jobs, inspect upcoming schedules, and keep the last run narrative visible before you flip a switch.",
      refresh: "Refresh desk",
      refreshing: "Refreshing...",
      filters: {
        scope: "Scope",
        source: "Source",
      },
      enabledFilter: {
        all: "All automations",
        enabled: "Enabled only",
      },
      sourceFilterAll: "All sources",
      indexTitle: "Automation Index",
      loaded: (count: number) => `${count} loaded`,
      loadingList: "Loading automations...",
      emptyList: "No automations match the current filters.",
      detailEyebrow: "Automation Detail",
      detailSubtitle: (schedule: string, source: string) => `${schedule} · source ${source}`,
      inspectDetail: "Inspect detail",
      inspecting: "Inspecting",
      states: {
        enabled: "Enabled",
        disabled: "Disabled",
      },
      model: "Model",
      modelDefault: "Default model",
      channels: "Channels",
      notifyMode: "Push Mode",
      notifyModeAuto: "Auto",
      notifyModeApproval: "On Approval",
      nextRun: "Next run",
      lastRunStatus: "Last run status",
      toggleEnable: "Enable automation",
      toggleDisable: "Disable automation",
      saving: "Saving...",
      nextRunHint: (value: string) => `Next run ${value}`,
      promptSummary: "Prompt",
      inputPaths: "Input paths",
      noInputPaths: "No explicit input paths configured.",
      lastError: "Last error",
      noRecentError: "No recent automation error.",
      recentRuns: "Recent runs",
      loadingRuns: "Loading recent runs...",
      emptyRuns: "No recent runs found for this automation.",
      promptDiagnostics: "Latest run prompt diagnostics",
      loadingPromptDiagnostics: "Loading prompt diagnostics for the latest run...",
      emptyPromptDiagnostics: "No prompt diagnostics are available for the latest run.",
      loadPromptDiagnosticsError: "Failed to load latest-run prompt diagnostics.",
      latestRunSession: "Latest run session",
      promptProfile: "Prompt profile",
      promptSize: "Prompt size",
      promptBudget: "Prompt budget",
      promptGeneratedAt: "Generated at",
      loadedContextFiles: "Loaded context files",
      noLoadedContextFiles: "No loaded context files were recorded for the latest run.",
      truncationState: "Truncation",
      truncationOn: "Truncated",
      truncationOff: "Not truncated",
      truncatedContextFiles: "Truncated files",
      truncationNotes: "Truncation notes",
      memoryBoundary: "Memory boundary",
      memoryBoundaryDefault:
        "Long-term memory stays out by default; only baseline operating files and explicit input paths enter automation prompts.",
      memoryBoundaryWithMemory:
        "This run explicitly loaded MEMORY.md, so long-term memory was included intentionally rather than recalled by default.",
      promptSizeValue: (count: number) => `${count.toLocaleString()} chars`,
      promptBudgetValue: (count: number, budget: number, remaining: number | null | undefined) =>
        `${count.toLocaleString()} / ${budget.toLocaleString()}${remaining === null || remaining === undefined ? "" : ` (${remaining.toLocaleString()} remaining)`}`,
      promptBudgetUnavailable: "No character budget configured",
      emptyDetail: "Select an automation to inspect prompt, runs, and toggle state.",
      sourceLabels: {
        Manual: "Manual",
        Heartbeat: "Heartbeat",
      },
      runStatus: {
        Succeeded: "Succeeded",
        Failed: "Failed",
        Running: "Running",
        Queued: "Queued",
        Pending: "Pending",
        Canceled: "Canceled",
      },
      triggerLabels: {
        schedule: "Scheduled",
        manual: "Manual trigger",
        heartbeat: "Heartbeat",
      },
      schedule: {
        everyMinutes: (interval: number) => `Every ${interval} min`,
        hourly: "Every hour",
        everyHours: (interval: number) => `Every ${interval}h`,
        daily: "Daily",
        dailyAt: (localTime: string) => `Daily ${localTime}`,
        weekdays: (localTime: string) => `Weekdays ${localTime}`,
        weekly: (days: string, localTime?: string | null) =>
          `${days}${localTime ? ` ${localTime}` : ""}`,
        selectedDays: "selected days",
      },
      scheduleLabel: "Schedule",
      cronRaw: "Cron",
      nextRunAt: "Next run",
      nextRunAbsolute: (abs: string) => `(${abs})`,
      nextRunToday: (t: string) => `Today ${t}`,
      nextRunTomorrow: (t: string) => `Tomorrow ${t}`,
      dayNames: {
        Monday: "Monday",
        Tuesday: "Tuesday",
        Wednesday: "Wednesday",
        Thursday: "Thursday",
        Friday: "Friday",
        Saturday: "Saturday",
        Sunday: "Sunday",
      },
      runAttempt: (attempt: number) => `attempt ${attempt}`,
      unavailable: "n/a",
      loadRunsError: "Failed to load automation runs.",
      loadAutomationsError: "Failed to load automations.",
      updateAutomationError: "Failed to update automation state.",
      triggerNow: "Run now",
      triggering: "Running...",
      triggerError: "Failed to trigger automation.",
      engineDisabledBanner: "The automations engine is currently disabled. Go to Settings and enable \"Automations engine\" so scheduled automations can run.",
      engineEnabled: "Engine on",
      engineDisabled: "Engine off",
      confirmDisableTitle: "Disable this automation?",
      confirmDisableDesc: "This automation will no longer run on schedule until re-enabled.",
      confirmDisableLabel: "Disable",
      confirmTriggerTitle: (title: string) => `Run "${title}" now?`,
      confirmTriggerDesc: "A manual run will be triggered immediately. If channels are configured, results will be pushed per the delivery mode.",
      confirmTriggerLabel: "Run now",
      cancel: "Cancel",
      durationSecs: (s: number) => `${s}s`,
      durationMins: (m: number, s: number) => `${m}m ${s}s`,
      relativeNow: "just now",
      relativeSoon: "soon",
      relativeMinutesAgo: (n: number) => `${n}m ago`,
      relativeMinutesLater: (n: number) => `in ${n}m`,
      relativeHoursAgo: (n: number) => `${n}h ago`,
      relativeHoursLater: (n: number) => `in ${n}h`,
      relativeDaysAgo: (n: number) => `${n}d ago`,
      relativeDaysLater: (n: number) => `in ${n}d`,
    },
  });

  const queryClient = useQueryClient();
  const { data: settingsData } = useQuery({ queryKey: queryKeys.settings, queryFn: () => fetchSettings() });
  const automationsEngineEnabled: boolean | null = settingsData?.automationsEnabled ?? null;
  const [automations, setAutomations] = useState<AutomationDefinition[]>([]);
  const [selectedAutomationId, setSelectedAutomationId] = useState<string | null>(null);
  const [recentRuns, setRecentRuns] = useState<AutomationRunRecord[]>([]);
  const [latestRunSessionDetail, setLatestRunSessionDetail] = useState<SessionDetail | null>(null);
  const [enabledFilter, setEnabledFilter] = useState<EnabledFilter>("all");
  const [sourceFilter, setSourceFilter] = useState<SourceFilter>("all");
  const [isLoadingList, setIsLoadingList] = useState(true);
  const [isRefreshing, setIsRefreshing] = useState(false);
  const [isLoadingRuns, setIsLoadingRuns] = useState(false);
  const [isLoadingPromptDiagnostics, setIsLoadingPromptDiagnostics] = useState(false);
  const [pendingToggleIds, setPendingToggleIds] = useState<Record<string, boolean>>({});
  const [pendingTriggerIds, setPendingTriggerIds] = useState<Record<string, boolean>>({});
  const [error, setError] = useState<string | null>(null);
  const [promptDiagnosticsError, setPromptDiagnosticsError] = useState<string | null>(null);
  const [isTogglingEngine, setIsTogglingEngine] = useState(false);
  const [confirmState, setConfirmState] = useState<ConfirmState>(null);
  const [isPolling, setIsPolling] = useState(false);

  const listRequestIdRef = useRef(0);
  const runsRequestIdRef = useRef(0);
  const promptDiagnosticsRequestIdRef = useRef(0);
  const selectedAutomationIdRef = useRef<string | null>(null);

  // Keep ref in sync for use inside polling effect
  useEffect(() => {
    selectedAutomationIdRef.current = selectedAutomationId;
  }, [selectedAutomationId]);

  const selectedAutomation = useMemo(
    () => automations.find((item) => item.id === selectedAutomationId) ?? null,
    [automations, selectedAutomationId],
  );

  const displayedLastError = useMemo(() => {
    if (!selectedAutomation) {
      return null;
    }

    if (selectedAutomation.lastError?.trim()) {
      return selectedAutomation.lastError;
    }

    const failedRun = recentRuns.find((run) => run.errorMessage?.trim());
    return failedRun?.errorMessage ?? null;
  }, [recentRuns, selectedAutomation]);

  function resolveSourceLabel(source: AutomationDefinitionSource): string {
    return text.sourceLabels[source] ?? source;
  }

  function resolveRunStatusLabel(status?: string | null): string {
    if (!status) {
      return text.unavailable;
    }

    return text.runStatus[status as keyof typeof text.runStatus] ?? status;
  }

  function resolveTriggerLabel(trigger?: string | null): string {
    if (!trigger) {
      return text.unavailable;
    }

    return text.triggerLabels[trigger as keyof typeof text.triggerLabels] ?? trigger;
  }

  function formatSchedule(cronExpression: string): string {
    return parseCronHuman(cronExpression, text.schedule, text.dayNames);
  }

  /**
   * Format the absolute next-run time as a short "today HH:MM" / "tomorrow HH:MM" / date string.
   * Returns empty string if dateStr is null/undefined.
   */
  function formatNextRunAbsolute(dateStr: string | null | undefined): string {
    if (!dateStr) return "";
    const date = new Date(dateStr);
    if (isNaN(date.getTime())) return "";
    const now = new Date();
    const timeStr = date.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
    const todayMidnight = new Date(now.getFullYear(), now.getMonth(), now.getDate());
    const tomorrowMidnight = new Date(todayMidnight.getTime() + 86400000);
    const dayAfterMidnight = new Date(todayMidnight.getTime() + 2 * 86400000);
    if (date >= todayMidnight && date < tomorrowMidnight) return text.nextRunToday(timeStr);
    if (date >= tomorrowMidnight && date < dayAfterMidnight) return text.nextRunTomorrow(timeStr);
    return formatDateTime(dateStr, "");
  }

  function formatDuration(
    startedAt: string | null | undefined,
    completedAt: string | null | undefined,
  ): string {
    if (!startedAt || !completedAt) return "";
    const ms = new Date(completedAt).getTime() - new Date(startedAt).getTime();
    if (ms <= 0) return "";
    const secs = Math.round(ms / 1000);
    if (secs < 60) return text.durationSecs(secs);
    return text.durationMins(Math.floor(secs / 60), secs % 60);
  }

  function formatRelativeTime(dateStr: string | null | undefined): string {
    if (!dateStr) return text.unavailable;
    const ms = new Date(dateStr).getTime() - Date.now();
    const abs = Math.abs(ms);
    const future = ms > 0;

    if (abs < 30000) return future ? text.relativeSoon : text.relativeNow;
    if (abs < 3600000) {
      const mins = Math.round(abs / 60000);
      return future ? text.relativeMinutesLater(mins) : text.relativeMinutesAgo(mins);
    }
    if (abs < 86400000) {
      const hours = Math.round(abs / 3600000);
      return future ? text.relativeHoursLater(hours) : text.relativeHoursAgo(hours);
    }
    const days = Math.round(abs / 86400000);
    return future ? text.relativeDaysLater(days) : text.relativeDaysAgo(days);
  }

  function formatPromptSize(characterCount: number): string {
    return text.promptSizeValue(characterCount);
  }

  function formatPromptBudget(detail: SessionDetail): string {
    const report = detail.promptReport;
    if (!report) {
      return text.promptBudgetUnavailable;
    }

    if (report.characterBudget === null || report.characterBudget === undefined) {
      return text.promptBudgetUnavailable;
    }

    return text.promptBudgetValue(
      report.characterCount,
      report.characterBudget,
      report.remainingCharacterBudget,
    );
  }

  async function loadPromptDiagnostics(sessionId: string | null) {
    const requestId = ++promptDiagnosticsRequestIdRef.current;

    if (!sessionId) {
      setLatestRunSessionDetail(null);
      setPromptDiagnosticsError(null);
      setIsLoadingPromptDiagnostics(false);
      return;
    }

    setIsLoadingPromptDiagnostics(true);
    setPromptDiagnosticsError(null);

    try {
      const detail = await fetchSessionDetail(sessionId);
      if (promptDiagnosticsRequestIdRef.current !== requestId) {
        return;
      }

      setLatestRunSessionDetail(detail);
    } catch (nextError) {
      if (promptDiagnosticsRequestIdRef.current !== requestId) {
        return;
      }

      setLatestRunSessionDetail(null);
      setPromptDiagnosticsError(
        nextError instanceof Error ? nextError.message : text.loadPromptDiagnosticsError,
      );
    } finally {
      if (promptDiagnosticsRequestIdRef.current === requestId) {
        setIsLoadingPromptDiagnostics(false);
      }
    }
  }

  async function loadAutomationRuns(automationId: string | null) {
    const requestId = ++runsRequestIdRef.current;

    if (!automationId) {
      setRecentRuns([]);
      setLatestRunSessionDetail(null);
      setPromptDiagnosticsError(null);
      setIsLoadingRuns(false);
      return;
    }

    setIsLoadingRuns(true);

    try {
      const payload = await fetchAutomationRuns(automationId, { limit: 12 });
      if (runsRequestIdRef.current !== requestId) {
        return;
      }

      setRecentRuns(payload.items);
      const latestRunSessionId = payload.items.find((run) => run.sessionId?.trim())?.sessionId ?? null;
      await loadPromptDiagnostics(latestRunSessionId);
    } catch (nextError) {
      if (runsRequestIdRef.current !== requestId) {
        return;
      }

      setRecentRuns([]);
      setLatestRunSessionDetail(null);
      setPromptDiagnosticsError(null);
      setError(nextError instanceof Error ? nextError.message : text.loadRunsError);
    } finally {
      if (runsRequestIdRef.current === requestId) {
        setIsLoadingRuns(false);
      }
    }
  }

  async function loadAutomations(mode: "initial" | "refresh") {
    const requestId = ++listRequestIdRef.current;

    if (mode === "initial") {
      setIsLoadingList(true);
    } else {
      setIsRefreshing(true);
    }

    setError(null);

    try {
      const payload = await fetchAutomations({
        limit: 60,
        enabled: enabledFilter === "enabled" ? true : undefined,
        source: sourceFilter === "all" ? undefined : sourceFilter,
      });

      if (listRequestIdRef.current !== requestId) {
        return;
      }

      setAutomations(payload.items);
      const hasCurrent = payload.items.some((item) => item.id === selectedAutomationId);
      const nextSelectedAutomationId = hasCurrent
        ? selectedAutomationId
        : (payload.items[0]?.id ?? null);
      setSelectedAutomationId(nextSelectedAutomationId);
      await loadAutomationRuns(nextSelectedAutomationId);
    } catch (nextError) {
      if (listRequestIdRef.current !== requestId) {
        return;
      }

      setAutomations([]);
      setRecentRuns([]);
      setSelectedAutomationId(null);
      setError(nextError instanceof Error ? nextError.message : text.loadAutomationsError);
    } finally {
      if (listRequestIdRef.current === requestId) {
        if (mode === "initial") {
          setIsLoadingList(false);
        } else {
          setIsRefreshing(false);
        }
      }
    }
  }

  // Polling: after a manual trigger, poll automations every 3s until no active runs
  useEffect(() => {
    if (!isPolling) return;

    let stopped = false;
    let attempts = 0;
    const maxAttempts = 20; // ~60 seconds

    const tick = async () => {
      if (stopped) return;
      attempts++;

      try {
        const payload = await fetchAutomations({ limit: 60 });
        if (stopped) return;
        setAutomations(payload.items);

        const hasActive = payload.items.some(
          (a) => a.lastRunStatus === "Running" || a.lastRunStatus === "Queued",
        );

        if (!hasActive || attempts >= maxAttempts) {
          stopped = true;
          setIsPolling(false);
          const currentId = selectedAutomationIdRef.current;
          if (currentId) {
            void loadAutomationRuns(currentId);
          }
        }
      } catch {
        if (attempts >= 3) {
          stopped = true;
          setIsPolling(false);
        }
      }
    };

    const interval = setInterval(() => {
      void tick();
    }, 3000);
    void tick();

    return () => {
      stopped = true;
      clearInterval(interval);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isPolling]);

  useEffect(() => {
    void loadAutomations("initial");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [enabledFilter, sourceFilter]);

  async function handleRefresh() {
    await loadAutomations("refresh");
  }

  function handleSelectAutomation(automationId: string) {
    setSelectedAutomationId(automationId);
    setError(null);
    void loadAutomationRuns(automationId);
  }

  // Step 1: show confirm or immediately enable
  function handleToggleAutomation(automation: AutomationDefinition) {
    if (automation.enabled) {
      setConfirmState({ kind: "disable", automation });
    } else {
      void doToggleAutomation(automation);
    }
  }

  // Step 2: actual toggle after confirmation
  async function doToggleAutomation(automation: AutomationDefinition) {
    setPendingToggleIds((current) => ({ ...current, [automation.id]: true }));
    setError(null);

    try {
      await updateAutomationDefinition(automation.id, !automation.enabled);
      void queryClient.invalidateQueries({ queryKey: ['automations'] });
      await loadAutomations("refresh");
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : text.updateAutomationError);
    } finally {
      setPendingToggleIds((current) => {
        const next = { ...current };
        delete next[automation.id];
        return next;
      });
    }
  }

  // Step 1: show confirm before trigger
  function handleTriggerAutomation(automation: AutomationDefinition) {
    setConfirmState({ kind: "trigger", automationId: automation.id, title: automation.title });
  }

  // Step 2: actual trigger after confirmation
  async function doTriggerAutomation(automationId: string) {
    setPendingTriggerIds((current) => ({ ...current, [automationId]: true }));
    setError(null);

    try {
      await triggerAutomation(automationId);
      setIsPolling(true);
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : text.triggerError);
    } finally {
      setPendingTriggerIds((current) => {
        const next = { ...current };
        delete next[automationId];
        return next;
      });
    }
  }

  async function handleConfirm() {
    if (!confirmState) return;
    const state = confirmState;
    setConfirmState(null);

    if (state.kind === "disable") {
      await doToggleAutomation(state.automation);
    } else if (state.kind === "trigger") {
      await doTriggerAutomation(state.automationId);
    }
  }

  const latestPromptReport = latestRunSessionDetail?.promptReport ?? null;
  const latestPromptHasMemory = latestPromptReport?.loadedContextFiles.some((path) =>
    path.endsWith("/MEMORY.md") || path === "workspace/MEMORY.md",
  ) ?? false;

  async function handleToggleEngine() {
    if (automationsEngineEnabled === null || isTogglingEngine) return;
    setIsTogglingEngine(true);
    try {
      const next = !automationsEngineEnabled;
      await setAutomationsEnabled(next);
      void queryClient.invalidateQueries({ queryKey: queryKeys.settings });
    } catch {
      // Keep current state on failure
    } finally {
      setIsTogglingEngine(false);
    }
  }

  return (
    <section className="automations-desk" data-testid="automations-desk">
      <h2 className="desk-section-title">{text.title}</h2>
      <p className="desk-section-desc">{text.copy}</p>

      {automationsEngineEnabled === false && (
        <div
          className="automations-engine-banner automations-engine-banner--warning"
          data-testid="automations-engine-banner"
          role="alert"
        >
          {text.engineDisabledBanner}
        </div>
      )}

      <div className="automations-desk__toolbar">
        <Button
          variant="secondary"
          size="control"
          data-testid="automations-refresh"
          disabled={isLoadingList || isRefreshing}
          onClick={() => { void handleRefresh(); }}
        >
          {isRefreshing ? text.refreshing : text.refresh}
        </Button>

        <label className="metric-label" htmlFor="automations-enabled-filter">
          {text.filters.scope}
        </label>
        <Select
          id="automations-enabled-filter"
          data-testid="automations-enabled-filter"
          className="control-plane-filter"
          value={enabledFilter}
          onChange={(event) => setEnabledFilter(event.target.value as EnabledFilter)}
        >
          <option value="all">{text.enabledFilter.all}</option>
          <option value="enabled">{text.enabledFilter.enabled}</option>
        </Select>

        <label className="metric-label" htmlFor="automations-source-filter">
          {text.filters.source}
        </label>
        <Select
          id="automations-source-filter"
          data-testid="automations-source-filter"
          className="control-plane-filter"
          value={sourceFilter}
          onChange={(event) => setSourceFilter(event.target.value as SourceFilter)}
        >
          <option value="all">{text.sourceFilterAll}</option>
          {SOURCE_FILTER_OPTIONS.map((source) => (
            <option value={source} key={source}>
              {resolveSourceLabel(source)}
            </option>
          ))}
        </Select>

        {automationsEngineEnabled !== null && (
          <button
            type="button"
            className={`automations-engine-toggle-pill${automationsEngineEnabled ? " automations-engine-toggle-pill--on" : ""}`}
            data-testid="automations-engine-toggle"
            disabled={isTogglingEngine}
            onClick={() => { void handleToggleEngine(); }}
            aria-pressed={automationsEngineEnabled}
          >
            <span className="automations-engine-toggle-pill__dot" />
            <span className="automations-engine-toggle-pill__label">
              {automationsEngineEnabled ? text.engineEnabled : text.engineDisabled}
            </span>
          </button>
        )}
      </div>

      {error ? (
        <p
          className="desk-feedback desk-feedback--error"
          data-testid="automations-error"
        >
          {error}
        </p>
      ) : null}

      <div className="automations-desk__layout">
        <section className="timeline" data-testid="automations-list">
          <div className="timeline__header">
            <h3 className="desk-section-title">{text.indexTitle}</h3>
            <span className="composer__status">
              {isLoadingList ? text.loadingList : text.loaded(automations.length)}
            </span>
          </div>

          <div className="timeline__body automations-desk__list-body">
            {isLoadingList ? <Skeleton height={52} count={3} /> : null}
            {!isLoadingList && automations.length === 0 ? (
              <EmptyState icon={<Zap size={28} strokeWidth={1.5} />} title={text.emptyList} />
            ) : null}

            {automations.map((automation) => {
              const isSelected = automation.id === selectedAutomationId;
              const isRunningNow =
                automation.lastRunStatus === "Running" ||
                automation.lastRunStatus === "Queued";

              return (
                <article
                  key={automation.id}
                  className={`automation-card${automation.enabled ? "" : " automation-card--disabled"}${isSelected ? " automation-card--selected" : ""}${isRunningNow ? " automation-card--running" : ""}`}
                  data-testid={`automation-item-${automation.id}`}
                >
                  <div className="automation-card__stripe" aria-hidden="true" />
                  <div className="automation-card__body">
                    {/* Row 1: title + enabled badge */}
                    <div className="automation-card__row automation-card__row--topline">
                      <strong className="automation-card__title">{automation.title}</strong>
                      <span className={automation.enabled ? "mode-badge mode-badge--main automation-card__badge" : "mode-badge automation-card__badge"}>
                        {automation.enabled ? text.states.enabled : text.states.disabled}
                      </span>
                    </div>
                    {/* Row 2: schedule + next run time */}
                    <div className="automation-card__row automation-card__row--schedule">
                      <span className="automation-card__schedule" title={automation.cronExpression}>
                        {formatSchedule(automation.cronExpression)}
                      </span>
                      <span
                        className="automation-card__nextrun"
                        title={formatDateTime(automation.nextRunAt, text.unavailable)}
                      >
                        {formatRelativeTime(automation.nextRunAt)}
                        {(() => {
                          const abs = formatNextRunAbsolute(automation.nextRunAt);
                          return abs ? (
                            <span className="automation-card__nextrun-abs">{text.nextRunAbsolute(abs)}</span>
                          ) : null;
                        })()}
                      </span>
                    </div>
                    {/* Row 3: source + last status + actions */}
                    <div className="automation-card__row automation-card__row--footer">
                      <div className="automation-card__meta">
                        <span className="automation-card__source">{resolveSourceLabel(automation.source)}</span>
                        <span className="automation-card__meta-sep">·</span>
                        <span
                          className={`automation-card__meta-item automation-card__status--${(automation.lastRunStatus ?? "none").toLowerCase()}${isRunningNow ? " automation-card__status--live" : ""}`}
                        >
                          {isRunningNow && <span className="automation-status-spinner" aria-hidden="true" />}
                          {resolveRunStatusLabel(automation.lastRunStatus)}
                        </span>
                        {automation.notificationChannels && automation.notificationChannels.length > 0 ? (
                          <>
                            <span className="automation-card__meta-sep">·</span>
                            <span className="automation-card__meta-item automation-card__channels-tag">
                              {automation.notificationChannels.length} {text.channels}
                            </span>
                          </>
                        ) : null}
                      </div>
                      <div className="automation-card__actions">
                        <Button
                          variant="secondary"
                          size="sm"
                          selected={isSelected}
                          data-testid={`automation-select-${automation.id}`}
                          aria-pressed={isSelected}
                          onClick={() => handleSelectAutomation(automation.id)}
                          disabled={isLoadingList || isRefreshing}
                        >
                          {isSelected ? text.inspecting : text.inspectDetail}
                        </Button>
                        {automation.enabled ? (
                          <Button
                            variant="primary"
                            size="sm"
                            data-testid={`automation-trigger-${automation.id}`}
                            onClick={() => { handleTriggerAutomation(automation); }}
                            disabled={pendingTriggerIds[automation.id] ?? isRunningNow}
                          >
                            {pendingTriggerIds[automation.id] ? text.triggering : text.triggerNow}
                          </Button>
                        ) : null}
                      </div>
                    </div>
                  </div>
                </article>
              );
            })}
          </div>
        </section>

        <section className="status-card status-card--normal automations-desk__detail-panel" data-testid="automation-detail">
          {selectedAutomation ? (
            <>
              <h3 className="desk-section-title">{selectedAutomation.title}</h3>
              <p className="desk-section-desc">
                {text.detailSubtitle(
                  formatSchedule(selectedAutomation.cronExpression),
                  resolveSourceLabel(selectedAutomation.source),
                )}
              </p>

              <div className="automations-desk__detail-toolbar">
                <span
                  className={selectedAutomation.enabled ? "mode-badge mode-badge--main" : "mode-badge"}
                  data-testid="automation-enabled-chip"
                >
                  {selectedAutomation.enabled ? text.states.enabled : text.states.disabled}
                </span>
                <Button
                  variant={selectedAutomation.enabled ? "danger" : "secondary"}
                  size="sm"
                  shape="pill"
                  data-testid={`automation-toggle-${selectedAutomation.id}`}
                  onClick={() => { handleToggleAutomation(selectedAutomation); }}
                  disabled={pendingToggleIds[selectedAutomation.id] ?? false}
                >
                  {pendingToggleIds[selectedAutomation.id]
                    ? text.saving
                    : selectedAutomation.enabled
                      ? text.toggleDisable
                      : text.toggleEnable}
                </Button>
                <span className="metric-label" title={formatDateTime(selectedAutomation.nextRunAt, text.unavailable)}>
                  {text.nextRunHint(formatRelativeTime(selectedAutomation.nextRunAt))}
                  {(() => {
                    const abs = formatNextRunAbsolute(selectedAutomation.nextRunAt);
                    return abs ? (
                      <span className="automations-next-run-abs">{text.nextRunAbsolute(abs)}</span>
                    ) : null;
                  })()}
                </span>
              </div>

              <div className="metric-item automation-detail-metric" data-testid="automation-detail-schedule">
                <span className="metric-label">{text.scheduleLabel}</span>
                <span className="metric-value">
                  {formatSchedule(selectedAutomation.cronExpression)}
                  <code className="automation-cron-raw" title={text.cronRaw}>{selectedAutomation.cronExpression}</code>
                </span>
              </div>

              <div className="metric-item automation-detail-metric" data-testid="automation-detail-model">
                <span className="metric-label">{text.model}</span>
                <span className="metric-value">
                  {selectedAutomation.modelId
                    ? <code className="automation-detail-model-id">{selectedAutomation.modelId}</code>
                    : <span className="metric-value--muted">{text.modelDefault}</span>
                  }
                </span>
              </div>
              {selectedAutomation.notificationChannels && selectedAutomation.notificationChannels.length > 0 ? (
                <div className="metric-item automation-detail-metric" data-testid="automation-detail-channels">
                  <span className="metric-label">{text.channels}</span>
                  <div className="automation-channels-list">
                    {selectedAutomation.notificationChannels.map((id) => (
                      <span key={id} className="automation-channel-chip">
                        {resolveChannelLabel(id)}
                      </span>
                    ))}
                  </div>
                </div>
              ) : null}
              {selectedAutomation.notifyMode && selectedAutomation.notifyMode !== "None" ? (
                <div className="metric-item automation-detail-metric" data-testid="automation-detail-notify-mode">
                  <span className="metric-label">{text.notifyMode}</span>
                  <span className="metric-value">
                    {selectedAutomation.notifyMode === "Auto" ? text.notifyModeAuto : text.notifyModeApproval}
                  </span>
                </div>
              ) : null}

              <div className="metric-item automation-detail-metric" data-testid="automation-detail-prompt">
                <span className="metric-label">{text.promptSummary}</span>
                <div className="automation-prompt-body">
                  <ReactMarkdown remarkPlugins={[remarkGfm]}>
                    {selectedAutomation.prompt}
                  </ReactMarkdown>
                </div>
              </div>

              <div
                className="metric-item automation-detail-metric"
                data-testid="automation-detail-input-paths"
              >
                <span className="metric-label">{text.inputPaths}</span>
                {selectedAutomation.inputPaths?.length ? (
                  <ul className="automation-bullets-list">
                    {selectedAutomation.inputPaths.map((path) => (
                      <li key={path} className="metric-value metric-value--path">
                        {path}
                      </li>
                    ))}
                  </ul>
                ) : (
                  <span className="metric-value">{text.noInputPaths}</span>
                )}
              </div>

              {displayedLastError ? (
                <div
                  className="metric-item automation-detail-error"
                  data-testid="automation-detail-last-error"
                >
                  <span className="metric-label">{text.lastError}</span>
                  <span className="metric-value metric-value--error">{displayedLastError}</span>
                </div>
              ) : (
                // Keep test ID in DOM even when no error so tests can query it
                <div
                  className="metric-item automation-detail-error"
                  data-testid="automation-detail-last-error"
                  style={{ display: "none" }}
                  aria-hidden="true"
                >
                  <span className="metric-label">{text.lastError}</span>
                  <span className="metric-value">{text.noRecentError}</span>
                </div>
              )}

              <div data-testid="automation-runs">
                <p className="metric-label">{text.recentRuns}</p>
                {isLoadingRuns ? <p className="desk-section-desc">{text.loadingRuns}</p> : null}
                {!isLoadingRuns && recentRuns.length === 0 ? (
                  <p className="desk-section-desc">{text.emptyRuns}</p>
                ) : null}
                {!isLoadingRuns && recentRuns.length > 0 ? (
                  <ul className="automation-runs-list">
                    {recentRuns.map((run) => {
                      const runSummary = stripXml(run.summary) || resolveTriggerLabel(run.trigger);
                      const duration = formatDuration(run.startedAt, run.completedAt);
                      const isRunning = run.status === "Running" || run.status === "Queued";
                      return (
                        <li key={run.runId} className="automation-run-item" data-testid={`automation-run-${run.runId}`}>
                          <div className="automation-run-item__header">
                            <span className={`automation-run-item__status automation-run-item__status--${run.status.toLowerCase()}`}>
                              {isRunning && <span className="automation-status-spinner" aria-hidden="true" />}
                              {resolveRunStatusLabel(run.status)}
                            </span>
                            <span className="automation-run-item__attempt">{text.runAttempt(run.attempt)}</span>
                            {duration ? (
                              <span className="automation-run-item__duration">{duration}</span>
                            ) : null}
                            <span className="automation-run-item__window">
                              {run.startedAt ? formatRelativeTime(run.startedAt) : text.unavailable}
                            </span>
                          </div>
                          {runSummary ? (
                            <p className="automation-run-item__summary">{runSummary}</p>
                          ) : null}
                        </li>
                      );
                    })}
                  </ul>
                ) : null}
              </div>

              <div
                className="metric-item automation-detail-metric"
                data-testid="automation-prompt-diagnostics"
              >
                <span className="metric-label">{text.promptDiagnostics}</span>
                {isLoadingPromptDiagnostics ? (
                  <p className="desk-section-desc">{text.loadingPromptDiagnostics}</p>
                ) : promptDiagnosticsError ? (
                  <span className="metric-value">{promptDiagnosticsError}</span>
                ) : latestPromptReport ? (
                  <>
                    <span className="metric-label">{text.latestRunSession}</span>
                    <span className="metric-value metric-value--path" data-testid="automation-prompt-session-id">
                      {latestRunSessionDetail?.sessionId ?? text.unavailable}
                    </span>
                    <span className="metric-label">{text.promptProfile}</span>
                    <span className="metric-value">{latestPromptReport.profileId}</span>
                    <span className="metric-label">{text.promptSize}</span>
                    <span className="metric-value">{formatPromptSize(latestPromptReport.characterCount)}</span>
                    <span className="metric-label">{text.promptBudget}</span>
                    <span className="metric-value">{formatPromptBudget(latestRunSessionDetail!)}</span>
                    <span className="metric-label">{text.promptGeneratedAt}</span>
                    <span className="metric-value">
                      {formatDateTime(latestPromptReport.generatedAt, text.unavailable)}
                    </span>
                    <span className="metric-label">{text.memoryBoundary}</span>
                    <span className="metric-value">
                      {latestPromptHasMemory
                        ? text.memoryBoundaryWithMemory
                        : text.memoryBoundaryDefault}
                    </span>
                    <span className="metric-label">{text.truncationState}</span>
                    <span className="metric-value">
                      {latestPromptReport.wasTruncated ? text.truncationOn : text.truncationOff}
                    </span>
                    <span className="metric-label">{text.loadedContextFiles}</span>
                    {latestPromptReport.loadedContextFiles.length > 0 ? (
                      <ul className="automation-bullets-list">
                        {latestPromptReport.loadedContextFiles.map((path) => (
                          <li key={path} className="metric-value metric-value--path">
                            {path}
                          </li>
                        ))}
                      </ul>
                    ) : (
                      <span className="metric-value">{text.noLoadedContextFiles}</span>
                    )}
                    {latestPromptReport.wasTruncated ? (
                      <>
                        <span className="metric-label">{text.truncatedContextFiles}</span>
                        {(latestPromptReport.truncatedContextFiles?.length ?? 0) > 0 ? (
                          <ul className="automation-bullets-list">
                            {latestPromptReport.truncatedContextFiles!.map((path) => (
                              <li key={path} className="metric-value metric-value--path">
                                {path}
                              </li>
                            ))}
                          </ul>
                        ) : (
                          <span className="metric-value">{text.emptyPromptDiagnostics}</span>
                        )}
                        <span className="metric-label">{text.truncationNotes}</span>
                        {(latestPromptReport.truncationNotes?.length ?? 0) > 0 ? (
                          <ul className="automation-bullets-list">
                            {latestPromptReport.truncationNotes!.map((note) => (
                              <li key={note} className="metric-value">
                                {note}
                              </li>
                            ))}
                          </ul>
                        ) : (
                          <span className="metric-value">{text.emptyPromptDiagnostics}</span>
                        )}
                      </>
                    ) : null}
                  </>
                ) : (
                  <span className="metric-value">{text.emptyPromptDiagnostics}</span>
                )}
              </div>
            </>
          ) : (
            <EmptyState icon={<Zap size={28} strokeWidth={1.5} />} title={text.emptyDetail} />
          )}
        </section>
      </div>

      {/* Confirm: disable automation */}
      <ConfirmModal
        open={confirmState?.kind === "disable"}
        title={text.confirmDisableTitle}
        description={text.confirmDisableDesc}
        confirmLabel={text.confirmDisableLabel}
        cancelLabel={text.cancel}
        variant="danger"
        onConfirm={() => { void handleConfirm(); }}
        onCancel={() => setConfirmState(null)}
      />

      {/* Confirm: trigger automation */}
      <ConfirmModal
        open={confirmState?.kind === "trigger"}
        title={confirmState?.kind === "trigger" ? text.confirmTriggerTitle(confirmState.title) : ""}
        description={text.confirmTriggerDesc}
        confirmLabel={text.confirmTriggerLabel}
        cancelLabel={text.cancel}
        variant="warning"
        onConfirm={() => { void handleConfirm(); }}
        onCancel={() => setConfirmState(null)}
      />
    </section>
  );
}
