import "@testing-library/jest-dom";
import React from "react";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ModelsSettingsDesk } from "../components/ModelsSettingsDesk";
import { renderWithI18n } from "./test-utils";
import {
  createProviderAccount,
  deleteProviderAccount,
  fetchModelPresets,
  fetchProviderAccounts,
  setDefaultAccountModel,
  testModelConnection,
  updateProviderAccount,
  updateAccountModel,
} from "../lib/api";
import type { ProviderAccountResponse, AccountModelResponse } from "../types/contracts";

vi.mock("../lib/api", () => ({
  fetchProviderAccounts: vi.fn(),
  fetchModelPresets: vi.fn(),
  fetchSandboxRiskOverview: vi.fn(),
  runUpdateCheck: vi.fn(),
  createProviderAccount: vi.fn(),
  updateProviderAccount: vi.fn(),
  updateAccountModel: vi.fn(),
  deleteProviderAccount: vi.fn(),
  setDefaultAccountModel: vi.fn(),
  testModelConnection: vi.fn(),
}));

const accountsApi = vi.mocked(fetchProviderAccounts);
const presetsApi = vi.mocked(fetchModelPresets);
const createApi = vi.mocked(createProviderAccount);
const updateAccountApi = vi.mocked(updateProviderAccount);
const updateModelApi = vi.mocked(updateAccountModel);
const deleteApi = vi.mocked(deleteProviderAccount);
const setDefaultApi = vi.mocked(setDefaultAccountModel);
const testConnectionApi = vi.mocked(testModelConnection);

const modelA: AccountModelResponse = {
  id: "model-a",
  accountId: "acc-a",
  displayName: "OpenAI Core",
  modelId: "gpt-4.1",
  capabilities: 3, // Text | Image
  isDefaultForAccount: true,
  isGlobalDefault: true,
  enabled: true,
  contextWindowSize: 128000,
  maxOutputTokens: 8192,
  isReasoning: false,
  supportsToolCalling: true,
};

const modelB: AccountModelResponse = {
  id: "model-b",
  accountId: "acc-b",
  displayName: "Anthropic Draft",
  modelId: "claude-3-7-sonnet",
  capabilities: 1, // Text
  isDefaultForAccount: true,
  isGlobalDefault: false,
  enabled: true,
  contextWindowSize: 200000,
  maxOutputTokens: 8192,
  isReasoning: false,
  supportsToolCalling: true,
};

const accountA: ProviderAccountResponse = {
  id: "acc-a",
  displayName: "OpenAI Core",
  providerKind: "OpenAI",
  baseUrl: null,
  accessMode: null,
  enabled: true,
  hasApiKey: true,
  createdAt: "2026-03-18T10:00:00Z",
  updatedAt: "2026-03-18T10:00:00Z",
  models: [modelA],
};

const accountB: ProviderAccountResponse = {
  id: "acc-b",
  displayName: "Anthropic Draft",
  providerKind: "AnthropicCompatible",
  baseUrl: "https://proxy.example",
  accessMode: null,
  enabled: true,
  hasApiKey: true,
  createdAt: "2026-03-18T11:00:00Z",
  updatedAt: "2026-03-18T11:00:00Z",
  models: [modelB],
};

const defaultAccounts: ProviderAccountResponse[] = [accountA, accountB];

describe("ModelsSettingsDesk", () => {
  beforeEach(() => {
    accountsApi.mockResolvedValue(defaultAccounts);
    presetsApi.mockResolvedValue([]);
    createApi.mockResolvedValue(accountB);
    updateAccountApi.mockResolvedValue(accountB);
    updateModelApi.mockResolvedValue(modelB);
    deleteApi.mockResolvedValue();
    setDefaultApi.mockResolvedValue({
      ...modelB,
      isGlobalDefault: true,
    });
    testConnectionApi.mockResolvedValue({ ok: true, latencyMs: 42 });
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it("switches default model endpoint", async () => {
    const user = userEvent.setup();
    renderWithI18n(<ModelsSettingsDesk />);

    // Wait for sidebar to render and select accountB (which has model-b, not global default)
    await screen.findByTestId("sidebar-item-acc-b");
    await user.click(screen.getByTestId("sidebar-item-acc-b"));

    // model-b should now be visible in the detail panel
    await waitFor(() => {
      expect(screen.getByTestId("model-item-model-b")).toBeInTheDocument();
    });

    const button = screen.getByTestId("model-default");
    await user.click(button);

    await waitFor(() => {
      expect(setDefaultApi).toHaveBeenCalledWith("acc-b", "model-b");
    });
    expect(screen.getByText("默认模型已切换。")).toBeInTheDocument();
  });

  it("keeps a focused model detail stage in sync with the selected endpoint", async () => {
    const user = userEvent.setup();
    renderWithI18n(<ModelsSettingsDesk />);

    // Select accountB in the sidebar to see model-b
    await screen.findByTestId("sidebar-item-acc-b");
    await user.click(screen.getByTestId("sidebar-item-acc-b"));

    // Wait for model-b to render in the detail panel
    await waitFor(() => {
      expect(screen.getByTestId("model-item-model-b")).toBeInTheDocument();
    });

    // Click edit on model-b to open the model modal
    await user.click(
      within(screen.getByTestId("model-item-model-b")).getByRole("button", { name: "编辑" }),
    );

    // Modal opens with model-b's data
    await waitFor(() => {
      expect(document.querySelector("dialog[open]")).not.toBeNull();
    });

    expect(screen.getByTestId("model-id")).toHaveValue("claude-3-7-sonnet");
  });

  it("renders load error state", async () => {
    accountsApi.mockRejectedValueOnce(new Error("models unavailable"));
    renderWithI18n(<ModelsSettingsDesk />);

    await waitFor(() => {
      expect(screen.getByText("models unavailable")).toBeInTheDocument();
    });
    expect(screen.getByTestId("models-settings-desk")).toBeInTheDocument();
  });

});
