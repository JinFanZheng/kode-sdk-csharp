export type GatewayMode = "Bootstrap" | "Normal";
export type ThemeMode = "System" | "Light" | "Dark";

export type InboxItemKind =
  | "Approval"
  | "AutomationResult"
  | "PluginRequest"
  | "ChannelUpdate"
  | "Alert"
  | "TaskResult"
  | "Information";

export type InboxItemStatus = "Open" | "Acknowledged" | "Resolved" | "Archived";

export type ApprovalKind =
  | "OutboundMessage"
  | "OutboundEmail"
  | "PluginAuthorization"
  | "ChannelDelivery"
  | "AutomationAction"
  | "ExternalAction";

export type ApprovalStatus = "Pending" | "Approved" | "Rejected" | "Canceled";
export type AutomationDefinitionSource = "Heartbeat" | "Manual";
export type AutomationRunStatus = "Queued" | "Running" | "Succeeded" | "Failed" | "Canceled";
export type CanvasArtifactKind =
  | "Report"
  | "Dashboard"
  | "Board"
  | "TaskList"
  | "PluginPanel"
  | "Html"
  | "Image";

export type SessionKind = "Main" | "ChannelDirectMessage" | "ChannelGroup" | "Automation" | "Plugin";

export type ModelProviderKind =
  | "OpenAI"
  | "Anthropic"
  | "OpenAICompatible"
  | "AnthropicCompatible"
  | "OpenAIResponses";

export type PluginType = "Tool" | "Channel" | "Memory" | "Ui";
export type PluginTransportKind = "Stdio" | "Http" | "StreamableHttp" | "Sse";
export type PluginInstallSource = "Bundled" | "LocalDirectory";
export type PluginTrustState = "Untrusted" | "Trusted" | "Signed";
export type PluginTrustEvidenceSource = "LocalDigest" | "SignatureSidecar";
export type PluginTrustVerificationState = "DigestOnly" | "Verified" | "Mismatch" | "Invalid";
export type PluginRuntimeState = "Stopped" | "Starting" | "Running" | "Degraded";
export type ChannelConnectorKind = "Telegram" | "GenericWebhook" | "Feishu" | "WeChat" | "DingTalk" | "Relay";
export type ChannelAccountState = "Disconnected" | "Connecting" | "Connected" | "Degraded";
export type ChannelThreadType = "DirectMessage" | "Group";
export type ChannelTurnOutcomeKind = "NoAction" | "DraftCreated" | "ApprovalRequested" | "Delivered" | "Failed";
export type ChannelEventType =
  | "MessageReceived"
  | "MessageEdited"
  | "MessageDeleted"
  | "ReactionReceived"
  | "AccountConnected"
  | "AccountDisconnected"
  | "DeliveryFailed";
export type DeliveryMode = "AutoSend" | "DraftApproval" | "RequireApproval";
export type SecretMigrationState = "Migrated" | "LegacyFallback" | "Missing";
export type RepairChecklistSeverity = "Info" | "Warning" | "ActionRequired" | "Blocking";
export type RepairChecklistState = "Pending" | "Completed";
export type UpdateAvailability = "Unknown" | "UpToDate" | "UpdateAvailable" | "CheckFailed";
export type UpdateReleaseChannel = "Stable" | "Preview" | "Nightly" | "Custom";

export interface ErrorResponse {
  code: string;
  message: string;
  requestId?: string | null;
}

export interface GatewayHealthResponse {
  name: string;
  status: string;
  mode: GatewayMode;
}

export interface BootstrapStateResponse {
  workspaceRootPath: string;
  workspaceVersion: number;
  workspaceInitialized: boolean;
  requiresBootstrap: boolean;
  activeMainSessionId: string | null;
  mode: GatewayMode;
}

export interface SecretMigrationSummary {
  totalCount: number;
  migratedCount: number;
  legacyFallbackCount: number;
  missingCount: number;
}

export interface SecretMigrationItem {
  id: string;
  kind: string;
  displayName: string;
  state: SecretMigrationState;
  location: string;
  field: string;
  configuredSecretRef?: string | null;
  secretRefExists: boolean;
  legacySource?: string | null;
  legacySourceAvailable: boolean;
  notes?: string | null;
}

export interface SecretMigrationReport {
  generatedAt: string;
  workspaceRootPath: string;
  artifactPath: string;
  summary: SecretMigrationSummary;
  items: SecretMigrationItem[];
}

export interface StartupRepairReportResponse {
  generatedAt: string;
  workspaceRootPath: string;
  reportPath: string;
  checklist: RepairChecklist;
}

export interface UpdateCheckRequest {
  desktopCurrentVersion?: string | null;
  desktopReleaseChannel?: UpdateReleaseChannel | null;
}

export interface UpdateComponentState {
  component: string;
  displayName: string;
  currentVersion: string;
  releaseChannel: UpdateReleaseChannel;
  lastCheckedAt?: string | null;
  latestKnownVersion?: string | null;
  updateAvailability: UpdateAvailability;
  downloadUrl?: string | null;
  releaseNotesUrl?: string | null;
  releaseNotes: string[];
  guidance: string;
}

export interface UpdateStateResponse {
  generatedAt: string;
  artifactPath: string;
  manifestSource: string;
  components: UpdateComponentState[];
  operatorNotes: string[];
}

export interface DiagnosticBundleDesktopContext {
  desktopMode: boolean;
  platform: string;
  appVersion: string;
  releaseChannel: UpdateReleaseChannel;
  gatewayLifecycleMode?: string | null;
}

export interface DiagnosticBundleExportRequest {
  sessionId?: string | null;
  timelineLimit?: number;
  archivePath?: string | null;
  desktopContext?: DiagnosticBundleDesktopContext | null;
}

export interface DiagnosticBundleRedactionSummary {
  includesRawSecrets: boolean;
  includesMessageBodies: boolean;
  appliedRules: string[];
  notes: string[];
}

export interface DiagnosticBundleManifestEntry {
  path: string;
  sha256: string;
  sizeBytes: number;
  category: string;
}

export interface DiagnosticBundleManifest {
  product: string;
  formatVersion: number;
  generatedAt: string;
  archiveName: string;
  sourceWorkspaceRoot: string;
  requestedSessionId?: string | null;
  desktopContext?: DiagnosticBundleDesktopContext | null;
  redactionSummary: DiagnosticBundleRedactionSummary;
  entries: DiagnosticBundleManifestEntry[];
  includes: string[];
  excludes: string[];
  notes: string[];
}

export interface DiagnosticBundleExportResponse {
  generatedAt: string;
  workspaceRootPath: string;
  bundlePath: string;
  manifest: DiagnosticBundleManifest;
}

export interface BackupDeviceIdentitySnapshot {
  deviceId?: string | null;
  fingerprintHash?: string | null;
  appVersion?: string | null;
  lastSeenAtUtc?: string | null;
}

export interface BackupManifestEntry {
  path: string;
  sha256: string;
  sizeBytes: number;
  category: string;
}

export interface BackupManifest {
  product: string;
  formatVersion: number;
  workspaceVersion: number;
  generatedAt: string;
  archiveName: string;
  sourceWorkspaceRoot: string;
  sourceDevice?: BackupDeviceIdentitySnapshot | null;
  entries: BackupManifestEntry[];
  includes: string[];
  excludes: string[];
}

export interface BackupExportRequest {
  archivePath?: string | null;
}

export interface BackupExportResponse {
  generatedAt: string;
  workspaceRootPath: string;
  archivePath: string;
  manifest: BackupManifest;
}

export interface RepairChecklistItem {
  id: string;
  severity: RepairChecklistSeverity;
  state: RepairChecklistState;
  category: string;
  title: string;
  summary: string;
  resource?: string | null;
  action?: string | null;
  evidence?: string | null;
}

export interface RepairChecklistSummary {
  totalCount: number;
  blockingCount: number;
  actionRequiredCount: number;
  warningCount: number;
  infoCount: number;
}

export interface RepairChecklist {
  generatedAt: string;
  scope: string;
  summary: RepairChecklistSummary;
  items: RepairChecklistItem[];
}

export interface BackupImportPreflightRequest {
  archivePath: string;
}

export interface BackupImportPreflightResponse {
  evaluatedAt: string;
  workspaceRootPath: string;
  archivePath: string;
  manifest: BackupManifest;
  checksumVerified: boolean;
  canImport: boolean;
  checklist: RepairChecklist;
}

export interface BackupImportRequest {
  archivePath: string;
}

export interface BackupImportResponse {
  importedAt: string;
  workspaceRootPath: string;
  archivePath: string;
  repairReportPath: string;
  manifest: BackupManifest;
  checklist: RepairChecklist;
  restoredPaths: string[];
  skippedPaths: string[];
}

export interface BootstrapCompletionRequest {
  identityMarkdown: string;
  soulMarkdown: string;
  userMarkdown: string;
  archiveBootstrapFile?: boolean;
}

export interface BootstrapDraftMessage {
  role: "user" | "assistant" | "system";
  text: string;
}

export interface BootstrapDraftRequest {
  conversation?: BootstrapDraftMessage[] | null;
  identityMarkdown?: string | null;
  soulMarkdown?: string | null;
  userMarkdown?: string | null;
}

export interface BootstrapDraftResult {
  identityMarkdown: string;
  soulMarkdown: string;
  userMarkdown: string;
  summary: string;
}

export interface BootstrapCompletionResult {
  workspaceRootPath: string;
  bootstrapCompleted: boolean;
  identityFilePath: string;
  soulFilePath: string;
  userFilePath: string;
  bootstrapFileArchived: boolean;
}

export interface ChatStreamRequest {
  message: string;
  sessionId?: string | null;
  mediaIds?: string[] | null;
}

export interface MediaMeta {
  id: string;
  fileName: string;
  contentType: string;
  sizeBytes: number;
  storedAt: string;
}

export interface ChatStreamEvent {
  type: "text_chunk" | "done" | "error" | "tool_warning" | "model_retrying" | "approval_required" | "approval_decided" | "tool_activity" | "agent_working" | "session_rotated" | "subagent_start" | "subagent_working" | "subagent_tool_done";
  sessionId: string;
  step?: number | null;
  sequence?: number | null;
  timestamp?: number | null;
  delta?: string | null;
  reason?: string | null;
  error?: ErrorResponse | null;
  // Approval event fields
  approvalId?: string | null;
  callId?: string | null;
  toolName?: string | null;
  inputPreview?: string | null;
  decision?: string | null;
  // Tool activity fields
  durationMs?: number | null;
  // Sub-agent progress fields (subagent_start / subagent_working / subagent_tool_done)
  subAgentId?: string | null;
  label?: string | null;
  subAgentToolName?: string | null;
}

export interface InboxItem {
  id: string;
  kind: InboxItemKind;
  status: InboxItemStatus;
  title: string;
  summary: string;
  source: string;
  createdAt: string;
  updatedAt: string;
  requiresAction?: boolean;
  route?: string | null;
  sessionId?: string | null;
  correlationId?: string | null;
  approvalId?: string | null;
  payloadJson?: string | null;
  resolvedAt?: string | null;
}

export interface InboxQueryResponse {
  items: InboxItem[];
}

export interface InboxStatusUpdateRequest {
  status: InboxItemStatus;
}

export interface ApprovalDecisionRequest {
  note?: string | null;
}

export interface Approval {
  id: string;
  kind: ApprovalKind;
  status: ApprovalStatus;
  title: string;
  summary: string;
  source: string;
  requestedAt: string;
  updatedAt: string;
  sessionId?: string | null;
  correlationId?: string | null;
  inboxItemId?: string | null;
  payloadJson?: string | null;
  decidedAt?: string | null;
  decidedBy?: string | null;
  decisionNote?: string | null;
}

export interface ApprovalQueryResponse {
  items: Approval[];
}

export interface AutomationDefinition {
  id: string;
  title: string;
  prompt: string;
  source: AutomationDefinitionSource;
  sourcePath?: string | null;
  cronExpression: string;
  enabled: boolean;
  inputPaths?: string[] | null;
  modelId?: string | null;
  notificationChannels?: string[] | null;
  notifyMode?: "None" | "Auto" | "Approval";
  createdAt: string;
  updatedAt: string;
  lastRunAt?: string | null;
  nextRunAt?: string | null;
  lastRunStatus?: AutomationRunStatus | null;
  lastError?: string | null;
}

export interface ChannelPushResult {
  bindingId: string;
  ok: boolean;
  errorMessage?: string | null;
  sentAt?: string | null;
}

export interface PushToChannelResponse {
  results: ChannelPushResult[];
}

export interface AutomationDefinitionsQueryResponse {
  items: AutomationDefinition[];
}

export interface AutomationRunRecord {
  runId: string;
  automationId: string;
  status: AutomationRunStatus;
  trigger: string;
  attempt: number;
  sessionId?: string | null;
  startedAt: string;
  completedAt?: string | null;
  summary?: string | null;
  errorMessage?: string | null;
}

export interface AutomationRunsQueryResponse {
  items: AutomationRunRecord[];
}

export interface UpdateAutomationDefinitionRequest {
  enabled: boolean;
}

export interface UpdateThreadSettingsRequest {
  deliveryMode?: DeliveryMode | null;
}

export interface SessionStatusSummary {
  isActiveMainSession: boolean;
  breakpointState?: string | null;
  messageCount: number;
  pendingApprovalCount: number;
}

export interface SessionSummary {
  sessionId: string;
  sessionKind: SessionKind;
  status: SessionStatusSummary;
  createdAt?: string | null;
  lastEventAt?: string | null;
  title?: string | null;
}

export interface SessionDetail {
  sessionId: string;
  sessionKind: SessionKind;
  status: SessionStatusSummary;
  createdAt?: string | null;
  lastEventAt?: string | null;
  userMessageCount: number;
  assistantMessageCount: number;
  toolCallCount: number;
  lastSfpIndex: number;
  pendingApprovalCallIds: string[];
  promptReport?: PromptReport | null;
  promptReportDelta?: PromptReportDelta | null;
  recentPromptReports?: PromptReport[] | null;
  title?: string | null;
  accountModelId?: string | null;
  accountModelName?: string | null;
  modelCapabilities?: number;
}

export interface PromptReport {
  profileId: string;
  systemPrompt: string;
  characterCount: number;
  loadedContextFiles: string[];
  generatedAt: string;
  characterBudget?: number | null;
  remainingCharacterBudget?: number | null;
  wasTruncated: boolean;
  truncatedContextFiles?: string[] | null;
  truncationNotes?: string[] | null;
}

export interface PromptReportDelta {
  previousGeneratedAt?: string | null;
  characterCountDelta: number;
  truncationStateChanged: boolean;
  addedContextFiles: string[];
  removedContextFiles: string[];
}

export interface SessionsQueryResponse {
  sessions: SessionSummary[];
}

export interface RotateSessionResponse {
  ok: boolean;
  previousSessionId?: string | null;
}

export interface ResumeSessionResponse {
  ok: boolean;
  resumedSessionId: string;
}

export interface DiagnosticEvent {
  id: string;
  source: string;
  eventType: string;
  level: string;
  message: string;
  timestamp: string;
  correlationId?: string | null;
  sessionId?: string | null;
  attributes?: Record<string, string | null> | null;
}

export interface DiagnosticsQueryResponse {
  events: DiagnosticEvent[];
}

export interface DiagnosticsSourceStats {
  source: string;
  count: number;
  errorCount: number;
  warningCount: number;
}

export interface DiagnosticsStatsResponse {
  totalEvents: number;
  errorCount: number;
  warningCount: number;
  bySource: DiagnosticsSourceStats[];
  oldestEvent: string | null;
  newestEvent: string | null;
}

export interface CanvasArtifact {
  id: string;
  title: string;
  kind: CanvasArtifactKind;
  summary: string;
  source: string;
  route?: string | null;
  entryPath: string;
  assetDirectory: string;
  sessionId?: string | null;
  correlationId?: string | null;
  createdAt: string;
  updatedAt: string;
  metadataJson?: string | null;
  contentText?: string | null;
}

export interface CanvasQueryResponse {
  items: CanvasArtifact[];
  defaultEntryPath: string;
  defaultArtifactId?: string | null;
}

export interface CanvasEntryResponse {
  entryUrl: string;
  entryPath: string;
  artifactId?: string | null;
  route?: string | null;
  title?: string | null;
}

export interface UpsertCanvasArtifactRequest {
  id: string;
  title: string;
  kind: CanvasArtifactKind;
  summary: string;
  source: string;
  route?: string | null;
  entryPath: string;
  assetDirectory: string;
  sessionId?: string | null;
  correlationId?: string | null;
  metadataJson?: string | null;
}

export interface ModelPricing {
  inputTokenPrice?: number | null;
  outputTokenPrice?: number | null;
  currency: string;
  pricingModel: string;
  pricingNote?: string | null;
}

export interface AccountModelResponse {
  id: string;
  accountId: string;
  displayName: string;
  modelId: string;
  capabilities: number;
  isDefaultForAccount: boolean;
  isGlobalDefault: boolean;
  enabled: boolean;
  contextWindowSize: number;
  maxOutputTokens: number;
  isReasoning: boolean;
  supportsToolCalling: boolean;
  pricing?: ModelPricing | null;
}

export interface ProviderAccountResponse {
  id: string;
  displayName: string;
  providerKind: ModelProviderKind;
  baseUrl?: string | null;
  accessMode?: string | null;
  enabled: boolean;
  hasApiKey: boolean;
  createdAt: string;
  updatedAt: string;
  models: AccountModelResponse[];
  /** Non-sensitive account fields exposed for the "edit endpoint" modal. */
  apiKeyEnvironmentVariable?: string | null;
  customHeaders?: Record<string, string> | null;
}

/** @deprecated Use ProviderAccountResponse + AccountModelResponse instead */
export type ModelEndpoint = AccountModelResponse;

export interface PluginRuntimeSpec {
  transport: PluginTransportKind;
  command?: string | null;
  args?: string[] | null;
  environment?: Record<string, string> | null;
  url?: string | null;
  headers?: Record<string, string> | null;
  environmentReferences?: Record<string, string> | null;
  headerReferences?: Record<string, string> | null;
}

export interface PluginPermissionSet {
  filesystem?: string[] | null;
  network: boolean;
  notifications: boolean;
  background: boolean;
  channels?: string[] | null;
  uiPanels?: string[] | null;
  secrets?: string[] | null;
}

export interface PluginCapabilitySet {
  tools?: string[] | null;
  channels?: string[] | null;
  uiPanels?: string[] | null;
  memoryProviders?: string[] | null;
}

export interface PluginDisplaySpec {
  description?: string | null;
  icon?: string | null;
  accentColor?: string | null;
}

export interface PluginHealthcheckSpec {
  toolName?: string | null;
  intervalSeconds?: number | null;
  timeoutSeconds?: number | null;
}

export interface PluginManifest {
  id: string;
  name: string;
  version: string;
  types: PluginType[];
  runtime: PluginRuntimeSpec;
  permissions: PluginPermissionSet;
  capabilities: PluginCapabilitySet;
  display?: PluginDisplaySpec | null;
  healthcheck?: PluginHealthcheckSpec | null;
  configSchema?: unknown;
}

export interface PluginRecord {
  id: string;
  manifest: PluginManifest;
  installSource: PluginInstallSource;
  rootPath: string;
  trustState: PluginTrustState;
  enabled: boolean;
  runtimeState: PluginRuntimeState;
  discoveredAt: string;
  installedAt: string;
  updatedAt: string;
  lastStartedAt?: string | null;
  lastStoppedAt?: string | null;
  lastHealthAt?: string | null;
  restartCount: number;
  lastError?: string | null;
  trustEvidence?: PluginTrustEvidence | null;
}

export interface PluginSummary {
  id: string;
  name: string;
  version: string;
  types: PluginType[];
  installSource: PluginInstallSource;
  trustState: PluginTrustState;
  enabled: boolean;
  runtimeState: PluginRuntimeState;
  rootPath: string;
  updatedAt: string;
  lastError?: string | null;
}

export interface PluginsQueryResponse {
  items: PluginSummary[];
}

export interface PluginPermissionRiskSummary {
  highRiskReasons: string[];
  mediumRiskReasons: string[];
  hasHighRisk: boolean;
}

export interface PluginTrustEvidence {
  source: PluginTrustEvidenceSource;
  verificationState: PluginTrustVerificationState;
  summary: string;
  verifiedAt: string;
  manifestDigestSha256?: string | null;
  packageDigestSha256?: string | null;
  signer?: string | null;
  signatureFilePath?: string | null;
}

export interface PluginHealthSummary {
  status: string;
  message?: string | null;
  lastHealthAt?: string | null;
  restartCount: number;
  isHealthy: boolean;
}

export interface PluginDetail {
  record: PluginRecord;
  permissionSummary: PluginPermissionRiskSummary;
  healthSummary: PluginHealthSummary;
  availableTools: string[];
}

export interface PluginLogEntry {
  entryId: string;
  pluginId: string;
  level: string;
  source: string;
  message: string;
  timestamp: string;
  payloadJson?: string | null;
}

export interface InstallLocalPluginRequest {
  path: string;
}

export interface PluginStateUpdateRequest {
  note?: string | null;
}

export interface ChannelConnectorDescriptor {
  kind: ChannelConnectorKind;
  displayName: string;
  implemented: boolean;
  supportsInbound: boolean;
  supportsOutbound: boolean;
  productOwned: boolean;
}

export interface ChannelIdentity {
  id: string;
  username?: string | null;
  displayName?: string | null;
  isBot?: boolean;
}

export interface ChannelAccount {
  id: string;
  connectorKind: ChannelConnectorKind;
  displayName: string;
  state: ChannelAccountState;
  createdAt: string;
  updatedAt: string;
  externalAccountId?: string | null;
  credentialReference?: string | null;
  description?: string | null;
  configurationJson?: string | null;
  inboundEnabled: boolean;
  lastConnectedAt?: string | null;
  lastDisconnectedAt?: string | null;
  lastError?: string | null;
}

export interface ChannelPolicy {
  id: string;
  threadType: ChannelThreadType;
  updatedAt: string;
  loadAgents: boolean;
  loadIdentity: boolean;
  loadSoul: boolean;
  loadUserProfile: boolean;
  loadLongTermMemory: boolean;
  loadRecentThreadSummary: boolean;
  allowDirectReply: boolean;
  requireExplicitMention: boolean;
  workspaceMuted: boolean;
  connectorMuted: boolean;
  threadMuted: boolean;
  notes?: string | null;
}

export interface DeliveryRule {
  id: string;
  mode: DeliveryMode;
  updatedAt: string;
  allowProactiveSend: boolean;
  muteDuringQuietHours: boolean;
}

export interface ThreadBinding {
  id: string;
  connectorKind: ChannelConnectorKind;
  accountId: string;
  externalThreadId: string;
  threadType: ChannelThreadType;
  sessionId: string;
  sessionKind: SessionKind;
  channelIdentity: ChannelIdentity;
  policyId: string;
  deliveryRuleId: string;
  createdAt: string;
  updatedAt: string;
  lastInboundAt?: string | null;
  lastOutboundAt?: string | null;
  lastMessagePreview?: string | null;
}

export interface ChannelThreadSummary {
  bindingId: string;
  connectorKind: ChannelConnectorKind;
  accountId: string;
  externalThreadId: string;
  threadType: ChannelThreadType;
  sessionId: string;
  sessionKind: SessionKind;
  displayTitle: string;
  deliveryMode: DeliveryMode;
  accountState: ChannelAccountState;
  updatedAt: string;
  lastInboundAt?: string | null;
  lastOutboundAt?: string | null;
  lastMessagePreview?: string | null;
  pendingApprovalId?: string | null;
  hasPendingDraft: boolean;
  lastTurnOutcome?: ChannelTurnOutcome | null;
}

export interface ChannelsQueryResponse {
  items: ChannelThreadSummary[];
}

export interface ChannelAuditEntry {
  id: string;
  bindingId: string;
  connectorKind: ChannelConnectorKind;
  accountId: string;
  externalThreadId: string;
  threadType: ChannelThreadType;
  eventType: string;
  createdAt: string;
  sessionId?: string | null;
  approvalId?: string | null;
  deliveryMode?: DeliveryMode | null;
  externalMessageId?: string | null;
  summary?: string | null;
  metadataJson?: string | null;
}

export interface ChannelTurnOutcome {
  kind: ChannelTurnOutcomeKind;
  summary: string;
  occurredAt: string;
  replyText?: string | null;
  deliveryMode?: DeliveryMode | null;
  approvalId?: string | null;
  inboxItemId?: string | null;
  draftId?: string | null;
  sourceEventId?: string | null;
  reasonCode?: string | null;
  hasExplicitMention?: boolean | null;
}

export interface ChannelThreadDetail {
  account: ChannelAccount;
  binding: ThreadBinding;
  policy: ChannelPolicy;
  deliveryRule: DeliveryRule;
  recentAudit: ChannelAuditEntry[];
  session?: SessionSummary | null;
  pendingApprovalId?: string | null;
  hasPendingDraft: boolean;
  policyEvidence?: string[] | null;
  lastTurnOutcome?: ChannelTurnOutcome | null;
}

export interface TriggerAutomationResponse {
  ok: boolean;
  runId: string;
}

export interface TestTelegramTokenRequest {
  botToken: string;
}

export interface TestTelegramTokenResponse {
  ok: boolean;
  botName?: string | null;
  botUsername?: string | null;
  error?: string | null;
}

export interface TestFeishuCredentialsRequest {
  appId: string;
  appSecret: string;
}

export interface TestFeishuCredentialsResponse {
  ok: boolean;
  appName?: string | null;
  error?: string | null;
}

export interface WeChatQrCodeResult {
  qrcode: string;
  qrcodeImgUrl: string;
}

export type WeChatQrCodeStatusValue = "wait" | "scaned" | "confirmed" | "expired";

export interface WeChatQrCodeStatus {
  status: WeChatQrCodeStatusValue;
  botToken?: string | null;
}

export interface TestWeChatCredentialsResponse {
  ok: boolean;
  error?: string | null;
}

export interface CreateChannelAccountRequest {
  id: string;
  connectorKind: ChannelConnectorKind;
  displayName: string;
  externalAccountId?: string | null;
  credentialReference?: string | null;
  description?: string | null;
  configurationJson?: string | null;
  inboundEnabled?: boolean;
}

export interface PatchChannelAccountRequest {
  displayName?: string | null;
  deliveryMode?: DeliveryMode | null;
  enabled?: boolean | null;
  configurationJson?: string | null;
}

export interface CreateAccountModelRequest {
  displayName: string;
  modelId: string;
  capabilities?: number;
  contextWindowSize?: number;
  maxOutputTokens?: number;
  isReasoning?: boolean;
  supportsToolCalling?: boolean;
  isDefaultForAccount?: boolean;
  isGlobalDefault?: boolean;
  pricing?: ModelPricing | null;
}

export interface CreateProviderAccountRequest {
  displayName: string;
  providerKind: ModelProviderKind;
  baseUrl?: string | null;
  apiKeyValue?: string | null;
  apiKeyEnvironmentVariable?: string | null;
  accessMode?: string | null;
  customHeaders?: Record<string, string> | null;
  models: CreateAccountModelRequest[];
}

export interface UpdateProviderAccountRequest {
  displayName?: string | null;
  baseUrl?: string | null;
  apiKeyValue?: string | null;
  apiKeyEnvironmentVariable?: string | null;
  enabled?: boolean | null;
  customHeaders?: Record<string, string> | null;
}

export interface UpdateAccountModelRequest {
  displayName?: string | null;
  modelId?: string | null;
  capabilities?: number | null;
  contextWindowSize?: number | null;
  maxOutputTokens?: number | null;
  isReasoning?: boolean | null;
  supportsToolCalling?: boolean | null;
  enabled?: boolean | null;
  pricing?: ModelPricing | null;
}

export interface KodaClawSettings {
  defaultLandingRoute: string;
  theme: ThemeMode;
  requireApprovalForExternalActions: boolean;
  notificationsEnabled: boolean;
  quietHoursEnabled: boolean;
  quietHoursStartLocalTime?: string | null;
  quietHoursEndLocalTime?: string | null;
  updatedAt: string;
  automationsEnabled: boolean;
  autoApproveToolCalls: boolean;
  mainMaxIterations?: number | null;
  channelMaxIterations?: number | null;
  automationMaxIterations?: number | null;
}

export interface SandboxExecutionProfile {
  key: string;
  displayName: string;
  active: boolean;
  supported: boolean;
  boundaryEnforced: boolean;
  bestEffort: boolean;
  summary: string;
  blastRadius: string;
  guardrails: string[];
  residualRisks: string[];
}

export interface SandboxApprovalPosture {
  requireApprovalForExternalActions: boolean;
  notificationsEnabled: boolean;
  quietHoursEnabled: boolean;
  quietHoursWindow?: string | null;
  persistedPreferenceOnly: boolean;
  advisory: string;
}

export interface PluginRiskItem {
  pluginId: string;
  displayName: string;
  trustState: PluginTrustState;
  enabled: boolean;
  runtimeState: PluginRuntimeState;
  requestedScopes: string[];
  highRiskReasons: string[];
  mediumRiskReasons: string[];
  trustEvidenceSummary?: string | null;
}

export interface PluginRiskOverview {
  totalCount: number;
  signedCount: number;
  trustedCount: number;
  untrustedCount: number;
  highRiskCount: number;
  networkEnabledCount: number;
  backgroundCount: number;
  broadFilesystemCount: number;
  secretAccessCount: number;
  channelAccessCount: number;
  riskItems: PluginRiskItem[];
  advisory: string;
}

export interface ChannelRiskItem {
  bindingId: string;
  displayTitle: string;
  connectorKind: ChannelConnectorKind;
  supportsOutbound: boolean;
  threadType: ChannelThreadType;
  accountState: ChannelAccountState;
  deliveryMode: DeliveryMode;
  hasPendingApproval: boolean;
  pendingApprovalId?: string | null;
}

export interface ChannelRiskOverview {
  totalThreads: number;
  outboundCapableThreadCount: number;
  autoSendCount: number;
  draftApprovalCount: number;
  requireApprovalCount: number;
  pendingApprovalCount: number;
  riskItems: ChannelRiskItem[];
  advisory: string;
}

export interface SandboxRiskOverviewResponse {
  generatedAt: string;
  executionProfiles: SandboxExecutionProfile[];
  approvalPosture: SandboxApprovalPosture;
  pluginRisk: PluginRiskOverview;
  channelRisk: ChannelRiskOverview;
  operatorWarnings: string[];
}

// ===== Iter 23：引导程序基础数据层 =====

export interface ModelPreset {
  presetId: string;
  displayName: string;
  provider: string;
  modelId: string;
  baseUrl?: string;
  contextWindowSize: number;
  maxOutputTokens?: number;
  isReasoning?: boolean;
  supportsToolCalling?: boolean;
  tier: 'Recommended' | 'Advanced' | 'Fast' | 'Reasoning' | 'Local';
  description: string;
  costHint?: string;
  pricing?: ModelPricing | null;
  requiresBaseUrl: boolean;
  defaultCapabilities: number;
  accessMode?: 'api' | 'coding-plan';
  anthropicBaseUrl?: string;
  group?: string;
}

export interface ModelConnectionTestRequest {
  presetId?: string;
  modelId?: string;
  baseUrl?: string;
  apiKey: string;
  provider?: ModelProviderKind;
  accountId?: string;
}

export interface ModelConnectionTestResponse {
  ok: boolean;
  latencyMs: number;
  modelId?: string;
  error?: string;
  errorMessage?: string;
}

export interface PersonaPreset {
  presetId: string;
  displayName: string;
  tagLine: string;
  description: string;
  tags: string[];
  soulMarkdown: string;
  identityMarkdown: string;
}

export interface OnboardingState {
  isCompleted: boolean;
  currentStepId?: string;
  completedSteps: string[];
  selectedLanguage?: string;
  selectedPresetId?: string;
  selectedPersonaPresetId?: string;
  channelStepSkipped: boolean;
  startedAt: string;
  completedAt?: string;
}

export interface ApplyPersonaRequest {
  presetId: string;
}

export interface WorkspaceMcpServerEntry {
  command?: string | null;
  args?: string[] | null;
  env?: Record<string, string> | null;
  transport?: string | null;
  url?: string | null;
  headers?: Record<string, string> | null;
  enabled?: boolean | null;
  sessionScopes?: string[] | null;
}

export interface WorkspaceMcpConfig {
  mcpServers: Record<string, WorkspaceMcpServerEntry>;
}

export interface McpConnectionTestResult {
  success: boolean;
  toolCount: number;
  errorMessage?: string | null;
  toolNames?: string[] | null;
}

export interface SessionMessageItem {
  id: string;
  role: "user" | "assistant" | "tool_activity";
  text: string;
  timestamp?: number | null;
  toolName?: string | null;
  inputPreview?: string | null;
}

export interface SessionMessagesResponse {
  items: SessionMessageItem[];
  totalCount: number;
  hasMore: boolean;
}

export interface SessionStorageUsage {
  count: number;
  sizeBytes: number;
}

export interface StorageUsageResponse {
  main: SessionStorageUsage;
  auto: SessionStorageUsage;
  channel: SessionStorageUsage;
  totalSizeBytes: number;
}

export interface WorkspaceGitCommit {
  hash: string;
  shortHash: string;
  message: string;
  author: string;
  committedAt: string;
  changedFiles: string[];
}

export interface WorkspaceGitLogResponse {
  commits: WorkspaceGitCommit[];
  hasMore: boolean;
}

export interface WorkspaceGitRevertFileRequest {
  hash: string;
  filePath: string;
}

export interface WorkspaceGitRevertFileResponse {
  newHash: string;
}

export interface SkillDescriptor {
  name: string;
  description?: string | null;
  source: string;
  path: string;
  hasResources: boolean;
  kind: string;
  tags: string[];
  allowedTools: string[];
  version?: string | null;
  compatibility?: string | null;
}
