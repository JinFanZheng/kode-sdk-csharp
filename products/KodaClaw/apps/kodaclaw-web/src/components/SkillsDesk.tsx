import { fetchSkills } from "../lib/api";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { queryKeys } from "../lib/queryKeys";
import { type SkillDescriptor } from "../types/contracts";
import { useLocaleText } from "../i18n/I18nProvider";
import { Skeleton } from "./ui/Skeleton";
import { EmptyState } from "./ui/EmptyState";
import { Button } from "./ui/Button";
import { Lightbulb } from "lucide-react";
import "./ControlPlaneDesk.css";

type SkillSource = "built-in" | "global" | "workspace";

const sourceBadgeMap: Record<SkillSource, string> = {
  "built-in": "mode-badge",
  "global": "mode-badge",
  "workspace": "mode-badge mode-badge--main",
};

const SOURCE_ORDER: SkillSource[] = ["built-in", "global", "workspace"];

/** builtin-core before optional; same kind alphabetical */
function sortSkills(skills: SkillDescriptor[], source: SkillSource): SkillDescriptor[] {
  if (source !== "built-in") {
    return [...skills].sort((a, b) => a.name.localeCompare(b.name));
  }
  return [...skills].sort((a, b) => {
    const aCore = a.kind === "builtin-core" ? 0 : 1;
    const bCore = b.kind === "builtin-core" ? 0 : 1;
    if (aCore !== bCore) return aCore - bCore;
    return a.name.localeCompare(b.name);
  });
}

export function SkillsDesk() {
  const text = useLocaleText({
    zh: {
      eyebrow: "能力层",
      title: "技能浏览台",
      copy: "查看已发现的技能，了解每个技能的来源和描述。在对话中用 skill_activate 激活技能，或用 fs_write 向 workspace/skills/ 写入 SKILL.md 来创建新技能。",
      refresh: "刷新列表",
      refreshing: "刷新中...",
      loading: "正在加载技能...",
      empty: "未发现任何技能。",
      loaded: (count: number) => `已发现 ${count} 个技能`,
      sourceLabels: {
        "built-in": "内置",
        "global": "全局",
        "workspace": "工作区",
      } as Record<SkillSource, string>,
      hasResources: "含资源文件",
      noDescription: "未提供描述",
      loadError: "加载技能列表失败。",
      skillKindBuiltinCore: "核心",
      skillKindOptional: "可选",
      skillAllowedToolsLabel: "所需工具",
      skillCompatibilityLabel: "兼容性",
    },
    en: {
      eyebrow: "Capability Layer",
      title: "Skills Browser",
      copy: "Browse discovered skills and their sources. Use skill_activate in chat to load a skill, or write a SKILL.md to workspace/skills/ to author a new one.",
      refresh: "Refresh list",
      refreshing: "Refreshing...",
      loading: "Loading skills...",
      empty: "No skills discovered.",
      loaded: (count: number) => `${count} skill${count === 1 ? "" : "s"} discovered`,
      sourceLabels: {
        "built-in": "Built-in",
        "global": "Global",
        "workspace": "Workspace",
      } as Record<SkillSource, string>,
      hasResources: "Has resources",
      noDescription: "No description provided",
      loadError: "Failed to load skills.",
      skillKindBuiltinCore: "Core",
      skillKindOptional: "Optional",
      skillAllowedToolsLabel: "Allowed tools",
      skillCompatibilityLabel: "Compatibility",
    },
  });

  const queryClient = useQueryClient();
  const { data: skillsData, isLoading, isFetching, error: queryError } = useQuery({
    queryKey: queryKeys.skills,
    queryFn: () => fetchSkills(),
  });
  const skills: SkillDescriptor[] = skillsData ?? [];
  const isRefreshing = isFetching && !isLoading;
  const error: string | null = queryError instanceof Error ? queryError.message : null;

  const groupedSkills = SOURCE_ORDER
    .map((source) => ({
      source,
      items: sortSkills(skills.filter((s) => s.source === source), source),
    }))
    .filter((group) => group.items.length > 0);

  function kindBadgeClass(kind: string): string {
    return kind === "builtin-core"
      ? "skill-card__kind-badge skill-card__kind-badge--core"
      : "skill-card__kind-badge skill-card__kind-badge--optional";
  }

  function kindLabel(kind: string): string {
    return kind === "builtin-core" ? text.skillKindBuiltinCore : text.skillKindOptional;
  }

  return (
    <div className="control-plane-stack" data-testid="skills-desk">
      <div>
        <h2 className="desk-section-title">{text.title}</h2>
        <p className="desk-section-desc">{text.copy}</p>
        <div className="control-plane-toolbar">
        <Button
          variant="secondary"
          data-testid="skills-refresh"
          disabled={isLoading || isRefreshing}
          onClick={() => { void queryClient.invalidateQueries({ queryKey: queryKeys.skills }); }}
        >
          {isRefreshing ? text.refreshing : text.refresh}
        </Button>
        </div>
      </div>

      {error ? (
        <p className="desk-feedback desk-feedback--error" data-testid="skills-error">
          {error}
        </p>
      ) : null}

      {isLoading ? <Skeleton height={56} count={4} /> : null}

      {!isLoading && skills.length === 0 && !error ? (
        <div data-testid="skills-empty">
          <EmptyState icon={<Lightbulb size={28} strokeWidth={1.5} />} title={text.empty} />
        </div>
      ) : null}

      {groupedSkills.map(({ source, items }) => (
        <section key={source} data-testid={`skills-group-${source}`}>
          <p className="metric-label" style={{ marginBottom: 'var(--space-2)' }}>
            {text.sourceLabels[source]}
          </p>
          <div className="skills-list">
            {items.map((skill) => (
              <div
                key={`${source}-${skill.name}`}
                className="skill-card"
                data-testid={`skill-item-${skill.name}`}
              >
                <div className="skill-card__header">
                  <strong className="skill-card__name">{skill.name}</strong>
                  <div className="skill-card__badges">
                    <span className={kindBadgeClass(skill.kind)}>
                      {kindLabel(skill.kind)}
                    </span>
                    <span className={sourceBadgeMap[skill.source as SkillSource]}>
                      {text.sourceLabels[skill.source as SkillSource]}
                    </span>
                    {skill.hasResources ? (
                      <span className="mode-badge">{text.hasResources}</span>
                    ) : null}
                  </div>
                </div>
                <p className="skill-card__desc">
                  {skill.description ?? text.noDescription}
                </p>
                {skill.tags.length > 0 ? (
                  <div className="skill-card__tags">
                    {skill.tags.slice(0, 3).map((tag) => (
                      <span key={tag} className="skill-card__tag">{tag}</span>
                    ))}
                  </div>
                ) : null}
                {skill.allowedTools.length > 0 ? (
                  <p className="skill-card__requires">
                    {text.skillAllowedToolsLabel}: {skill.allowedTools.join(", ")}
                  </p>
                ) : null}
                {skill.compatibility ? (
                  <p className="skill-card__requires" style={{ color: "var(--text-tertiary)" }}>
                    {text.skillCompatibilityLabel}: {skill.compatibility}
                  </p>
                ) : null}
                <p className="skill-card__path">{skill.path}</p>
              </div>
            ))}
          </div>
        </section>
      ))}
    </div>
  );
}
