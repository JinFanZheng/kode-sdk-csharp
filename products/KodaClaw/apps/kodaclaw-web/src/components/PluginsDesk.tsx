import { FormEvent, useEffect, useMemo, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../lib/queryKeys";
import { Skeleton } from "./ui/Skeleton";
import { EmptyState } from "./ui/EmptyState";
import { Button } from "./ui/Button";
import { Select } from "./ui/Select";
import { Puzzle } from "lucide-react";
import {
  disablePlugin,
  discoverPlugins,
  enablePlugin,
  fetchPlugin,
  fetchPluginLogs,
  fetchPlugins,
  installLocalPlugin,
  startPlugin,
  stopPlugin,
  trustPlugin,
} from "../lib/api";
import { useI18n, useLocaleText } from "../i18n/I18nProvider";
import type {
  PluginDetail,
  PluginLogEntry,
  PluginRuntimeState,
  PluginSummary,
  PluginTrustState,
  PluginType,
} from "../types/contracts";
import "./ControlPlaneDesk.css";

type PluginTypeFilter = PluginType | "all";
type PluginTrustFilter = PluginTrustState | "all";
type PluginRuntimeFilter = PluginRuntimeState | "all";

const PLUGIN_TYPES: PluginType[] = ["Tool", "Channel", "Memory", "Ui"];
const PLUGIN_TRUST_STATES: PluginTrustState[] = ["Signed", "Trusted", "Untrusted"];
const PLUGIN_RUNTIME_STATES: PluginRuntimeState[] = ["Running", "Starting", "Stopped", "Degraded"];
const PLUGIN_LIST_LIMIT = 80;
const PLUGIN_LOG_LIMIT = 200;

function formatDigest(value?: string | null): string {
  if (!value) {
    return "n/a";
  }

  return value.length <= 20 ? value : `${value.slice(0, 16)}...${value.slice(-12)}`;
}

export function PluginsDesk() {
  const { formatDateTime } = useI18n();
  const text = useLocaleText({
    zh: {
      common: {
        controlPlane: "控制平面",
        loading: "加载中…",
        none: "暂无",
        refresh: "刷新工作台",
        refreshing: "正在刷新…",
        allTypes: "全部类型",
        allStates: "全部状态",
      },
      title: "插件指挥台",
      intro: "在插件工具进入新会话前，先检查信任状态、启动姿态、权限范围与运行证据。",
      errors: {
        loadDetail: "加载插件详情失败。",
        loadRegistry: "加载插件注册表失败。",
        actionFailed: "插件操作失败。",
        installPathRequired: "安装前请先提供本地插件目录。",
        selectPluginRequired: "发送生命周期指令前，请先选择一个插件。",
      },
      notes: {
        discoveryCompleted: "发现扫描已完成。",
        installed: "已安装",
        trustVerified: "插件信任已授予，并验证了签名证据。",
        trustDigest: "插件信任已授予，并记录了本地摘要证据。",
        enabled: "插件已启用。",
        disabled: "插件已停用。",
        started: "插件运行时已启动。",
        stopped: "插件运行时已停止。",
      },
      filters: {
        discover: "扫描插件根目录",
        type: "类型",
        trust: "信任",
        runtime: "运行时",
        enabledOnly: "仅看已启用",
      },
      install: {
        label: "安装本地插件",
        placeholder: "/插件目录的绝对路径",
        submit: "安装",
      },
      list: {
        title: "注册表索引",
        loading: "正在加载插件…",
        empty: "当前筛选条件下没有插件。",
        pluginsSuffix: "个插件",
      },
      detail: {
        eyebrow: "插件详情",
        empty: "尚未选择插件",
        idle: "空闲",
        pluginId: "插件 ID",
        installSource: "安装来源",
        runtimeState: "运行状态",
        health: "健康状态",
        trust: "信任状态",
        verification: "校验结果",
        trustSource: "信任来源",
        enablement: "启用状态",
        lastHealth: "最近健康检查",
        restartCount: "重启次数",
        noHealthSummary: "暂无健康摘要。",
        enabled: "已启用",
        disabled: "未启用",
      },
      actions: {
        trust: "信任",
        enable: "启用",
        disable: "停用",
        start: "启动",
        stop: "停止",
      },
      trustEvidence: {
        title: "信任证据",
        verification: "校验",
        launchGate: "启动门控",
        digest: "证据摘要",
        signerUnavailable: "签名者信息不可用",
        unavailable: "不可用",
      },
      permissions: {
        title: "权限姿态",
        reviewRequired: "需要复核",
        lowFriction: "低摩擦",
        declaredScope: "声明范围",
        capabilities: "能力声明",
      },
      tools: {
        title: "工具暴露",
        namespacedTool: "命名空间工具",
        empty: "这个插件当前没有暴露任何运行时工具。",
        toolsSuffix: "个工具",
      },
      logs: {
        title: "最近日志",
        copy: "调整启动姿态前，先把宿主证据放在手边。",
        rawLogs: "原始日志",
        empty: "还没有记录到插件日志。",
      },
      future: {
        eyebrow: "未来设置",
        title: "面板挂载位已预留",
        copy: "Iteration 4 暂时只保留插件设置占位面。等 UI 插件到来后，这里会成为安全的宿主容器。",
      },
      labels: {
        noExtraScopes: "没有声明额外的文件系统、网络或 Secrets 范围。",
        noCapabilities: "尚未加载能力元数据。",
        noCapabilityFragments: "Manifest 没有声明额外能力。",
        trustAwaiting: "等待建立信任",
        trustTrusted: "允许启动",
        trustSigned: "已验证签名证据",
        runtimeRunning: "运行中",
        runtimeStarting: "启动中",
        runtimeDegraded: "需要操作员恢复",
        runtimeStopped: "已停止",
        verificationVerified: "签名证据与当前插件内容一致。",
        verificationMismatch: "签名证据与当前插件内容已经不匹配。",
        verificationInvalid: "信任证据不完整或不可读。",
        verificationDigestOnly: "已记录本地摘要，但没有发现签名 sidecar。",
        verificationNone: "尚未记录任何信任证据。",
        gateEmpty: "选择一个插件后，可在这里检查信任与权限门控。",
        gateSignedMismatch: "签名插件必须先恢复到可验证状态，才能再次启动。",
        gateHighRisk: "信任这个插件会解锁高风险权限；启动前请复查每一项声明。",
        gateDefault: "启动门控取决于信任、启用状态、运行健康，以及最新校验证据。",
        noLogsMessage: "暂无",
      },
      typeLabels: {
        Tool: "工具",
        Channel: "渠道",
        Memory: "记忆",
        Ui: "界面",
      },
      trustStateLabels: {
        Signed: "已签名",
        Trusted: "已信任",
        Untrusted: "未信任",
      },
      runtimeStateLabels: {
        Running: "运行中",
        Starting: "启动中",
        Stopped: "已停止",
        Degraded: "降级",
      },
    },
    en: {
      common: {
        controlPlane: "Control Plane",
        loading: "Loading...",
        none: "n/a",
        refresh: "Refresh deck",
        refreshing: "Refreshing...",
        allTypes: "All types",
        allStates: "All states",
      },
      title: "Plugin Command Deck",
      intro: "Inspect trust, launch posture, permissions, and runtime evidence before plugin tools enter a fresh operator session.",
      errors: {
        loadDetail: "Failed to load plugin detail.",
        loadRegistry: "Failed to load plugin registry.",
        actionFailed: "Plugin action failed.",
        installPathRequired: "Provide a local plugin directory before installing.",
        selectPluginRequired: "Select a plugin before sending lifecycle commands.",
      },
      notes: {
        discoveryCompleted: "Discovery sweep completed.",
        installed: "Installed",
        trustVerified: "Plugin trust granted with verified signature evidence.",
        trustDigest: "Plugin trust granted with local digest evidence.",
        enabled: "Plugin enabled.",
        disabled: "Plugin disabled.",
        started: "Plugin runtime started.",
        stopped: "Plugin runtime stopped.",
      },
      filters: {
        discover: "Discover roots",
        type: "Type",
        trust: "Trust",
        runtime: "Runtime",
        enabledOnly: "Enabled only",
      },
      install: {
        label: "Install local plugin",
        placeholder: "/absolute/path/to/plugin",
        submit: "Install",
      },
      list: {
        title: "Registry index",
        loading: "Loading plugins...",
        empty: "No plugins match the current filters.",
        pluginsSuffix: "plugins",
      },
      detail: {
        eyebrow: "Plugin detail",
        empty: "No plugin selected",
        idle: "Idle",
        pluginId: "Plugin id",
        installSource: "Install source",
        runtimeState: "Runtime state",
        health: "Health",
        trust: "Trust",
        verification: "Verification",
        trustSource: "Trust source",
        enablement: "Enablement",
        lastHealth: "Last health",
        restartCount: "Restart count",
        noHealthSummary: "No health summary yet.",
        enabled: "Enabled",
        disabled: "Disabled",
      },
      actions: {
        trust: "Trust",
        enable: "Enable",
        disable: "Disable",
        start: "Start",
        stop: "Stop",
      },
      trustEvidence: {
        title: "Trust evidence",
        verification: "Verification",
        launchGate: "Launch gate",
        digest: "Evidence digest",
        signerUnavailable: "Signer unavailable",
        unavailable: "Unavailable",
      },
      permissions: {
        title: "Permission posture",
        reviewRequired: "Review required",
        lowFriction: "Low friction",
        declaredScope: "Declared scope",
        capabilities: "Capabilities",
      },
      tools: {
        title: "Tool exposure",
        namespacedTool: "Namespaced tool",
        empty: "No runtime tools are currently exposed for this plugin.",
        toolsSuffix: "tool(s)",
      },
      logs: {
        title: "Recent logs",
        copy: "Keep the host evidence close before changing launch posture.",
        rawLogs: "Raw logs",
        empty: "No plugin logs have been recorded yet.",
      },
      future: {
        eyebrow: "Future settings",
        title: "Panel mount is reserved",
        copy: "Iteration 4 keeps plugin settings as a placeholder surface. Once UI plugins arrive, this card becomes the safe host container.",
      },
      labels: {
        noExtraScopes: "No extra filesystem, network, or secret scopes were declared.",
        noCapabilities: "No capability metadata loaded.",
        noCapabilityFragments: "Manifest does not advertise extra capabilities.",
        trustAwaiting: "Awaiting trust",
        trustTrusted: "Trusted for launch",
        trustSigned: "Signed evidence verified",
        runtimeRunning: "Runtime active",
        runtimeStarting: "Starting",
        runtimeDegraded: "Needs operator recovery",
        runtimeStopped: "Stopped",
        verificationVerified: "Signature evidence matches the current plugin contents.",
        verificationMismatch: "Signature evidence no longer matches the plugin contents.",
        verificationInvalid: "Trust evidence is incomplete or unreadable.",
        verificationDigestOnly: "Local digest captured; no signature sidecar was found.",
        verificationNone: "No trust evidence recorded yet.",
        gateEmpty: "Select a plugin to inspect its trust and permission gates.",
        gateSignedMismatch: "Signed plugins cannot launch until signature evidence verifies cleanly again.",
        gateHighRisk: "Trusting this plugin unlocks high-risk scopes; review every declared permission before launch.",
        gateDefault: "Launch stays gated by trust, enablement, runtime health, and the latest verification evidence.",
        noLogsMessage: "n/a",
      },
      typeLabels: {
        Tool: "Tool",
        Channel: "Channel",
        Memory: "Memory",
        Ui: "UI",
      },
      trustStateLabels: {
        Signed: "Signed",
        Trusted: "Trusted",
        Untrusted: "Untrusted",
      },
      runtimeStateLabels: {
        Running: "Running",
        Starting: "Starting",
        Stopped: "Stopped",
        Degraded: "Degraded",
      },
    },
  });

  const queryClient = useQueryClient();
  const [plugins, setPlugins] = useState<PluginSummary[]>([]);
  const [selectedPluginId, setSelectedPluginId] = useState<string | null>(null);
  const [detail, setDetail] = useState<PluginDetail | null>(null);
  const [logs, setLogs] = useState<PluginLogEntry[]>([]);
  const [installPath, setInstallPath] = useState("");
  const [typeFilter, setTypeFilter] = useState<PluginTypeFilter>("all");
  const [trustFilter, setTrustFilter] = useState<PluginTrustFilter>("all");
  const [runtimeFilter, setRuntimeFilter] = useState<PluginRuntimeFilter>("all");
  const [enabledOnly, setEnabledOnly] = useState(false);
  const [isLoadingList, setIsLoadingList] = useState(true);
  const [isRefreshing, setIsRefreshing] = useState(false);
  const [isLoadingDetail, setIsLoadingDetail] = useState(false);
  const [isMutating, setIsMutating] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [note, setNote] = useState<string | null>(null);

  const listRequestIdRef = useRef(0);
  const detailRequestIdRef = useRef(0);

  const selectedSummary = useMemo(
    () => plugins.find((item) => item.id === selectedPluginId) ?? null,
    [plugins, selectedPluginId],
  );

  const describeTrust = (trustState: PluginTrustState): string => {
    switch (trustState) {
      case "Signed":
        return text.labels.trustSigned;
      case "Trusted":
        return text.labels.trustTrusted;
      default:
        return text.labels.trustAwaiting;
    }
  };

  const describeRuntime = (runtimeState: PluginRuntimeState): string => {
    switch (runtimeState) {
      case "Running":
        return text.labels.runtimeRunning;
      case "Starting":
        return text.labels.runtimeStarting;
      case "Degraded":
        return text.labels.runtimeDegraded;
      default:
        return text.labels.runtimeStopped;
    }
  };

  const summarizeTypes = (types: PluginType[]): string => {
    return types.map((type) => text.typeLabels[type] ?? type).join(" · ");
  };

  const summarizePermissions = (pluginDetail: PluginDetail | null): string[] => {
    if (!pluginDetail) {
      return [];
    }

    const high = pluginDetail.permissionSummary.highRiskReasons;
    const medium = pluginDetail.permissionSummary.mediumRiskReasons;
    if (high.length > 0 || medium.length > 0) {
      return [...high, ...medium];
    }

    return [text.labels.noExtraScopes];
  };

  const summarizeCapabilities = (pluginDetail: PluginDetail | null): string => {
    if (!pluginDetail) {
      return text.labels.noCapabilities;
    }

    const capabilities = pluginDetail.record.manifest.capabilities;
    const fragments = [
      capabilities.tools && capabilities.tools.length > 0 ? `${capabilities.tools.length} ${text.tools.toolsSuffix}` : null,
      capabilities.channels && capabilities.channels.length > 0 ? `${capabilities.channels.length} ${text.typeLabels.Channel}` : null,
      capabilities.memoryProviders && capabilities.memoryProviders.length > 0
        ? `${capabilities.memoryProviders.length} ${text.typeLabels.Memory}`
        : null,
      capabilities.uiPanels && capabilities.uiPanels.length > 0 ? `${capabilities.uiPanels.length} ${text.typeLabels.Ui}` : null,
    ].filter(Boolean);

    return fragments.length > 0 ? fragments.join(" · ") : text.labels.noCapabilityFragments;
  };

  const describeVerificationState = (pluginDetail: PluginDetail | null): string => {
    const verificationState = pluginDetail?.record.trustEvidence?.verificationState;
    switch (verificationState) {
      case "Verified":
        return text.labels.verificationVerified;
      case "Mismatch":
        return text.labels.verificationMismatch;
      case "Invalid":
        return text.labels.verificationInvalid;
      case "DigestOnly":
        return text.labels.verificationDigestOnly;
      default:
        return text.labels.verificationNone;
    }
  };

  const describeTrustGate = (pluginDetail: PluginDetail | null): string => {
    if (!pluginDetail) {
      return text.labels.gateEmpty;
    }

    if (pluginDetail.record.trustState === "Signed" && pluginDetail.record.trustEvidence?.verificationState !== "Verified") {
      return text.labels.gateSignedMismatch;
    }

    if (pluginDetail.permissionSummary.hasHighRisk) {
      return text.labels.gateHighRisk;
    }

    return text.labels.gateDefault;
  };

  const permissionSummary = useMemo(() => summarizePermissions(detail), [detail]);
  const toolNames = detail?.availableTools ?? [];
  const currentEnabled = detail?.record.enabled ?? selectedSummary?.enabled ?? false;
  const currentRestartCount = detail?.record.restartCount ?? 0;
  const hasVerifiedSignature =
    detail?.record.trustState !== "Signed" || detail?.record.trustEvidence?.verificationState === "Verified";

  async function loadPluginDetail(pluginId: string | null) {
    const requestId = ++detailRequestIdRef.current;

    if (!pluginId) {
      setDetail(null);
      setLogs([]);
      setIsLoadingDetail(false);
      return;
    }

    setIsLoadingDetail(true);

    try {
      const [detailPayload, logsPayload] = await Promise.all([
        fetchPlugin(pluginId),
        fetchPluginLogs(pluginId, { limit: PLUGIN_LOG_LIMIT }),
      ]);

      if (detailRequestIdRef.current !== requestId) {
        return;
      }

      setDetail(detailPayload);
      setLogs(logsPayload);
    } catch (nextError) {
      if (detailRequestIdRef.current !== requestId) {
        return;
      }

      setDetail(null);
      setLogs([]);
      setError(nextError instanceof Error ? nextError.message : text.errors.loadDetail);
    } finally {
      if (detailRequestIdRef.current === requestId) {
        setIsLoadingDetail(false);
      }
    }
  }

  async function loadPlugins(mode: "initial" | "refresh", preferredPluginId?: string | null) {
    const requestId = ++listRequestIdRef.current;

    if (mode === "initial") {
      setIsLoadingList(true);
    } else {
      setIsRefreshing(true);
    }

    setError(null);

    try {
      const payload = await fetchPlugins({
        type: typeFilter === "all" ? undefined : typeFilter,
        trustState: trustFilter === "all" ? undefined : trustFilter,
        runtimeState: runtimeFilter === "all" ? undefined : runtimeFilter,
        enabled: enabledOnly ? true : undefined,
        limit: PLUGIN_LIST_LIMIT,
      });

      if (listRequestIdRef.current !== requestId) {
        return;
      }

      setPlugins(payload.items);
      const nextSelectedPluginId = payload.items.some((item) => item.id === preferredPluginId)
        ? preferredPluginId ?? null
        : payload.items[0]?.id ?? null;
      setSelectedPluginId(nextSelectedPluginId);
      await loadPluginDetail(nextSelectedPluginId);
    } catch (nextError) {
      if (listRequestIdRef.current !== requestId) {
        return;
      }

      setPlugins([]);
      setSelectedPluginId(null);
      setDetail(null);
      setLogs([]);
      setError(nextError instanceof Error ? nextError.message : text.errors.loadRegistry);
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

  useEffect(() => {
    void loadPlugins("initial", selectedPluginId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [typeFilter, trustFilter, runtimeFilter, enabledOnly]);

  async function handleRefresh() {
    await loadPlugins("refresh", selectedPluginId);
  }

  async function handleSelectPlugin(pluginId: string) {
    setSelectedPluginId(pluginId);
    setError(null);
    await loadPluginDetail(pluginId);
  }

  async function handleAction(action: "trust" | "enable" | "disable" | "start" | "stop" | "discover" | "install") {
    setIsMutating(true);
    setError(null);
    setNote(null);

    try {
      if (action === "discover") {
        await discoverPlugins();
        void queryClient.invalidateQueries({ queryKey: ['plugins'] });
        setNote(text.notes.discoveryCompleted);
        await loadPlugins("refresh", selectedPluginId);
        return;
      }

      if (action === "install") {
        const normalizedPath = installPath.trim();
        if (!normalizedPath) {
          setError(text.errors.installPathRequired);
          return;
        }

        const installed = await installLocalPlugin({ path: normalizedPath });
        void queryClient.invalidateQueries({ queryKey: ['plugins'] });
        setInstallPath("");
        setNote(`${text.notes.installed} ${installed.record.manifest.name}。`);
        await loadPlugins("refresh", installed.record.id);
        return;
      }

      if (!selectedPluginId) {
        setError(text.errors.selectPluginRequired);
        return;
      }

      if (action === "trust") {
        const trusted = await trustPlugin(selectedPluginId);
        setNote(trusted.record.trustState === "Signed" ? text.notes.trustVerified : text.notes.trustDigest);
      } else if (action === "enable") {
        await enablePlugin(selectedPluginId);
        setNote(text.notes.enabled);
      } else if (action === "disable") {
        await disablePlugin(selectedPluginId);
        setNote(text.notes.disabled);
      } else if (action === "start") {
        await startPlugin(selectedPluginId);
        setNote(text.notes.started);
      } else {
        await stopPlugin(selectedPluginId);
        setNote(text.notes.stopped);
      }

      void queryClient.invalidateQueries({ queryKey: ['plugins'] });
      await loadPlugins("refresh", selectedPluginId);
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : text.errors.actionFailed);
    } finally {
      setIsMutating(false);
    }
  }

  const trustButtonEnabled = !isMutating && !!detail && detail.record.trustState === "Untrusted";
  const enableButtonEnabled = !isMutating && !!detail && (detail.record.trustState === "Trusted" || detail.record.trustState === "Signed") && !detail.record.enabled;
  const disableButtonEnabled = !isMutating && !!detail && detail.record.enabled;
  const startButtonEnabled = !isMutating && !!detail && (detail.record.trustState === "Trusted" || detail.record.trustState === "Signed") && hasVerifiedSignature && detail.record.enabled && detail.record.runtimeState !== "Running";
  const stopButtonEnabled = !isMutating && !!detail && detail.record.runtimeState === "Running";

  return (
    <section data-testid="plugins-desk">
      <h2 className="desk-section-title">{text.title}</h2>
      <p className="desk-section-desc">{text.intro}</p>

      <div className="control-plane-toolbar">
        <Button
          variant="ghost"
          size="control"
          data-testid="plugins-refresh"
          disabled={isLoadingList || isRefreshing || isMutating}
          onClick={() => { void handleRefresh(); }}
        >
          {isRefreshing ? text.common.refreshing : text.common.refresh}
        </Button>
        <Button
          variant="ghost"
          size="control"
          data-testid="plugins-discover"
          disabled={isMutating}
          onClick={() => { void handleAction("discover"); }}
        >
          {text.filters.discover}
        </Button>
        <label className="metric-label" htmlFor="plugins-type-filter">{text.filters.type}</label>
        <Select
          id="plugins-type-filter"
          data-testid="plugins-type-filter"
          className="control-plane-filter"
          value={typeFilter}
          onChange={(event) => setTypeFilter(event.target.value as PluginTypeFilter)}
        >
          <option value="all">{text.common.allTypes}</option>
          {PLUGIN_TYPES.map((type) => (
            <option key={type} value={type}>{text.typeLabels[type]}</option>
          ))}
        </Select>
        <label className="metric-label" htmlFor="plugins-trust-filter">{text.filters.trust}</label>
        <Select
          id="plugins-trust-filter"
          data-testid="plugins-trust-filter"
          className="control-plane-filter"
          value={trustFilter}
          onChange={(event) => setTrustFilter(event.target.value as PluginTrustFilter)}
        >
          <option value="all">{text.common.allStates}</option>
          {PLUGIN_TRUST_STATES.map((state) => (
            <option key={state} value={state}>{text.trustStateLabels[state]}</option>
          ))}
        </Select>
        <label className="metric-label" htmlFor="plugins-runtime-filter">{text.filters.runtime}</label>
        <Select
          id="plugins-runtime-filter"
          data-testid="plugins-runtime-filter"
          className="control-plane-filter control-plane-filter--wide"
          value={runtimeFilter}
          onChange={(event) => setRuntimeFilter(event.target.value as PluginRuntimeFilter)}
        >
          <option value="all">{text.common.allStates}</option>
          {PLUGIN_RUNTIME_STATES.map((state) => (
            <option key={state} value={state}>{text.runtimeStateLabels[state]}</option>
          ))}
        </Select>
        <label className="bootstrap-form__toggle" htmlFor="plugins-enabled-only">
          <input
            id="plugins-enabled-only"
            data-testid="plugins-enabled-only"
            type="checkbox"
            checked={enabledOnly}
            onChange={(event) => setEnabledOnly(event.target.checked)}
          />
          {text.filters.enabledOnly}
        </label>
      </div>

      <form
        data-testid="plugin-install-form"
        onSubmit={(event: FormEvent<HTMLFormElement>) => {
          event.preventDefault();
          void handleAction("install");
        }}
        className="plugins-install-form"
      >
        <label className="plugins-install-form__label">
          {text.install.label}
          <input
            data-testid="plugin-install-path"
            className="plugins-install-form__input kc-input"
            value={installPath}
            onChange={(event) => setInstallPath(event.target.value)}
            placeholder={text.install.placeholder}
          />
        </label>
        <Button
          type="submit"
          variant="primary"
          size="sm"
          data-testid="plugin-install-submit"
          disabled={isMutating}
        >
          {text.install.submit}
        </Button>
      </form>

      {error ? (
        <p className="desk-feedback desk-feedback--error" data-testid="plugins-error">{error}</p>
      ) : null}
      {note ? (
        <p className="desk-feedback desk-feedback--success" data-testid="plugins-note">{note}</p>
      ) : null}

      <div className="plugins-desk__layout">
        <section className="timeline" data-testid="plugins-list">
          <div className="timeline__header">
            <h3 className="desk-section-title">{text.list.title}</h3>
            <span className="composer__status">
              {isLoadingList ? text.common.loading : `${plugins.length} ${text.list.pluginsSuffix}`}
            </span>
          </div>

          <div className="plugins-list-body">
            {isLoadingList ? <Skeleton height={52} count={3} /> : null}
            {!isLoadingList && plugins.length === 0 ? (
              <EmptyState icon={<Puzzle size={28} strokeWidth={1.5} />} title={text.list.empty} />
            ) : null}

            {plugins.map((plugin) => {
              const isSelected = plugin.id === selectedPluginId;
              const runtimeMod = plugin.runtimeState.toLowerCase() as Lowercase<typeof plugin.runtimeState>;
              return (
                <button
                  type="button"
                  key={plugin.id}
                  className={`plugin-card${isSelected ? " plugin-card--selected" : ""}`}
                  data-testid={`plugin-item-${plugin.id}`}
                  aria-pressed={isSelected}
                  onClick={() => { void handleSelectPlugin(plugin.id); }}
                >
                  <div className="plugin-card__header">
                    <span className={`plugin-card__runtime plugin-card__runtime--${runtimeMod}`}>
                      {describeRuntime(plugin.runtimeState)}
                    </span>
                    <span className="plugin-card__type">{summarizeTypes(plugin.types)}</span>
                  </div>
                  <span className="plugin-card__name">{plugin.name}</span>
                  <span className="plugin-card__id">{plugin.id}</span>
                  <div className="plugin-card__footer">
                    <span className={`plugin-card__trust plugin-card__trust--${plugin.trustState.toLowerCase()}`}>
                      {describeTrust(plugin.trustState)}
                    </span>
                    <span className={`plugin-card__trust ${plugin.enabled ? "plugin-card__trust--trusted" : ""}`}>
                      {plugin.enabled ? text.detail.enabled : text.detail.disabled}
                    </span>
                  </div>
                  {plugin.lastError ? (
                    <span className="plugin-card__error">{plugin.lastError}</span>
                  ) : (
                    <span className="plugin-card__id">{plugin.rootPath}</span>
                  )}
                </button>
              );
            })}
          </div>
        </section>

        <div className="plugins-detail-panel">
          <section className="plugin-identity-card" data-testid="plugin-detail">
            <div className="plugin-identity-card__header">
              <div>
                <p className="plugin-identity-card__eyebrow">{text.detail.eyebrow}</p>
                <h3 className="plugin-identity-card__name">
                  {detail?.record.manifest.name ?? selectedSummary?.name ?? text.detail.empty}
                </h3>
              </div>
              <span className={`plugin-identity-card__health${detail?.healthSummary.isHealthy ? " plugin-identity-card__health--healthy" : " plugin-identity-card__health--unhealthy"}`}>
                {detail?.healthSummary.status ?? text.detail.idle}
              </span>
            </div>

            <div data-testid="plugin-detail-core" className="plugin-fields-grid">
              <div>
                <span className="plugin-field__label">{text.detail.pluginId}</span>
                <span className="plugin-field__value plugin-field__value--mono">
                  {detail?.record.id ?? selectedSummary?.id ?? text.common.none}
                </span>
              </div>
              <div>
                <span className="plugin-field__label">{text.detail.installSource}</span>
                <span className="plugin-field__value">
                  {detail?.record.installSource ?? selectedSummary?.installSource ?? text.common.none}
                </span>
              </div>
              <div>
                <span className="plugin-field__label">{text.detail.runtimeState}</span>
                <span className="plugin-field__value">
                  {detail?.record.runtimeState ?? selectedSummary?.runtimeState ?? text.common.none}
                </span>
              </div>
              <div>
                <span className="plugin-field__label">{text.detail.health}</span>
                <span className="plugin-field__value">
                  {detail?.healthSummary.message ?? text.detail.noHealthSummary}
                </span>
              </div>
              <div>
                <span className="plugin-field__label">{text.detail.trust}</span>
                <span className="plugin-field__value">
                  {detail?.record.trustState ?? selectedSummary?.trustState ?? text.common.none}
                </span>
              </div>
              <div>
                <span className="plugin-field__label">{text.detail.verification}</span>
                <span className="plugin-field__value">
                  {detail?.record.trustEvidence?.verificationState ?? text.common.none}
                </span>
              </div>
              <div>
                <span className="plugin-field__label">{text.detail.trustSource}</span>
                <span className="plugin-field__value">
                  {detail?.record.trustEvidence?.source ?? text.common.none}
                </span>
              </div>
              <div>
                <span className="plugin-field__label">{text.detail.enablement}</span>
                <span className="plugin-field__value" data-testid="plugin-enabled-chip">
                  {currentEnabled ? text.detail.enabled : text.detail.disabled}
                </span>
              </div>
              <div>
                <span className="plugin-field__label">{text.detail.lastHealth}</span>
                <span className="plugin-field__value">
                  {formatDateTime(detail?.healthSummary.lastHealthAt, text.common.none)}
                </span>
              </div>
              <div>
                <span className="plugin-field__label">{text.detail.restartCount}</span>
                <span className="plugin-field__value">{currentRestartCount}</span>
              </div>
            </div>

            <div className="plugin-action-bar">
              <Button
                variant="primary"
                size="sm"
                data-testid="plugin-action-trust"
                disabled={!trustButtonEnabled}
                onClick={() => { void handleAction("trust"); }}
              >
                {text.actions.trust}
              </Button>
              <Button
                variant="primary"
                size="sm"
                data-testid="plugin-action-enable"
                disabled={!enableButtonEnabled}
                onClick={() => { void handleAction("enable"); }}
              >
                {text.actions.enable}
              </Button>
              <Button
                variant="ghost"
                size="sm"
                data-testid="plugin-action-disable"
                disabled={!disableButtonEnabled}
                onClick={() => { void handleAction("disable"); }}
              >
                {text.actions.disable}
              </Button>
              <Button
                variant="primary"
                size="sm"
                data-testid="plugin-action-start"
                disabled={!startButtonEnabled}
                onClick={() => { void handleAction("start"); }}
              >
                {text.actions.start}
              </Button>
              <Button
                variant="ghost"
                size="sm"
                data-testid="plugin-action-stop"
                disabled={!stopButtonEnabled}
                onClick={() => { void handleAction("stop"); }}
              >
                {text.actions.stop}
              </Button>
            </div>

            <div className="timeline" data-testid="plugin-trust-evidence">
              <div className="timeline__header">
                <h3 className="desk-section-title">{text.trustEvidence.title}</h3>
                <span className={`composer__status${detail?.record.trustEvidence?.verificationState === "Verified" ? " is-live" : ""}`}>
                  {detail?.record.trustEvidence?.verificationState ?? text.trustEvidence.unavailable}
                </span>
              </div>
              <div className="timeline__body">
                <article className="plugin-evidence-row">
                  <div className="plugin-evidence-row__header">
                    <span className="plugin-evidence-row__label">{text.trustEvidence.verification}</span>
                    <span className="plugin-evidence-row__meta">
                      {formatDateTime(detail?.record.trustEvidence?.verifiedAt, text.common.none)}
                    </span>
                  </div>
                  <p className="plugin-evidence-row__text">{describeVerificationState(detail)}</p>
                </article>
                <article className="plugin-evidence-row">
                  <div className="plugin-evidence-row__header">
                    <span className="plugin-evidence-row__label">{text.trustEvidence.launchGate}</span>
                  </div>
                  <p className="plugin-evidence-row__text">{describeTrustGate(detail)}</p>
                </article>
                <article className="plugin-evidence-row">
                  <div className="plugin-evidence-row__header">
                    <span className="plugin-evidence-row__label">{text.trustEvidence.digest}</span>
                    <span className="plugin-evidence-row__meta">
                      {detail?.record.trustEvidence?.signer ?? text.trustEvidence.signerUnavailable}
                    </span>
                  </div>
                  <p className="plugin-evidence-row__digest">
                    Manifest {formatDigest(detail?.record.trustEvidence?.manifestDigestSha256)} · Package {formatDigest(detail?.record.trustEvidence?.packageDigestSha256)}
                  </p>
                  {detail?.record.trustEvidence?.signatureFilePath ? (
                    <p className="plugin-evidence-row__text">{detail.record.trustEvidence.signatureFilePath}</p>
                  ) : null}
                  {detail?.record.trustEvidence?.summary ? (
                    <p className="plugin-evidence-row__text">{detail.record.trustEvidence.summary}</p>
                  ) : null}
                </article>
              </div>
            </div>
          </section>

          <section className="timeline" data-testid="plugin-permissions">
            <div className="timeline__header">
              <h3 className="desk-section-title">{text.permissions.title}</h3>
              <span className={`composer__status${detail?.permissionSummary.hasHighRisk ? "" : " is-live"}`}>
                {detail?.permissionSummary.hasHighRisk ? text.permissions.reviewRequired : text.permissions.lowFriction}
              </span>
            </div>
            <div className="timeline__body">
              {permissionSummary.map((item, index) => (
                <article className="plugin-permission-item" key={`${item}-${index}`}>
                  <span className="plugin-permission-item__label">
                    {text.permissions.declaredScope} {index + 1}
                  </span>
                  <p className="plugin-permission-item__text">{item}</p>
                </article>
              ))}
              <article className="plugin-permission-item">
                <span className="plugin-permission-item__label">{text.permissions.capabilities}</span>
                <p className="plugin-permission-item__text">{summarizeCapabilities(detail)}</p>
              </article>
            </div>
          </section>

          <section className="timeline" data-testid="plugin-tools">
            <div className="timeline__header">
              <h3 className="desk-section-title">{text.tools.title}</h3>
              <span className="composer__status">
                {isLoadingDetail ? text.common.loading : `${toolNames.length} ${text.tools.toolsSuffix}`}
              </span>
            </div>
            <div className="timeline__body">
              {toolNames.length === 0 ? (
                <p className="desk-section-desc">{text.tools.empty}</p>
              ) : (
                toolNames.map((toolName) => (
                  <article className="plugin-tool-item" key={toolName}>
                    <span className="plugin-field__label">{text.tools.namespacedTool}</span>
                    <span className="plugin-field__value plugin-field__value--mono">{toolName}</span>
                  </article>
                ))
              )}
            </div>
          </section>

          <section className="timeline" data-testid="plugin-logs">
            <div className="timeline__header">
              <div>
                <h3 className="desk-section-title">{text.logs.title}</h3>
                <p className="desk-section-desc">{text.logs.copy}</p>
              </div>
              <a
                data-testid="plugin-logs-link"
                className="btn btn--ghost btn--sm"
                href={selectedPluginId ? `/api/plugins/${selectedPluginId}/logs?limit=${PLUGIN_LOG_LIMIT}` : "#"}
              >
                {text.logs.rawLogs}
              </a>
            </div>
            <div className="timeline__body">
              {logs.length === 0 ? (
                <p className="desk-section-desc">{text.logs.empty}</p>
              ) : (
                logs.map((entry) => (
                  <article className="plugin-log-entry" key={entry.entryId}>
                    <span className="plugin-log-entry__source">{entry.source}</span>
                    <span className="plugin-log-entry__message">{entry.message}</span>
                    <span className="plugin-log-entry__time">
                      {formatDateTime(entry.timestamp, text.common.none)}
                    </span>
                  </article>
                ))
              )}
            </div>
          </section>
        </div>
      </div>
    </section>
  );
}
