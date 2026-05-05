import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { SettingsDesk } from "../components/SettingsDesk";
import { I18nProvider } from "../i18n/I18nProvider";

function jsonResponse(data: unknown, status = 200) {
  return new Response(JSON.stringify(data), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function renderDesk() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <I18nProvider>
        <SettingsDesk />
      </I18nProvider>
    </QueryClientProvider>,
  );
}

describe("SettingsDesk", () => {
  beforeEach(() => {
    vi.spyOn(globalThis, "fetch").mockImplementation(async (input) => {
      const url = typeof input === "string" ? input : input instanceof URL ? input.toString() : (input as Request).url;
      if (url.includes("persona-presets")) return jsonResponse([]);
      if (url.includes("channels/accounts")) return jsonResponse([]);
      if (url.includes("/api/memory/stats")) {
        return jsonResponse({ activeCount: 0, dormantCount: 0, archivedCount: 0, topicsCount: 0, sessionsCount: 0 });
      }
      if (url.includes("/api/memory/entries")) {
        return jsonResponse({ count: 0, entries: [] });
      }
      if (url.includes("/api/system/storage-usage")) {
        return jsonResponse({
          main: { count: 0, sizeBytes: 0 },
          auto: { count: 0, sizeBytes: 0 },
          channel: { count: 0, sizeBytes: 0 },
          totalSizeBytes: 0,
        });
      }
      if (url.includes("/api/sessions")) return jsonResponse({ sessions: [] });
      return jsonResponse({ target: "identity", content: "" });
    });
  });

  afterEach(() => { vi.restoreAllMocks(); });

  it("renders all four sections", async () => {
    renderDesk();
    await waitFor(() => {
      expect(screen.getByTestId("settings-desk")).toBeInTheDocument();
    });
    expect(screen.getByTestId("settings-identity-section")).toBeInTheDocument();
    expect(screen.getByTestId("settings-connections-section")).toBeInTheDocument();
    expect(screen.getByTestId("settings-preferences-section")).toBeInTheDocument();
    expect(screen.getByTestId("settings-system-section")).toBeInTheDocument();
  });

  it("renders workspace identity editors", async () => {
    renderDesk();
    await waitFor(() => {
      expect(screen.getByTestId("settings-identity-editor")).toBeInTheDocument();
    });
    expect(screen.getByTestId("settings-soul-editor")).toBeInTheDocument();
    expect(screen.getByTestId("settings-user-editor")).toBeInTheDocument();
  });

  it("shows persona selector trigger", async () => {
    renderDesk();
    await waitFor(() => {
      expect(screen.getByTestId("settings-persona-trigger")).toBeInTheDocument();
    });
    fireEvent.click(screen.getByTestId("settings-persona-trigger"));
    await waitFor(() => {
      expect(screen.getByTestId("persona-selector")).toBeInTheDocument();
    });
  });

  it("renders system action buttons", async () => {
    renderDesk();
    await waitFor(() => {
      expect(screen.getByTestId("settings-reset-onboarding")).toBeInTheDocument();
    });
    expect(screen.getByTestId("settings-clear-identity")).toBeInTheDocument();
  });

  it("shows confirmation before reset-onboarding action", async () => {
    renderDesk();
    await waitFor(() => {
      expect(screen.getByTestId("settings-reset-onboarding")).toBeInTheDocument();
    });
    fireEvent.click(screen.getByTestId("settings-reset-onboarding"));
    await waitFor(() => {
      expect(document.querySelector("dialog[open]")).not.toBeNull();
    });
    const dialog = document.querySelector("dialog[open]") as HTMLElement;
    expect(within(dialog).getByText(/此操作将重置 Onboarding 状态/)).toBeInTheDocument();
  });

  it("cancels reset-onboarding confirmation", async () => {
    renderDesk();
    await waitFor(() => {
      expect(screen.getByTestId("settings-reset-onboarding")).toBeInTheDocument();
    });
    fireEvent.click(screen.getByTestId("settings-reset-onboarding"));
    await waitFor(() => {
      expect(document.querySelector("dialog[open]")).not.toBeNull();
    });
    const dialog = document.querySelector("dialog[open]") as HTMLElement;
    fireEvent.click(within(dialog).getByRole("button", { name: "取消" }));
    await waitFor(() => {
      expect(document.querySelector("dialog[open]")).toBeNull();
    });
    expect(screen.getByTestId("settings-reset-onboarding")).toBeInTheDocument();
  });

  it("shows confirmation before clear-identity action", async () => {
    renderDesk();
    await waitFor(() => {
      expect(screen.getByTestId("settings-clear-identity")).toBeInTheDocument();
    });
    fireEvent.click(screen.getByTestId("settings-clear-identity"));
    await waitFor(() => {
      expect(document.querySelector("dialog[open]")).not.toBeNull();
    });
    const dialog = document.querySelector("dialog[open]") as HTMLElement;
    expect(within(dialog).getByText(/此操作将清空 IDENTITY\.md/)).toBeInTheDocument();
  });

  it("clears identity files on confirm", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch");
    fetchMock.mockImplementation(async (input) => {
      const url = typeof input === "string" ? input : input instanceof URL ? input.toString() : (input as Request).url;
      if (url.includes("persona-presets")) return jsonResponse([]);
      if (url.includes("channels/accounts")) return jsonResponse([]);
      if (url.includes("/api/memory/stats")) {
        return jsonResponse({ activeCount: 0, dormantCount: 0, archivedCount: 0, topicsCount: 0, sessionsCount: 0 });
      }
      if (url.includes("/api/memory/entries")) {
        return jsonResponse({ count: 0, entries: [] });
      }
      if (url.includes("/api/system/storage-usage")) {
        return jsonResponse({
          main: { count: 0, sizeBytes: 0 },
          auto: { count: 0, sizeBytes: 0 },
          channel: { count: 0, sizeBytes: 0 },
          totalSizeBytes: 0,
        });
      }
      if (url.includes("/api/sessions")) return jsonResponse({ sessions: [] });
      return jsonResponse({ target: "identity", content: "" });
    });

    renderDesk();
    await waitFor(() => {
      expect(screen.getByTestId("settings-clear-identity")).toBeInTheDocument();
    });
    fireEvent.click(screen.getByTestId("settings-clear-identity"));
    await waitFor(() => {
      expect(document.querySelector("dialog[open]")).not.toBeNull();
    });
    const dialog = document.querySelector("dialog[open]") as HTMLElement;
    fireEvent.click(within(dialog).getByRole("button", { name: "确认" }));
    await waitFor(() => {
      expect(screen.getByText(/已清除/)).toBeInTheDocument();
    });
  });

  it("renders locale toggle in preferences section", async () => {
    renderDesk();
    await waitFor(() => {
      expect(screen.getByTestId("settings-preferences-section")).toBeInTheDocument();
    });
    // LocaleToggle renders buttons with data-testid locale-toggle-*
    expect(screen.getAllByTestId(/locale-toggle/).length).toBeGreaterThan(0);
  });
});
