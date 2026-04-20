export type ChatRole = "user" | "assistant" | "system" | "error" | "approval" | "tool_activity" | "history_separator";
export type ChatMessageStatus = "streaming" | "done" | "error";
export type ApprovalDecision = "approved" | "rejected" | "pending";

/** Collapsed sub-agent summary stored on a completed tool_activity message. */
export interface SubAgentRowData {
  label: string;
  toolCount: number;
}

/** Live sub-agent status exposed while an orchestration tool is running. */
export interface LiveSubAgentRow {
  subAgentId: string;
  label: string;
  toolName: string | null;
  isDone: boolean;
}

export interface ChatMessage {
  id: string;
  role: ChatRole;
  text: string;
  status: ChatMessageStatus;
  timestamp: number;
  sessionId?: string | null;
  // approval card fields
  approvalId?: string | null;
  callId?: string | null;
  toolName?: string | null;
  inputPreview?: string | null;
  decision?: ApprovalDecision;
  // tool activity fields
  durationMs?: number | null;
  // sub-agent progress rows (collapsed, after tool_activity completes)
  subAgentRows?: SubAgentRowData[];
  // history fields
  isHistory?: boolean;
  // tool warning: marks system messages that originated from a tool_warning event
  isToolWarning?: boolean;
  // local preview URLs for sent images (user messages only)
  mediaUrls?: string[];
  // Extended thinking content (assistant messages only)
  thinking?: string;
  // True while think_chunk is streaming; flips false on think_chunk_end
  thinkingStreaming?: boolean;
  // Wall-clock ms spent on thinking (set on first start, finalized on end)
  thinkingStartedAt?: number;
  thinkingDurationMs?: number;
}
