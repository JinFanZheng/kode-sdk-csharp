import React, { useCallback, useState } from "react";
import { Check, Copy } from "lucide-react";
import type { ChannelDeliveryPayload, InboxText } from "./inboxTranslations";
import { resolveGatewayPath } from "../../lib/config";
import { formatDeliveryMode } from "./inboxTranslations";

type Props = {
  payload: ChannelDeliveryPayload;
  text: InboxText;
};

function buildThreadDetailApi(bindingId: string) {
  return resolveGatewayPath(`/api/channels/threads/${bindingId}`);
}

function buildThreadAuditApi(bindingId: string) {
  return resolveGatewayPath(`/api/channels/threads/${bindingId}/audit?limit=20`);
}

type CopiedKey = "detail" | "audit" | null;

export function InboxDeliveryContext({ payload, text }: Props) {
  const [copied, setCopied] = useState<CopiedKey>(null);

  const handleCopy = useCallback(async (key: CopiedKey, value: string) => {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(key);
      setTimeout(() => setCopied(null), 1500);
    } catch {
      // clipboard API may not be available (e.g. non-HTTPS); silently ignore
    }
  }, []);
  return (
    <div className="inbox-delivery-context">
      <p className="metric-label">{text.deliveryContext}</p>
      <div className="control-plane-summary-grid">
        <div className="metric-item">
          <span className="metric-label">{text.connector}</span>
          <span className="metric-value metric-value--path">{payload.connectorKind}</span>
        </div>
        <div className="metric-item">
          <span className="metric-label">{text.account}</span>
          <span className="metric-value metric-value--path">{payload.accountId}</span>
        </div>
        <div className="metric-item">
          <span className="metric-label">{text.threadBinding}</span>
          <span className="metric-value metric-value--path">{payload.bindingId}</span>
        </div>
        <div className="metric-item">
          <span className="metric-label">{text.thread}</span>
          <span className="metric-value metric-value--path">{payload.externalThreadId}</span>
        </div>
        <div className="metric-item">
          <span className="metric-label">{text.draft}</span>
          <span className="metric-value metric-value--path">{payload.draftId}</span>
        </div>
        <div className="metric-item">
          <span className="metric-label">{text.status}</span>
          <span className="metric-value">{formatDeliveryMode(payload.deliveryMode, text)}</span>
        </div>
        <div className="metric-item metric-item--full-width">
          <span className="metric-label">{text.messagePreview}</span>
          <span className="metric-value">{payload.messageText}</span>
        </div>
        {payload.mediaAttachments
          ?.filter((a) => a.contentType.startsWith("image/"))
          .map((a) => (
            <div key={a.mediaId} className="metric-item metric-item--full-width">
              <img
                src={`/api/media/${a.mediaId}`}
                alt={a.mediaId}
                className="inbox-media-thumbnail"
                data-testid={`inbox-media-thumbnail-${a.mediaId}`}
              />
            </div>
          ))}
        <div className="metric-item">
          <span className="metric-label">{text.threadDetailApi}</span>
          <span className="metric-value metric-value--path" style={{ display: "flex", alignItems: "center", gap: 4 }}>
            {buildThreadDetailApi(payload.bindingId)}
            <button
              type="button"
              className="inbox-copy-btn"
              data-testid="copy-thread-detail-api"
              onClick={() => { void handleCopy("detail", buildThreadDetailApi(payload.bindingId)); }}
              title="Copy"
            >
              {copied === "detail" ? <Check size={14} /> : <Copy size={14} />}
            </button>
          </span>
        </div>
        <div className="metric-item">
          <span className="metric-label">{text.threadAuditApi}</span>
          <span className="metric-value metric-value--path" style={{ display: "flex", alignItems: "center", gap: 4 }}>
            {buildThreadAuditApi(payload.bindingId)}
            <button
              type="button"
              className="inbox-copy-btn"
              data-testid="copy-thread-audit-api"
              onClick={() => { void handleCopy("audit", buildThreadAuditApi(payload.bindingId)); }}
              title="Copy"
            >
              {copied === "audit" ? <Check size={14} /> : <Copy size={14} />}
            </button>
          </span>
        </div>
      </div>
    </div>
  );
}
