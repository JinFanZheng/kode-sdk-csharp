import type { ChannelDeliveryPayload } from "./inboxTranslations";

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
