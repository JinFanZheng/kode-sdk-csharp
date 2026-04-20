import { useEffect, useRef } from "react";
import type { FormEvent, KeyboardEvent, ClipboardEvent } from "react";
import { ArrowUp, Brain, Paperclip, Square } from "lucide-react";
import { useLocaleText } from "../i18n/I18nProvider";
import { CAP_IMAGE, CAP_TEXT } from "../constants/modelCapabilities";
import { AttachmentBar, type AttachedMedia } from "./composer/AttachmentBar";
import { ModelPicker, type ModelOption } from "./composer/ModelPicker";
import { Tooltip } from "./ui/Tooltip";

export type { AttachedMedia } from "./composer/AttachmentBar";
export type { ModelOption } from "./composer/ModelPicker";

const MAX_TEXTAREA_HEIGHT = 160;

type ModelGroup = {
  name: string;
  capabilities: number;
  selectedId?: string | null;
  available?: ModelOption[];
  onChange?: (modelId: string) => void;
  supportsThinking?: boolean;
  thinkingEnabled?: boolean;
  onToggleThinking?: () => void;
};

type AttachmentGroup = {
  media: AttachedMedia[];
  onAttach: (files: File[]) => void;
  onRemove?: (mediaId: string) => void;
};

type ChatComposerProps = {
  value: string;
  disabled?: boolean;
  placeholder: string;
  isStreaming: boolean;
  activeToolName?: string | null;
  onChange: (next: string) => void;
  onSubmit: () => void;
  onStop?: () => void;
  model?: ModelGroup | null;
  attachment?: AttachmentGroup | null;
};

export function ChatComposer({
  value,
  disabled,
  placeholder,
  isStreaming,
  activeToolName,
  onChange,
  onSubmit,
  onStop,
  model,
  attachment,
}: ChatComposerProps) {
  const textareaRef = useRef<HTMLTextAreaElement>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const supportsVision =
    attachment != null &&
    model != null &&
    (model.capabilities & CAP_TEXT) !== 0 &&
    (model.capabilities & CAP_IMAGE) !== 0;

  useEffect(() => {
    const el = textareaRef.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${Math.min(el.scrollHeight, MAX_TEXTAREA_HEIGHT)}px`;
  }, [value]);

  function handleSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    onSubmit();
  }

  function handleKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      onSubmit();
    }
  }

  function handlePaste(event: ClipboardEvent<HTMLTextAreaElement>) {
    if (!supportsVision || !attachment) return;
    const files = Array.from(event.clipboardData.files).filter((f) =>
      f.type.startsWith("image/"),
    );
    if (files.length > 0) {
      event.preventDefault();
      attachment.onAttach(files);
    }
  }

  function handleFileChange() {
    const files = Array.from(fileInputRef.current?.files ?? []);
    if (files.length > 0 && attachment) attachment.onAttach(files);
    if (fileInputRef.current) fileInputRef.current.value = "";
  }

  const text = useLocaleText({
    zh: {
      live: (tool: string | null | undefined) => tool ? `Koda 正在执行 ${tool}…` : "正在接收回复…",
      waiting: "正在等待 Gateway 同步，请稍候。",
      hint: "Shift+Enter 换行",
      submit: "发送",
      stop: "停止",
      attach: "附加图片",
      thinking: (enabled: boolean) => enabled ? "深度思考：已开启（再次点击关闭）" : "开启深度思考（仅推理型模型）",
    },
    en: {
      live: (tool: string | null | undefined) => tool ? `Koda is running ${tool}…` : "Receiving response…",
      waiting: "Waiting for gateway sync…",
      hint: "Shift+Enter for new line",
      submit: "Send",
      stop: "Stop",
      attach: "Attach image",
      thinking: (enabled: boolean) => enabled ? "Extended thinking: on (click to disable)" : "Enable extended thinking (reasoning models only)",
    },
  });

  const hasAttachedMedia = attachment != null && attachment.media.length > 0;
  const hasContent = value.trim().length > 0 || hasAttachedMedia;
  const canSubmit = !disabled && (isStreaming || hasContent);

  return (
    <form className="composer" data-testid="chat-composer" onSubmit={handleSubmit}>
      {attachment && (
        <AttachmentBar items={attachment.media} onRemove={attachment.onRemove} />
      )}

      <textarea
        ref={textareaRef}
        id="chat-input"
        name="message"
        data-testid="chat-input"
        className="composer__input"
        value={value}
        disabled={disabled}
        placeholder={placeholder}
        rows={1}
        onKeyDown={handleKeyDown}
        onPaste={handlePaste}
        onChange={(event) => onChange(event.target.value)}
      />

      <div className="composer__actions">
        <div className="composer__actions-left">
          {model && (
            <ModelPicker
              modelName={model.name}
              selectedModelId={model.selectedId}
              availableModels={model.available}
              onModelChange={model.onChange}
            />
          )}
          {model?.supportsThinking && (
            <Tooltip content={text.thinking(model.thinkingEnabled ?? false)}>
              <button
                type="button"
                className={
                  model.thinkingEnabled
                    ? "composer__thinking-toggle composer__thinking-toggle--active"
                    : "composer__thinking-toggle"
                }
                onClick={() => model.onToggleThinking?.()}
                disabled={disabled || isStreaming}
                aria-pressed={model.thinkingEnabled ?? false}
                aria-label={text.thinking(model.thinkingEnabled ?? false)}
              >
                <Brain size={15} strokeWidth={2} />
              </button>
            </Tooltip>
          )}
          {supportsVision && (
            <>
              <input
                ref={fileInputRef}
                type="file"
                accept="image/*"
                multiple
                hidden
                onChange={handleFileChange}
              />
              <button
                type="button"
                className="composer__attach-btn"
                onClick={() => fileInputRef.current?.click()}
                disabled={disabled || isStreaming}
                aria-label={text.attach}
              >
                <Paperclip size={15} strokeWidth={2} />
              </button>
            </>
          )}
        </div>

        {(disabled || isStreaming) && (
          <p className="composer__hint">
            {disabled ? text.waiting : text.live(activeToolName)}
          </p>
        )}
        {isStreaming ? (
          <button
            data-testid="chat-stop"
            className="composer__submit composer__submit--stop"
            type="button"
            disabled={disabled}
            aria-label={text.stop}
            onClick={onStop}
          >
            <Square size={14} strokeWidth={2.5} fill="currentColor" />
          </button>
        ) : (
          <button
            data-testid="chat-submit"
            className="composer__submit"
            type="submit"
            disabled={!canSubmit}
            aria-label={text.submit}
          >
            <ArrowUp size={16} strokeWidth={2.5} />
          </button>
        )}
      </div>
    </form>
  );
}
