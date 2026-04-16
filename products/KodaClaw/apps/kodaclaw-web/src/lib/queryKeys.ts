/**
 * TanStack Query key 常量。
 * 所有 useQuery / invalidateQueries 统一引用此处，避免字符串散落各处。
 */
export const queryKeys = {
  providerAccounts: ['providerAccounts'] as const,
  models: ['models'] as const,

  settings: ['settings'] as const,

  channelConnectors: ['channelConnectors'] as const,
  channelAccounts: (connectorKind?: string, state?: string) =>
    ['channelAccounts', connectorKind, state] as const,
  channelThreads: (accountId?: string) => ['channelThreads', accountId] as const,
  channelThreadDetail: (bindingId?: string) => ['channelThreadDetail', bindingId] as const,
  channelThreadAudit: (bindingId?: string) => ['channelThreadAudit', bindingId] as const,

  inbox: (status?: string, kind?: string) => ['inbox', status, kind] as const,
  approvals: (status?: string, kind?: string) => ['approvals', status, kind] as const,

  automations: (enabledFilter?: boolean, source?: string) =>
    ['automations', enabledFilter, source] as const,
  automation: (id: string) => ['automation', id] as const,
  automationRuns: (id: string) => ['automationRuns', id] as const,

  plugins: (typeFilter?: string, trustFilter?: string, runtimeFilter?: string) =>
    ['plugins', typeFilter, trustFilter, runtimeFilter] as const,
  plugin: (id: string) => ['plugin', id] as const,
  pluginLogs: (id: string) => ['pluginLogs', id] as const,

  canvas: (kindFilter?: string) => ['canvas', kindFilter] as const,
  canvasArtifact: (id: string) => ['canvasArtifact', id] as const,
  canvasDefault: ['canvasDefault'] as const,

  sessions: ['sessions'] as const,
  sessionDetail: (id: string) => ['sessionDetail', id] as const,
  sessionMessages: (id: string, skip?: number) => ['sessionMessages', id, skip] as const,

  workspaceFile: (target: string) => ['workspaceFile', target] as const,
  memoryStats: ['memoryStats'] as const,
  memoryEntries: (status?: string) => ['memoryEntries', status] as const,

  skills: ['skills'] as const,
  mcpServers: ['mcpServers'] as const,

  diagnosticsStats: (since?: string) => ['diagnosticsStats', since] as const,
  diagnosticsRecent: (filters?: object) => ['diagnosticsRecent', filters] as const,
};
