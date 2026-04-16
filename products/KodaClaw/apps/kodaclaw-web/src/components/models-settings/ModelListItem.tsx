import React, { useState } from "react";
import {
  Check,
  Copy,
  CopyPlus,
  Eye,
  ImageIcon,
  Layers,
  MessageSquare,
  Mic,
  Pencil,
  Star,
  Trash2,
  Zap,
} from "lucide-react";
import { Button } from "../ui/Button";
import { Tooltip } from "../ui/Tooltip";
import type { ModelConnectionTestResponse } from "../../types/contracts";
import type { ModelsSettingsText } from "./locale";
import {
  AccountModelPair,
  CAP_AUDIO,
  CAP_FILE,
  CAP_IMAGE,
  CAP_TEXT,
  CAP_VIDEO,
  fmtK,
} from "./types";

export type CardTestState = {
  testing: boolean;
  result: ModelConnectionTestResponse | null;
};

const CAP_ICON_MAP: [number, React.ReactNode, string, string][] = [
  [CAP_TEXT, <MessageSquare size={12} />, "文本", "Text"],
  [CAP_IMAGE, <Eye size={12} />, "图像", "Image"],
  [CAP_VIDEO, <Layers size={12} />, "视频", "Video"],
  [CAP_FILE, <ImageIcon size={12} />, "文件", "File"],
  [CAP_AUDIO, <Mic size={12} />, "音频", "Audio"],
];

export interface ModelListItemProps {
  pair: AccountModelPair;
  text: ModelsSettingsText;
  isZh: boolean;
  testState: CardTestState | undefined;
  isMutating: boolean;
  onEdit: () => void;
  onDuplicate: () => void;
  onSetDefault: () => void;
  onDelete: () => void;
  onQuickTest: () => void;
}

export function ModelListItem({
  pair,
  text,
  isZh,
  testState,
  isMutating,
  onEdit,
  onDuplicate,
  onSetDefault,
  onDelete,
  onQuickTest,
}: ModelListItemProps) {
  const { model } = pair;
  const ctxK = model.contextWindowSize ? fmtK(model.contextWindowSize) : null;
  const maxOutK = model.maxOutputTokens ? fmtK(model.maxOutputTokens) : null;
  const enabledCaps = CAP_ICON_MAP.filter(([flag]) => model.capabilities & flag);
  const [copied, setCopied] = useState(false);

  function handleCopyModelId() {
    void navigator.clipboard.writeText(model.modelId).then(() => {
      setCopied(true);
      setTimeout(() => setCopied(false), 1500);
    });
  }

  return (
    <li>
      <article
        className={`model-list-item ${!model.enabled ? "model-list-item--disabled" : ""}`}
        data-testid={`model-item-${model.id}`}
      >
        <div className="model-list-item__info">
          <div className="model-list-item__name">
            <span>{model.displayName}</span>
            <div className="model-list-item__badges">
              {model.isGlobalDefault && (
                <span className="control-plane-chip control-plane-chip--active">
                  {text.common.defaultBadge}
                </span>
              )}
              {model.isReasoning && (
                <span className="control-plane-chip">
                  {isZh ? "推理" : "Reasoning"}
                </span>
              )}
              {!model.enabled && (
                <span className="control-plane-chip control-plane-chip--inactive">
                  {text.detail.disabled}
                </span>
              )}
            </div>
          </div>
          <div className="model-list-item__meta">
            <code className="model-list-item__model-id">{model.modelId}</code>
            <button
              type="button"
              className="model-list-item__copy-btn"
              onClick={handleCopyModelId}
              title={copied ? text.modelList.copied : text.modelList.copyModelId}
            >
              {copied ? <Check size={11} strokeWidth={2.5} /> : <Copy size={11} strokeWidth={2} />}
            </button>
            {(ctxK || maxOutK) && (
              <>
                <span className="model-endpoint-card__sep">·</span>
                {ctxK && <span>{ctxK} {isZh ? "上下文" : "ctx"}</span>}
                {ctxK && maxOutK && <span className="model-endpoint-card__sep">·</span>}
                {maxOutK && <span>{maxOutK} {isZh ? "最大输出" : "max out"}</span>}
              </>
            )}
            {enabledCaps.length > 0 && (
              <>
                <span className="model-endpoint-card__sep">·</span>
                {enabledCaps.map(([flag, icon, labelZh, labelEn]) => (
                  <Tooltip key={flag} content={isZh ? labelZh : labelEn}>
                    <span className="model-cap-icon">{icon}</span>
                  </Tooltip>
                ))}
              </>
            )}
          </div>
        </div>

        <div className="model-list-item__actions">
          {testState?.result ? (
            <span
              className={`model-list-item__test-result desk-feedback ${testState.result.ok ? "desk-feedback--success" : "desk-feedback--error"}`}
            >
              {testState.result.ok
                ? `✓ ${testState.result.latencyMs}ms`
                : `✗ ${text.composer.errorCodes[testState.result.error ?? ""] ?? testState.result.error}`}
            </span>
          ) : null}
          <Tooltip content={text.modelList.quickTest}>
            <Button
              variant="ghost"
              size="sm"
              className="model-list-item__icon-btn"
              aria-label={text.modelList.quickTest}
              onClick={onQuickTest}
              disabled={isMutating || testState?.testing === true}
            >
              {testState?.testing ? <Zap size={13} className="icon-spinning" /> : <Zap size={13} />}
            </Button>
          </Tooltip>
          <Tooltip content={text.modelList.edit}>
            <Button
              variant="ghost"
              size="sm"
              className="model-list-item__icon-btn"
              aria-label={text.modelList.edit}
              onClick={onEdit}
              disabled={isMutating}
            >
              <Pencil size={13} />
            </Button>
          </Tooltip>
          <Tooltip content={text.modelList.duplicate}>
            <Button
              variant="ghost"
              size="sm"
              className="model-list-item__icon-btn"
              aria-label={text.modelList.duplicate}
              onClick={onDuplicate}
              disabled={isMutating}
            >
              <CopyPlus size={13} />
            </Button>
          </Tooltip>
          {!model.isGlobalDefault && (
            <Tooltip content={text.modelList.setDefault}>
              <Button
                variant="ghost"
                size="sm"
                className="model-list-item__icon-btn"
                aria-label={text.modelList.setDefault}
                data-testid="model-default"
                onClick={onSetDefault}
                disabled={isMutating || !model.enabled}
              >
                <Star size={13} />
              </Button>
            </Tooltip>
          )}
          <Tooltip content={text.modelList.delete}>
            <Button
              variant="ghost"
              size="sm"
              className="model-list-item__icon-btn model-list-item__icon-btn--danger"
              aria-label={text.modelList.delete}
              data-testid={`model-delete-${model.id}`}
              onClick={onDelete}
              disabled={isMutating}
            >
              <Trash2 size={13} />
            </Button>
          </Tooltip>
        </div>
      </article>
    </li>
  );
}
