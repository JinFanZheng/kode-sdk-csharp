import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from "react";

import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import { Check, CheckCircle, ChevronDown, Cog, Copy, MessageSquare, X, XCircle } from "lucide-react";
import type { ChatMessage, ChatRole, LiveSubAgentRow } from "../types/chat";
import { useI18n, useLocaleText } from "../i18n/I18nProvider";
import { EmptyState } from "./ui/EmptyState";
import { ApprovalCard } from "./chat/ApprovalCard";
import { ThinkingBlock } from "./chat/ThinkingBlock";

// ── Tool name localization ────────────────────────────────────────────────────

const TOOL_DISPLAY_NAMES: Record<string, string> = {
  fs_read: "读取文件",
  fs_grep: "搜索代码",
  fs_glob: "列举文件",
  fs_list: "列举目录",
  fs_write: "写入文件",
  fs_edit: "编辑文件",
  fs_rm: "删除文件",
  bash_run: "执行命令",
  bash_logs: "查看日志",
  bash_kill: "终止进程",
  todo_read: "读取待办",
  todo_write: "更新待办",
};

function toolDisplayName(toolName: string): string {
  return TOOL_DISPLAY_NAMES[toolName] ?? toolName;
}

// ── Tool Band: groups consecutive tool calls into a flat chip row ──────────────

type ToolGroupItem = {
  msg: ChatMessage;
  seq: number;        // 1-indexed occurrence of this toolName within the group
  warning?: string;   // associated tool_warning text, if any
};

type RenderItem =
  | { kind: "message"; msg: ChatMessage }
  | { kind: "tool_group"; items: ToolGroupItem[] };

function groupMessages(messages: ChatMessage[]): RenderItem[] {
  const result: RenderItem[] = [];
  let currentGroup: ToolGroupItem[] | null = null;
  const nameCounts: Record<string, number> = {};

  function flushGroup() {
    if (currentGroup && currentGroup.length > 0) {
      result.push({ kind: "tool_group", items: currentGroup });
    }
    currentGroup = null;
    for (const k in nameCounts) delete nameCounts[k];
  }

  for (const msg of messages) {
    // Skip empty done assistant messages (tool placeholders that received no text)
    if (msg.role === "assistant" && !msg.text && !msg.thinking && msg.status !== "streaming") continue;

    const isDecidedApproval = msg.role === "approval" && msg.decision !== "pending";
    const isToolActivity = msg.role === "tool_activity";
    const isToolWarning = msg.role === "system" && msg.isToolWarning === true;

    if (isDecidedApproval || isToolActivity) {
      if (!currentGroup) currentGroup = [];
      const toolName = msg.toolName ?? "tool";
      nameCounts[toolName] = (nameCounts[toolName] ?? 0) + 1;
      currentGroup.push({ msg, seq: nameCounts[toolName] });
    } else if (isToolWarning) {
      if (currentGroup && currentGroup.length > 0) {
        currentGroup[currentGroup.length - 1].warning = msg.text.replace(/^⚠\s*/, "");
      } else {
        flushGroup();
        result.push({ kind: "message", msg });
      }
    } else {
      flushGroup();
      result.push({ kind: "message", msg });
    }
  }

  flushGroup();
  return result;
}

// ── extractKeyParam: extract the most informative parameter for a given tool ──

type ToolCategory = "bash" | "fs-read" | "fs-write" | "fs-delete" | "todo" | "workspace" | "mcp" | "default";

function getToolCategory(toolName: string): ToolCategory {
  if (toolName === "bash_run") return "bash";
  if (toolName === "fs_read") return "fs-read";
  if (["fs_write", "fs_edit", "fs_multi_edit"].includes(toolName)) return "fs-write";
  if (["fs_delete", "fs_rm"].includes(toolName)) return "fs-delete";
  if (toolName === "todo_write") return "todo";
  if (toolName.startsWith("workspace_")) return "workspace";
  if (toolName.startsWith("mcp__")) return "mcp";
  return "default";
}

function extractKeyParam(toolName: string, inputPreview?: string | null): string | null {
  if (!inputPreview) return null;
  try {
    const parsed = JSON.parse(inputPreview);
    if (toolName === "bash_run") {
      return typeof parsed.command === "string" ? parsed.command : null;
    }
    if (["fs_read", "fs_write", "fs_edit", "fs_delete", "fs_rm", "fs_multi_edit"].includes(toolName)) {
      return typeof parsed.path === "string" ? parsed.path
           : typeof parsed.file_path === "string" ? parsed.file_path
           : null;
    }
    if (toolName === "todo_write" && Array.isArray(parsed.todos) && parsed.todos.length > 0) {
      const first = parsed.todos[0];
      return typeof first?.content === "string" ? first.content.slice(0, 80) : null;
    }
    if (toolName === "workspace_protocol_update") {
      return typeof parsed.target === "string" ? `→ ${parsed.target}` : null;
    }
    if (toolName === "workspace_memory_append") {
      return typeof parsed.content === "string" ? parsed.content.slice(0, 80) : null;
    }
  } catch {
    // Truncated JSON fallback: try to extract key value via regex
    if (toolName === "bash_run") {
      const m = inputPreview.match(/^\{"command":"([\s\S]*)/);
      if (m) return m[1].replace(/\\(["\\])/g, "$1").replace(/"[\s\S]*$/, "").slice(0, 300) || null;
    }
    if (["fs_read", "fs_write", "fs_edit", "fs_delete", "fs_rm", "fs_multi_edit"].includes(toolName)) {
      const m = inputPreview.match(/["'](?:path|file_path)["']\s*:\s*["']([\s\S]*)/);
      if (m) return m[1].replace(/["'][\s\S]*$/, "").slice(0, 200) || null;
    }
  }
  return inputPreview.length < 100 ? inputPreview : null;
}

// ── ActivityRow: single tool execution row ────────────────────────────────────

type ActivityRowProps = { item: ToolGroupItem; totalCounts: Record<string, number> };

function ActivityRow({ item, totalCounts }: ActivityRowProps) {
  const toolName = item.msg.toolName ?? "tool";
  const showSeq = totalCounts[toolName] > 1;
  const category = getToolCategory(toolName);
  const param = extractKeyParam(toolName, item.msg.inputPreview);
  const hasWarning = !!item.warning;
  const durationMs = item.msg.durationMs;
  const [expanded, setExpanded] = useState(false);
  const [copied, setCopied] = useState(false);

  const canExpand = !!param;

  function handleCopy(e: React.MouseEvent) {
    e.stopPropagation();
    if (!param) return;
    navigator.clipboard.writeText(param).catch(() => {});
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  }

  let rowClass = "activity-row";
  let dotClass = "activity-row__dot--neutral";

  if (item.msg.role === "approval") {
    if (item.msg.decision === "approved") {
      dotClass = hasWarning ? "activity-row__dot--warning" : "activity-row__dot--success";
      if (hasWarning) rowClass += " activity-row--warning";
    } else {
      dotClass = "activity-row__dot--error";
      rowClass += " activity-row--rejected";
    }
  } else if (hasWarning) {
    dotClass = "activity-row__dot--warning";
    rowClass += " activity-row--warning";
  }
  if (canExpand) rowClass += " activity-row--expandable";
  if (expanded) rowClass += " activity-row--expanded";

  return (
    <div className={rowClass} onClick={canExpand ? () => setExpanded(e => !e) : undefined}>
      <div className="activity-row__header">
        <span className={`activity-row__dot ${dotClass}`} aria-hidden="true" />
        <code className={`activity-row__name activity-row__name--${category}`}>
          {toolName}
          {showSeq && <sub className="activity-row__seq">{item.seq}</sub>}
        </code>
        {param && <span className="activity-row__param" title={param}>{param}</span>}
        <span className="activity-row__right">
          {hasWarning && <span className="activity-row__warn">△ {item.warning}</span>}
          {!hasWarning && item.msg.decision === "rejected" && (
            <span className="activity-row__rejected">已拒绝</span>
          )}
          {!hasWarning && item.msg.decision !== "rejected" && durationMs != null && (
            <span className="activity-row__dur">{durationMs}ms</span>
          )}
          {canExpand && (
            <ChevronDown size={10} strokeWidth={2} className="activity-row__chevron" aria-hidden="true" />
          )}
        </span>
      </div>

      {canExpand && param && (
        <div className="activity-row__body-outer">
          <div className="activity-row__body">
            <code className="activity-row__full-param">{param}</code>
            <button
              className={`activity-row__copy-btn${copied ? " activity-row__copy-btn--copied" : ""}`}
              onClick={handleCopy}
              title="复制"
            >
              {copied
                ? <><Check size={11} strokeWidth={2} /><span>已复制</span></>
                : <><Copy size={11} strokeWidth={2} /><span>复制</span></>
              }
            </button>
          </div>
        </div>
      )}
      {item.msg.subAgentRows && item.msg.subAgentRows.length > 0 && (
        <div className="subagent-rows">
          {item.msg.subAgentRows.map((row, i) => (
            <div key={i} className="subagent-row">
              <span className="subagent-row__prefix">↳</span>
              <span className="subagent-row__label">{row.label}</span>
              <span className="subagent-row__summary">使用了 {row.toolCount} 个工具</span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

// ── ActivityStrip: collapsible execution log ──────────────────────────────────

const ACTIVITY_PREVIEW_COUNT = 5;

function ActivityStrip({ items }: { items: ToolGroupItem[] }) {
  // History groups default to collapsed; live groups default to expanded
  const isHistory = items[0]?.msg.isHistory === true;
  const [expanded, setExpanded] = useState(!isHistory);
  const [showAll, setShowAll] = useState(false);

  const totalCounts = useMemo(
    () => items.reduce<Record<string, number>>((acc, item) => {
      const n = item.msg.toolName ?? "tool";
      acc[n] = (acc[n] ?? 0) + 1;
      return acc;
    }, {}),
    [items],
  );

  // Build summary: "bash_run×18  fs_edit×3"
  const summaryLabel = useMemo(() =>
    Object.entries(totalCounts)
      .sort(([, a], [, b]) => b - a)
      .slice(0, 4)
      .map(([n, c]) => (c > 1 ? `${n}×${c}` : n))
      .join("  "),
    [totalCounts],
  );

  const displayItems = expanded
    ? (showAll ? items : items.slice(0, ACTIVITY_PREVIEW_COUNT))
    : [];
  const hiddenCount = items.length - ACTIVITY_PREVIEW_COUNT;

  return (
    <div className={`activity-strip${expanded ? " activity-strip--expanded" : ""}`}>
      <button
        className="activity-strip__header"
        onClick={() => setExpanded(e => !e)}
        aria-expanded={expanded}
      >
        <Cog size={12} strokeWidth={1.75} className="activity-strip__gear" aria-hidden="true" />
        <span className="activity-strip__count">{items.length} 步</span>
        <span className="activity-strip__tools">{summaryLabel}</span>
        <ChevronDown size={12} strokeWidth={2} className="activity-strip__chevron" aria-hidden="true" />
      </button>

      {expanded && (
        <div className="activity-strip__rows">
          {displayItems.map((item, idx) => (
            <ActivityRow key={item.msg.id ?? idx} item={item} totalCounts={totalCounts} />
          ))}
          {!showAll && hiddenCount > 0 && (
            <button className="activity-strip__more" onClick={() => setShowAll(true)}>
              ··· 还有 {hiddenCount} 步 · 展开全部
            </button>
          )}
        </div>
      )}
    </div>
  );
}

// ── Lightbox ──────────────────────────────────────────────────────────────────

function ImageLightbox({ url, onClose }: { url: string; onClose: () => void }) {
  const [scale, setScale] = useState<number | null>(null); // null = not yet measured
  const [pos, setPos] = useState({ x: 0, y: 0 });
  const baseScaleRef = useRef(1); // fitted scale = "100%" for this image
  const [isDragging, setIsDragging] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const scaleRef = useRef(1);
  const posRef = useRef({ x: 0, y: 0 });
  const mouseStartRef = useRef<{ mx: number; my: number; tx: number; ty: number } | null>(null);
  const touchDistRef = useRef<number | null>(null);
  const touchPosRef = useRef<{ x: number; y: number } | null>(null);

  const resolvedScale = scale ?? 1;
  scaleRef.current = resolvedScale;
  posRef.current = pos;

  // ESC to close
  useEffect(() => {
    const handler = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, [onClose]);

  // Wheel zoom (non-passive so we can preventDefault)
  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;
    const handler = (e: WheelEvent) => {
      e.preventDefault();
      const factor = e.deltaY > 0 ? 0.85 : 1.18;
      setScale(s => Math.min(Math.max((s ?? 1) * factor, baseScaleRef.current * 0.3), baseScaleRef.current * 10));
    };
    el.addEventListener("wheel", handler, { passive: false });
    return () => el.removeEventListener("wheel", handler);
  }, []);

  // Touch: pinch-to-zoom + single-finger pan
  useEffect(() => {
    const el = containerRef.current;
    if (!el) return;

    function dist(t: TouchList) {
      return Math.hypot(t[0].clientX - t[1].clientX, t[0].clientY - t[1].clientY);
    }

    function onStart(e: TouchEvent) {
      if (e.touches.length === 2) {
        touchDistRef.current = dist(e.touches);
      } else if (e.touches.length === 1) {
        touchPosRef.current = { x: e.touches[0].clientX, y: e.touches[0].clientY };
      }
    }
    function onMove(e: TouchEvent) {
      e.preventDefault();
      if (e.touches.length === 2 && touchDistRef.current !== null) {
        const d = dist(e.touches);
        const ratio = d / touchDistRef.current;
        setScale(s => Math.min(Math.max((s ?? 1) * ratio, baseScaleRef.current * 0.3), baseScaleRef.current * 10));
        touchDistRef.current = d;
      } else if (e.touches.length === 1 && scaleRef.current > 1 && touchPosRef.current) {
        const dx = e.touches[0].clientX - touchPosRef.current.x;
        const dy = e.touches[0].clientY - touchPosRef.current.y;
        setPos(p => ({ x: p.x + dx, y: p.y + dy }));
        touchPosRef.current = { x: e.touches[0].clientX, y: e.touches[0].clientY };
      }
    }
    function onEnd() {
      touchDistRef.current = null;
      touchPosRef.current = null;
    }

    el.addEventListener("touchstart", onStart, { passive: true });
    el.addEventListener("touchmove", onMove, { passive: false });
    el.addEventListener("touchend", onEnd);
    return () => {
      el.removeEventListener("touchstart", onStart);
      el.removeEventListener("touchmove", onMove);
      el.removeEventListener("touchend", onEnd);
    };
  }, []);

  function handleDoubleClick() {
    const base = baseScaleRef.current;
    if (resolvedScale > base * 1.05) {
      setScale(base); setPos({ x: 0, y: 0 });
    } else {
      setScale(base * 2.5);
    }
  }

  function handleMouseDown(e: React.MouseEvent) {
    if (resolvedScale <= baseScaleRef.current * 1.05) return;
    e.preventDefault();
    mouseStartRef.current = { mx: e.clientX, my: e.clientY, tx: pos.x, ty: pos.y };
    setIsDragging(true);
  }
  function handleMouseMove(e: React.MouseEvent) {
    if (!mouseStartRef.current) return;
    setPos({
      x: mouseStartRef.current.tx + (e.clientX - mouseStartRef.current.mx),
      y: mouseStartRef.current.ty + (e.clientY - mouseStartRef.current.my),
    });
  }
  function handleMouseUp() {
    mouseStartRef.current = null;
    setIsDragging(false);
  }

  function handleImgLoad(e: React.SyntheticEvent<HTMLImageElement>) {
    const img = e.currentTarget;
    const sw = (window.innerWidth * 0.88) / img.naturalWidth;
    const sh = (window.innerHeight * 0.88) / img.naturalHeight;
    const fitted = Math.min(sw, sh, 1); // never upscale beyond natural size
    baseScaleRef.current = fitted;
    setScale(fitted);
  }

  function handleBackdropClick() {
    const base = baseScaleRef.current;
    if (resolvedScale > base * 1.05) { setScale(base); setPos({ x: 0, y: 0 }); } else { onClose(); }
  }

  const isZoomed = resolvedScale > baseScaleRef.current * 1.05;

  return (
    <div
      ref={containerRef}
      className="lightbox"
      onClick={handleBackdropClick}
      onMouseMove={handleMouseMove}
      onMouseUp={handleMouseUp}
      onMouseLeave={handleMouseUp}
      role="dialog"
      aria-modal="true"
    >
      <button
        className="lightbox__close"
        onClick={e => { e.stopPropagation(); onClose(); }}
        aria-label="关闭"
      >
        <X size={18} strokeWidth={2} />
      </button>
      {isZoomed && (
        <div className="lightbox__tip">双击 / 点击背景 重置 · ESC 关闭</div>
      )}
      <img
        className="lightbox__img"
        src={url}
        alt=""
        style={{
          transform: `translate(${pos.x}px, ${pos.y}px) scale(${resolvedScale})`,
          cursor: isZoomed ? (isDragging ? "grabbing" : "grab") : "zoom-in",
          transition: isDragging ? "opacity 0.2s ease" : "transform 0.18s ease, opacity 0.2s ease",
          opacity: scale === null ? 0 : 1,
        }}
        onClick={e => e.stopPropagation()}
        onDoubleClick={handleDoubleClick}
        onMouseDown={handleMouseDown}
        onLoad={handleImgLoad}
        draggable={false}
      />
    </div>
  );
}

// ── Image grid inside user bubble ─────────────────────────────────────────────

function MessageImageGrid({ urls, onPreview }: { urls: string[]; onPreview: (url: string) => void }) {
  const count = urls.length;
  return (
    <div className={`msg-images msg-images--${Math.min(count, 4)}`}>
      {urls.slice(0, 4).map((url, i) => (
        <button
          key={i}
          type="button"
          className="msg-images__thumb"
          onClick={() => onPreview(url)}
          aria-label="预览图片"
        >
          <img src={url} alt="" draggable={false} />
          {i === 3 && count > 4 && (
            <span className="msg-images__overflow">+{count - 4}</span>
          )}
        </button>
      ))}
    </div>
  );
}

// ── Timeline ──────────────────────────────────────────────────────────────────

type MessageTimelineProps = {
  messages: ChatMessage[];
  isStreaming: boolean;
  liveSubAgentRows?: LiveSubAgentRow[];
  onSubmitApproval?: (approvalId: string, approve: boolean) => void;
  hasMoreHistory?: boolean;
  isLoadingHistory?: boolean;
  onLoadMoreHistory?: () => void;
  /** Incremented by the parent whenever the timeline should auto-scroll to bottom.
   *  History prepends do NOT increment this, keeping scroll position stable. */
  scrollToBottomVersion?: number;
};

export function MessageTimeline({ messages, isStreaming, liveSubAgentRows, onSubmitApproval, hasMoreHistory, isLoadingHistory, onLoadMoreHistory, scrollToBottomVersion }: MessageTimelineProps) {
  const { formatTime } = useI18n();
  const bottomRef = useRef<HTMLDivElement>(null);
  const [lightboxUrl, setLightboxUrl] = useState<string | null>(null);

  const openLightbox = useCallback((url: string) => setLightboxUrl(url), []);
  const closeLightbox = useCallback(() => setLightboxUrl(null), []);

  const text = useLocaleText({
    zh: {
      empty: "还没有消息。使用下方输入框开启新一轮对话。",
      historySeparator: "以上为历史对话",
      loadMore: "加载更多",
      loadingHistory: "加载中…",
      roles: {
        user: "用户",
        assistant: "Koda",
        system: "系统",
        error: "错误",
        approval: "审批",
        history_separator: "",
        tool_activity: "",
      } as Record<ChatRole, string>,
    },
    en: {
      empty: "No messages yet. Use the composer below to start a conversation.",
      historySeparator: "History",
      loadMore: "Load more",
      loadingHistory: "Loading…",
      roles: {
        user: "You",
        assistant: "Koda",
        system: "System",
        error: "Error",
        approval: "Approval",
        history_separator: "",
        tool_activity: "",
      } as Record<ChatRole, string>,
    },
  });

  // ── Scroll management ────────────────────────────────────────────────────────
  //
  // Two independent concerns:
  //
  // 1. Scroll-to-bottom: triggered only by `scrollToBottomVersion` increments from the
  //    parent (send, done, clearMessages). History prepends never increment it, so the
  //    user's scroll position is unaffected when "加载更多" fires.
  //
  // 2. Scroll restoration for "加载更多": when history is prepended at the top, we
  //    capture the distance-from-bottom before the load and restore it after, so the
  //    currently-visible content stays in place. This runs in useLayoutEffect (before
  //    paint) so there's no flicker.

  const scrollContainerRef = useRef<HTMLElement | null>(null); // cached; walk DOM once
  const prevIsLoadingRef = useRef(false);
  const savedScrollBottomRef = useRef<number | null>(null);

  function getScrollContainer(): HTMLElement | null {
    if (scrollContainerRef.current) return scrollContainerRef.current;
    let el = bottomRef.current?.parentElement ?? null;
    while (el) {
      const oy = getComputedStyle(el).overflowY;
      if (oy === "auto" || oy === "scroll") { scrollContainerRef.current = el; return el; }
      el = el.parentElement;
    }
    return null;
  }

  // "加载更多" scroll anchor: save before load, restore after
  useLayoutEffect(() => {
    const wasLoading = prevIsLoadingRef.current;
    const nowLoading = !!isLoadingHistory;
    prevIsLoadingRef.current = nowLoading;

    const container = getScrollContainer();
    if (!container) return;

    if (!wasLoading && nowLoading) {
      savedScrollBottomRef.current = container.scrollHeight - container.scrollTop;
    } else if (wasLoading && !nowLoading && savedScrollBottomRef.current !== null) {
      container.scrollTop = container.scrollHeight - savedScrollBottomRef.current;
      savedScrollBottomRef.current = null;
    }
  }, [isLoadingHistory]); // eslint-disable-line react-hooks/exhaustive-deps

  // Explicit scroll-to-bottom: only when the parent says so
  useEffect(() => {
    if (scrollToBottomVersion === undefined) return;
    // Skip if a history load is in progress — useLayoutEffect will restore position instead
    if (isLoadingHistory) return;
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [scrollToBottomVersion, isLoadingHistory]);

  // ── Sticky-bottom during streaming ─────────────────────────────────────────
  // If the user is parked near the bottom (< 48px gap), keep auto-scrolling as
  // streaming deltas land. If they scroll up to read earlier content, respect
  // that and stop pulling them down. They regain auto-follow as soon as they
  // scroll back to the bottom.
  const stickToBottomRef = useRef(true);

  useEffect(() => {
    const el = getScrollContainer();
    if (!el) return;
    const onScroll = () => {
      const gap = el.scrollHeight - el.scrollTop - el.clientHeight;
      stickToBottomRef.current = gap < 48;
    };
    el.addEventListener("scroll", onScroll, { passive: true });
    return () => el.removeEventListener("scroll", onScroll);
  }, []);

  useEffect(() => {
    if (!isStreaming) return;
    if (isLoadingHistory) return;
    if (!stickToBottomRef.current) return;
    // Use "auto" — streaming deltas fire rapidly, smooth would queue and lag.
    bottomRef.current?.scrollIntoView({ behavior: "auto" });
  }, [messages, isStreaming, isLoadingHistory]);

  return (
    <>
      <section className="timeline" data-testid="chat-stream">
        {hasMoreHistory && (
          <div className="load-more-history">
            <button
              type="button"
              className="load-more-history__btn"
              disabled={isLoadingHistory}
              onClick={onLoadMoreHistory}
            >
              {isLoadingHistory ? text.loadingHistory : text.loadMore}
            </button>
          </div>
        )}
        <div className="timeline__body" role="log" aria-live="polite">
          {messages.length === 0 ? (
            <EmptyState
              icon={<MessageSquare size={32} strokeWidth={1.5} />}
              title={text.empty}
            />
          ) : (
            groupMessages(messages).map((item, idx) => {
              if (item.kind === "tool_group") {
                return <ActivityStrip key={`tg-${idx}`} items={item.items} />;
              }

              const message = item.msg;

              if (message.role === "user") {
                return (
                  <article key={message.id} className={`message message--user${message.isHistory ? " message--history" : ""}`}>
                    {message.mediaUrls && message.mediaUrls.length > 0 && (
                      <MessageImageGrid urls={message.mediaUrls} onPreview={openLightbox} />
                    )}
                    {message.text && <p className="message__text">{message.text}</p>}
                  </article>
                );
              }

              if (message.role === "assistant") {
                return (
                  <article
                    key={message.id}
                    className={`message message--assistant message--${message.status}${message.isHistory ? " message--history" : ""}`}
                  >
                    <header className="message__meta">
                      <div className="msg-koda-label">
                        <span className="msg-koda-label__avatar">KC</span>
                        <span className="message__role">{text.roles.assistant}</span>
                      </div>
                      <span className="message__time">{formatTime(message.timestamp)}</span>
                    </header>
                    {(message.thinking || message.thinkingStreaming) && (
                      <ThinkingBlock
                        content={message.thinking ?? ""}
                        streaming={message.thinkingStreaming ?? false}
                        startedAt={message.thinkingStartedAt}
                        durationMs={message.thinkingDurationMs}
                      />
                    )}
                    <div className="message__prose">
                      <ReactMarkdown remarkPlugins={[remarkGfm]}>
                        {message.text || ""}
                      </ReactMarkdown>
                      {message.status === "streaming" && (
                        <span className="message__cursor" aria-hidden="true">▋</span>
                      )}
                    </div>
                  </article>
                );
              }

              if (message.role === "history_separator") {
                return (
                  <div key={message.id} className="history-separator">
                    <span className="history-separator__label">{text.historySeparator}</span>
                  </div>
                );
              }

              if (message.role === "approval" && message.approvalId) {
                return (
                  <ApprovalCard
                    key={message.id}
                    approvalId={message.approvalId}
                    toolName={message.toolName ?? "unknown"}
                    inputPreview={message.inputPreview}
                    decision={message.decision ?? "pending"}
                    onApprove={(id) => onSubmitApproval?.(id, true)}
                    onReject={(id) => onSubmitApproval?.(id, false)}
                  />
                );
              }

              if (message.role === "system") {
                return (
                  <div key={message.id} className="system-note">
                    <span className="system-note__text">{message.text}</span>
                  </div>
                );
              }

              // error
              return (
                <article
                  key={message.id}
                  className={`message message--${message.role}`}
                >
                  <header className="message__meta">
                    <span className="message__role">{text.roles[message.role]}</span>
                    <span className="message__time">{formatTime(message.timestamp)}</span>
                  </header>
                  <p className="message__text">{message.text}</p>
                </article>
              );
            })
          )}
          {isStreaming && liveSubAgentRows && liveSubAgentRows.length > 0 && (
            <div className="live-subagent-strip">
              {liveSubAgentRows.map((row) => (
                <div key={row.subAgentId} className={`subagent-row${row.isDone ? " subagent-row--done" : ""}`}>
                  <span className="subagent-row__prefix">↳</span>
                  <span className="subagent-row__label">{row.label}</span>
                  {row.isDone ? (
                    <span className="subagent-row__summary">✓</span>
                  ) : row.toolName ? (
                    <span className="subagent-row__tool">{toolDisplayName(row.toolName)} ({row.toolName})</span>
                  ) : (
                    <span className="subagent-row__tool subagent-row__tool--pending">正在运行…</span>
                  )}
                </div>
              ))}
            </div>
          )}
          <div ref={bottomRef} />
        </div>
      </section>

      {lightboxUrl && <ImageLightbox url={lightboxUrl} onClose={closeLightbox} />}
    </>
  );
}
