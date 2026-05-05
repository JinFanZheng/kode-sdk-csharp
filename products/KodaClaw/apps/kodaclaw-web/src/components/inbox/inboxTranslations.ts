import type { ApprovalStatus, DeliveryMode, InboxItem, InboxItemStatus } from "../types/contracts";

export const INBOX_STATUS_OPTIONS: Array<InboxItemStatus> = [
  "Open",
  "Acknowledged",
  "Resolved",
  "Archived",
];

export type InboxStatusFilter = InboxItemStatus | "all";
export type MediaAttachmentRef = { mediaId: string; contentType: string };

export type ChannelDeliveryPayload = {
  draftId: string;
  bindingId: string;
  connectorKind: string;
  accountId: string;
  externalThreadId: string;
  deliveryMode: DeliveryMode;
  messageText: string;
  mediaAttachments?: MediaAttachmentRef[];
};

export const inboxTranslations = {
  zh: {
    common: {
      none: "暂无",
      loading: "加载中...",
      all: "全部",
      items: (count: number) => `${count} 项`,
    },
    title: "收件箱",
    intro: "查看 Agent 发来的通知、审批请求和自动化结果。",
    refresh: "刷新",
    refreshing: "正在刷新...",
    statusFilter: "状态",
    kindTabs: { all: "全部", approvals: "审批", automations: "自动化结果" },
    emptyInbox: "当前筛选下没有收件。",
    emptyDetail: "从左侧选择一条收件记录查看详情。",
    errorTitle: "收件加载失败",
    actionRequired: "需要动作",
    notePlaceholder: "补充审批备注（可选）",
    approve: "批准",
    reject: "拒绝",
    status: "状态",
    kind: "类型",
    source: "来源",
    route: "路由",
    session: "会话",
    correlation: "关联 ID",
    lastUpdated: "最近更新",
    linkedApproval: "关联审批",
    decisionNote: "决策备注",
    deliveryContext: "渠道投递上下文",
    connector: "连接器",
    account: "账号",
    threadBinding: "线程绑定",
    thread: "线程",
    draft: "草稿",
    messagePreview: "消息预览",
    threadDetailApi: "线程详情 API",
    threadAuditApi: "线程审计 API",
    deliveryModeLabels: {
      AutoSend: "自动发送",
      DraftApproval: "草稿审批",
      RequireApproval: "需审批后发送",
    },
    kindLabels: {
      Approval: "审批",
      AutomationResult: "自动化结果",
      PluginRequest: "插件请求",
      ChannelUpdate: "渠道更新",
      Alert: "告警",
      TaskResult: "任务结果",
      Information: "信息",
      OutboundMessage: "对外消息",
      OutboundEmail: "对外邮件",
      PluginAuthorization: "插件授权",
      ExternalAction: "外部动作",
      ChannelDelivery: "渠道投递",
      AutomationAction: "自动化动作",
    },
    inboxStatusLabels: {
      Open: "待处理",
      Acknowledged: "已知悉",
      Resolved: "已解决",
      Archived: "已归档",
    },
    approvalStatusLabels: {
      Pending: "待审批",
      Approved: "已批准",
      Rejected: "已拒绝",
      Canceled: "已取消",
    },
    markRead: "标为已读",
    markAllRead: "全部已读",
    markingAllRead: "标记中...",
    errors: {
      loadFailed: "加载收件失败。",
      decisionFailed: "提交审批决策失败。",
      updateFailed: "更新收件状态失败。",
    },
    pushToChannel: "推送到渠道",
    pushing: "推送中...",
    pushFailed: "推送失败",
  },
  en: {
    common: {
      none: "n/a",
      loading: "Loading...",
      all: "All",
      items: (count: number) => `${count} items`,
    },
    title: "Inbox",
    intro: "View notifications, approval requests, and automation results from the Agent.",
    refresh: "Refresh",
    refreshing: "Refreshing...",
    statusFilter: "Status",
    kindTabs: { all: "All", approvals: "Approvals", automations: "Automation Results" },
    emptyInbox: "No inbox items match the current filter.",
    emptyDetail: "Select an inbox item to view details.",
    errorTitle: "Inbox failed to load",
    actionRequired: "Action required",
    notePlaceholder: "Decision note (optional)",
    approve: "Approve",
    reject: "Reject",
    status: "Status",
    kind: "Kind",
    source: "Source",
    route: "Route",
    session: "Session",
    correlation: "Correlation",
    lastUpdated: "Last updated",
    linkedApproval: "Linked approval",
    decisionNote: "Decision note",
    deliveryContext: "Channel delivery context",
    connector: "Connector",
    account: "Account",
    threadBinding: "Thread binding",
    thread: "Thread",
    draft: "Draft",
    messagePreview: "Message preview",
    threadDetailApi: "Thread detail API",
    threadAuditApi: "Thread audit API",
    deliveryModeLabels: {
      AutoSend: "Auto send",
      DraftApproval: "Draft approval",
      RequireApproval: "Require approval",
    },
    kindLabels: {
      Approval: "Approval",
      AutomationResult: "Automation result",
      PluginRequest: "Plugin request",
      ChannelUpdate: "Channel update",
      Alert: "Alert",
      TaskResult: "Task result",
      Information: "Information",
      OutboundMessage: "Outbound message",
      OutboundEmail: "Outbound email",
      PluginAuthorization: "Plugin authorization",
      ExternalAction: "External action",
      ChannelDelivery: "Channel delivery",
      AutomationAction: "Automation action",
    },
    inboxStatusLabels: {
      Open: "Open",
      Acknowledged: "Acknowledged",
      Resolved: "Resolved",
      Archived: "Archived",
    },
    approvalStatusLabels: {
      Pending: "Pending",
      Approved: "Approved",
      Rejected: "Rejected",
      Canceled: "Canceled",
    },
    markRead: "Mark as read",
    markAllRead: "Mark all read",
    markingAllRead: "Marking...",
    errors: {
      loadFailed: "Failed to load inbox.",
      decisionFailed: "Failed to submit approval decision.",
      updateFailed: "Failed to update inbox status.",
    },
    pushToChannel: "Push to channel",
    pushing: "Pushing...",
    pushFailed: "Push failed",
  },
};

export type InboxText = typeof inboxTranslations.en;

export function formatInboxKind(
  kind: InboxItem["kind"] | string,
  text: InboxText,
): string {
  return (text.kindLabels as Record<string, string>)[kind] ?? kind;
}

export function formatInboxStatus(status: InboxItemStatus, text: InboxText): string {
  return text.inboxStatusLabels[status] ?? status;
}

export function formatApprovalStatus(status: ApprovalStatus, text: InboxText): string {
  return text.approvalStatusLabels[status] ?? status;
}

export function formatDeliveryMode(mode: DeliveryMode, text: InboxText): string {
  return text.deliveryModeLabels[mode] ?? mode;
}
