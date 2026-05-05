import { useEffect, useMemo, useRef, useState } from "react";
import { useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../lib/queryKeys";
import ReactMarkdown from "react-markdown";
import remarkGfm from "remark-gfm";
import {
  buildCanvasEntryUrl,
  fetchCanvasArtifact,
  fetchCanvasArtifactEntry,
  fetchCanvasArtifacts,
  fetchCanvasDefaultEntry,
} from "../lib/api";
import { useI18n, useLocaleText } from "../i18n/I18nProvider";
import { Skeleton } from "./ui/Skeleton";
import { EmptyState } from "./ui/EmptyState";
import { Button } from "./ui/Button";
import { Select } from "./ui/Select";
import { Layers } from "lucide-react";
import type { CanvasArtifact, CanvasArtifactKind, CanvasEntryResponse } from "../types/contracts";
import "./ControlPlaneDesk.css";
import { formatRelativeTime } from "../lib/timeUtils";

type CanvasKindFilter = CanvasArtifactKind | "all";

const CANVAS_KIND_OPTIONS: CanvasArtifactKind[] = [
  "Report",
  "Dashboard",
  "Board",
  "TaskList",
  "PluginPanel",
  "Html",
  "Image",
];


function resolveDefaultEntryUrl(
  defaultEntry: CanvasEntryResponse | null,
  fallbackEntryPath: string | null,
): string | null {
  if (defaultEntry?.entryUrl) {
    return defaultEntry.entryUrl;
  }

  if (defaultEntry?.entryPath) {
    return buildCanvasEntryUrl(defaultEntry.entryPath);
  }

  if (fallbackEntryPath) {
    return buildCanvasEntryUrl(fallbackEntryPath);
  }

  return null;
}

export function CanvasDesk() {
  const { formatDateTime } = useI18n();
  const text = useLocaleText({
    zh: {
      eyebrow: "画布通道",
      title: "Canvas 工坊",
      copy:
        "管理已发布制品、保持渲染面板可检查，并始终锚定默认画布路由。",
      refresh: "刷新",
      refreshing: "刷新中...",
      kindFilter: "类型筛选",
      allKinds: "全部类型",
      loading: "加载中...",
      artifactCount: (count: number) => `${count} 个制品`,
      errorEyebrow: "错误",
      errorTitle: "画布工作台请求失败",
      listTitle: "已发布制品",
      emptyState: "当前筛选下暂无制品。预览仍固定在默认画布入口。",
      selected: "已选中",
      renderSurface: "渲染面板",
      previewFrameTitle: "画布制品预览",
      previewUnavailable:
        "画布预览路由暂不可用。请在运行时发布默认入口后刷新工作台。",
      metadataEyebrow: "制品元数据",
      metadataTitle: "检查器",
      metadata: {
        route: "路由",
        entryPath: "入口路径",
        assetDirectory: "资源目录",
        session: "会话",
        summary: "摘要",
      },
      defaultEntryTitle: "画布默认入口",
      defaultKind: "默认入口",
      defaultSummary: "当前未选择已发布制品，预览固定显示默认画布入口。",
      unavailable: "暂无",
      loadDeskError: "加载画布工作台失败。",
      loadArtifactError: "加载画布制品失败。",
      relativeTime: {
        justNow: "刚刚",
        minutesAgo: (n: number) => `${n} 分钟前`,
        hoursAgo: (n: number) => `${n} 小时前`,
        daysAgo: (n: number) => `${n} 天前`,
      },
      kinds: {
        Report: "报告",
        Dashboard: "仪表盘",
        Board: "看板",
        TaskList: "任务列表",
        PluginPanel: "插件面板",
        Html: "HTML 页面",
        Image: "图片",
      },
    },
    en: {
      eyebrow: "Canvas Lane",
      title: "Canvas Atelier",
      copy:
        "Curate published artifacts, keep the render surface inspectable, and stay anchored to the default canvas route.",
      refresh: "Refresh",
      refreshing: "Refreshing...",
      kindFilter: "Kind filter",
      allKinds: "All kinds",
      loading: "Loading...",
      artifactCount: (count: number) => `${count} artifacts`,
      errorEyebrow: "Error",
      errorTitle: "Canvas desk request failed",
      listTitle: "Published artifacts",
      emptyState: "No artifacts match this filter yet. Default canvas entry stays active for preview.",
      selected: "selected",
      renderSurface: "Render surface",
      previewFrameTitle: "Canvas artifact preview",
      previewUnavailable:
        "Canvas preview route is unavailable. Refresh the desk after runtime publishes the default entry.",
      metadataEyebrow: "Artifact metadata",
      metadataTitle: "Inspector",
      metadata: {
        route: "Route",
        entryPath: "Entry path",
        assetDirectory: "Asset directory",
        session: "Session",
        summary: "Summary",
      },
      defaultEntryTitle: "Canvas default entry",
      defaultKind: "Default",
      defaultSummary: "No published artifact selected. Preview is pinned to the default canvas entry.",
      unavailable: "n/a",
      loadDeskError: "Failed to load canvas desk.",
      loadArtifactError: "Failed to load canvas artifact.",
      relativeTime: {
        justNow: "just now",
        minutesAgo: (n: number) => `${n}m ago`,
        hoursAgo: (n: number) => `${n}h ago`,
        daysAgo: (n: number) => `${n}d ago`,
      },
      kinds: {
        Report: "Report",
        Dashboard: "Dashboard",
        Board: "Board",
        TaskList: "Task List",
        PluginPanel: "Plugin Panel",
        Html: "HTML",
        Image: "Image",
      },
    },
  });

  const queryClient = useQueryClient();
  const [kindFilter, setKindFilter] = useState<CanvasKindFilter>("all");
  const [artifacts, setArtifacts] = useState<CanvasArtifact[]>([]);
  const [selectedArtifact, setSelectedArtifact] = useState<CanvasArtifact | null>(null);
  const [selectedEntry, setSelectedEntry] = useState<CanvasEntryResponse | null>(null);
  const [defaultEntry, setDefaultEntry] = useState<CanvasEntryResponse | null>(null);
  const [defaultEntryPath, setDefaultEntryPath] = useState<string | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [isRefreshing, setIsRefreshing] = useState(false);
  const [selectionPendingId, setSelectionPendingId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [fetchedContent, setFetchedContent] = useState<string | null>(null);
  const requestIdRef = useRef(0);
  const contentFetchAbortRef = useRef<AbortController | null>(null);

  function resolveKindLabel(kind: CanvasArtifactKind): string {
    return text.kinds[kind] ?? kind;
  }

  async function loadDesk(mode: "initial" | "refresh") {
    const requestId = requestIdRef.current + 1;
    requestIdRef.current = requestId;

    if (mode === "initial") {
      setIsLoading(true);
    } else {
      setIsRefreshing(true);
    }

    setError(null);

    try {
      const query =
        kindFilter === "all"
          ? {
              limit: 60,
            }
          : {
              kind: kindFilter,
              limit: 60,
            };

      const [artifactsPayload, defaultEntryPayload] = await Promise.all([
        fetchCanvasArtifacts(query),
        fetchCanvasDefaultEntry(),
      ]);

      if (requestId !== requestIdRef.current) {
        return;
      }

      setArtifacts(artifactsPayload.items);
      setDefaultEntry(defaultEntryPayload);
      setDefaultEntryPath(artifactsPayload.defaultEntryPath ?? null);

      if (!selectedArtifact) {
        setSelectedEntry(null);
        return;
      }

      const selectedStillVisible = artifactsPayload.items.some(
        (item) => item.id === selectedArtifact.id,
      );

      if (!selectedStillVisible) {
        setSelectedArtifact(null);
        setSelectedEntry(null);
        return;
      }

      const [hydrated, hydratedEntry] = await Promise.all([
        fetchCanvasArtifact(selectedArtifact.id),
        fetchCanvasArtifactEntry(selectedArtifact.id),
      ]);
      if (requestId !== requestIdRef.current) {
        return;
      }

      setSelectedArtifact(hydrated);
      setSelectedEntry(hydratedEntry);
    } catch (nextError) {
      if (requestId !== requestIdRef.current) {
        return;
      }

      setError(nextError instanceof Error ? nextError.message : text.loadDeskError);
    } finally {
      if (requestId !== requestIdRef.current) {
        return;
      }

      if (mode === "initial") {
        setIsLoading(false);
      } else {
        setIsRefreshing(false);
      }
    }
  }

  useEffect(() => {
    void loadDesk("initial");
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [kindFilter]);

  async function handleRefresh() {
    void queryClient.invalidateQueries({ queryKey: ['canvas'] });
    await loadDesk("refresh");
  }

  async function handleSelectArtifact(artifactId: string) {
    setSelectionPendingId(artifactId);
    setError(null);

    try {
      const [artifact, entry] = await Promise.all([
        fetchCanvasArtifact(artifactId),
        fetchCanvasArtifactEntry(artifactId),
      ]);
      setSelectedArtifact(artifact);
      setSelectedEntry(entry);
    } catch (nextError) {
      setError(nextError instanceof Error ? nextError.message : text.loadArtifactError);
    } finally {
      setSelectionPendingId((current) => (current === artifactId ? null : current));
    }
  }

  const previewEntryUrl = useMemo(() => {
    if (selectedEntry?.entryUrl) {
      return selectedEntry.entryUrl;
    }

    if (selectedArtifact) {
      return buildCanvasEntryUrl(selectedArtifact.entryPath);
    }

    return resolveDefaultEntryUrl(defaultEntry, defaultEntryPath);
  }, [defaultEntry, defaultEntryPath, selectedArtifact, selectedEntry]);

  // Only Report and TaskList are markdown-renderable kinds; Dashboard/Board/PluginPanel use iframes.
  const MARKDOWN_KINDS: CanvasArtifactKind[] = ["Report", "TaskList"];

  // When contentText is absent and kind is markdown-renderable, fetch the entry URL as text
  // so it can be rendered by ReactMarkdown instead of a raw iframe.
  const shouldFetchContent =
    previewEntryUrl !== null &&
    (selectedArtifact === null
      // No selection: fetch if the default entry URL looks like markdown
      ? /\.md(\?|$)/.test(previewEntryUrl)
      // Has selection: fetch if kind is markdown-renderable and contentText is absent
      : MARKDOWN_KINDS.includes(selectedArtifact.kind) &&
        (selectedArtifact.contentText == null || selectedArtifact.contentText === ""));

  useEffect(() => {
    if (!shouldFetchContent || !previewEntryUrl) {
      setFetchedContent(null);
      return;
    }
    contentFetchAbortRef.current?.abort();
    const controller = new AbortController();
    contentFetchAbortRef.current = controller;
    setFetchedContent(null);
    fetch(previewEntryUrl, { signal: controller.signal })
      .then(r => r.text())
      .then(text => { if (!controller.signal.aborted) setFetchedContent(text); })
      .catch(() => { /* silently fall back to iframe */ });
    return () => { controller.abort(); };
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [previewEntryUrl, shouldFetchContent]);

  const selectionLabel = selectedArtifact ? selectedArtifact.title : defaultEntry?.title ?? text.defaultEntryTitle;
  const selectionKind = selectedArtifact ? resolveKindLabel(selectedArtifact.kind) : text.defaultKind;
  const metadataRoute = selectedArtifact?.route ?? defaultEntry?.route ?? text.unavailable;
  const metadataEntryPath =
    selectedArtifact?.entryPath ?? defaultEntry?.entryPath ?? defaultEntryPath ?? text.unavailable;
  const metadataAssetDirectory = selectedArtifact?.assetDirectory ?? text.unavailable;
  const metadataSessionId = selectedArtifact?.sessionId ?? text.unavailable;
  const metadataSummary = selectedArtifact?.summary ?? text.defaultSummary;

  return (
    <section data-testid="canvas-desk" className="canvas-desk canvas-root-layout">
      <div className="canvas-desk__header">
        <h2 className="desk-section-title">{text.title}</h2>
        <p className="desk-section-desc">{text.copy}</p>
        <div className="canvas-desk__toolbar canvas-toolbar">
          <Button
            variant="secondary"
            size="control"
            data-testid="canvas-refresh"
            disabled={isLoading || isRefreshing}
            onClick={() => {
              void handleRefresh();
            }}
          >
            {isRefreshing ? text.refreshing : text.refresh}
          </Button>
          <label className="metric-label" htmlFor="canvas-kind-filter">
            {text.kindFilter}
          </label>
          <Select
            id="canvas-kind-filter"
            data-testid="canvas-kind-filter"
            className="control-plane-filter"
            value={kindFilter}
            onChange={(event) => setKindFilter(event.target.value as CanvasKindFilter)}
          >
            <option value="all">{text.allKinds}</option>
            {CANVAS_KIND_OPTIONS.map((kind) => (
              <option value={kind} key={kind}>
                {resolveKindLabel(kind)}
              </option>
            ))}
          </Select>
          <span className="composer__status">
            {isLoading ? text.loading : text.artifactCount(artifacts.length)}
          </span>
        </div>
      </div>

      {error ? (
        <section className="status-card status-card--error" data-testid="canvas-error">
          <h3 className="desk-section-title">{text.errorTitle}</h3>
          <p className="desk-section-desc">{error}</p>
        </section>
      ) : null}

      <div className="canvas-desk__layout canvas-split-layout">
        <section className="timeline" data-testid="canvas-artifact-list">
          <div className="timeline__header">
            <h3 className="desk-section-title">{text.listTitle}</h3>
            <span className="composer__status">
              {isLoading ? text.loading : kindFilter === "all" ? text.allKinds : resolveKindLabel(kindFilter)}
            </span>
          </div>
          <div className="timeline__body">
            {isLoading ? <Skeleton height={52} count={2} /> : null}
            {!isLoading && artifacts.length === 0 ? (
              <div data-testid="canvas-empty-state">
                <EmptyState icon={<Layers size={28} strokeWidth={1.5} />} title={text.emptyState} />
              </div>
            ) : null}

            {artifacts.map((artifact) => {
              const isPending = selectionPendingId === artifact.id;
              const isSelected = selectedArtifact?.id === artifact.id;

              return (
                <button
                  type="button"
                  key={artifact.id}
                  className={`canvas-artifact-card${isSelected ? " canvas-artifact-card--selected" : ""}${isPending ? " canvas-artifact-card--pending" : ""}`}
                  data-testid={`canvas-artifact-select-${artifact.id}`}
                  aria-pressed={isSelected}
                  disabled={isPending}
                  onClick={() => { void handleSelectArtifact(artifact.id); }}
                >
                  <div className="canvas-artifact-card__header">
                    <span className="canvas-artifact-card__kind">{resolveKindLabel(artifact.kind)}</span>
                    <span
                      className="canvas-artifact-card__date"
                      title={formatDateTime(artifact.updatedAt, text.unavailable)}
                    >
                      {formatRelativeTime(artifact.updatedAt, text.relativeTime)}
                    </span>
                  </div>
                  <span className="canvas-artifact-card__title">{artifact.title}</span>
                  {artifact.route ? <span className="canvas-artifact-card__route">{artifact.route}</span> : null}
                </button>
              );
            })}
          </div>
        </section>

        <div className="canvas-preview-column">
          <section className="status-card status-card--normal" data-testid="canvas-preview">
            <div className="canvas-preview-header">
              <h3 className="desk-section-title">{selectionLabel}</h3>
              <span className={`canvas-kind-chip ${selectedArtifact ? "" : "canvas-kind-chip--inactive"}`}>{selectionKind}</span>
            </div>

            <div className="canvas-preview-surface">
              {selectedArtifact?.kind === "Image" ? (
                <img
                  data-testid="canvas-artifact-image"
                  src={`/api/${selectedArtifact.entryPath}`}
                  alt={selectedArtifact.title}
                  className="canvas-preview-image"
                />
              ) : selectedArtifact?.kind === "Html" ? (
                selectedArtifact.contentText != null ? (
                  <iframe
                    title={text.previewFrameTitle}
                    data-testid="canvas-entry-frame"
                    srcDoc={selectedArtifact.contentText}
                    sandbox="allow-scripts"
                    className="canvas-preview-frame"
                  />
                ) : previewEntryUrl ? (
                  <iframe
                    title={text.previewFrameTitle}
                    data-testid="canvas-entry-frame"
                    src={previewEntryUrl}
                    className="canvas-preview-frame"
                  />
                ) : null
              ) : (selectedArtifact?.contentText ?? fetchedContent) != null ? (
                <div
                  data-testid="canvas-artifact-content"
                  data-kind={selectedArtifact?.kind}
                  className={`canvas-markdown-view${selectedArtifact?.kind === "TaskList" ? " canvas-tasklist-view" : ""}`}
                >
                  <ReactMarkdown remarkPlugins={[remarkGfm]}>{(selectedArtifact?.contentText ?? fetchedContent)!}</ReactMarkdown>
                </div>
              ) : shouldFetchContent ? (
                <div className="canvas-preview-fallback">
                  <span className="desk-section-desc canvas-no-margin">{text.loading}</span>
                </div>
              ) : previewEntryUrl ? (
                <iframe
                  title={text.previewFrameTitle}
                  data-testid="canvas-entry-frame"
                  src={previewEntryUrl}
                  className="canvas-preview-frame"
                />
              ) : (
                <div data-testid="canvas-fallback" className="canvas-preview-fallback">
                  <p className="desk-section-desc canvas-no-margin">
                    {text.previewUnavailable}
                  </p>
                </div>
              )}
            </div>
          </section>

          <section className="status-card status-card--normal" data-testid="canvas-metadata">
            <h3 className="desk-section-title">{text.metadataTitle}</h3>
            <div className="canvas-metadata-grid">
              <div className="metric-item">
                <span className="metric-label">{text.metadata.route}</span>
                <span className="metric-value metric-value--path">{metadataRoute}</span>
              </div>
              <div className="metric-item">
                <span className="metric-label">{text.metadata.entryPath}</span>
                <span className="metric-value metric-value--path">{metadataEntryPath}</span>
              </div>
              <div className="metric-item">
                <span className="metric-label">{text.metadata.assetDirectory}</span>
                <span className="metric-value metric-value--path">{metadataAssetDirectory}</span>
              </div>
              <div className="metric-item">
                <span className="metric-label">{text.metadata.session}</span>
                <span className="metric-value metric-value--path">{metadataSessionId}</span>
              </div>
              <div className="metric-item">
                <span className="metric-label">{text.metadata.summary}</span>
                <span className="metric-value">{metadataSummary}</span>
              </div>
            </div>
          </section>
        </div>
      </div>
    </section>
  );
}
