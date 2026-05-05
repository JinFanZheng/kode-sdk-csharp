import "@testing-library/jest-dom";
import React from "react";
import { fireEvent, screen, waitFor, within } from "@testing-library/react";
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

  it("supports keyboard navigation with ArrowDown and ArrowUp", async () => {
    vi.mocked(fetch).mockImplementation(async (input) => {
      const url = resolveRequestUrl(input);
      if (url.includes("/api/inbox")) {
        return jsonResponse({
          items: [
            {
              id: "inbox-kb-1", kind: "Approval", status: "Open",
              title: "First item", summary: "First", source: "src",
              createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
              requiresAction: false, approvalId: "apr-1",
            } satisfies InboxItem,
            {
              id: "inbox-kb-2", kind: "Approval", status: "Open",
              title: "Second item", summary: "Second", source: "src",
              createdAt: "2026-01-02T00:00:00Z", updatedAt: "2026-01-02T00:00:00Z",
              requiresAction: false, approvalId: "apr-2",
            } satisfies InboxItem,
          ],
        });
      }
      if (url.includes("/api/approvals")) {
        return jsonResponse({
          items: [
            { id: "apr-1", kind: "ExternalAction", status: "Pending", title: "Apr 1", summary: "", source: "src", requestedAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z", inboxItemId: "inbox-kb-1" } satisfies Approval,
            { id: "apr-2", kind: "ExternalAction", status: "Pending", title: "Apr 2", summary: "", source: "src", requestedAt: "2026-01-02T00:00:00Z", updatedAt: "2026-01-02T00:00:00Z", inboxItemId: "inbox-kb-2" } satisfies Approval,
          ],
        });
      }
      throw new Error(`Unexpected: ${url}`);
    });

    renderWithI18n(<InboxApprovalDesk />);

    await waitFor(() => {
      expect(screen.getByTestId("inbox-item-inbox-kb-1")).toBeInTheDocument();
      expect(screen.getByTestId("inbox-item-inbox-kb-2")).toBeInTheDocument();
    });

    const listbox = screen.getByTestId("inbox-list").querySelector('[role="listbox"]')!;
    expect(listbox).toBeInTheDocument();

    // First item auto-selected
    expect(screen.getByTestId("inbox-item-inbox-kb-1")).toHaveAttribute("aria-selected", "true");

    // ArrowDown moves to second item
    fireEvent.keyDown(listbox, { key: "ArrowDown" });
    await waitFor(() => {
      expect(screen.getByTestId("inbox-item-inbox-kb-2")).toHaveAttribute("aria-selected", "true");
      expect(screen.getByTestId("inbox-item-inbox-kb-1")).toHaveAttribute("aria-selected", "false");
    });

    // ArrowDown at last item stays put
    fireEvent.keyDown(listbox, { key: "ArrowDown" });
    await waitFor(() => {
      expect(screen.getByTestId("inbox-item-inbox-kb-2")).toHaveAttribute("aria-selected", "true");
    });

    // ArrowUp moves back
    fireEvent.keyDown(listbox, { key: "ArrowUp" });
    await waitFor(() => {
      expect(screen.getByTestId("inbox-item-inbox-kb-1")).toHaveAttribute("aria-selected", "true");
    });
  });

  it("filters inbox items by search query", async () => {
    vi.mocked(fetch).mockImplementation(async (input) => {
      const url = resolveRequestUrl(input);
      if (url.includes("/api/inbox")) {
        return jsonResponse({
          items: [
            {
              id: "inbox-s-1", kind: "Approval", status: "Open",
              title: "Deploy request", summary: "Production deploy", source: "src",
              createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
              requiresAction: false, approvalId: "apr-s-1",
            } satisfies InboxItem,
            {
              id: "inbox-s-2", kind: "AutomationResult", status: "Open",
              title: "Daily report", summary: "Automated summary", source: "src",
              createdAt: "2026-01-02T00:00:00Z", updatedAt: "2026-01-02T00:00:00Z",
              requiresAction: false,
            } satisfies InboxItem,
          ],
        });
      }
      if (url.includes("/api/approvals")) {
        return jsonResponse({
          items: [
            { id: "apr-s-1", kind: "ExternalAction", status: "Pending", title: "Apr", summary: "", source: "src", requestedAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z", inboxItemId: "inbox-s-1" } satisfies Approval,
          ],
        });
      }
      throw new Error(`Unexpected: ${url}`);
    });

    renderWithI18n(<InboxApprovalDesk />);
    const user = userEvent.setup();

    await waitFor(() => {
      expect(screen.getByTestId("inbox-item-inbox-s-1")).toBeInTheDocument();
      expect(screen.getByTestId("inbox-item-inbox-s-2")).toBeInTheDocument();
    });

    const searchInput = screen.getByTestId("inbox-search");
    await user.type(searchInput, "deploy");

    // Wait for debounce + render — "Deploy request" stays, "Daily report" hides
    await waitFor(() => {
      expect(screen.getByTestId("inbox-item-inbox-s-1")).toBeInTheDocument();
      expect(screen.queryByTestId("inbox-item-inbox-s-2")).not.toBeInTheDocument();
    });

    // Clear search restores both
    await user.clear(searchInput);
    await waitFor(() => {
      expect(screen.getByTestId("inbox-item-inbox-s-1")).toBeInTheDocument();
      expect(screen.getByTestId("inbox-item-inbox-s-2")).toBeInTheDocument();
    });
  });

  it("renders copy buttons on API paths in delivery context", async () => {
    const payloadJson = JSON.stringify({
      draftId: "d-1", bindingId: "b-1", connectorKind: "Slack",
      accountId: "slack-1", externalThreadId: "C01", deliveryMode: "DraftApproval",
      messageText: "Hello",
    });

    vi.mocked(fetch).mockImplementation(async (input) => {
      const url = resolveRequestUrl(input);
      if (url.includes("/api/inbox")) {
        return jsonResponse({
          items: [{
            id: "inbox-copy-1", kind: "ChannelUpdate", status: "Open",
            title: "Copy test", summary: "Test", source: "ch",
            createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
            requiresAction: true, approvalId: "apr-copy-1", payloadJson,
          } satisfies InboxItem],
        });
      }
      if (url.includes("/api/approvals")) {
        return jsonResponse({
          items: [{
            id: "apr-copy-1", kind: "ChannelDelivery", status: "Pending",
            title: "Apr copy", summary: "", source: "ch",
            requestedAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
            inboxItemId: "inbox-copy-1", payloadJson,
          } satisfies Approval],
        });
      }
      throw new Error(`Unexpected: ${url}`);
    });

    renderWithI18n(<InboxApprovalDesk />);

    await waitFor(() => {
      expect(screen.getByTestId("copy-thread-detail-api")).toBeInTheDocument();
      expect(screen.getByTestId("copy-thread-audit-api")).toBeInTheDocument();
    });
  });

  it("shows inline error when approval decision fails", async () => {
    vi.mocked(fetch).mockImplementation(async (input, init) => {
      const url = resolveRequestUrl(input);
      const method = init?.method ?? "GET";

      if (url.includes("/api/approvals/approval-err/approve") && method === "POST") {
        return jsonResponse({ message: "decision rejected by server" }, 500);
      }
      if (url.includes("/api/inbox")) {
        return jsonResponse({
          items: [{
            id: "inbox-err", kind: "Approval", status: "Open",
            title: "Error test", summary: "Test", source: "src",
            createdAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
            requiresAction: true, approvalId: "approval-err",
          } satisfies InboxItem],
        });
      }
      if (url.includes("/api/approvals")) {
        return jsonResponse({
          items: [{
            id: "approval-err", kind: "ExternalAction", status: "Pending",
            title: "Should fail", summary: "", source: "src",
            requestedAt: "2026-01-01T00:00:00Z", updatedAt: "2026-01-01T00:00:00Z",
            inboxItemId: "inbox-err",
          } satisfies Approval],
        });
      }
      throw new Error(`Unexpected: ${method} ${url}`);
    });

    renderWithI18n(<InboxApprovalDesk />);
    const user = userEvent.setup();

    await waitFor(() => {
      expect(screen.getByTestId("approval-approve")).toBeInTheDocument();
    });

    await user.click(screen.getByTestId("approval-approve"));

    // Inline error appears (not global error banner)
    await waitFor(() => {
      const detail = screen.getByTestId("inbox-detail");
      expect(within(detail).getByText("decision rejected by server")).toBeInTheDocument();
    });

    // Global error banner should NOT appear for operation errors
    expect(screen.queryByTestId("inbox-approval-error")).not.toBeInTheDocument();
  });
});
