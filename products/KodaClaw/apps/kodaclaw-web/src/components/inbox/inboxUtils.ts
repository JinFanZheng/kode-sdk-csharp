import type { ChannelDeliveryPayload, InboxText } from "./inboxTranslations";

export function formatRelativeTime(
  isoString: string | null | undefined,
  text: InboxText,
  nowMs?: number,
): string {
  if (!isoString) return text.common.none;
  const diff = (nowMs ?? Date.now()) - new Date(isoString).getTime();
  const mins = Math.floor(diff / 60_000);
  if (mins < 1) return text.relativeTime.justNow;
  if (mins < 60) return text.relativeTime.minutesAgo(mins);
  const hours = Math.floor(mins / 60);
  if (hours < 24) return text.relativeTime.hoursAgo(hours);
  const days = Math.floor(hours / 24);
  return text.relativeTime.daysAgo(days);
}

export function parseChannelDeliveryPayload(
  payloadJson?: string | null,
): ChannelDeliveryPayload | null {
  if (!payloadJson) return null;
  try {
    const parsed = JSON.parse(payloadJson) as Partial<ChannelDeliveryPayload>;
    if (
      typeof parsed.draftId !== "string" ||
      typeof parsed.bindingId !== "string" ||
      typeof parsed.connectorKind !== "string" ||
      typeof parsed.accountId !== "string" ||
      typeof parsed.externalThreadId !== "string" ||
      typeof parsed.deliveryMode !== "string" ||
      typeof parsed.messageText !== "string"
    ) {
      return null;
    }
    return {
      draftId: parsed.draftId,
      bindingId: parsed.bindingId,
      connectorKind: parsed.connectorKind,
      accountId: parsed.accountId,
      externalThreadId: parsed.externalThreadId,
      deliveryMode: parsed.deliveryMode,
      messageText: parsed.messageText,
      mediaAttachments: Array.isArray(parsed.mediaAttachments)
        ? parsed.mediaAttachments
        : undefined,
    };
  } catch {
    return null;
  }
}
