import React from "react";
import type { ChannelPushResult, InboxItem } from "../../types/contracts";
import type { InboxText } from "./inboxTranslations";
import { PushToChannelButton } from "./InboxPushToChannel";

type Props = {
  item: InboxItem;
  onRefresh: () => void;
  text: InboxText;
};

export function InboxAutomationResultPush({ item, onRefresh, text }: Props) {
  if (item.kind !== "AutomationResult") return null;

  let channelPushResults: ChannelPushResult[] | undefined;
  let notifyMode: string | undefined;
  let notificationChannels: string[] | undefined;

  try {
    const p = JSON.parse(item.payloadJson ?? "{}") as Record<string, unknown>;
    channelPushResults = p.channelPushResults as ChannelPushResult[] | undefined;
    notifyMode = p.notifyMode as string | undefined;
    notificationChannels = p.notificationChannels as string[] | undefined;
  } catch {
    // ignore parse errors
  }

  if (!notificationChannels || notificationChannels.length === 0) return null;

  return (
    <div className="inbox-detail-push-status">
      {channelPushResults && channelPushResults.length > 0 ? (
        <div className="inbox-push-results">
          {channelPushResults.map((r) => (
            <div
              key={r.bindingId}
              className={`inbox-push-result ${r.ok ? "inbox-push-result--ok" : "inbox-push-result--fail"}`}
            >
              <span className="inbox-push-result__binding">{r.bindingId}</span>
              <span className="inbox-push-result__status">{r.ok ? "✓" : "✗"}</span>
              {!r.ok && r.errorMessage ? (
                <span className="inbox-push-result__error">{r.errorMessage}</span>
              ) : null}
            </div>
          ))}
        </div>
      ) : notifyMode === "Approval" ? (
        <PushToChannelButton
          inboxId={item.id}
          channels={notificationChannels}
          onSuccess={onRefresh}
          text={text}
        />
      ) : null}
    </div>
  );
}
