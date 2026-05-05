import "@testing-library/jest-dom";
import React from "react";
import { ReadableStream } from "node:stream/web";
import { act, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import App from "../App";
import { I18nProvider } from "../i18n/I18nProvider";
import {
  __resetRuntimeConfigForTests,
  initializeRuntimeConfig,
  type DesktopLaunchTarget,
} from "../lib/config";

const originalFetch = global.fetch;

function renderApp() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <I18nProvider>
        <App />
      </I18nProvider>
    </QueryClientProvider>,
  );
}

function jsonResponse(payload: unknown, status = 200): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    statusText: status === 200 ? "OK" : "ERROR",
    json: async () => payload,
    text: async () => JSON.stringify(payload),
  } as Response;
}

function streamResponse(payload: string): Response {
  const encoder = new TextEncoder();

  return {
    ok: true,
    status: 200,
    statusText: "OK",
    body: new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(encoder.encode(payload));
        controller.close();
      },
    }),
  } as unknown as Response;
}

const automationFixture = {
  id: "auto-heartbeat",
  title: "Heartbeat Digest",
  prompt: "Review unresolved inbox items and summarize the queue.",
  source: "Heartbeat",
  sourcePath: "workspace/HEARTBEAT.md",
  cronExpression: "0 9 * * 1,3,5",
  schedule: {
    kind: "Weekly",
    interval: null,
    localTime: "09:00",
    daysOfWeek: ["Monday", "Wednesday", "Friday"],
  },
  enabled: true,
  inputPaths: ["inbox", "tasks"],
  createdAt: "2026-03-18T08:00:00Z",
  updatedAt: "2026-03-18T08:00:00Z",
  lastRunAt: "2026-03-18T09:00:00Z",
  nextRunAt: "2026-03-19T09:00:00Z",
  lastRunStatus: "Succeeded",
  lastError: null,
};

const automationRunFixture = {
  runId: "run-heartbeat-001",
  automationId: "auto-heartbeat",
  status: "Succeeded",
  trigger: "heartbeat",
  attempt: 1,
  sessionId: "auto-session-001",
  startedAt: "2026-03-18T09:00:00Z",
  completedAt: "2026-03-18T09:01:00Z",
  summary: "Queue digest posted.",
  errorMessage: null,
};

const automationSessionDetailFixture = {
  sessionId: "auto-session-001",
  sessionKind: "Automation",
  status: {
    isActiveMainSession: false,
    breakpointState: "Ready",
    messageCount: 4,
    pendingApprovalCount: 0,
  },
  createdAt: "2026-03-18T09:00:00Z",
  lastEventAt: "2026-03-18T09:01:00Z",
  userMessageCount: 1,
  assistantMessageCount: 2,
  toolCallCount: 1,
  lastSfpIndex: 3,
  pendingApprovalCallIds: [],
  promptReport: {
    profileId: "Automation",
    systemPrompt: "Automation prompt",
    characterCount: 320,
    loadedContextFiles: ["workspace/IDENTITY.md", "workspace/tasks/index.md"],
    generatedAt: "2026-03-18T09:00:00Z",
    characterBudget: 1200,
    remainingCharacterBudget: 880,
    wasTruncated: false,
    truncatedContextFiles: [],
    truncationNotes: [],
  },
  promptReportDelta: {
    previousGeneratedAt: "2026-03-18T08:00:00Z",
    characterCountDelta: 14,
    truncationStateChanged: false,
    addedContextFiles: ["workspace/tasks/index.md"],
    removedContextFiles: [],
  },
  recentPromptReports: [],
};

const canvasFixture = {
  id: "canvas-launch",
  title: "Launch Snapshot",
  kind: "Report",
  summary: "Executive summary board.",
  source: "runtime.main",
  route: "/canvas/launch",
  entryPath: "canvas/launch/index.html",
  assetDirectory: "canvas/launch",
  sessionId: "session-main",
  correlationId: "corr-launch",
  createdAt: "2026-03-18T08:00:00Z",
  updatedAt: "2026-03-18T08:30:00Z",
  metadataJson: null,
};

const pluginSummaryFixture = {
  id: "plugin.fixture",
  name: "Fixture Plugin",
  version: "0.1.0",
  types: ["Tool"],
  installSource: "LocalDirectory",
  trustState: "Signed",
  enabled: true,
  runtimeState: "Running",
  rootPath: "/tmp/.kodaclaw/workspace/plugins/plugin.fixture",
  updatedAt: "2026-03-18T08:35:00Z",
  lastError: null,
};

const pluginDetailFixture = {
  record: {
    id: "plugin.fixture",
    manifest: {
      id: "plugin.fixture",
      name: "Fixture Plugin",
      version: "0.1.0",
      types: ["Tool"],
      runtime: {
        transport: "Stdio",
        command: "fixture",
      },
      permissions: {
        background: true,
      },
      capabilities: {
        tools: ["echo"],
      },
    },
    installSource: "LocalDirectory",
    rootPath: "/tmp/.kodaclaw/workspace/plugins/plugin.fixture",
    trustState: "Signed",
    enabled: true,
    runtimeState: "Running",
    discoveredAt: "2026-03-18T08:00:00Z",
    installedAt: "2026-03-18T08:00:00Z",
    updatedAt: "2026-03-18T08:35:00Z",
    lastStartedAt: "2026-03-18T08:10:00Z",
    lastStoppedAt: null,
    lastHealthAt: "2026-03-18T08:35:00Z",
    restartCount: 0,
    lastError: null,
    trustEvidence: {
      source: "SignatureSidecar",
      verificationState: "Verified",
      summary: "Signature sidecar matched the current manifest and package digests.",
      verifiedAt: "2026-03-18T08:35:00Z",
      manifestDigestSha256: "fixture-manifest-digest",
      packageDigestSha256: "fixture-package-digest",
      signer: "Fixture Publisher",
      signatureFilePath: "/tmp/.kodaclaw/workspace/plugins/plugin.fixture/plugin.signature.json",
    },
  },
  permissionSummary: {
    highRiskReasons: ["Can run in background."],
    mediumRiskReasons: [],
    hasHighRisk: true,
  },
  healthSummary: {
    status: "Healthy",
    message: "Plugin runtime is responding.",
    lastHealthAt: "2026-03-18T08:35:00Z",
    restartCount: 0,
    isHealthy: true,
  },
  availableTools: ["mcp__plugin.fixture__echo"],
};

const pluginLogFixture = {
  entryId: "plugin-log-001",
  pluginId: "plugin.fixture",
  level: "info",
  source: "plugin.host",
  message: "Plugin started successfully.",
  timestamp: "2026-03-18T08:35:00Z",
  payloadJson: null,
};

const channelConnectorFixture = [
  {
    kind: "Telegram",
    displayName: "Telegram",
    implemented: true,
    supportsInbound: true,
    supportsOutbound: true,
    productOwned: true,
  },
];

const channelAccountFixture = {
  id: "telegram-main",
  connectorKind: "Telegram",
  displayName: "Telegram Bot",
  state: "Connected",
  createdAt: "2026-03-18T08:00:00Z",
  updatedAt: "2026-03-18T08:30:00Z",
  externalAccountId: "bot-001",
  credentialReference: "env:KODACLAW_TELEGRAM_TOKEN",
  description: null,
  configurationJson: "{\"botToken\":\"inline:token\"}",
  inboundEnabled: true,
};

const channelThreadFixture = {
  bindingId: "binding-telegram-001",
  connectorKind: "Telegram",
  accountId: "telegram-main",
  externalThreadId: "10001",
  threadType: "DirectMessage",
  sessionId: "channel-dm-001",
  sessionKind: "ChannelDirectMessage",
  displayTitle: "Alice",
  deliveryMode: "DraftApproval",
  accountState: "Connected",
  updatedAt: "2026-03-18T08:30:00Z",
  lastInboundAt: "2026-03-18T08:29:00Z",
  lastOutboundAt: null,
  lastMessagePreview: "hello from telegram",
  pendingApprovalId: null,
  hasPendingDraft: false,
};

const channelThreadDetailFixture = {
  account: channelAccountFixture,
  binding: {
    id: "binding-telegram-001",
    connectorKind: "Telegram",
    accountId: "telegram-main",
    externalThreadId: "10001",
    threadType: "DirectMessage",
    sessionId: "channel-dm-001",
    sessionKind: "ChannelDirectMessage",
    channelIdentity: {
      id: "20001",
      username: "alice",
      displayName: "Alice",
      isBot: false,
    },
    policyId: "policy-default-dm",
    deliveryRuleId: "delivery-default-dm",
    createdAt: "2026-03-18T08:00:00Z",
    updatedAt: "2026-03-18T08:30:00Z",
    lastInboundAt: "2026-03-18T08:29:00Z",
    lastOutboundAt: null,
    lastMessagePreview: "hello from telegram",
  },
  policy: {
    id: "policy-default-dm",
    threadType: "DirectMessage",
    updatedAt: "2026-03-18T08:30:00Z",
    loadAgents: true,
    loadIdentity: true,
    loadSoul: true,
    loadUserProfile: true,
    loadLongTermMemory: false,
    loadRecentThreadSummary: true,
    allowDirectReply: true,
    requireExplicitMention: false,
    workspaceMuted: false,
    connectorMuted: false,
    threadMuted: false,
    notes: null,
  },
  deliveryRule: {
    id: "delivery-default-dm",
    mode: "DraftApproval",
    updatedAt: "2026-03-18T08:30:00Z",
    allowProactiveSend: false,
    muteDuringQuietHours: true,
  },
  recentAudit: [],
  session: {
    sessionId: "channel-dm-001",
    sessionKind: "ChannelDirectMessage",
    status: {
      isActiveMainSession: false,
      breakpointState: null,
      messageCount: 3,
      pendingApprovalCount: 0,
    },
    createdAt: "2026-03-18T08:00:00Z",
    lastEventAt: "2026-03-18T08:29:00Z",
  },
  pendingApprovalId: null,
  hasPendingDraft: false,
};

const onboardingCompleted = { isCompleted: true, currentStep: null, completedAt: "2026-03-18T10:00:00Z" };

describe("App shell", () => {
  beforeEach(() => {
    vi.stubGlobal("fetch", vi.fn());
    __resetRuntimeConfigForTests();
    delete window.kodaClawDesktop;
    window.localStorage.clear();
    window.history.pushState({}, "", "/");
  });

  afterEach(() => {
    __resetRuntimeConfigForTests();
    delete window.kodaClawDesktop;
    vi.restoreAllMocks();
    global.fetch = originalFetch;
  });

  it("defaults to Chinese and allows switching the shell copy to English", async () => {
    vi.mocked(fetch).mockImplementation(async (input) => {
      const url =
        typeof input === "string"
          ? input
          : input instanceof URL
            ? input.toString()
            : input.url;

      if (url.endsWith("/api/system/health")) {
        return jsonResponse({ name: "KodaClaw Gateway", status: "healthy", mode: "Normal" });
      }
      if (url.endsWith("/api/system/bootstrap-state")) {
        return jsonResponse({
          workspaceRootPath: "/tmp/.kodaclaw",
          workspaceVersion: 1,
          workspaceInitialized: true,
          requiresBootstrap: false,
          activeMainSessionId: "main-001",
          mode: "Normal",
        });
      }
      if (url.endsWith("/api/onboarding/state")) {
        return jsonResponse(onboardingCompleted);
      }
      if (url.includes("/api/settings")) {
        return jsonResponse({ automationsEnabled: true });
      }
      if (url.includes("/api/memory/stats")) {
        return jsonResponse({ activeCount: 0, dormantCount: 0, archivedCount: 0, topicsCount: 0, sessionsCount: 0 });
      }
      if (url.includes("/api/memory/entries")) {
        return jsonResponse({ count: 0, entries: [] });
      }
      if (url.includes("/api/system/storage-usage")) {
        return jsonResponse({ main: { count: 0, sizeBytes: 0 }, auto: { count: 0, sizeBytes: 0 }, channel: { count: 0, sizeBytes: 0 }, totalSizeBytes: 0 });
      }
      if (url.includes("/api/sessions")) return jsonResponse({ sessions: [] });
      if (url.includes("/api/workspace/file")) return jsonResponse({ target: "identity", content: "" });
      if (url.includes("/api/channels/accounts")) return jsonResponse([]);
      if (url.includes("/api/provider-accounts")) return jsonResponse([]);
      if (url.includes("/api/workspace/git/log")) return jsonResponse({ commits: [], hasMore: false });
      return jsonResponse({});
    });

    renderApp();

    await waitFor(() => {
      expect(screen.getByTestId("kc-shell")).toBeInTheDocument();
    });

    expect(document.documentElement.lang).toBe("zh-CN");
    expect(document.title).toBe("KodaClaw");

    const user = userEvent.setup();
    // LocaleToggle has moved to Settings Desk — navigate there first
    await user.click(screen.getByTestId("desk-tab-settings"));
    await waitFor(() => { expect(screen.getByTestId("settings-desk")).toBeInTheDocument(); });
    await user.click(screen.getByTestId("locale-toggle-en-US"));

    await waitFor(() => {
      expect(document.title).toBe("KodaClaw Field Console");
    });
    expect(window.localStorage.getItem("kodaclaw.locale")).toBe("en-US");
    expect(document.documentElement.lang).toBe("en-US");
  });

  it("renders main chat status when bootstrap has completed", async () => {
    vi.mocked(fetch)
      .mockResolvedValueOnce(
        jsonResponse({ name: "KodaClaw Gateway", status: "healthy", mode: "Normal" }),
      )
      .mockResolvedValueOnce(
        jsonResponse({
          workspaceRootPath: "/tmp/.kodaclaw",
          workspaceVersion: 1,
          workspaceInitialized: true,
          requiresBootstrap: false,
          activeMainSessionId: "main-001",
          mode: "Normal",
        }),
      )
      .mockResolvedValueOnce(
        jsonResponse(onboardingCompleted),
      );

    renderApp();

    await waitFor(() => {
      expect(screen.getByTestId("kc-shell")).toBeInTheDocument();
    });
    expect(screen.getByTestId("kc-shell").getAttribute("data-kc-mode")).toBe("main");
    expect(screen.getByTestId("kc-chat-view")).toBeInTheDocument();
    expect(screen.getByTestId("desk-tab-chat")).toBeInTheDocument();
    expect(screen.getByTestId("desk-tab-automations")).toBeInTheDocument();
    expect(screen.getByTestId("desk-tab-channels")).toBeInTheDocument();
    expect(screen.getByTestId("desk-tab-plugins")).toBeInTheDocument();
    expect(screen.getByTestId("desk-tab-canvas")).toBeInTheDocument();
  });

  it("renders main mode with new AppShell and supports desk navigation", async () => {
    vi.mocked(fetch).mockImplementation(async (input) => {
      const url =
        typeof input === "string"
          ? input
          : input instanceof URL
            ? input.toString()
            : input.url;

      if (url.endsWith("/api/system/health")) {
        return jsonResponse({ name: "KodaClaw Gateway", status: "healthy", mode: "Normal" });
      }

      if (url.endsWith("/api/system/bootstrap-state")) {
        return jsonResponse({
          workspaceRootPath: "/tmp/.kodaclaw",
          workspaceVersion: 7,
          workspaceInitialized: true,
          requiresBootstrap: false,
          activeMainSessionId: "main-v2-001",
          mode: "Normal",
        });
      }

      if (url.endsWith("/api/onboarding/state")) {
        return jsonResponse(onboardingCompleted);
      }

      if (url.includes("/api/inbox?limit=50")) {
        return jsonResponse({ items: [] });
      }

      if (url.includes("/api/approvals?limit=50")) {
        return jsonResponse({ items: [] });
      }

      throw new Error(`Unexpected fetch request: ${url}`);
    });

    renderApp();

    await waitFor(() => {
      expect(screen.getByTestId("kc-shell")).toBeInTheDocument();
    });

    expect(screen.getByTestId("kc-shell").getAttribute("data-kc-mode")).toBe("main");
    expect(screen.getByTestId("kc-sidebar")).toBeInTheDocument();
    expect(screen.getByTestId("kc-main-content")).toBeInTheDocument();
    expect(screen.getByTestId("kc-chat-view")).toBeInTheDocument();

    const user = userEvent.setup();
    await user.click(screen.getByTestId("desk-tab-inbox"));

    await waitFor(() => {
      expect(screen.getByTestId("inbox-approval-desk")).toBeInTheDocument();
    });

    expect(window.localStorage.getItem("kodaclaw.mainDesk")).toBe("inbox");
  });

  it("switches into automations and canvas desks from the main shell", async () => {
    vi.mocked(fetch).mockImplementation(async (input) => {
      const url =
        typeof input === "string"
          ? input
          : input instanceof URL
            ? input.toString()
            : input.url;

      if (url.endsWith("/api/system/health")) {
        return jsonResponse({ name: "KodaClaw Gateway", status: "healthy", mode: "Normal" });
      }

      if (url.endsWith("/api/system/bootstrap-state")) {
        return jsonResponse({
          workspaceRootPath: "/tmp/.kodaclaw",
          workspaceVersion: 1,
          workspaceInitialized: true,
          requiresBootstrap: false,
          activeMainSessionId: "main-001",
          mode: "Normal",
        });
      }

      if (url.endsWith("/api/onboarding/state")) {
        return jsonResponse(onboardingCompleted);
      }

      if (url.includes("/api/automations/auto-heartbeat/runs")) {
        return jsonResponse({ items: [automationRunFixture] });
      }

      if (url.endsWith("/api/automations?limit=60")) {
        return jsonResponse({ items: [automationFixture] });
      }

      if (url.endsWith("/api/sessions/auto-session-001")) {
        return jsonResponse(automationSessionDetailFixture);
      }

      if (url.endsWith("/api/canvas/default")) {
        return jsonResponse({
          entryUrl: "/api/canvas/fs/canvas/default/index.html",
          entryPath: "canvas/default/index.html",
          artifactId: null,
          route: "/canvas/default",
          title: "Canvas default entry",
        });
      }

      if (url.endsWith("/api/canvas?limit=60")) {
        return jsonResponse({
          items: [canvasFixture],
          defaultEntryPath: "canvas/default/index.html",
          defaultArtifactId: null,
        });
      }

      if (url.endsWith("/api/plugins?limit=80")) {
        return jsonResponse({
          items: [pluginSummaryFixture],
        });
      }

      if (url.endsWith("/api/plugins/plugin.fixture")) {
        return jsonResponse(pluginDetailFixture);
      }

      if (url.includes("/api/plugins/plugin.fixture/logs")) {
        return jsonResponse([pluginLogFixture]);
      }

      if (url.endsWith("/api/channels/connectors")) {
        return jsonResponse(channelConnectorFixture);
      }

      if (url.includes("/api/channels/accounts")) {
        return jsonResponse([channelAccountFixture]);
      }

      if (url.includes("/api/channels/threads?")) {
        return jsonResponse({ items: [channelThreadFixture] });
      }

      if (url.endsWith("/api/channels/threads/binding-telegram-001")) {
        return jsonResponse(channelThreadDetailFixture);
      }

      if (url.includes("/api/channels/threads/binding-telegram-001/audit")) {
        return jsonResponse([]);
      }

      if (url.includes("/api/settings")) {
        return jsonResponse({ automationsEnabled: true });
      }

      if (url.includes("/api/provider-accounts")) {
        return jsonResponse([]);
      }

      throw new Error(`Unexpected fetch request: ${url}`);
    });

    renderApp();

    await waitFor(() => {
      expect(screen.getByTestId("kc-chat-view")).toBeInTheDocument();
    });
    expect(screen.getByTestId("kc-shell").getAttribute("data-kc-mode")).toBe("main");

    const user = userEvent.setup();
    await user.click(screen.getByTestId("desk-tab-automations"));
    await waitFor(() => {
      expect(screen.getByTestId("automations-desk")).toBeInTheDocument();
    });

    await user.click(screen.getByTestId("desk-tab-canvas"));
    await waitFor(() => {
      expect(screen.getByTestId("canvas-desk")).toBeInTheDocument();
    });

    await user.click(screen.getByTestId("desk-tab-plugins"));
    await waitFor(() => {
      expect(screen.getByTestId("plugins-desk")).toBeInTheDocument();
    });

    await user.click(screen.getByTestId("desk-tab-channels"));
    await waitFor(() => {
      expect(screen.getByTestId("channels-desk")).toBeInTheDocument();
    });
  });

  it("prefers desktop runtime config and reacts to launch target events", async () => {
    let launchTargetListener: ((target: DesktopLaunchTarget) => void) | null = null;

    window.kodaClawDesktop = {
      getRuntimeConfig: vi.fn().mockResolvedValue({
        gatewayUrl: "http://127.0.0.1:5076",
        gatewayToken: "desktop-token",
        platform: "darwin",
        desktopMode: true,
        gatewayLifecycleMode: "ManagedChild",
        initialTarget: {
          desk: "inbox",
          reason: "notification",
        },
      }),
      onLaunchTarget: (listener) => {
        launchTargetListener = listener;
        return () => {
          launchTargetListener = null;
        };
      },
    };

    await initializeRuntimeConfig();

    vi.mocked(fetch).mockImplementation(async (input, init) => {
      const url =
        typeof input === "string"
          ? input
          : input instanceof URL
            ? input.toString()
            : input.url;
      const authorizationHeader = new Headers(init?.headers).get("Authorization");

      expect(authorizationHeader).toBe("Bearer desktop-token");

      if (url === "http://127.0.0.1:5076/api/system/health") {
        return jsonResponse({ name: "KodaClaw Gateway", status: "healthy", mode: "Normal" });
      }

      if (url === "http://127.0.0.1:5076/api/system/bootstrap-state") {
        return jsonResponse({
          workspaceRootPath: "/tmp/.kodaclaw",
          workspaceVersion: 1,
          workspaceInitialized: true,
          requiresBootstrap: false,
          activeMainSessionId: "main-001",
          mode: "Normal",
        });
      }

      if (url === "http://127.0.0.1:5076/api/onboarding/state") {
        return jsonResponse(onboardingCompleted);
      }

      if (url.startsWith("http://127.0.0.1:5076/api/inbox")) {
        return jsonResponse({ items: [] });
      }

      if (url.startsWith("http://127.0.0.1:5076/api/approvals")) {
        return jsonResponse({ items: [] });
      }

      if (url === "http://127.0.0.1:5076/api/channels/connectors") {
        return jsonResponse(channelConnectorFixture);
      }

      if (url.startsWith("http://127.0.0.1:5076/api/channels/accounts")) {
        return jsonResponse([channelAccountFixture]);
      }

      if (url.startsWith("http://127.0.0.1:5076/api/channels/threads?")) {
        return jsonResponse({ items: [channelThreadFixture] });
      }

      if (url === "http://127.0.0.1:5076/api/channels/threads/binding-telegram-001") {
        return jsonResponse(channelThreadDetailFixture);
      }

      if (url.startsWith("http://127.0.0.1:5076/api/channels/threads/binding-telegram-001/audit")) {
        return jsonResponse([]);
      }

      throw new Error(`Unexpected fetch request: ${url}`);
    });

    renderApp();

    await waitFor(() => {
      expect(screen.getByTestId("inbox-approval-desk")).toBeInTheDocument();
    });

    if (launchTargetListener) {
      await act(async () => {
        (launchTargetListener as (target: DesktopLaunchTarget) => void)({
          desk: "channels",
          reason: "tray-shortcut",
        });
      });
    }

    await waitFor(() => {
      expect(screen.getByTestId("channels-desk")).toBeInTheDocument();
    });
  });
});
