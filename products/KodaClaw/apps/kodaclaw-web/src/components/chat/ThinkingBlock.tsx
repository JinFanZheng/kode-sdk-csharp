import { useEffect, useState } from "react";
import { Brain, ChevronDown } from "lucide-react";
import { useLocaleText } from "../../i18n/I18nProvider";

type ThinkingBlockProps = {
  content: string;
  streaming: boolean;
  startedAt?: number;
  durationMs?: number;
};

function formatSeconds(ms: number): string {
  if (ms < 1000) return `${ms}ms`;
  const s = ms / 1000;
  return s >= 10 ? `${Math.round(s)}s` : `${s.toFixed(1)}s`;
}

export function ThinkingBlock({ content, streaming, startedAt, durationMs }: ThinkingBlockProps) {
  const [userOverride, setUserOverride] = useState<boolean | null>(null);
  const [liveMs, setLiveMs] = useState(0);

  useEffect(() => {
    if (!streaming || !startedAt) return;
    const tick = () => setLiveMs(Date.now() - startedAt);
    tick();
    const id = window.setInterval(tick, 500);
    return () => window.clearInterval(id);
  }, [streaming, startedAt]);

  const text = useLocaleText({
    zh: {
      streaming: "思考中",
      done: "思考",
      tokens: (n: number) => `${n} tokens`,
    },
    en: {
      streaming: "Thinking",
      done: "Thought",
      tokens: (n: number) => `${n} tokens`,
    },
  });

  if (!content && !streaming) return null;

  const open = userOverride ?? streaming;
  const elapsedMs = durationMs ?? (streaming ? liveMs : 0);
  const tokenEstimate = Math.max(1, Math.round(content.length / 4));

  return (
    <div className={`thinking-block${streaming ? " thinking-block--streaming" : ""}${open ? " thinking-block--open" : ""}`}>
      <button
        type="button"
        className="thinking-block__summary"
        onClick={() => setUserOverride(!open)}
        aria-expanded={open}
      >
        <Brain size={12} strokeWidth={2} className="thinking-block__icon" aria-hidden="true" />
        <span className="thinking-block__label">
          {streaming ? text.streaming : text.done}
          {elapsedMs > 0 && <> · {formatSeconds(elapsedMs)}</>}
          {!streaming && content && <> · {text.tokens(tokenEstimate)}</>}
        </span>
        <ChevronDown size={12} strokeWidth={2} className="thinking-block__chevron" aria-hidden="true" />
      </button>
      {open && content && (
        <pre className="thinking-block__body">{content}</pre>
      )}
    </div>
  );
}
