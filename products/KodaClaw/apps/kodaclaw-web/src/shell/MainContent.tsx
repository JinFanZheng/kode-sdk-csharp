import type { ReactNode } from 'react';
import {
  MessageSquare, Inbox, LayoutDashboard, Radio, Zap,
  Cpu, Puzzle, Search, Lightbulb, Settings, Network, Activity,
} from 'lucide-react';
import type { MainDesk } from '../shell-shared/types';
import { AutomationsDesk } from '../components/AutomationsDesk';
import { CanvasDesk } from '../components/CanvasDesk';
import { ChannelsDesk } from '../components/ChannelsDesk';
import { InboxApprovalDesk } from '../components/inbox/InboxApprovalDesk';
import { ModelsSettingsDesk } from '../components/ModelsSettingsDesk';
import { PluginsDesk } from '../components/PluginsDesk';
import { SessionsDiagnosticsDesk } from '../components/SessionsDiagnosticsDesk';
import { SkillsDesk } from '../components/SkillsDesk';
import { SettingsDesk } from '../components/SettingsDesk';
import { McpServersDesk } from '../components/McpServersDesk';
import { DiagnosticsDesk } from '../components/DiagnosticsDesk';
import { DeskPageHeader } from './DeskPageHeader';
import { useLocaleText } from '../i18n/I18nProvider';

const STROKE = 1.75;
const ICON_SIZE = 20;

type MainContentProps = {
  mainDesk: MainDesk;
  chatTimeline: ReactNode;
  chatComposer: ReactNode;
  chatHeaderActions?: ReactNode;
  chatBanner?: ReactNode;
  sessionsFocusRequest?: { sessionId: string; requestId: number } | null;
  onFocusRequestConsumed?: () => void;
  onOpenSessionDetail?: (sessionId: string) => void;
};

type DeskConfig = {
  icon: ReactNode;
  label: string;
};

export function MainContent({
  mainDesk,
  chatTimeline,
  chatComposer,
  chatHeaderActions,
  chatBanner,
  sessionsFocusRequest,
  onFocusRequestConsumed,
  onOpenSessionDetail,
}: MainContentProps) {
  const labels = useLocaleText({
    zh: {
      chat: '对话', inbox: '收件箱 / 审批', canvas: '画布',
      channels: '渠道', automations: '自动化', models: '模型设置',
      plugins: '插件', sessions: '会话诊断', skills: '技能', settings: '设置',
      mcpServers: 'MCP 工具', diagnostics: '诊断',
    },
    en: {
      chat: 'Chat', inbox: 'Inbox / Approvals', canvas: 'Canvas',
      channels: 'Channels', automations: 'Automations', models: 'Model Settings',
      plugins: 'Plugins', sessions: 'Session Diagnostics', skills: 'Skills', settings: 'Settings',
      mcpServers: 'MCP Tools', diagnostics: 'Diagnostics',
    },
  });

  const DESK_CONFIG: Record<MainDesk, DeskConfig> = {
    chat:        { icon: <MessageSquare size={ICON_SIZE} strokeWidth={STROKE} />, label: labels.chat },
    inbox:       { icon: <Inbox size={ICON_SIZE} strokeWidth={STROKE} />, label: labels.inbox },
    canvas:      { icon: <LayoutDashboard size={ICON_SIZE} strokeWidth={STROKE} />, label: labels.canvas },
    channels:    { icon: <Radio size={ICON_SIZE} strokeWidth={STROKE} />, label: labels.channels },
    automations: { icon: <Zap size={ICON_SIZE} strokeWidth={STROKE} />, label: labels.automations },
    models:      { icon: <Cpu size={ICON_SIZE} strokeWidth={STROKE} />, label: labels.models },
    plugins:     { icon: <Puzzle size={ICON_SIZE} strokeWidth={STROKE} />, label: labels.plugins },
    sessions:    { icon: <Search size={ICON_SIZE} strokeWidth={STROKE} />, label: labels.sessions },
    skills:      { icon: <Lightbulb size={ICON_SIZE} strokeWidth={STROKE} />, label: labels.skills },
    settings:    { icon: <Settings size={ICON_SIZE} strokeWidth={STROKE} />, label: labels.settings },
    mcpServers:  { icon: <Network size={ICON_SIZE} strokeWidth={STROKE} />, label: labels.mcpServers },
    diagnostics: { icon: <Activity size={ICON_SIZE} strokeWidth={STROKE} />, label: labels.diagnostics },
  };

  if (mainDesk === 'chat') {
    return (
      <div className="kc-chat-view" data-testid="kc-chat-view" data-kc-mode="chat">
        <div className="kc-chat-header" data-testid="kc-chat-header">
          <h1 className="kc-chat-header__title">{labels.chat}</h1>
          {chatHeaderActions && (
            <div className="kc-chat-header__actions">{chatHeaderActions}</div>
          )}
        </div>
        {chatBanner && <div className="kc-chat-banner">{chatBanner}</div>}
        <div className="kc-chat-timeline">{chatTimeline}</div>
        <div className="kc-chat-composer">{chatComposer}</div>
        <div className="kc-chat-disclaimer">
          <span className="kc-chat-disclaimer__hint">Shift+Enter 换行</span>
          <span className="kc-chat-disclaimer__center">Koda (AI) 也会犯错，请注意甄别。</span>
        </div>
      </div>
    );
  }

  const config = DESK_CONFIG[mainDesk];
  return (
    <div className="kc-desk-view" data-testid="control-plane-view" data-kc-view={mainDesk}>
      <DeskPageHeader icon={config.icon} title={config.label} />
      <div className={`kc-desk-content${mainDesk === 'settings' ? ' kc-desk-content--split' : ''}${mainDesk === 'diagnostics' ? ' kc-desk-content--fullbleed' : ''}${mainDesk === 'automations' ? ' kc-desk-content--automations' : ''}`}>
        {mainDesk === 'inbox' && <InboxApprovalDesk />}
        {mainDesk === 'sessions' && (
          <SessionsDiagnosticsDesk
            focusRequest={sessionsFocusRequest ?? null}
            onFocusRequestConsumed={onFocusRequestConsumed ?? (() => {})}
          />
        )}
        {mainDesk === 'models' && <ModelsSettingsDesk />}
        {mainDesk === 'automations' && <AutomationsDesk />}
        {mainDesk === 'canvas' && <CanvasDesk />}
        {mainDesk === 'plugins' && <PluginsDesk />}
        {mainDesk === 'channels' && <ChannelsDesk />}
        {mainDesk === 'skills' && <SkillsDesk />}
        {mainDesk === 'settings' && <SettingsDesk />}
        {mainDesk === 'mcpServers' && <McpServersDesk />}
        {mainDesk === 'diagnostics' && <DiagnosticsDesk />}
      </div>
    </div>
  );
}
