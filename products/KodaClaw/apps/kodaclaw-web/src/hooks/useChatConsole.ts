import { useCallback, useMemo, useRef, useState } from "react";
import { streamChatEvents, submitApprovalDecision, fetchSessionMessages } from "../lib/api";
import type { ChatMessage, LiveSubAgentRow, SubAgentRowData } from "../types/chat";
import type { SessionMessageItem } from "../types/contracts";

// ── sub-agent tracking types ──────────────────────────────────────────────────

type SubAgentStatus = {
  subAgentId: string;
  label: string;
  toolName: string | null;
  toolCount: number;
  isDone: boolean;
};

function subLabelMatches(label: string, toolName: string): boolean {
  return label === toolName || label.startsWith(toolName + ":");
}

export type ChatConsoleCopy = {
  initialSystemNote: string;
  placeholderMain: string;
  emptyCompletion: string;
  unknownStreamError: string;
  streamClosed: string;
  failedToReachStream: string;
  newSessionNote?: string;
  sessionResumedNote?: string;
};

function createId(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`;
}

function createMessage(
  role: ChatMessage["role"],
  text: string,
  status: ChatMessage["status"],
  sessionId?: string | null,
  approvalFields?: Pick<ChatMessage, "approvalId" | "callId" | "toolName" | "inputPreview" | "decision">,
): ChatMessage {
  return {
    id: createId(role),
    role,
    text,
    status,
    timestamp: Date.now(),
    sessionId,
    ...approvalFields,
  };
}

function hasAssistantContent(message: ChatMessage | undefined): boolean {
  if (!message) {
    return false;
  }

  return Boolean(message.text || message.thinking);
}

export function useChatConsole(copy: ChatConsoleCopy, onSessionRotated?: (newSessionId: string) => void) {
  const [draft, setDraft] = useState("");
  const [isStreaming, setIsStreaming] = useState(false);
  const [activeToolName, setActiveToolName] = useState<string | null>(null);
  const activeToolNameRef = useRef<string | null>(null); // mirrors activeToolName for sync reads
  const [isLoadingHistory, setIsLoadingHistory] = useState(false);
  const [hasMoreHistory, setHasMoreHistory] = useState(false);
  const historySkipRef = useRef(0);
  const historySessionIdRef = useRef<string | null>(null);
  const streamAbortRef = useRef<AbortController | null>(null);
  // Track callIds that already have an approval message, so tool_activity can skip duplicates
  const approvalCallIds = useRef<Set<string>>(new Set());
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  // Incremented whenever the timeline should auto-scroll to bottom.
  // History prepend does NOT increment this, so it never accidentally yanks scroll position.
  const [scrollToBottomVersion, setScrollToBottomVersion] = useState(0);

  // ── sub-agent live tracking ─────────────────────────────────────────────────
  // Use a ref for synchronous reads inside the async stream loop + state for renders.
  const activeSubAgentsRef = useRef<Map<string, SubAgentStatus>>(new Map());
  const pendingSubAgentsRef = useRef<Map<string, SubAgentStatus>>(new Map()); // keyed by label
  const [liveSubAgentRows, setLiveSubAgentRows] = useState<LiveSubAgentRow[]>([]);

  function syncSubAgentState() {
    setLiveSubAgentRows(Array.from(activeSubAgentsRef.current.values()).map(s => ({
      subAgentId: s.subAgentId,
      label: s.label,
      toolName: s.toolName,
      isDone: s.isDone,
    })));
  }

  function clearSubAgents() {
    activeSubAgentsRef.current = new Map();
    pendingSubAgentsRef.current = new Map();
    setLiveSubAgentRows([]);
  }

  const placeholder = useMemo(() => copy.placeholderMain, [copy.placeholderMain]);

  const stopStreaming = useCallback(() => {
    streamAbortRef.current?.abort();
    streamAbortRef.current = null;
    setActiveToolName(null);
    activeToolNameRef.current = null;
    clearSubAgents();
    setIsStreaming(false);
    setMessages((current) =>
      current.map((m) =>
        m.status === "streaming"
          ? { ...m, status: "done" as const, text: m.text || "（已中断）" }
          : m,
      ),
    );
  }, []); // eslint-disable-line react-hooks/exhaustive-deps

  const sendMessage = useCallback(async (
    mediaIds?: string[],
    mediaUrls?: string[],
    opts?: { enableThinking?: boolean },
  ) => {
    const content = draft.trim();
    const hasMedia = mediaIds != null && mediaIds.length > 0;
    if (!content && !hasMedia) {
      return;
    }

    // If currently streaming, abort and mark the in-progress message as stopped
    if (isStreaming) {
      streamAbortRef.current?.abort();
      streamAbortRef.current = null;
      setActiveToolName(null);
      activeToolNameRef.current = null;
      clearSubAgents();
      setMessages((current) =>
        current.map((m) =>
          m.status === "streaming"
            ? { ...m, status: "done" as const, text: m.text || "（已中断）" }
            : m,
        ),
      );
    }

    const userMessage: ChatMessage = {
      ...createMessage("user", content || "📎", "done"),
      ...(mediaUrls && mediaUrls.length > 0 ? { mediaUrls } : {}),
    };
    const initialAssistantMessage = createMessage("assistant", "", "streaming");
    let currentAssistantMsgId = initialAssistantMessage.id;

    setDraft("");
    setIsStreaming(true);
    approvalCallIds.current = new Set();
    setMessages((current) => [...current, userMessage, initialAssistantMessage]);
    setScrollToBottomVersion((v) => v + 1);
    const ctrl = new AbortController();
    streamAbortRef.current = ctrl;

    try {
      let rotatedSessionId: string | null = null;
      for await (const event of streamChatEvents({
        message: content,
        mediaIds: hasMedia ? mediaIds : null,
        enableThinking: opts?.enableThinking || undefined,
      }, ctrl.signal)) {
        if (event.type === "session_rotated") {
          rotatedSessionId = event.sessionId ?? null;
          // Insert a visual separator just before the in-progress assistant message so the user can
          // see that workspace files were updated and a new conversation context has started.
          setMessages((current) => {
            const idx = current.findIndex((m) => m.id === currentAssistantMsgId);
            const sep = createMessage("system", "✨ workspace 已更新 · 进入新对话", "done", event.sessionId);
            if (idx < 0) return [...current, sep];
            return [...current.slice(0, idx), sep, ...current.slice(idx)];
          });
          continue;
        }

        if (event.type === "text_chunk") {
          setMessages((current) =>
            current.map((message) =>
              message.id === currentAssistantMsgId
                ? {
                    ...message,
                    text: `${message.text}${event.delta ?? ""}`,
                    status: "streaming",
                    timestamp: event.timestamp ?? Date.now(),
                    sessionId: event.sessionId,
                  }
                : message,
            ),
          );
          continue;
        }

        if (event.type === "think_chunk_start") {
          const startedAt = event.timestamp ?? Date.now();
          setMessages((current) =>
            current.map((message) =>
              message.id === currentAssistantMsgId
                ? {
                    ...message,
                    thinking: message.thinking ?? "",
                    thinkingStreaming: true,
                    thinkingStartedAt: message.thinkingStartedAt ?? startedAt,
                  }
                : message,
            ),
          );
          continue;
        }

        if (event.type === "think_chunk") {
          setMessages((current) =>
            current.map((message) =>
              message.id === currentAssistantMsgId
                ? {
                    ...message,
                    thinking: `${message.thinking ?? ""}${event.thinkingDelta ?? ""}`,
                    thinkingStreaming: true,
                  }
                : message,
            ),
          );
          continue;
        }

        if (event.type === "think_chunk_end") {
          const endedAt = event.timestamp ?? Date.now();
          setMessages((current) =>
            current.map((message) =>
              message.id === currentAssistantMsgId
                ? {
                    ...message,
                    thinkingStreaming: false,
                    thinkingDurationMs: message.thinkingStartedAt
                      ? Math.max(0, endedAt - message.thinkingStartedAt)
                      : message.thinkingDurationMs,
                  }
                : message,
            ),
          );
          continue;
        }

        if (event.type === "approval_required") {
          if (event.callId) approvalCallIds.current.add(event.callId);
          setMessages((current) => [
            ...current,
            createMessage("approval", "", "done", event.sessionId, {
              approvalId: event.approvalId ?? null,
              callId: event.callId ?? null,
              toolName: event.toolName ?? null,
              inputPreview: event.inputPreview ?? null,
              decision: "pending",
            }),
          ]);
          continue;
        }

        if (event.type === "approval_decided") {
          setMessages((current) =>
            current.map((message) =>
              message.role === "approval" && message.approvalId === event.approvalId
                ? {
                    ...message,
                    decision: event.decision === "allow" ? "approved" : "rejected",
                  }
                : message,
            ),
          );
          continue;
        }

        if (event.type === "model_retrying") {
          setMessages((current) => [
            ...current,
            { ...createMessage("system", `↻ ${event.reason ?? "模型请求重试中..."}`, "done", event.sessionId), isToolWarning: true },
          ]);
          continue;
        }

        if (event.type === "tool_warning") {
          setMessages((current) => [
            ...current,
            { ...createMessage("system", `⚠ ${event.reason ?? "工具调用失败"}`, "done", event.sessionId), isToolWarning: true },
          ]);
          continue;
        }

        if (event.type === "agent_working") {
          const toolName = event.toolName ?? null;
          setActiveToolName(toolName);
          activeToolNameRef.current = toolName;
          // Drain pending sub-agents matching this tool
          if (toolName) {
            let changed = false;
            for (const [label, status] of pendingSubAgentsRef.current) {
              if (subLabelMatches(label, toolName)) {
                activeSubAgentsRef.current.set(status.subAgentId, status);
                pendingSubAgentsRef.current.delete(label);
                changed = true;
              }
            }
            if (changed) syncSubAgentState();
          }
          continue;
        }

        if (event.type === "subagent_start") {
          const { subAgentId, label } = event;
          if (!subAgentId || !label) continue;
          const status: SubAgentStatus = { subAgentId, label, toolName: null, toolCount: 0, isDone: false };
          const parentTool = activeToolNameRef.current;
          if (parentTool && subLabelMatches(label, parentTool)) {
            // Pipeline: mark previous active pipeline stages as done when a new one starts
            if (label.startsWith("pipeline:")) {
              for (const [id, s] of activeSubAgentsRef.current) {
                if (s.label.startsWith("pipeline:") && !s.isDone) {
                  activeSubAgentsRef.current.set(id, { ...s, isDone: true });
                }
              }
            }
            activeSubAgentsRef.current.set(subAgentId, status);
          } else {
            pendingSubAgentsRef.current.set(label, status);
          }
          syncSubAgentState();
          continue;
        }

        if (event.type === "subagent_working") {
          const { subAgentId, subAgentToolName } = event;
          if (!subAgentId) continue;
          const existing = activeSubAgentsRef.current.get(subAgentId);
          if (existing) {
            activeSubAgentsRef.current.set(subAgentId, { ...existing, toolName: subAgentToolName ?? null });
            syncSubAgentState();
          }
          continue;
        }

        if (event.type === "subagent_tool_done") {
          const { subAgentId } = event;
          if (!subAgentId) continue;
          const existing = activeSubAgentsRef.current.get(subAgentId);
          if (existing) {
            activeSubAgentsRef.current.set(subAgentId, { ...existing, toolCount: existing.toolCount + 1 });
            syncSubAgentState();
          }
          continue;
        }

        if (event.type === "tool_activity") {
          setActiveToolName(null);
          activeToolNameRef.current = null;
          // Collect sub-agent rows matching this tool and remove them from active tracking
          const completedToolName = event.toolName ?? null;
          const subAgentRows: SubAgentRowData[] = [];
          if (completedToolName) {
            for (const [id, status] of activeSubAgentsRef.current) {
              if (subLabelMatches(status.label, completedToolName)) {
                subAgentRows.push({ label: status.label, toolCount: status.toolCount });
                activeSubAgentsRef.current.delete(id);
              }
            }
            if (subAgentRows.length > 0) syncSubAgentState();
          }
          // Skip if an approval card already represents this call (no duplicate needed)
          if (event.callId && approvalCallIds.current.has(event.callId)) continue;
          const toolMsg = createMessage("tool_activity", "", "done", event.sessionId);
          const prevAssistantId = currentAssistantMsgId;
          const nextAssistantMsg = createMessage("assistant", "", "streaming");
          currentAssistantMsgId = nextAssistantMsg.id;
          setMessages((current) => {
            const prev = current.find((m) => m.id === prevAssistantId);
            // Keep thinking-only assistant bubbles; they are still user-visible content.
            const withPrev = hasAssistantContent(prev)
              ? current.map((m) => m.id === prevAssistantId ? { ...m, status: "done" as const } : m)
              : current.filter((m) => m.id !== prevAssistantId);
            return [
              ...withPrev,
              {
                ...toolMsg,
                toolName: event.toolName ?? null,
                durationMs: event.durationMs ?? null,
                inputPreview: event.inputPreview ?? null,
                subAgentRows: subAgentRows.length > 0 ? subAgentRows : undefined,
              },
              nextAssistantMsg,
            ];
          });
          continue;
        }

        if (event.type === "done") {
          setActiveToolName(null);
          activeToolNameRef.current = null;
          clearSubAgents();
          setMessages((current) =>
            current.map((message) =>
              message.id === currentAssistantMsgId
                ? {
                    ...message,
                    status: "done" as const,
                    sessionId: event.sessionId,
                    timestamp: event.timestamp ?? Date.now(),
                    // If agent ended with a tool call and produced no text, use emptyCompletion;
                    // otherwise keep the accumulated text.
                    text: message.text || copy.emptyCompletion,
                  }
                : message,
            ),
          );
          setScrollToBottomVersion((v) => v + 1);
          setIsStreaming(false);
          // Notify App to refresh the gateway snapshot so activeMainSessionId stays in sync.
          // We fire this after done (stream completed) to avoid triggering loadHistory mid-stream.
          if (rotatedSessionId) {
            onSessionRotated?.(rotatedSessionId);
          }
          return;
        }

        const detail = event.error?.message ?? event.reason ?? copy.unknownStreamError;
        setActiveToolName(null);
        activeToolNameRef.current = null;
        clearSubAgents();
        setMessages((current) =>
          current.map((message) =>
            message.id === currentAssistantMsgId
              ? {
                  ...message,
                  role: "error",
                  status: "error",
                  text: detail,
                  sessionId: event.sessionId,
                  timestamp: event.timestamp ?? Date.now(),
                }
              : message,
          ),
        );
        setIsStreaming(false);
        return;
      }

      setMessages((current) =>
        current.map((message) =>
          message.id === currentAssistantMsgId
            ? {
                ...message,
                status: "done",
                text: message.text || copy.streamClosed,
                timestamp: Date.now(),
              }
            : message,
        ),
      );
    } catch (error) {
      // AbortError = deliberate stream cancellation (resume/rotate), not an error to surface
      if (error instanceof Error && error.name === "AbortError") {
        setActiveToolName(null);
        activeToolNameRef.current = null;
        clearSubAgents();
        setIsStreaming(false);
        return;
      }
      const detail = error instanceof Error ? error.message : copy.failedToReachStream;
      setActiveToolName(null);
      activeToolNameRef.current = null;
      clearSubAgents();
      setMessages((current) =>
        current.map((message) =>
          message.id === currentAssistantMsgId
            ? {
                ...message,
                role: "error",
                status: "error",
                text: detail,
                timestamp: Date.now(),
              }
            : message,
        ),
      );
    } finally {
      setIsStreaming(false);
    }
  }, [copy.emptyCompletion, copy.failedToReachStream, copy.initialSystemNote, copy.placeholderMain, copy.streamClosed, copy.unknownStreamError, draft, isStreaming]);

  const appendSystemNote = useCallback((note: string) => {
    setMessages((current) => [...current, createMessage("system", note, "done")]);
    setScrollToBottomVersion((v) => v + 1);
  }, []);

  const clearMessages = useCallback((systemNote?: string) => {
    streamAbortRef.current?.abort();
    streamAbortRef.current = null;
    setMessages([
      createMessage("system", systemNote ?? copy.initialSystemNote, "done"),
    ]);
    setHasMoreHistory(false);
    historySkipRef.current = 0;
    historySessionIdRef.current = null;
    setScrollToBottomVersion((v) => v + 1);
  }, [copy.initialSystemNote]);

  const prependHistory = useCallback((items: SessionMessageItem[], hasMore: boolean, sessionId: string) => {
    if (items.length === 0) return;
    // API returns newest-first; reverse to chronological order before prepending
    const historyMessages: ChatMessage[] = [...items].reverse().map((item) => ({
      id: `history-${item.id}`,
      role: item.role,
      text: item.text,
      status: "done" as const,
      timestamp: item.timestamp ?? Date.now(),
      isHistory: true,
      toolName: item.toolName ?? null,
      inputPreview: item.inputPreview ?? null,
      thinking: item.thinking ?? undefined,
    }));
    const separator = createMessage("history_separator", "", "done");
    setMessages((current) => [...historyMessages, separator, ...current]);
    setHasMoreHistory(hasMore);
  }, []);

  const loadMoreHistory = useCallback(async () => {
    const sessionId = historySessionIdRef.current;
    if (!sessionId || isLoadingHistory) return;
    const limit = 20;
    const skip = historySkipRef.current + limit;
    setIsLoadingHistory(true);
    try {
      const result = await fetchSessionMessages(sessionId, limit, skip);
      if (result.items.length > 0) {
        // API returns newest-first within each page; reverse to chronological before prepending
        const historyMessages: ChatMessage[] = [...result.items].reverse().map((item) => ({
          id: `history-${skip}-${item.id}`,
          role: item.role,
          text: item.text,
          status: "done" as const,
          timestamp: item.timestamp ?? Date.now(),
          isHistory: true,
          toolName: item.toolName ?? null,
          inputPreview: item.inputPreview ?? null,
          thinking: item.thinking ?? undefined,
        }));
        setMessages((current) => [...historyMessages, ...current]);
        historySkipRef.current = skip;
      }
      setHasMoreHistory(result.hasMore);
    } catch {
      // silently fail — history load is best-effort
    } finally {
      setIsLoadingHistory(false);
    }
  }, [isLoadingHistory]);

  const loadHistory = useCallback(async (sessionId: string) => {
    historySessionIdRef.current = sessionId;
    historySkipRef.current = 0;
    setIsLoadingHistory(true);
    try {
      const result = await fetchSessionMessages(sessionId, 20, 0);
      prependHistory(result.items, result.hasMore, sessionId);
    } catch {
      // silently fail
    } finally {
      setIsLoadingHistory(false);
    }
  }, [prependHistory]);

  const submitApproval = useCallback(async (approvalId: string, approve: boolean) => {
    setMessages((current) =>
      current.map((message) =>
        message.role === "approval" && message.approvalId === approvalId
          ? { ...message, decision: approve ? "approved" : "rejected" }
          : message,
      ),
    );
    try {
      await submitApprovalDecision(approvalId, approve);
    } catch {
      // Revert to pending on error
      setMessages((current) =>
        current.map((message) =>
          message.role === "approval" && message.approvalId === approvalId
            ? { ...message, decision: "pending" }
            : message,
        ),
      );
    }
  }, []);

  return {
    draft,
    setDraft,
    isStreaming,
    activeToolName,
    liveSubAgentRows,
    messages,
    placeholder,
    sendMessage,
    stopStreaming,
    appendSystemNote,
    clearMessages,
    submitApproval,
    loadHistory,
    loadMoreHistory,
    isLoadingHistory,
    hasMoreHistory,
    scrollToBottomVersion,
  };
}
