import { useEffect, useMemo, useRef, useState } from "react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../lib/queryKeys";
import {
  createChannelAccount,
  deleteChannelAccount,
  fetchChannelAccounts,
  fetchChannelConnectors,
  fetchChannelThreadAudit,
  fetchChannelThreadDetail,
  fetchChannelThreads,
  testFeishuCredentials,
  testTelegramToken,
  updateChannelAccount,
  updateThreadSettings,
} from "../lib/api";
import { useI18n, useLocaleText } from "../i18n/I18nProvider";
import { Button } from "./ui/Button";
import { Select } from "./ui/Select";
import { Skeleton } from "./ui/Skeleton";
import { EmptyState } from "./ui/EmptyState";
import { ConfirmModal } from "./ui/ConfirmModal";
import { Copy, Radio } from "lucide-react";
import { WeChatQrLoginPanel } from "./settings/WeChatQrLoginPanel";
import type {
  ChannelAccount,
  ChannelAuditEntry,
  ChannelConnectorDescriptor,
  ChannelConnectorKind,
  ChannelThreadDetail,
  ChannelThreadSummary,
  CreateChannelAccountRequest,
  DeliveryMode,
  ChannelTurnOutcomeKind,
  SessionKind,
} from "../types/contracts";

type ConnectorFilter = ChannelConnectorKind | "all";
type AccountFilter = "all" | string;

const THREAD_LIMIT = 80;
const AUDIT_LIMIT = 20;



export function ChannelsDesk() {
  const { formatDateTime } = useI18n();
  const text = useLocaleText({
    zh: {
      eyebrow: "渠道枢纽",
      title: "渠道运营台",
      copy:
        "统一查看连接器账号、线程绑定，并在一个界面上核对策略、投递规则与待审批状态。",
      refresh: "刷新渠道面板",
      refreshing: "刷新中...",
      filters: {
        connector: "连接器",
        account: "账号",
      },
      allConnectors: "全部连接器",
      allAccounts: "全部账号",
      loading: "加载中...",
      summary: (accounts: number, threads: number) => `${accounts} 个账号 · ${threads} 条线程`,
      connectorsTitle: "连接器与账号",
      connectorsCount: (count: number) => `${count} 个连接器`,
      connectorSummary: {
        implemented: "已实现",
        planned: "规划中",
        inbound: "支持入站",
        noInbound: "无入站",
        outbound: "支持出站",
        noOutbound: "无出站",
      },
      accountState: {
        Connected: "已连接",
        Connecting: "连接中",
        Disconnected: "未连接",
        Degraded: "受限",
      },
      updatedAt: (value: string) => `更新时间 ${value}`,
      emptyConnectors: "Gateway 尚未返回渠道连接器或账号。",
      threadEyebrow: "线程绑定",
      threadTitle: "线程索引",
      noPreview: "暂无预览文本。",
      pendingApproval: (approvalId: string) => `待审批 ${approvalId}`,
      pendingDraft: "草稿待处理",
      noPendingDraft: "暂无待处理草稿",
      loadingDetail: "正在加载线程详情...",
      emptyThreads: "当前筛选下没有匹配的线程绑定。",
      emptyDetail: "选择一个线程以查看策略、投递规则与最近审计轨迹。",
      detail: {
        binding: "绑定",
        sessionKind: "会话类型",
        deliveryMode: "投递模式",
        policy: "策略",
        policyEvidence: "策略证据",
        replyGuard: "回复保护",
        pending: "待处理",
        lastOutcome: "最近结果",
        outcomeReason: "结果原因",
        mentionSignal: "提及信号",
        replyPreview: "回复预览",
        recentAudit: "最近审计",
        noAudit: "暂无审计记录。",
      },
      policySummary: (profile: string, memory: string) => `用户画像 ${profile} · 长期记忆 ${memory}`,
      replyGuardSummary: (directReply: string, mentionRequired: string) =>
        `直接回复 ${directReply} · 显式提及 ${mentionRequired}`,
      on: "开",
      off: "关",
      yes: "是",
      no: "否",
      detailPendingApproval: (approvalId: string) => `审批 ${approvalId}`,
      detailPendingDraft: "草稿待审批",
      detailNoPending: "暂无待审批或待发送草稿",
      noLastOutcome: "尚未产生入站处理结果",
      unavailable: "暂无",
      loadDetailError: "加载线程详情失败。",
      loadDeskError: "加载渠道工作台失败。",
      outcomeKind: {
        NoAction: "无动作",
        DraftCreated: "已生成草稿",
        ApprovalRequested: "已请求审批",
        Delivered: "已送达",
        Failed: "执行失败",
      },
      outcomeReason: {
        event_not_eligible: "事件只做记录，不触发回复回合",
        model_no_reply: "模型判断当前无需对外回复",
        policy_blocked: "命中渠道策略，暂不允许直接回复",
        policy_blocked_requires_mention: "群组未显式提及 Koda，按策略阻止回复",
        draft_created: "已生成草稿，等待人工复核",
        approval_requested: "回复需要审批后才能发送",
        auto_send_ready: "满足自动发送条件，允许直接投递",
        approval_rejected: "审批已拒绝，当前回合回退为不发送",
        turn_failed: "回合执行失败，需要检查运行时或提示词",
      },
      mentionSignal: {
        present: "已显式提及",
        absent: "未显式提及",
        unknown: "未记录",
      },
      evidence: {
        groupMentionRequired: "群组线程当前启用了显式提及保护。",
        blockedWithoutMention: "最近一轮未出现对 Koda 的显式提及，因此回复被阻止。",
        rejectedByOperator: (timestamp: string) => `最近一次待发送回复已被操作员拒绝${timestamp ? `（${timestamp}）` : ""}。`,
        pendingApproval: "线程上仍有一个待审批回复，尚未决策。",
        draftWaiting: "线程上已有待处理草稿，尚未发送。",
        lastNoAction: (timestamp: string) => `最近一次无动作回合记录于 ${timestamp}。`,
        lastApprovalReject: (timestamp: string) => `最近一次审批拒绝记录于 ${timestamp}。`,
        noSpecialEvidence: "当前线程没有额外的策略证据摘要。",
      },
      deliveryMode: {
        AutoSend: "自动发送",
        DraftApproval: "草稿审批",
        RequireApproval: "需审批后发送",
      },
      sessionKind: {
        Main: "主会话",
        ChannelDirectMessage: "渠道私信",
        ChannelGroup: "渠道群组",
        Automation: "自动化",
        Plugin: "插件",
      },
      copyBindingId: "复制 BindingId",
      accountDeliveryMode: "默认投递模式",
      accountDeliveryHint: "将同步应用到所有线程",
      accountDeliveryApplying: "应用中...",
      progressIndicatorLabel: "进度指示器",
      progressIndicatorHint: "发送 turn 进度到渠道（仅 Telegram / 飞书）",
      progressIndicatorApplying: "更新中...",
      weChatRescan: "重新扫码",
      weChatRescanSuccess: "重新登录成功",
      addChannel: "+ 添加渠道",
      disable: "禁用",
      enable: "启用",
      delete: "删除",
      edit: "编辑",
      editForm: {
        title: "编辑配置",
        save: "保存",
        saving: "保存中...",
        cancel: "取消",
        clear: "清除",
        secretPlaceholder: "（保留原值）",
        telegramToken: "Bot Token",
        feishuAppId: "App ID",
        feishuAppSecret: "App Secret",
        dingTalkAppKey: "App Key",
        dingTalkAppSecret: "App Secret",
        dingTalkRobotCode: "Robot Code",
        relayAccountId: "Account ID",
        relayUrl: "Relay URL",
        relaySecret: "共享密钥",
        relayNotifyChannel: "通知渠道（可选）",
        relayNotifyChannelNone: "不推送通知",
        webhookPath: "Webhook 路径",
      },
      addForm: {
        title: "添加渠道",
        stepLabel: (step: number, total: number) => `步骤 ${step}/${total}`,
        connectorType: "连接器类型",
        telegramToken: "Bot Token",
        feishuAppId: "App ID",
        feishuAppSecret: "App Secret",
        webhookPath: "Webhook 路径",
        relayAccountId: "Account ID",
        relayUrl: "Relay URL",
        relaySecret: "共享密钥",
        relayNotifyChannel: "通知渠道（可选）",
        relayNotifyChannelNone: "不推送通知",
        relayNotifyChannelHint: "社区通知将实时推送到此渠道",
        verifyToken: "验证 Token",
        verifyCredentials: "验证凭证",
        verifying: "验证中...",
        deliveryRule: "投递规则",
        displayName: "显示名称",
        displayNamePlaceholder: "我的机器人",
        next: "下一步",
        back: "返回",
        cancel: "取消",
        save: "保存",
        saving: "保存中...",
      },
    },
    en: {
      eyebrow: "Channel Hub",
      title: "Channels Operations Desk",
      copy:
        "Monitor connector accounts, inspect thread bindings, and verify policy, delivery rule, and pending approval state on one surface.",
      refresh: "Refresh channels",
      refreshing: "Refreshing...",
      filters: {
        connector: "Connector",
        account: "Account",
      },
      allConnectors: "All connectors",
      allAccounts: "All accounts",
      loading: "Loading...",
      summary: (accounts: number, threads: number) => `${accounts} accounts · ${threads} threads`,
      connectorsTitle: "Connectors & Accounts",
      connectorsCount: (count: number) => `${count} connectors`,
      connectorSummary: {
        implemented: "Implemented",
        planned: "Planned",
        inbound: "Inbound",
        noInbound: "No inbound",
        outbound: "Outbound",
        noOutbound: "No outbound",
      },
      accountState: {
        Connected: "Connected",
        Connecting: "Connecting",
        Disconnected: "Disconnected",
        Degraded: "Degraded",
      },
      updatedAt: (value: string) => `Updated ${value}`,
      emptyConnectors: "No channel connectors or accounts returned by Gateway.",
      threadEyebrow: "Thread Bindings",
      threadTitle: "Thread Index",
      noPreview: "No preview text.",
      pendingApproval: (approvalId: string) => `Pending approval ${approvalId}`,
      pendingDraft: "Pending draft",
      noPendingDraft: "No pending draft",
      loadingDetail: "Loading thread detail...",
      emptyThreads: "No thread bindings matched this filter.",
      emptyDetail: "Select a thread to inspect policy, delivery rule, and recent audit trail.",
      detail: {
        binding: "Binding",
        sessionKind: "Session kind",
        deliveryMode: "Delivery mode",
        policy: "Policy",
        policyEvidence: "Policy evidence",
        replyGuard: "Reply guard",
        pending: "Pending",
        lastOutcome: "Last outcome",
        outcomeReason: "Outcome reason",
        mentionSignal: "Mention signal",
        replyPreview: "Reply preview",
        recentAudit: "Recent audit",
        noAudit: "No audit entries.",
      },
      policySummary: (profile: string, memory: string) => `user-profile ${profile} · memory ${memory}`,
      replyGuardSummary: (directReply: string, mentionRequired: string) =>
        `direct-reply ${directReply} · mention-required ${mentionRequired}`,
      on: "on",
      off: "off",
      yes: "yes",
      no: "no",
      detailPendingApproval: (approvalId: string) => `Approval ${approvalId}`,
      detailPendingDraft: "Draft pending",
      detailNoPending: "No pending approval/draft",
      noLastOutcome: "No inbound turn outcome recorded yet.",
      unavailable: "n/a",
      loadDetailError: "Failed to load thread detail.",
      loadDeskError: "Failed to load channels desk.",
      outcomeKind: {
        NoAction: "No action",
        DraftCreated: "Draft created",
        ApprovalRequested: "Approval requested",
        Delivered: "Delivered",
        Failed: "Failed",
      },
      outcomeReason: {
        event_not_eligible: "Event was recorded without triggering a reply turn",
        model_no_reply: "Model decided no outward reply was needed",
        policy_blocked: "Channel policy blocked a direct reply",
        policy_blocked_requires_mention: "Group thread did not explicitly mention Koda",
        draft_created: "Draft created and waiting for operator review",
        approval_requested: "Reply requires approval before delivery",
        auto_send_ready: "Turn qualified for direct delivery",
        approval_rejected: "Approval was rejected, so the turn remains no-send",
        turn_failed: "Turn execution failed and needs runtime inspection",
      },
      mentionSignal: {
        present: "Explicit mention",
        absent: "No explicit mention",
        unknown: "Not recorded",
      },
      evidence: {
        groupMentionRequired: "This group thread currently requires an explicit mention before replying.",
        blockedWithoutMention: "The latest turn did not explicitly mention Koda, so the reply stayed blocked.",
        rejectedByOperator: (timestamp: string) =>
          `The most recent proposed reply was rejected by an operator${timestamp ? ` (${timestamp})` : ""}.`,
        pendingApproval: "A reply is still pending approval on this thread.",
        draftWaiting: "A draft already exists on this thread and has not been sent.",
        lastNoAction: (timestamp: string) => `The latest no-action turn was recorded at ${timestamp}.`,
        lastApprovalReject: (timestamp: string) => `The latest approval rejection was recorded at ${timestamp}.`,
        noSpecialEvidence: "No extra policy evidence is available for this thread.",
      },
      deliveryMode: {
        AutoSend: "Auto send",
        DraftApproval: "Draft approval",
        RequireApproval: "Require approval",
      },
      sessionKind: {
        Main: "Main",
        ChannelDirectMessage: "Channel direct message",
        ChannelGroup: "Channel group",
        Automation: "Automation",
        Plugin: "Plugin",
      },
      copyBindingId: "Copy BindingId",
      accountDeliveryMode: "Default delivery mode",
      accountDeliveryHint: "Applied to all threads",
      accountDeliveryApplying: "Applying...",
      progressIndicatorLabel: "Progress indicator",
      progressIndicatorHint: "Stream turn progress to the channel (Telegram / Feishu only)",
      progressIndicatorApplying: "Updating...",
      weChatRescan: "Re-scan QR",
      weChatRescanSuccess: "Re-login successful",
      addChannel: "+ Add Channel",
      disable: "Disable",
      enable: "Enable",
      delete: "Delete",
      edit: "Edit",
      editForm: {
        title: "Edit Configuration",
        save: "Save",
        saving: "Saving...",
        cancel: "Cancel",
        clear: "Clear",
        secretPlaceholder: "(keep original)",
        telegramToken: "Bot Token",
        feishuAppId: "App ID",
        feishuAppSecret: "App Secret",
        dingTalkAppKey: "App Key",
        dingTalkAppSecret: "App Secret",
        dingTalkRobotCode: "Robot Code",
        relayAccountId: "Account ID",
        relayUrl: "Relay URL",
        relaySecret: "Shared Secret",
        relayNotifyChannel: "Notify channel (optional)",
        relayNotifyChannelNone: "No notifications",
        webhookPath: "Webhook path",
      },
      addForm: {
        title: "Add Channel",
        stepLabel: (step: number, total: number) => `Step ${step}/${total}`,
        connectorType: "Connector type",
        telegramToken: "Bot token",
        feishuAppId: "App ID",
        feishuAppSecret: "App Secret",
        dingTalkAppKey: "App Key",
        dingTalkAppSecret: "App Secret",
        dingTalkRobotCode: "Robot Code",
        relayAccountId: "Account ID",
        relayUrl: "Relay URL",
        relaySecret: "Shared Secret",
        relayNotifyChannel: "Notify channel (optional)",
        relayNotifyChannelNone: "No notifications",
        relayNotifyChannelHint: "Community notifications will be pushed to this channel in real time",
        webhookPath: "Webhook path",
        verifyToken: "Verify Token",
        verifyCredentials: "Verify Credentials",
        verifying: "Verifying...",
        deliveryRule: "Delivery rule",
        displayName: "Display name",
        displayNamePlaceholder: "My Telegram Bot",
        next: "Next",
        back: "Back",
        cancel: "Cancel",
        save: "Save",
        saving: "Saving...",
      },
    },
  });

  const queryClient = useQueryClient();
  const [connectorFilter, setConnectorFilter] = useState<ConnectorFilter>("all");
  const [accountFilter, setAccountFilter] = useState<AccountFilter>("all");
  const { data: connectorsData } = useQuery({
    queryKey: queryKeys.channelConnectors,
    queryFn: () => fetchChannelConnectors(),
  });
  const connectors: ChannelConnectorDescriptor[] = connectorsData ?? [];
  const [accounts, setAccounts] = useState<ChannelAccount[]>([]);
  const [threads, setThreads] = useState<ChannelThreadSummary[]>([]);
  const [selectedBindingId, setSelectedBindingId] = useState<string | null>(null);
  const [threadDetail, setThreadDetail] = useState<ChannelThreadDetail | null>(null);
  const [threadAudit, setThreadAudit] = useState<ChannelAuditEntry[]>([]);
  const [isLoadingList, setIsLoadingList] = useState(true);
  const [isRefreshing, setIsRefreshing] = useState(false);
  const [isLoadingDetail, setIsLoadingDetail] = useState(false);
  const [updatingDelivery, setUpdatingDelivery] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [deleteConfirm, setDeleteConfirm] = useState<{ id: string; name: string } | null>(null);
  const [weChatRescanId, setWeChatRescanId] = useState<string | null>(null);
  const [accountDeliveryApplying, setAccountDeliveryApplying] = useState<string | null>(null);
  const [progressIndicatorApplying, setProgressIndicatorApplying] = useState<string | null>(null);

  // Add channel form state
  const [showAddForm, setShowAddForm] = useState(false);
  const [addFormStep, setAddFormStep] = useState<1 | 2 | 3 | 4>(1);
  const [addFormConnectorKind, setAddFormConnectorKind] = useState<ChannelConnectorKind>("Telegram");
  const [addFormDisplayName, setAddFormDisplayName] = useState("");
  const [addFormBotToken, setAddFormBotToken] = useState("");
  const [addFormWebhookPath, setAddFormWebhookPath] = useState("");
  const [addFormDeliveryMode, setAddFormDeliveryMode] = useState<DeliveryMode>("RequireApproval");
  const [addFormTelegramTestResult, setAddFormTelegramTestResult] = useState<string | null>(null);
  const [addFormFeishuAppId, setAddFormFeishuAppId] = useState("");
  const [addFormFeishuAppSecret, setAddFormFeishuAppSecret] = useState("");
  const [addFormFeishuTestResult, setAddFormFeishuTestResult] = useState<string | null>(null);
  const [addFormDingTalkAppKey, setAddFormDingTalkAppKey] = useState("");
  const [addFormDingTalkAppSecret, setAddFormDingTalkAppSecret] = useState("");
  const [addFormDingTalkRobotCode, setAddFormDingTalkRobotCode] = useState("");
  const [addFormRelayAccountId, setAddFormRelayAccountId] = useState("");
  const [addFormRelayUrl, setAddFormRelayUrl] = useState("");
  const [addFormRelaySecret, setAddFormRelaySecret] = useState("");
  const [addFormTesting, setAddFormTesting] = useState(false);
  const [addFormSaving, setAddFormSaving] = useState(false);
  const [addFormError, setAddFormError] = useState<string | null>(null);

  // Edit account form state
  const [editingAccount, setEditingAccount] = useState<ChannelAccount | null>(null);
  const [editParsedConfig, setEditParsedConfig] = useState<Record<string, string>>({});
  const [editRelayAccountId, setEditRelayAccountId] = useState('');
  const [editRelayUrl, setEditRelayUrl] = useState('');
  const [editRelayNotifyChannelId, setEditRelayNotifyChannelId] = useState('');
  const [editFeishuAppId, setEditFeishuAppId] = useState('');
  const [editDingTalkAppKey, setEditDingTalkAppKey] = useState('');
  const [editDingTalkRobotCode, setEditDingTalkRobotCode] = useState('');
  const [editWebhookPath, setEditWebhookPath] = useState('');
  const [editSecretCleared, setEditSecretCleared] = useState<Record<string, boolean>>({});
  const [editSecretNew, setEditSecretNew] = useState<Record<string, string>>({});
  const [editSaving, setEditSaving] = useState(false);
  const [editError, setEditError] = useState<string | null>(null);

  const listRequestRef = useRef(0);
  const detailRequestRef = useRef(0);

  const selectedThread = useMemo(
    () => threads.find((item) => item.bindingId === selectedBindingId) ?? null,
    [threads, selectedBindingId],
  );

  const visibleAccounts = useMemo(() => {
    if (connectorFilter === "all") {
      return accounts;
    }

    return accounts.filter((item) => item.connectorKind === connectorFilter);
  }, [accounts, connectorFilter]);

  function resolveAccountStateLabel(state: ChannelAccount["state"]): string {
    return text.accountState[state] ?? state;
  }

  function resolveDeliveryModeLabel(mode: DeliveryMode): string {
    return text.deliveryMode[mode] ?? mode;
  }

  function resolveSessionKindLabel(kind: SessionKind): string {
    return text.sessionKind[kind] ?? kind;
  }

  function resolveOutcomeKindLabel(kind: ChannelTurnOutcomeKind): string {
    return text.outcomeKind[kind] ?? kind;
  }

  function resolveOutcomeReasonLabel(reasonCode?: string | null): string {
    if (!reasonCode) {
      return text.unavailable;
    }

    return text.outcomeReason[reasonCode as keyof typeof text.outcomeReason] ?? reasonCode;
  }

  function resolveMentionSignalLabel(hasExplicitMention?: boolean | null): string {
    if (hasExplicitMention === true) {
      return text.mentionSignal.present;
    }

    if (hasExplicitMention === false) {
      return text.mentionSignal.absent;
    }

    return text.mentionSignal.unknown;
  }

  function buildLegacyPolicyEvidence(detail: ChannelThreadDetail, audit: ChannelAuditEntry[]): string[] {
    const items: string[] = [];
    const outcome = detail.lastTurnOutcome;
    const latestNoAction = audit.find((entry) => entry.eventType === "turn.no_action");
    const latestApprovalRejected = audit.find((entry) => entry.eventType === "approval.rejected");

    if (detail.binding.threadType === "Group" && detail.policy.requireExplicitMention) {
      items.push(text.evidence.groupMentionRequired);
    }

    if (outcome?.reasonCode === "policy_blocked_requires_mention") {
      items.push(text.evidence.blockedWithoutMention);
    }

    if (outcome?.reasonCode === "approval_rejected") {
      items.push(text.evidence.rejectedByOperator(formatDateTime(outcome.occurredAt, "")));
    }

    if (detail.pendingApprovalId) {
      items.push(text.evidence.pendingApproval);
    } else if (detail.hasPendingDraft) {
      items.push(text.evidence.draftWaiting);
    }

    if (latestApprovalRejected && outcome?.reasonCode !== "approval_rejected") {
      items.push(text.evidence.lastApprovalReject(formatDateTime(latestApprovalRejected.createdAt, text.unavailable)));
    }

    if (latestNoAction && outcome?.kind === "NoAction" && outcome.reasonCode !== "approval_rejected") {
      items.push(text.evidence.lastNoAction(formatDateTime(latestNoAction.createdAt, text.unavailable)));
    }

    return items.length > 0 ? items : [text.evidence.noSpecialEvidence];
  }

  function resolvePolicyEvidenceLabel(token: string): string {
    const separatorIndex = token.indexOf("|");
    const code = separatorIndex >= 0 ? token.slice(0, separatorIndex) : token;
    const payload = separatorIndex >= 0 ? token.slice(separatorIndex + 1) : undefined;

    switch (code) {
      case "group_mention_required":
        return text.evidence.groupMentionRequired;
      case "blocked_without_mention":
        return text.evidence.blockedWithoutMention;
      case "approval_rejected":
        return text.evidence.rejectedByOperator(formatDateTime(payload, ""));
      case "pending_approval":
        return text.evidence.pendingApproval;
      case "draft_waiting":
        return text.evidence.draftWaiting;
      case "last_approval_reject":
        return text.evidence.lastApprovalReject(formatDateTime(payload, text.unavailable));
      case "last_no_action":
        return text.evidence.lastNoAction(formatDateTime(payload, text.unavailable));
      case "none":
        return text.evidence.noSpecialEvidence;
      default:
        return token;
    }
  }

  function resolvePolicyEvidence(detail: ChannelThreadDetail, audit: ChannelAuditEntry[]): string[] {
    if (detail.policyEvidence && detail.policyEvidence.length > 0) {
      return detail.policyEvidence.map(resolvePolicyEvidenceLabel);
    }

    return buildLegacyPolicyEvidence(detail, audit);
  }

  function trimText(value?: string | null, maxLength = 92): string {
    const normalized = (value ?? "").replace(/\s+/g, " ").trim();
    if (!normalized) {
      return text.noPreview;
    }

    if (normalized.length <= maxLength) {
      return normalized;
    }

    return `${normalized.slice(0, maxLength - 3)}...`;
  }

  function summarizeConnector(connector: ChannelConnectorDescriptor): string {
    const capabilityLabel = `${connector.supportsInbound ? text.connectorSummary.inbound : text.connectorSummary.noInbound} · ${connector.supportsOutbound ? text.connectorSummary.outbound : text.connectorSummary.noOutbound}`;
    return `${connector.implemented ? text.connectorSummary.implemented : text.connectorSummary.planned} · ${capabilityLabel}`;
  }

  function resolveAccountLabel(account: ChannelAccount): string {
    return `${account.displayName} · ${resolveAccountStateLabel(account.state)}`;
  }

  function resolveThreadState(thread: ChannelThreadSummary): string {
    if (thread.pendingApprovalId) {
      return text.pendingApproval(thread.pendingApprovalId);
    }

    if (thread.hasPendingDraft) {
      return text.pendingDraft;
    }

    return text.noPendingDraft;
  }

  async function loadDetail(bindingId: string | null) {
    const requestId = ++detailRequestRef.current;

    if (!bindingId) {
      setThreadDetail(null);
      setThreadAudit([]);
      setIsLoadingDetail(false);
      return;
    }

    setIsLoadingDetail(true);

    try {
      const [detail, audit] = await Promise.all([
        fetchChannelThreadDetail(bindingId),
        fetchChannelThreadAudit(bindingId, { limit: AUDIT_LIMIT }),
      ]);

      if (detailRequestRef.current !== requestId) {
        return;
      }

      setThreadDetail(detail);
      setThreadAudit(audit);
    } catch (nextError) {
      if (detailRequestRef.current !== requestId) {
        return;
      }

      setThreadDetail(null);
      setThreadAudit([]);
      setError(nextError instanceof Error ? nextError.message : text.loadDetailError);
    } finally {
      if (detailRequestRef.current === requestId) {
        setIsLoadingDetail(false);
      }
    }
  }

  async function loadDesk(mode: "initial" | "refresh") {
    const requestId = ++listRequestRef.current;

    if (mode === "initial") {
      setIsLoadingList(true);
    } else {
      setIsRefreshing(true);
    }

    setError(null);

    try {
      const [accountsPayload, threadsPayload] = await Promise.all([
        fetchChannelAccounts({
          connectorKind: connectorFilter === "all" ? undefined : connectorFilter,
          limit: THREAD_LIMIT,
        }),
        fetchChannelThreads({
          connectorKind: connectorFilter === "all" ? undefined : connectorFilter,
          accountId: accountFilter === "all" ? undefined : accountFilter,
          limit: THREAD_LIMIT,
        }),
      ]);

      if (listRequestRef.current !== requestId) {
        return;
      }

      setAccounts(accountsPayload);
      setThreads(threadsPayload.items);

      const preferredBindingId = threadsPayload.items.some((item) => item.bindingId === selectedBindingId)
        ? selectedBindingId
        : threadsPayload.items[0]?.bindingId ?? null;
      setSelectedBindingId(preferredBindingId);
      await loadDetail(preferredBindingId);
    } catch (nextError) {
      if (listRequestRef.current !== requestId) {
        return;
      }

      setAccounts([]);
      setThreads([]);
      setSelectedBindingId(null);
      setThreadDetail(null);
      setThreadAudit([]);
      setError(nextError instanceof Error ? nextError.message : text.loadDeskError);
    } finally {
      if (listRequestRef.current === requestId) {
        if (mode === "initial") {
          setIsLoadingList(false);
        } else {
          setIsRefreshing(false);
        }
      }
    }
  }

  useEffect(() => {
    void loadDesk("initial");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connectorFilter, accountFilter]);

  // Auto-refresh every 30 seconds
  useEffect(() => {
    const interval = setInterval(() => {
      void loadDesk("refresh");
    }, 30_000);
    return () => clearInterval(interval);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [connectorFilter, accountFilter]);

  async function handleRefresh() {
    await loadDesk("refresh");
  }

  function handleOpenAddForm() {
    setShowAddForm(true);
    setAddFormStep(1);
    setAddFormConnectorKind("Telegram");
    setAddFormDisplayName("");
    setAddFormBotToken("");
    setAddFormWebhookPath("");
    setAddFormFeishuAppId("");
    setAddFormFeishuAppSecret("");
    setAddFormFeishuTestResult(null);
    setAddFormDingTalkAppKey("");
    setAddFormDingTalkAppSecret("");
    setAddFormDingTalkRobotCode("");
    setAddFormRelayUrl("");
    setAddFormRelaySecret("");
    setAddFormDeliveryMode("RequireApproval");
    setAddFormTelegramTestResult(null);
    setAddFormTesting(false);
    setAddFormSaving(false);
    setAddFormError(null);
  }

  function handleCancelAddForm() {
    setShowAddForm(false);
  }

  async function handleTestTelegramToken() {
    if (!addFormBotToken.trim()) {
      return;
    }

    setAddFormTesting(true);
    setAddFormTelegramTestResult(null);
    setAddFormError(null);

    try {
      const result = await testTelegramToken(addFormBotToken.trim());
      if (result.ok) {
        setAddFormTelegramTestResult(`@${result.botUsername ?? "unknown"} · ${result.botName ?? ""}`);
        setAddFormStep(3);
      } else {
        setAddFormError(result.error ?? "Token verification failed.");
      }
    } catch (nextError) {
      setAddFormError(nextError instanceof Error ? nextError.message : "Token verification failed.");
    } finally {
      setAddFormTesting(false);
    }
  }

  async function handleTestFeishuCredentials() {
    if (!addFormFeishuAppId.trim() || !addFormFeishuAppSecret.trim()) {
      return;
    }

    setAddFormTesting(true);
    setAddFormFeishuTestResult(null);
    setAddFormError(null);

    try {
      const result = await testFeishuCredentials(addFormFeishuAppId.trim(), addFormFeishuAppSecret.trim());
      if (result.ok) {
        setAddFormFeishuTestResult(result.appName ?? addFormFeishuAppId.trim());
        setAddFormStep(3);
      } else {
        setAddFormError(result.error ?? "Feishu credentials verification failed.");
      }
    } catch (nextError) {
      setAddFormError(nextError instanceof Error ? nextError.message : "Feishu credentials verification failed.");
    } finally {
      setAddFormTesting(false);
    }
  }

  async function handleSaveChannelAccount() {
    setAddFormSaving(true);
    setAddFormError(null);

    try {
      const accountId = `account-${Date.now()}`;
      let configurationJson: string | undefined;

      if (addFormConnectorKind === "Telegram" && addFormBotToken.trim()) {
        configurationJson = JSON.stringify({ botToken: addFormBotToken.trim() });
      } else if (addFormConnectorKind === "GenericWebhook" && addFormWebhookPath.trim()) {
        configurationJson = JSON.stringify({ webhookPath: addFormWebhookPath.trim() });
      } else if (addFormConnectorKind === "Feishu" && addFormFeishuAppId.trim()) {
        configurationJson = JSON.stringify({
          appId: addFormFeishuAppId.trim(),
          appSecret: addFormFeishuAppSecret.trim(),
        });
      } else if (addFormConnectorKind === "DingTalk" && addFormDingTalkAppKey.trim()) {
        configurationJson = JSON.stringify({
          appKey: addFormDingTalkAppKey.trim(),
          appSecret: addFormDingTalkAppSecret.trim(),
          robotCode: addFormDingTalkRobotCode.trim(),
        });
      } else if (addFormConnectorKind === "Relay") {
        configurationJson = JSON.stringify({
          accountId: addFormRelayAccountId.trim() || undefined,
          relayUrl: addFormRelayUrl.trim(),
          sharedSecret: addFormRelaySecret.trim() || undefined,
        });
      }

      const defaultDisplayName =
        addFormConnectorKind === "Telegram" ? "Telegram Bot"
        : addFormConnectorKind === "Feishu" ? "飞书 Bot"
        : addFormConnectorKind === "DingTalk" ? "钉钉 Bot"
        : addFormConnectorKind === "Relay" ? "Relay"
        : "Webhook";

      const request: CreateChannelAccountRequest = {
        id: accountId,
        connectorKind: addFormConnectorKind,
        displayName: addFormDisplayName.trim() || defaultDisplayName,
        configurationJson,
        inboundEnabled: true,
      };

      await createChannelAccount(request);
      void queryClient.invalidateQueries({ queryKey: ['channelAccounts'] });
      void queryClient.invalidateQueries({ queryKey: queryKeys.channelConnectors });
      setShowAddForm(false);
      await loadDesk("refresh");
    } catch (nextError) {
      setAddFormError(nextError instanceof Error ? nextError.message : "Failed to create channel account.");
    } finally {
      setAddFormSaving(false);
    }
  }

  function handleOpenEdit(account: ChannelAccount) {
    let config: Record<string, string> = {};
    try {
      if (account.configurationJson) {
        config = JSON.parse(account.configurationJson) as Record<string, string>;
      }
    } catch { /* ignore */ }

    setEditingAccount(account);
    setEditParsedConfig(config);
    setEditSecretCleared({});
    setEditSecretNew({});
    setEditSaving(false);
    setEditError(null);

    if (account.connectorKind === 'Relay') {
      setEditRelayAccountId(config['accountId'] ?? '');
      setEditRelayUrl(config['relayUrl'] ?? '');
      setEditRelayNotifyChannelId(config['notifyChannelId'] ?? '');
    } else if (account.connectorKind === 'Feishu') {
      setEditFeishuAppId(config['appId'] ?? '');
    } else if (account.connectorKind === 'DingTalk') {
      setEditDingTalkAppKey(config['appKey'] ?? '');
      setEditDingTalkRobotCode(config['robotCode'] ?? '');
    } else if (account.connectorKind === 'GenericWebhook') {
      setEditWebhookPath(config['webhookPath'] ?? '');
    }
  }

  async function handleSaveEdit() {
    if (!editingAccount) return;
    setEditSaving(true);
    setEditError(null);

    const secretValue = (key: string): string | undefined => {
      if (editSecretCleared[key]) {
        return editSecretNew[key] ?? '';
      }
      return editParsedConfig[key] ?? undefined;
    };

    try {
      let config: Record<string, unknown> = {};

      if (editingAccount.connectorKind === 'Telegram') {
        config = { botToken: secretValue('botToken') };
      } else if (editingAccount.connectorKind === 'Feishu') {
        config = { appId: editFeishuAppId.trim(), appSecret: secretValue('appSecret') };
      } else if (editingAccount.connectorKind === 'DingTalk') {
        config = {
          appKey: editDingTalkAppKey.trim(),
          appSecret: secretValue('appSecret'),
          robotCode: editDingTalkRobotCode.trim(),
        };
      } else if (editingAccount.connectorKind === 'Relay') {
        config = {
          accountId: editRelayAccountId.trim() || undefined,
          relayUrl: editRelayUrl.trim(),
          sharedSecret: secretValue('sharedSecret') || undefined,
          notifyChannelId: editRelayNotifyChannelId.trim() || undefined,
        };
      } else if (editingAccount.connectorKind === 'GenericWebhook') {
        config = { webhookPath: editWebhookPath.trim() };
      }

      await updateChannelAccount(editingAccount.id, {
        configurationJson: JSON.stringify(config),
      });
      void queryClient.invalidateQueries({ queryKey: ['channelAccounts'] });
      setEditingAccount(null);
      await handleRefresh();
    } catch (nextError) {
      setEditError(nextError instanceof Error ? nextError.message : 'Failed to save configuration.');
    } finally {
      setEditSaving(false);
    }
  }

  async function handleDeleteAccount(accountId: string) {
    try {
      await deleteChannelAccount(accountId);
      void queryClient.invalidateQueries({ queryKey: ['channelAccounts'] });
      await loadDesk("refresh");
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : "Failed to delete channel account.");
    }
  }

  async function handleToggleAccount(account: ChannelAccount) {
    try {
      await updateChannelAccount(account.id, { enabled: !account.inboundEnabled });
      void queryClient.invalidateQueries({ queryKey: ['channelAccounts'] });
      await loadDesk("refresh");
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : "Failed to update channel account.");
    }
  }

  async function handleSelectThread(bindingId: string) {
    setSelectedBindingId(bindingId);
    setError(null);
    await loadDetail(bindingId);
  }

  async function handleDeliveryModeChange(bindingId: string, mode: DeliveryMode) {
    setUpdatingDelivery(true);
    setError(null);

    try {
      await updateThreadSettings(bindingId, { deliveryMode: mode });
      await loadDesk("refresh");
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : text.loadDetailError);
    } finally {
      setUpdatingDelivery(false);
    }
  }

  async function handleAccountDeliveryModeChange(accountId: string, mode: DeliveryMode) {
    setAccountDeliveryApplying(accountId);
    setError(null);
    try {
      await updateChannelAccount(accountId, { deliveryMode: mode });
      await loadDesk("refresh");
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : "Failed to update delivery mode.");
    } finally {
      setAccountDeliveryApplying(null);
    }
  }

  function parseAccountDefaultDeliveryMode(account: ChannelAccount): DeliveryMode | null {
    if (!account.configurationJson) return null;
    try {
      const config = JSON.parse(account.configurationJson) as Record<string, unknown>;
      const mode = config["defaultDeliveryMode"];
      if (mode === "AutoSend" || mode === "DraftApproval" || mode === "RequireApproval") return mode;
    } catch { /* */ }
    return null;
  }

  function parseAccountProgressIndicatorEnabled(account: ChannelAccount): boolean {
    if (!account.configurationJson) return false;
    try {
      const config = JSON.parse(account.configurationJson) as Record<string, unknown>;
      const indicator = config["progressIndicator"];
      if (indicator && typeof indicator === "object" && "enabled" in indicator) {
        return (indicator as { enabled?: unknown }).enabled === true;
      }
    } catch { /* */ }
    return false;
  }

  function supportsProgressIndicator(kind: ChannelConnectorKind): boolean {
    return kind === "Telegram" || kind === "Feishu";
  }

  async function handleProgressIndicatorToggle(account: ChannelAccount, next: boolean) {
    setProgressIndicatorApplying(account.id);
    setError(null);
    try {
      const current: Record<string, unknown> = account.configurationJson
        ? (JSON.parse(account.configurationJson) as Record<string, unknown>)
        : {};
      current["progressIndicator"] = { enabled: next };
      await updateChannelAccount(account.id, {
        configurationJson: JSON.stringify(current),
      });
      void queryClient.invalidateQueries({ queryKey: ['channelAccounts'] });
      await loadDesk("refresh");
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : "Failed to update progress indicator.");
    } finally {
      setProgressIndicatorApplying(null);
    }
  }

  function resolveConnectorColor(kind: ChannelConnectorKind): string {
    switch (kind) {
      case "WeChat": return "#07C160";
      case "Telegram": return "#0088CC";
      case "Feishu": return "#00B96B";
      case "DingTalk": return "#3296FA";
      case "Relay": return "#8B5CF6";
      default: return "var(--border-medium)";
    }
  }

  function resolveDeliveryModeBadgeClass(mode: DeliveryMode): string {
    switch (mode) {
      case "AutoSend": return "delivery-badge delivery-badge--auto";
      case "DraftApproval": return "delivery-badge delivery-badge--draft";
      case "RequireApproval": return "delivery-badge delivery-badge--require";
    }
  }

  return (
    <section className="" data-testid="channels-desk">
      <h2 className="desk-section-title">{text.title}</h2>
      <p className="desk-section-desc">{text.copy}</p>

      <div className="channels-desk__toolbar">
        <Button
          variant="ghost"
          size="control"
          data-testid="channels-refresh"
          disabled={isLoadingList || isRefreshing}
          onClick={() => {
            void handleRefresh();
          }}
        >
          {isRefreshing ? text.refreshing : text.refresh}
        </Button>
        {isRefreshing ? (
          <span className="composer__status" data-testid="channels-refresh-indicator">
            {text.refreshing}
          </span>
        ) : null}
        <Button
          variant="primary"
          size="control"
          data-testid="channel-add-btn"
          disabled={isLoadingList}
          onClick={handleOpenAddForm}
        >
          {text.addChannel}
        </Button>

        <label className="metric-label" htmlFor="channels-connector-filter">
          {text.filters.connector}
        </label>
        <Select
          id="channels-connector-filter"
          className="control-plane-filter channels-connector-select"
          data-testid="channels-connector-filter"
          value={connectorFilter}
          onChange={(event) => {
            setConnectorFilter(event.target.value as ConnectorFilter);
            setAccountFilter("all");
          }}
        >
          <option value="all">{text.allConnectors}</option>
          {connectors.map((connector) => (
            <option value={connector.kind} key={connector.kind}>
              {connector.displayName}
            </option>
          ))}
        </Select>

        <label className="metric-label" htmlFor="channels-account-filter">
          {text.filters.account}
        </label>
        <Select
          id="channels-account-filter"
          className="control-plane-filter channels-account-select"
          data-testid="channels-account-filter"
          value={accountFilter}
          onChange={(event) => setAccountFilter(event.target.value as AccountFilter)}
        >
          <option value="all">{text.allAccounts}</option>
          {visibleAccounts.map((account) => (
            <option value={account.id} key={account.id}>
              {resolveAccountLabel(account)}
            </option>
          ))}
        </Select>

        <span className="composer__status" data-testid="channels-summary">
          {isLoadingList ? text.loading : text.summary(accounts.length, threads.length)}
        </span>
      </div>

      {error ? (
        <p className="desk-feedback desk-feedback--error" data-testid="channels-error">
          {error}
        </p>
      ) : null}

      <div className="channels-desk__layout">
        <div className="channels-desk__left">
        <section className="timeline" data-testid="channels-connectors-accounts">
          <div className="timeline__header">
            <h3 className="desk-section-title">{text.connectorsTitle}</h3>
            <span className="composer__status">{text.connectorsCount(connectors.length)}</span>
          </div>
          <div className="timeline__body channels-desk__connectors-body">
            {connectors.map((connector) => (
              <div className="channel-connector-card" key={connector.kind}>
                <div className="channel-connector-card__name">{connector.displayName}</div>
                <div className="channel-connector-card__meta">{connector.kind} · {summarizeConnector(connector)}</div>
              </div>
            ))}

            {accounts.map((account) => (
              <div className="channel-account-card" key={account.id}>
                <div className="channel-account-card__header">
                  <div className="channel-account-card__title-row">
                    <span className="channel-account-card__kind">{account.connectorKind}</span>
                    <span className="channel-account-card__name">{account.displayName}</span>
                  </div>
                  <span className={`channel-account-card__state channel-account-card__state--${account.state}`}>
                    {resolveAccountStateLabel(account.state)}
                  </span>
                </div>
                <span className="metric-label">
                  {text.updatedAt(formatDateTime(account.updatedAt, text.unavailable))}
                </span>
                <div className="channel-account-card__delivery-row">
                  <label className="channel-account-card__delivery-label" htmlFor={`account-dm-${account.id}`}>
                    {text.accountDeliveryMode}
                  </label>
                  <Select
                    id={`account-dm-${account.id}`}
                    className="channel-account-card__delivery-select"
                    value={parseAccountDefaultDeliveryMode(account) ?? ""}
                    disabled={accountDeliveryApplying === account.id}
                    onChange={(e) => {
                      void handleAccountDeliveryModeChange(account.id, e.target.value as DeliveryMode);
                    }}
                  >
                    <option value="" disabled>{accountDeliveryApplying === account.id ? text.accountDeliveryApplying : "—"}</option>
                    <option value="AutoSend">{text.deliveryMode.AutoSend}</option>
                    <option value="DraftApproval">{text.deliveryMode.DraftApproval}</option>
                    <option value="RequireApproval">{text.deliveryMode.RequireApproval}</option>
                  </Select>
                  <span className="channel-account-card__delivery-hint">{text.accountDeliveryHint}</span>
                </div>
                {supportsProgressIndicator(account.connectorKind) ? (
                  <div className="channel-account-card__delivery-row" data-testid={`channel-account-progress-row-${account.id}`}>
                    <label className="channel-account-card__delivery-label" htmlFor={`account-pi-${account.id}`}>
                      {text.progressIndicatorLabel}
                    </label>
                    <input
                      id={`account-pi-${account.id}`}
                      type="checkbox"
                      data-testid={`channel-account-progress-toggle-${account.id}`}
                      checked={parseAccountProgressIndicatorEnabled(account)}
                      disabled={progressIndicatorApplying === account.id}
                      onChange={(e) => {
                        void handleProgressIndicatorToggle(account, e.target.checked);
                      }}
                    />
                    <span className="channel-account-card__delivery-hint">
                      {progressIndicatorApplying === account.id ? text.progressIndicatorApplying : text.progressIndicatorHint}
                    </span>
                  </div>
                ) : null}
                <div className="channel-account-card__actions">
                  <Button
                    variant={account.inboundEnabled ? "ghost" : "primary"}
                    size="sm"
                    data-testid={`channel-account-toggle-${account.id}`}
                    onClick={() => { void handleToggleAccount(account); }}
                  >
                    {account.inboundEnabled ? text.disable : text.enable}
                  </Button>
                  <Button
                    variant="ghost"
                    size="sm"
                    data-testid={`channel-account-edit-${account.id}`}
                    onClick={() => handleOpenEdit(account)}
                  >
                    {text.edit}
                  </Button>
                  <Button
                    variant="danger"
                    size="sm"
                    data-testid={`channel-account-delete-${account.id}`}
                    onClick={() => setDeleteConfirm({ id: account.id, name: account.displayName })}
                  >
                    {text.delete}
                  </Button>
                  {account.connectorKind === 'WeChat' && account.state === 'Degraded' && (
                    <Button
                      variant="ghost"
                      size="sm"
                      data-testid={`channel-account-wechat-rescan-${account.id}`}
                      onClick={() => setWeChatRescanId(weChatRescanId === account.id ? null : account.id)}
                    >
                      {text.weChatRescan}
                    </Button>
                  )}
                </div>
                {account.connectorKind === 'WeChat' && weChatRescanId === account.id && (
                  <div className="channel-wechat-rescan-panel">
                    <WeChatQrLoginPanel
                      onLoginSuccess={() => {
                        setWeChatRescanId(null);
                        void handleRefresh();
                      }}
                    />
                  </div>
                )}
              </div>
            ))}

            {isLoadingList ? <Skeleton height={52} count={3} /> : null}
            {!isLoadingList && connectors.length === 0 && accounts.length === 0 ? (
              <EmptyState icon={<Radio size={28} strokeWidth={1.5} />} title={text.emptyConnectors} />
            ) : null}

            {showAddForm ? (
              <div className="channel-add-form" data-testid="channel-config-form">
                <div className="channel-add-form__header">
                  <span className="channel-add-form__title">{text.addForm.title}</span>
                  <span className="channel-add-form__step">{text.addForm.stepLabel(addFormStep, 4)}</span>
                </div>

                {addFormStep === 1 ? (
                  <>
                    <label className="metric-label" htmlFor="channel-form-kind">
                      {text.addForm.connectorType}
                    </label>
                    <Select
                      id="channel-form-kind"
                      value={addFormConnectorKind}
                      onChange={(e) => setAddFormConnectorKind(e.target.value as ChannelConnectorKind)}
                    >
                      <option value="Telegram">Telegram</option>
                      <option value="Feishu">飞书 / Lark</option>
                      <option value="DingTalk">钉钉 / DingTalk</option>
                      <option value="Relay">Relay (WebSocket)</option>
                      <option value="GenericWebhook">Generic Webhook</option>
                    </Select>
                    <div className="channel-add-form__actions">
                      <Button variant="secondary" onClick={() => setAddFormStep(2)}>
                        {text.addForm.next}
                      </Button>
                      <Button variant="secondary" onClick={handleCancelAddForm}>
                        {text.addForm.cancel}
                      </Button>
                    </div>
                  </>
                ) : addFormStep === 2 ? (
                  <>
                    {addFormConnectorKind === "Telegram" ? (
                      <>
                        <label className="metric-label" htmlFor="channel-form-bot-token">
                          {text.addForm.telegramToken}
                        </label>
                        <input
                          id="channel-form-bot-token"
                          className="kc-input"
                          type="password"
                          value={addFormBotToken}
                          onChange={(e) => setAddFormBotToken(e.target.value)}
                          placeholder="123456789:ABC..."
                        />
                      </>
                    ) : addFormConnectorKind === "Feishu" ? (
                      <>
                        <label className="metric-label" htmlFor="channel-form-feishu-app-id">
                          {text.addForm.feishuAppId}
                        </label>
                        <input
                          id="channel-form-feishu-app-id"
                          className="kc-input"
                          type="text"
                          value={addFormFeishuAppId}
                          onChange={(e) => setAddFormFeishuAppId(e.target.value)}
                          placeholder="cli_xxxxxxxxxxxxxxxx"
                        />
                        <label className="metric-label" htmlFor="channel-form-feishu-app-secret">
                          {text.addForm.feishuAppSecret}
                        </label>
                        <input
                          id="channel-form-feishu-app-secret"
                          className="kc-input"
                          type="password"
                          value={addFormFeishuAppSecret}
                          onChange={(e) => setAddFormFeishuAppSecret(e.target.value)}
                          placeholder="App Secret"
                        />
                      </>
                    ) : addFormConnectorKind === "DingTalk" ? (
                      <>
                        <label className="metric-label" htmlFor="channel-form-dingtalk-app-key">
                          {text.addForm.dingTalkAppKey}
                        </label>
                        <input
                          id="channel-form-dingtalk-app-key"
                          className="kc-input"
                          type="text"
                          value={addFormDingTalkAppKey}
                          onChange={(e) => setAddFormDingTalkAppKey(e.target.value)}
                          placeholder="dingxxxxxxxxx"
                        />
                        <label className="metric-label" htmlFor="channel-form-dingtalk-app-secret">
                          {text.addForm.dingTalkAppSecret}
                        </label>
                        <input
                          id="channel-form-dingtalk-app-secret"
                          className="kc-input"
                          type="password"
                          value={addFormDingTalkAppSecret}
                          onChange={(e) => setAddFormDingTalkAppSecret(e.target.value)}
                          placeholder="App Secret"
                        />
                        <label className="metric-label" htmlFor="channel-form-dingtalk-robot-code">
                          {text.addForm.dingTalkRobotCode}
                        </label>
                        <input
                          id="channel-form-dingtalk-robot-code"
                          className="kc-input"
                          type="text"
                          value={addFormDingTalkRobotCode}
                          onChange={(e) => setAddFormDingTalkRobotCode(e.target.value)}
                          placeholder="dingxxxxxxxxx"
                        />
                      </>
                    ) : addFormConnectorKind === "Relay" ? (
                      <>
                        <label className="metric-label" htmlFor="channel-form-relay-account-id">
                          {text.addForm.relayAccountId}
                        </label>
                        <input
                          id="channel-form-relay-account-id"
                          className="kc-input"
                          type="text"
                          value={addFormRelayAccountId}
                          onChange={(e) => setAddFormRelayAccountId(e.target.value)}
                          placeholder="Account ID (from community relay instance)"
                        />
                        <label className="metric-label" htmlFor="channel-form-relay-url">
                          {text.addForm.relayUrl}
                        </label>
                        <input
                          id="channel-form-relay-url"
                          className="kc-input"
                          type="text"
                          value={addFormRelayUrl}
                          onChange={(e) => setAddFormRelayUrl(e.target.value)}
                          placeholder="wss://community.ai-koda.com/ws/relay"
                        />
                        <label className="metric-label" htmlFor="channel-form-relay-secret">
                          {text.addForm.relaySecret}
                        </label>
                        <input
                          id="channel-form-relay-secret"
                          className="kc-input"
                          type="password"
                          value={addFormRelaySecret}
                          onChange={(e) => setAddFormRelaySecret(e.target.value)}
                          placeholder="Shared Secret"
                        />
                      </>
                    ) : (
                      <>
                        <label className="metric-label" htmlFor="channel-form-webhook-path">
                          {text.addForm.webhookPath}
                        </label>
                        <input
                          id="channel-form-webhook-path"
                          className="kc-input"
                          type="text"
                          value={addFormWebhookPath}
                          onChange={(e) => setAddFormWebhookPath(e.target.value)}
                          placeholder="/my-webhook"
                        />
                      </>
                    )}
                    {addFormError ? (
                      <p className="desk-feedback desk-feedback--error">{addFormError}</p>
                    ) : null}
                    <div className="channel-add-form__actions">
                      {addFormConnectorKind === "Telegram" ? (
                        <Button
                          variant="secondary"
                          disabled={addFormTesting || !addFormBotToken.trim()}
                          onClick={() => { void handleTestTelegramToken(); }}
                        >
                          {addFormTesting ? text.addForm.verifying : text.addForm.verifyToken}
                        </Button>
                      ) : addFormConnectorKind === "Feishu" ? (
                        <Button
                          variant="secondary"
                          disabled={addFormTesting || !addFormFeishuAppId.trim() || !addFormFeishuAppSecret.trim()}
                          onClick={() => { void handleTestFeishuCredentials(); }}
                        >
                          {addFormTesting ? text.addForm.verifying : text.addForm.verifyCredentials}
                        </Button>
                      ) : (
                        <Button variant="secondary" onClick={() => setAddFormStep(3)}>
                          {text.addForm.next}
                        </Button>
                      )}
                      <Button variant="secondary" onClick={() => { setAddFormStep(1); setAddFormError(null); }}>
                        {text.addForm.back}
                      </Button>
                      <Button variant="secondary" onClick={handleCancelAddForm}>
                        {text.addForm.cancel}
                      </Button>
                    </div>
                    {addFormTelegramTestResult ? (
                      <span className="metric-value">{addFormTelegramTestResult}</span>
                    ) : addFormFeishuTestResult ? (
                      <span className="metric-value">{addFormFeishuTestResult}</span>
                    ) : null}
                  </>
                ) : addFormStep === 3 ? (
                  <>
                    <label className="metric-label" htmlFor="channel-form-delivery-rule">
                      {text.addForm.deliveryRule}
                    </label>
                    <Select
                      id="channel-form-delivery-rule"
                      data-testid="channel-delivery-rule-select"
                      value={addFormDeliveryMode}
                      onChange={(e) => setAddFormDeliveryMode(e.target.value as DeliveryMode)}
                    >
                      <option value="AutoSend">{text.deliveryMode.AutoSend}</option>
                      <option value="DraftApproval">{text.deliveryMode.DraftApproval}</option>
                      <option value="RequireApproval">{text.deliveryMode.RequireApproval}</option>
                    </Select>
                    <div className="channel-add-form__actions">
                      <Button variant="secondary" onClick={() => setAddFormStep(4)}>
                        {text.addForm.next}
                      </Button>
                      <Button variant="secondary" onClick={() => setAddFormStep(2)}>
                        {text.addForm.back}
                      </Button>
                      <Button variant="secondary" onClick={handleCancelAddForm}>
                        {text.addForm.cancel}
                      </Button>
                    </div>
                  </>
                ) : (
                  <>
                    <label className="metric-label" htmlFor="channel-form-display-name">
                      {text.addForm.displayName}
                    </label>
                    <input
                      id="channel-form-display-name"
                      className="kc-input"
                      type="text"
                      value={addFormDisplayName}
                      onChange={(e) => setAddFormDisplayName(e.target.value)}
                      placeholder={text.addForm.displayNamePlaceholder}
                    />
                    {addFormError ? (
                      <p className="desk-feedback desk-feedback--error">{addFormError}</p>
                    ) : null}
                    <div className="channel-add-form__actions">
                      <Button
                        variant="secondary"
                        disabled={addFormSaving}
                        onClick={() => { void handleSaveChannelAccount(); }}
                      >
                        {addFormSaving ? text.addForm.saving : text.addForm.save}
                      </Button>
                      <Button variant="secondary" onClick={() => setAddFormStep(3)}>
                        {text.addForm.back}
                      </Button>
                      <Button variant="secondary" onClick={handleCancelAddForm}>
                        {text.addForm.cancel}
                      </Button>
                    </div>
                  </>
                )}
              </div>
            ) : null}
          </div>
        </section>

        {/* Col 2: Thread list */}
        <section className="timeline" data-testid="channels-threads">
          <div className="timeline__header">
            <h3 className="desk-section-title">{text.threadTitle}</h3>
            <span className="composer__status">{threads.length}</span>
          </div>
          <div className="timeline__body channels-desk__thread-list">
            {threads.map((thread) => {
              const selected = selectedBindingId === thread.bindingId;
              return (
                <button
                  key={thread.bindingId}
                  type="button"
                  className={`channel-thread-card${selected ? " channel-thread-card--selected" : ""}`}
                  data-testid={`channel-thread-select-${thread.bindingId}`}
                  aria-pressed={selected}
                  style={{ "--platform-color": resolveConnectorColor(thread.connectorKind) } as React.CSSProperties}
                  onClick={() => { void handleSelectThread(thread.bindingId); }}
                >
                  <div className="channel-thread-card__topline">
                    <span className="channel-thread-card__kind">{thread.connectorKind}</span>
                    <span className={resolveDeliveryModeBadgeClass(thread.deliveryMode)}>
                      {resolveDeliveryModeLabel(thread.deliveryMode)}
                    </span>
                    <span className="channel-thread-card__spacer" />
                    {thread.lastInboundAt && (
                      <span className="channel-thread-card__time">
                        {formatDateTime(thread.lastInboundAt, "")}
                      </span>
                    )}
                    <button
                      type="button"
                      className="copy-binding-id-btn"
                      title={text.copyBindingId}
                      onClick={(e) => {
                        e.stopPropagation();
                        void navigator.clipboard.writeText(thread.bindingId);
                      }}
                    >
                      <Copy size={11} strokeWidth={2} />
                    </button>
                  </div>
                  <div className="channel-thread-card__title">{thread.displayTitle}</div>
                  {thread.lastMessagePreview && (
                    <div className="channel-thread-card__preview">{trimText(thread.lastMessagePreview)}</div>
                  )}
                  {(thread.pendingApprovalId || thread.hasPendingDraft) && (
                    <div className="channel-thread-card__pending">
                      <span className="channel-thread-card__pending-dot" />
                      {thread.pendingApprovalId ? text.pendingDraft : text.pendingDraft}
                    </div>
                  )}
                </button>
              );
            })}

            {!isLoadingList && threads.length === 0 ? (
              <div data-testid="channels-empty">
                <EmptyState icon={<Radio size={28} strokeWidth={1.5} />} title={text.emptyThreads} />
              </div>
            ) : null}
          </div>
        </section>
        </div>{/* /channels-desk__left */}

        {/* Col 2 (right): Thread detail */}
        <section className="status-card status-card--normal" data-testid="channel-thread-detail">
          {isLoadingDetail ? (
            <Skeleton height={52} count={3} />
          ) : threadDetail && selectedThread ? (
            <div className="channel-detail-body">
              <div className="metric-item">
                <span className="metric-label">{text.detail.binding}</span>
                <span className="metric-value metric-value--path">{threadDetail.binding.id}</span>
                <span className="metric-label">{text.detail.sessionKind}</span>
                <span className="metric-value">
                  {resolveSessionKindLabel(threadDetail.binding.sessionKind)}
                </span>
                <span className="metric-label">{text.detail.deliveryMode}</span>
                <Select
                  data-testid="channel-thread-delivery-mode-select"
                  value={threadDetail.deliveryRule.mode}
                  disabled={updatingDelivery}
                  onChange={(event) => {
                    void handleDeliveryModeChange(
                      threadDetail.binding.id,
                      event.target.value as DeliveryMode,
                    );
                  }}
                >
                  <option value="AutoSend">{text.deliveryMode.AutoSend}</option>
                  <option value="DraftApproval">{text.deliveryMode.DraftApproval}</option>
                  <option value="RequireApproval">{text.deliveryMode.RequireApproval}</option>
                </Select>
              </div>
              <div className="metric-item">
                <span className="metric-label">{text.detail.policy}</span>
                <span className="metric-value">
                  {text.policySummary(
                    threadDetail.policy.loadUserProfile ? text.on : text.off,
                    threadDetail.policy.loadLongTermMemory ? text.on : text.off,
                  )}
                </span>
                <span className="metric-label">{text.detail.replyGuard}</span>
                <span className="metric-value">
                  {text.replyGuardSummary(
                    threadDetail.policy.allowDirectReply ? text.on : text.off,
                    threadDetail.policy.requireExplicitMention ? text.yes : text.no,
                  )}
                </span>
                <span className="metric-label">{text.detail.pending}</span>
                <span className="metric-value">
                  {threadDetail.pendingApprovalId
                    ? text.detailPendingApproval(threadDetail.pendingApprovalId)
                    : threadDetail.hasPendingDraft
                      ? text.detailPendingDraft
                      : text.detailNoPending}
                </span>
                <span className="metric-label">{text.detail.lastOutcome}</span>
                <span className="metric-value">
                  {threadDetail.lastTurnOutcome
                    ? `${resolveOutcomeKindLabel(threadDetail.lastTurnOutcome.kind)} · ${threadDetail.lastTurnOutcome.summary}`
                    : text.noLastOutcome}
                </span>
                {threadDetail.lastTurnOutcome ? (
                  <>
                    <span className="metric-label">{text.detail.outcomeReason}</span>
                    <span className="metric-value">
                      {resolveOutcomeReasonLabel(threadDetail.lastTurnOutcome.reasonCode)}
                    </span>
                    <span className="metric-label">{text.detail.mentionSignal}</span>
                    <span className="metric-value">
                      {resolveMentionSignalLabel(threadDetail.lastTurnOutcome.hasExplicitMention)}
                    </span>
                  </>
                ) : null}
                {threadDetail.lastTurnOutcome?.replyText ? (
                  <>
                    <span className="metric-label">{text.detail.replyPreview}</span>
                    <span className="metric-value">{trimText(threadDetail.lastTurnOutcome.replyText, 140)}</span>
                  </>
                ) : null}
              </div>

              <div className="metric-item" data-testid="channel-thread-policy-evidence">
                <span className="metric-label">{text.detail.policyEvidence}</span>
                {resolvePolicyEvidence(threadDetail, threadAudit).map((item) => (
                  <span key={item} className="metric-value">
                    {item}
                  </span>
                ))}
              </div>

              <div className="metric-item" data-testid="channel-thread-audit">
                <span className="metric-label">{text.detail.recentAudit}</span>
                {threadAudit.length > 0 ? (
                  threadAudit.map((entry) => (
                    <span key={entry.id} className="metric-value">
                      {entry.eventType} · {entry.summary ?? text.unavailable} · {formatDateTime(entry.createdAt, text.unavailable)}
                    </span>
                  ))
                ) : (
                  <span className="metric-value">{text.detail.noAudit}</span>
                )}
              </div>
            </div>
          ) : (
            <EmptyState icon={<Radio size={28} strokeWidth={1.5} />} title={text.emptyDetail} />
          )}
        </section>
      </div>

      {editingAccount ? (
        <div className="channel-edit-modal-overlay" data-testid="channel-edit-modal">
          <div className="channel-add-form">
            <div className="channel-add-form__header">
              <span className="channel-add-form__title">{text.editForm.title}</span>
              <span className="channel-add-form__step">{editingAccount.connectorKind} · {editingAccount.displayName}</span>
            </div>

            {editingAccount.connectorKind === 'Telegram' ? (
              <>
                <label className="metric-label" htmlFor="channel-edit-bot-token">{text.editForm.telegramToken}</label>
                {editSecretCleared['botToken'] ? (
                  <input
                    id="channel-edit-bot-token"
                    className="kc-input"
                    type="password"
                    value={editSecretNew['botToken'] ?? ''}
                    onChange={(e) => setEditSecretNew((prev) => ({ ...prev, botToken: e.target.value }))}
                    placeholder="123456789:ABC..."
                  />
                ) : (
                  <div className="channel-edit-secret-row">
                    <input className="kc-input channel-edit-secret-masked" type="text" value="••••••" disabled />
                    <Button variant="ghost" size="sm" onClick={() => setEditSecretCleared((prev) => ({ ...prev, botToken: true }))}>
                      {text.editForm.clear}
                    </Button>
                  </div>
                )}
              </>
            ) : editingAccount.connectorKind === 'Feishu' ? (
              <>
                <label className="metric-label" htmlFor="channel-edit-feishu-app-id">{text.editForm.feishuAppId}</label>
                <input
                  id="channel-edit-feishu-app-id"
                  className="kc-input"
                  type="text"
                  value={editFeishuAppId}
                  onChange={(e) => setEditFeishuAppId(e.target.value)}
                  placeholder="cli_xxxxxxxxxxxxxxxx"
                />
                <label className="metric-label" htmlFor="channel-edit-feishu-app-secret">{text.editForm.feishuAppSecret}</label>
                {editSecretCleared['appSecret'] ? (
                  <input
                    id="channel-edit-feishu-app-secret"
                    className="kc-input"
                    type="password"
                    value={editSecretNew['appSecret'] ?? ''}
                    onChange={(e) => setEditSecretNew((prev) => ({ ...prev, appSecret: e.target.value }))}
                    placeholder="App Secret"
                  />
                ) : (
                  <div className="channel-edit-secret-row">
                    <input className="kc-input channel-edit-secret-masked" type="text" value="••••••" disabled />
                    <Button variant="ghost" size="sm" onClick={() => setEditSecretCleared((prev) => ({ ...prev, appSecret: true }))}>
                      {text.editForm.clear}
                    </Button>
                  </div>
                )}
              </>
            ) : editingAccount.connectorKind === 'DingTalk' ? (
              <>
                <label className="metric-label" htmlFor="channel-edit-dingtalk-app-key">{text.editForm.dingTalkAppKey}</label>
                <input
                  id="channel-edit-dingtalk-app-key"
                  className="kc-input"
                  type="text"
                  value={editDingTalkAppKey}
                  onChange={(e) => setEditDingTalkAppKey(e.target.value)}
                  placeholder="dingxxxxxxxxx"
                />
                <label className="metric-label" htmlFor="channel-edit-dingtalk-app-secret">{text.editForm.dingTalkAppSecret}</label>
                {editSecretCleared['appSecret'] ? (
                  <input
                    id="channel-edit-dingtalk-app-secret"
                    className="kc-input"
                    type="password"
                    value={editSecretNew['appSecret'] ?? ''}
                    onChange={(e) => setEditSecretNew((prev) => ({ ...prev, appSecret: e.target.value }))}
                    placeholder="App Secret"
                  />
                ) : (
                  <div className="channel-edit-secret-row">
                    <input className="kc-input channel-edit-secret-masked" type="text" value="••••••" disabled />
                    <Button variant="ghost" size="sm" onClick={() => setEditSecretCleared((prev) => ({ ...prev, appSecret: true }))}>
                      {text.editForm.clear}
                    </Button>
                  </div>
                )}
                <label className="metric-label" htmlFor="channel-edit-dingtalk-robot-code">{text.editForm.dingTalkRobotCode}</label>
                <input
                  id="channel-edit-dingtalk-robot-code"
                  className="kc-input"
                  type="text"
                  value={editDingTalkRobotCode}
                  onChange={(e) => setEditDingTalkRobotCode(e.target.value)}
                  placeholder="dingxxxxxxxxx"
                />
              </>
            ) : editingAccount.connectorKind === 'Relay' ? (
              <>
                <label className="metric-label" htmlFor="channel-edit-relay-account-id">{text.editForm.relayAccountId}</label>
                <input
                  id="channel-edit-relay-account-id"
                  className="kc-input"
                  type="text"
                  value={editRelayAccountId}
                  onChange={(e) => setEditRelayAccountId(e.target.value)}
                  placeholder="Account ID"
                />
                <label className="metric-label" htmlFor="channel-edit-relay-url">{text.editForm.relayUrl}</label>
                <input
                  id="channel-edit-relay-url"
                  className="kc-input"
                  type="text"
                  value={editRelayUrl}
                  onChange={(e) => setEditRelayUrl(e.target.value)}
                  placeholder="wss://..."
                />
                <label className="metric-label" htmlFor="channel-edit-relay-secret">{text.editForm.relaySecret}</label>
                {editSecretCleared['sharedSecret'] ? (
                  <input
                    id="channel-edit-relay-secret"
                    className="kc-input"
                    type="password"
                    value={editSecretNew['sharedSecret'] ?? ''}
                    onChange={(e) => setEditSecretNew((prev) => ({ ...prev, sharedSecret: e.target.value }))}
                    placeholder="Shared Secret"
                  />
                ) : (
                  <div className="channel-edit-secret-row">
                    <input className="kc-input channel-edit-secret-masked" type="text" value="••••••" disabled />
                    <Button variant="ghost" size="sm" onClick={() => setEditSecretCleared((prev) => ({ ...prev, sharedSecret: true }))}>
                      {text.editForm.clear}
                    </Button>
                  </div>
                )}
                <label className="metric-label" htmlFor="channel-edit-relay-notify">{text.editForm.relayNotifyChannel}</label>
                <select
                  id="channel-edit-relay-notify"
                  className="kc-input"
                  value={editRelayNotifyChannelId}
                  onChange={(e) =>
                    setEditRelayNotifyChannelId(e.target.value)}
                >
                  <option value="">{text.editForm.relayNotifyChannelNone}</option>
                  {accounts
                    .filter((a) => a.inboundEnabled && a.id !== editingAccount?.id)
                    .map((a) => (
                      <option key={a.id} value={a.id}>
                        {a.connectorKind} · {a.displayName}
                      </option>
                    ))}
                </select>
              </>
            ) : (
              <>
                <label className="metric-label" htmlFor="channel-edit-webhook-path">{text.editForm.webhookPath}</label>
                <input
                  id="channel-edit-webhook-path"
                  className="kc-input"
                  type="text"
                  value={editWebhookPath}
                  onChange={(e) => setEditWebhookPath(e.target.value)}
                  placeholder="/webhook/my-hook"
                />
              </>
            )}

            {editError ? (
              <p className="desk-feedback desk-feedback--error">{editError}</p>
            ) : null}
            <div className="channel-add-form__actions">
              <Button
                variant="secondary"
                disabled={editSaving}
                onClick={() => setEditingAccount(null)}
              >
                {text.editForm.cancel}
              </Button>
              <Button
                variant="secondary"
                disabled={editSaving}
                onClick={() => { void handleSaveEdit(); }}
              >
                {editSaving ? text.editForm.saving : text.editForm.save}
              </Button>
            </div>
          </div>
        </div>
      ) : null}

      <ConfirmModal
        open={deleteConfirm !== null}
        title="删除渠道账号"
        description={`确认删除渠道账号 "${deleteConfirm?.name}" 吗？关联的线程绑定将同时移除，此操作不可撤销。`}
        confirmLabel="删除"
        variant="danger"
        onConfirm={() => { if (deleteConfirm) void handleDeleteAccount(deleteConfirm.id); setDeleteConfirm(null); }}
        onCancel={() => setDeleteConfirm(null)}
      />
    </section>
  );
}
