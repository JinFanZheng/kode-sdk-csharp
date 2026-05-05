import "@testing-library/jest-dom";
import React from "react";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { InboxApprovalDesk } from "../components/inbox/InboxApprovalDesk";
import type { Approval, InboxItem } from "../types/contracts";
import { renderWithI18n } from "./test-utils";

const originalFetch = global.fetch;

function jsonResponse(payload: unknown, status = 200): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 200 ? "OK" : "ERROR",
    json: async () => payload,
    text: async () => JSON.stringify(payload),
  } as Response;
}

function resolveRequestUrl(input: string | URL | Request): string {
  if (typeof input === "string") {
    return input;
  }

  if (input instanceof URL) {
    return input.toString();
  }

  return input.url;
}

describe("InboxApprovalDesk", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn());
  });

  afterEach(() => {
    vi.restoreAllMocks();
    global.fetch = originalFetch;
  });

  it("loads inbox and approvals with stable test ids", async () => {
    vi.mocked(fetch).mockImplementation(async (input) => {
      const url = resolveRequestUrl(input);

      if (url.includes("/api/inbox")) {
        return jsonResponse({
          items: [
            {
              id: "inbox-001",
              kind: "Approval",
              status: "Open",
              title: "Outbound approval",
              summary: "Need user confirmation",
              source: "runtime.main_session.approval",
              createdAt: "2026-03-18T09:00:00.000Z",
              updatedAt: "2026-03-18T09:00:00.000Z",
              requiresAction: true,
              approvalId: "approval-001",
            } satisfies InboxItem,
          ],
        });
      }

      if (url.includes("/api/approvals")) {
        return jsonResponse({
          items: [
            {
              id: "approval-001",
              kind: "ExternalAction",
              status: "Pending",
              title: "Send channel message",
              summary: "Tool requires approval",
              source: "runtime.main_session.approval",
              requestedAt: "2026-03-18T09:00:00.000Z",
              updatedAt: "2026-03-18T09:00:00.000Z",
              inboxItemId: "inbox-001",
            } satisfies Approval,
          ],
        });
      }

      throw new Error(`Unexpected request: ${url}`);
    });

    renderWithI18n(<InboxApprovalDesk />);

    await waitFor(() => {
      expect(screen.getByTestId("inbox-item-inbox-001")).toBeInTheDocument();
    });

    expect(screen.getByTestId("inbox-list")).toBeInTheDocument();
    expect(screen.getByTestId("inbox-refresh")).toBeInTheDocument();
    expect(screen.getByTestId("inbox-item-inbox-001")).toHaveTextContent("Outbound approval");

    // Auto-selects first item; linked pending approval renders approve/reject in detail pane
    await waitFor(() => {
      expect(screen.getByTestId("inbox-detail")).toHaveTextContent("Send channel message");
    });
    expect(screen.getByTestId("approval-approve")).toBeInTheDocument();
    expect(screen.getByTestId("approval-reject")).toBeInTheDocument();
  });

  it("supports approval decisions and inbox status updates", async () => {
    const approvals: Approval[] = [
      {
        id: "approval-002",
        kind: "ExternalAction",
        status: "Pending",
        title: "Call risky tool",
        summary: "Awaiting decision",
        source: "runtime.main_session.approval",
        requestedAt: "2026-03-18T10:00:00.000Z",
        updatedAt: "2026-03-18T10:00:00.000Z",
        inboxItemId: "inbox-002",
      },
    ];

    const inboxItems: InboxItem[] = [
      {
        id: "inbox-002",
        kind: "Approval",
        status: "Open",
        title: "Risky tool",
        summary: "Handle approval result",
        source: "runtime.main_session.approval",
        createdAt: "2026-03-18T10:00:00.000Z",
        updatedAt: "2026-03-18T10:00:00.000Z",
        requiresAction: true,
        approvalId: "approval-002",
      },
    ];

    const decisionPayloads: unknown[] = [];

    vi.mocked(fetch).mockImplementation(async (input, init) => {
      const url = resolveRequestUrl(input);
      const method = init?.method ?? "GET";

      if (url.includes("/api/approvals/approval-002/approve") && method === "POST") {
        decisionPayloads.push(JSON.parse((init?.body as string) ?? "{}"));
        approvals[0] = {
          ...approvals[0],
          status: "Approved",
          updatedAt: "2026-03-18T10:02:00.000Z",
          decisionNote: "ship it",
        };
        return jsonResponse(approvals[0]);
      }

      if (url.includes("/api/inbox/inbox-002/status") && method === "PATCH") {
        const payload = JSON.parse((init?.body as string) ?? "{}") as { status: InboxItem["status"] };
        inboxItems[0] = {
          ...inboxItems[0],
          status: payload.status,
          updatedAt: "2026-03-18T10:03:00.000Z",
        };
        return jsonResponse(inboxItems[0]);
      }

      if (url.includes("/api/inbox")) {
        return jsonResponse({ items: inboxItems });
      }

      if (url.includes("/api/approvals")) {
        return jsonResponse({ items: approvals });
      }

      throw new Error(`Unexpected request: ${method} ${url}`);
    });

    renderWithI18n(<InboxApprovalDesk />);

    const user = userEvent.setup();

    // Auto-selects inbox-002; linked approval section appears in detail pane
    await waitFor(() => {
      expect(screen.getByTestId("approval-note-approval-002")).toBeInTheDocument();
    });

    await user.type(screen.getByTestId("approval-note-approval-002"), "ship it");
    await user.click(screen.getByTestId("approval-approve"));

    await waitFor(() => {
      expect(screen.getByTestId("inbox-detail")).toHaveTextContent("已批准");
    }, { timeout: 3000 });

    expect(decisionPayloads).toEqual([{ note: "ship it" }]);

    await user.selectOptions(screen.getByTestId("inbox-status-inbox-002"), "Resolved");

    await waitFor(() => {
      const element = screen.getByTestId("inbox-status-inbox-002") as HTMLSelectElement;
      expect(element.value).toBe("Resolved");
    });
  });

  it("keeps inbox detail in sync with selected item and shows linked approval info", async () => {
    vi.mocked(fetch).mockImplementation(async (input) => {
      const url = resolveRequestUrl(input);

      if (url.includes("/api/inbox")) {
        return jsonResponse({
          items: [
            {
              id: "inbox-010",
              kind: "Approval",
              status: "Open",
              title: "First inbox",
              summary: "First inbox summary",
              source: "runtime.main_session.approval",
              createdAt: "2026-03-18T09:00:00.000Z",
              updatedAt: "2026-03-18T09:00:00.000Z",
              requiresAction: true,
              approvalId: "approval-010",
              correlationId: "corr-010",
            } satisfies InboxItem,
            {
              id: "inbox-011",
              kind: "PluginRequest",
              status: "Acknowledged",
              title: "Second inbox",
              summary: "Second inbox summary",
              source: "runtime.automation.session",
              createdAt: "2026-03-18T10:00:00.000Z",
              updatedAt: "2026-03-18T10:10:00.000Z",
              requiresAction: false,
              approvalId: "approval-011",
              sessionId: "session-011",
            } satisfies InboxItem,
          ],
        });
      }

      if (url.includes("/api/approvals")) {
        return jsonResponse({
          items: [
            {
              id: "approval-010",
              kind: "ExternalAction",
              status: "Pending",
              title: "First approval",
              summary: "First approval summary",
              source: "runtime.main_session.approval",
              requestedAt: "2026-03-18T09:00:00.000Z",
              updatedAt: "2026-03-18T09:00:00.000Z",
              inboxItemId: "inbox-010",
            } satisfies Approval,
            {
              id: "approval-011",
              kind: "AutomationAction",
              status: "Approved",
              title: "Second approval",
              summary: "Second approval summary",
              source: "runtime.automation.session",
              requestedAt: "2026-03-18T10:00:00.000Z",
              updatedAt: "2026-03-18T10:12:00.000Z",
              inboxItemId: "inbox-011",
              sessionId: "session-011",
            } satisfies Approval,
          ],
        });
      }

      throw new Error(`Unexpected request: ${url}`);
    });

    renderWithI18n(<InboxApprovalDesk />);

    const user = userEvent.setup();

    // Auto-selects first item; detail shows inbox title and linked approval title
    await waitFor(() => {
      expect(screen.getByTestId("inbox-detail")).toHaveTextContent("First inbox");
      expect(screen.getByTestId("inbox-detail")).toHaveTextContent("First approval");
    });

    await user.click(screen.getByTestId("inbox-item-inbox-011"));

    await waitFor(() => {
      expect(screen.getByTestId("inbox-detail")).toHaveTextContent("Second inbox");
      expect(screen.getByTestId("inbox-detail")).toHaveTextContent("Second approval");
      expect(screen.getByTestId("inbox-detail")).toHaveTextContent("session-011");
    });
  });

  it("renders error state when endpoint fails", async () => {
    vi.mocked(fetch).mockImplementation(async (input) => {
      const url = resolveRequestUrl(input);

      if (url.includes("/api/inbox")) {
        return jsonResponse({ message: "inbox unavailable" }, 500);
      }

      if (url.includes("/api/approvals")) {
        return jsonResponse({ items: [] });
      }

      throw new Error(`Unexpected request: ${url}`);
    });

    renderWithI18n(<InboxApprovalDesk />);

    await waitFor(() => {
      expect(screen.getByTestId("inbox-approval-error")).toBeInTheDocument();
    });

    expect(screen.getByText("inbox unavailable")).toBeInTheDocument();
  });

  it("renders channel delivery payload context in inbox detail", async () => {
    const payloadJson = JSON.stringify({
      draftId: "draft-chan-001",
      bindingId: "binding-chan-001",
      connectorKind: "Telegram",
      accountId: "telegram-main",
      externalThreadId: "10001",
      deliveryMode: "DraftApproval",
      messageText: "Thanks, I have a draft reply ready.",
    });

    vi.mocked(fetch).mockImplementation(async (input) => {
      const url = resolveRequestUrl(input);

      if (url.includes("/api/inbox")) {
        return jsonResponse({
          items: [
            {
              id: "inbox-channel-001",
              kind: "ChannelUpdate",
              status: "Open",
              title: "Channel draft pending",
              summary: "Review the draft before delivery",
              source: "channel.delivery",
              createdAt: "2026-03-18T09:00:00.000Z",
              updatedAt: "2026-03-18T09:10:00.000Z",
              requiresAction: true,
              approvalId: "approval-channel-001",
              payloadJson,
            } satisfies InboxItem,
          ],
        });
      }

      if (url.includes("/api/approvals")) {
        return jsonResponse({
          items: [
            {
              id: "approval-channel-001",
              kind: "ChannelDelivery",
              status: "Pending",
              title: "Approve channel delivery",
              summary: "Operator review required",
              source: "channel.delivery",
              requestedAt: "2026-03-18T09:00:00.000Z",
              updatedAt: "2026-03-18T09:10:00.000Z",
              inboxItemId: "inbox-channel-001",
              payloadJson,
            } satisfies Approval,
          ],
        });
      }

      throw new Error(`Unexpected request: ${url}`);
    });

    renderWithI18n(<InboxApprovalDesk />);

    // Auto-selects the item; inbox-detail shows delivery context + linked approval title
    await waitFor(() => {
      expect(screen.getByTestId("inbox-detail")).toHaveTextContent("Channel draft pending");
    });

    const detail = screen.getByTestId("inbox-detail");
    expect(within(detail).getByText("渠道投递上下文")).toBeInTheDocument();
    expect(detail).toHaveTextContent("Telegram");
    expect(detail).toHaveTextContent("telegram-main");
    expect(detail).toHaveTextContent("binding-chan-001");
    expect(detail).toHaveTextContent("draft-chan-001");
    expect(detail).toHaveTextContent("10001");
    expect(detail).toHaveTextContent("Thanks, I have a draft reply ready.");
    expect(detail).toHaveTextContent("/api/channels/threads/binding-chan-001");
    expect(detail).toHaveTextContent("/api/channels/threads/binding-chan-001/audit?limit=20");
    expect(detail).toHaveTextContent("Approve channel delivery");
  });
});
