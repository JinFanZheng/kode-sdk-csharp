import { useState, useEffect } from 'react';
import type { ModelPreset, ModelConnectionTestResponse, ModelProviderKind } from '../../types/contracts';
import {
  fetchModelPresets,
  testModelConnection,
  createProviderAccount,
  setDefaultAccountModel,
} from '../../lib/api';

interface Props {
  onNext: () => void;
  onSkip: () => void;
}

type ProviderDef = { id: string; label: string; comingSoon?: boolean };

const PROVIDERS: ProviderDef[] = [
  { id: 'Anthropic', label: 'Anthropic'  },
  { id: 'OpenAI',    label: 'OpenAI'     },
  { id: 'DeepSeek',  label: 'DeepSeek'   },
  { id: 'GLM',       label: '智谱 GLM'   },
  { id: 'MiniMax',   label: 'MiniMax'    },
  { id: 'Kimi',      label: 'Kimi'       },
  { id: 'Xiaomi',    label: '小米 MiMo'  },
  { id: 'Ollama',    label: 'Ollama'     },
];

// Coding Plan 模式下的 Provider 列表
const CODING_PLAN_PROVIDERS: ProviderDef[] = [
  { id: 'GLM',    label: '智谱 GLM'               },
  { id: 'Xiaomi', label: '小米 MiMo'              },
  { id: 'MiniMax', label: 'MiniMax' },
];

const PROVIDER_FILTER: Record<string, (p: ModelPreset) => boolean> = {
  Anthropic: p => p.provider === 'Anthropic',
  OpenAI:    p => p.provider === 'OpenAI' || p.provider === 'OpenAIResponses',
  DeepSeek:  p => p.presetId.startsWith('deepseek-'),
  // 标准 API 模式下过滤掉 coding-plan 专属 preset
  GLM:       p => p.presetId.startsWith('glm-') && p.accessMode !== 'coding-plan',
  MiniMax:   p => p.presetId.startsWith('minimax-') && p.accessMode !== 'coding-plan',
  Kimi:      p => p.presetId.startsWith('kimi-') || p.presetId.startsWith('moonshot-'),
  Xiaomi:    p => p.presetId.startsWith('xiaomi-') && p.accessMode !== 'coding-plan',
  Ollama:    p => p.presetId.startsWith('ollama-') || p.presetId === 'custom',
};

// Coding Plan 模式下各 Provider 展示的 preset
const CODING_PLAN_FILTER: Record<string, (p: ModelPreset) => boolean> = {
  // glm-5-anthropic 是冗余变体，onboarding 不展示
  GLM:    p => (p.presetId === 'glm-5.1' || p.presetId === 'glm-5-turbo') && p.accessMode === 'coding-plan',
  MiniMax: p => ['minimax-m2.7-coding', 'minimax-m2.5-coding', 'minimax-m2.7-highspeed-coding', 'minimax-m2.5-highspeed-coding'].includes(p.presetId) && p.accessMode === 'coding-plan',
  Xiaomi: p => (p.presetId === 'xiaomi-mimo-v2-pro-coding' || p.presetId === 'xiaomi-mimo-v2-omni-coding') && p.accessMode === 'coding-plan',
};

export function ModelStep({ onNext, onSkip }: Props) {
  const [presets, setPresets]                     = useState<ModelPreset[]>([]);
  const [mode, setMode]                           = useState<'api' | 'coding-plan'>('api');
  const [provider, setProvider]                   = useState<string | null>(null);
  const [preset, setPreset]                       = useState<ModelPreset | null>(null);
  const [apiKey, setApiKey]                       = useState('');
  const [customModelId, setCustomModelId]         = useState('');
  const [customBaseUrl, setCustomBaseUrl]         = useState('');
  const [showAdvanced, setShowAdvanced]           = useState(false);
  const [testing, setTesting]                     = useState(false);
  const [testResult, setTestResult]               = useState<ModelConnectionTestResponse | null>(null);
  const [protocol, setProtocol]                   = useState<'Anthropic' | 'OpenAI'>('OpenAI');
  const [saving, setSaving]                       = useState(false);
  const [saveError, setSaveError]                 = useState<string | null>(null);

  useEffect(() => {
    fetchModelPresets().then(setPresets).catch(() => {});
  }, []);

  const providerPresets = provider
    ? mode === 'coding-plan'
      ? presets.filter(p => (CODING_PLAN_FILTER[provider] ?? (() => false))(p))
      // 标准 API 模式：只展示文本/多模态模型，过滤掉 coding-plan 专属 preset
      : presets.filter(p => (PROVIDER_FILTER[provider] ?? (() => false))(p) && (p.defaultCapabilities & 1) !== 0)
    : [];

  const isOllama         = !!preset && preset.presetId.startsWith('ollama-');
  const effectiveModelId = customModelId.trim() || preset?.modelId || '';
  const presetBaseUrl = (protocol === 'Anthropic' && preset?.anthropicBaseUrl)
    ? preset.anthropicBaseUrl
    : preset?.baseUrl;
  const effectiveBaseUrl = customBaseUrl.trim() || presetBaseUrl || undefined;
  const needsModelId     = !!preset && preset.modelId === '';
  const needsApiKey      = !!preset && !isOllama;
  const showBaseUrl      = !!preset && (preset.requiresBaseUrl || isOllama);
  const showAdvancedOpt  = !!preset && !preset.requiresBaseUrl && !isOllama;
  const canTest          = !!preset && (!needsModelId || !!effectiveModelId) && (!needsApiKey || !!apiKey);

  function selectMode(m: 'api' | 'coding-plan') {
    setMode(m);
    setProvider(null);
    setPreset(null);
    setApiKey('');
    setCustomModelId('');
    setCustomBaseUrl('');
    setShowAdvanced(false);
    setTestResult(null);
    setSaveError(null);
  }

  function selectProvider(pid: string) {
    setProvider(pid);
    setPreset(null);
    setApiKey('');
    setCustomModelId('');
    setCustomBaseUrl('');
    setShowAdvanced(false);
    setTestResult(null);
    setSaveError(null);
  }

  function selectPreset(p: ModelPreset) {
    setPreset(p);
    setApiKey('');
    setCustomModelId('');
    setCustomBaseUrl('');
    setProtocol('OpenAI');
    setTestResult(null);
    setSaveError(null);
  }

  async function handleTest() {
    if (!preset) return;
    setTesting(true);
    setTestResult(null);
    try {
      const r = await testModelConnection({
        presetId: preset.presetId,
        modelId:  effectiveModelId || undefined,
        baseUrl:  effectiveBaseUrl,
        apiKey,
      });
      setTestResult(r);
    } catch {
      setTestResult({ ok: false, latencyMs: 0, error: 'network_error' });
    } finally {
      setTesting(false);
    }
  }

  async function handleSave() {
    if (!preset || !testResult?.ok) return;
    setSaving(true);
    setSaveError(null);
    try {
      const displayName = customModelId.trim()
        ? `${preset.displayName} (${customModelId.trim()})`
        : preset.displayName;
      // Coding-plan 路径由 preset 决定；标准 API 路径下，Compatible 预设需要
      // 根据协议选择器补上 "Compatible" 后缀，否则会丢成 literal 'OpenAI'/'Anthropic'。
      const providerKind: ModelProviderKind = (() => {
        if (mode === 'coding-plan') return preset.provider as ModelProviderKind;
        if (preset.provider === 'Anthropic') return 'Anthropic';
        if (preset.provider === 'OpenAI') return 'OpenAI';
        if (preset.provider === 'OpenAIResponses') return 'OpenAIResponses';
        // OpenAICompatible / AnthropicCompatible: 协议选择器决定走哪一边
        return protocol === 'Anthropic' ? 'AnthropicCompatible' : 'OpenAICompatible';
      })();
      const created = await createProviderAccount({
        displayName,
        providerKind,
        baseUrl: effectiveBaseUrl ?? null,
        apiKeyEnvironmentVariable: null,
        apiKeyValue: apiKey || null,
        models: [{
          displayName,
          modelId: effectiveModelId,
          capabilities: preset.defaultCapabilities,
          contextWindowSize: preset.contextWindowSize ?? undefined,
          maxOutputTokens: preset.maxOutputTokens ?? undefined,
          isReasoning: preset.isReasoning ?? undefined,
          supportsToolCalling: preset.supportsToolCalling ?? undefined,
          isDefaultForAccount: true,
          isGlobalDefault: false,
        }],
      });
      const createdModel = created.models[0];
      if (createdModel) {
        await setDefaultAccountModel(created.id, createdModel.id);
      }
      onNext();
    } catch (err) {
      setSaveError(err instanceof Error ? err.message : '保存失败，请重试');
    } finally {
      setSaving(false);
    }
  }

  return (
    <div className="ob-step" data-testid="onboarding-step-model">
      {/* Header */}
      <h1 className="ob-title">配置 AI 模型</h1>
      <p className="ob-desc">选择接入方式，填入密钥，即可开始对话</p>

      {/* 模式切换 */}
      <div className="ob-mode-tabs" role="tablist" aria-label="接入方式">
        <button
          role="tab"
          aria-selected={mode === 'api'}
          className={`ob-mode-tab ${mode === 'api' ? 'is-active' : ''}`}
          data-testid="mode-tab-api"
          onClick={() => selectMode('api')}
        >
          标准 API
        </button>
        <button
          role="tab"
          aria-selected={mode === 'coding-plan'}
          className={`ob-mode-tab ${mode === 'coding-plan' ? 'is-active' : ''}`}
          data-testid="mode-tab-coding-plan"
          onClick={() => selectMode('coding-plan')}
        >
          Coding Plan
        </button>
      </div>

      {/* Provider tabs */}
      <div className="ob-provider-tabs" role="tablist">
        {(mode === 'coding-plan' ? CODING_PLAN_PROVIDERS : PROVIDERS).map(p => (
          <button
            key={p.id}
            role="tab"
            aria-selected={provider === p.id}
            disabled={p.comingSoon === true}
            className={`ob-provider-tab ${provider === p.id ? 'is-active' : ''} ${p.comingSoon ? 'is-coming-soon' : ''}`}
            data-testid={`provider-card-${p.id.toLowerCase()}`}
            onClick={() => { if (!p.comingSoon) selectProvider(p.id); }}
          >
            {p.label}
            {p.comingSoon && <span className="ob-coming-soon-badge">即将支持</span>}
          </button>
        ))}
      </div>

      {/* Model list */}
      {provider && (
        <div className="ob-model-list">
          {providerPresets.length === 0 && (
            <p className="ob-loading">加载预设中…</p>
          )}
          {providerPresets.map(p => (
            <label
              key={p.presetId}
              className={`ob-model-row ${preset?.presetId === p.presetId ? 'is-selected' : ''}`}
              data-testid={`model-card-${p.presetId}`}
            >
              <input
                type="radio"
                name="model"
                checked={preset?.presetId === p.presetId}
                onChange={() => selectPreset(p)}
              />
              <div className="ob-model-info">
                <span className="ob-model-name">
                  {p.displayName}
                  {p.tier === 'Recommended' && <span className="ob-badge">推荐</span>}
                </span>
                <span className="ob-model-meta">{p.description}</span>
              </div>
              {p.contextWindowSize >= 100000 && (
                <span className="ob-model-ctx">{(p.contextWindowSize / 1000).toFixed(0)}K</span>
              )}
            </label>
          ))}
        </div>
      )}

      {/* Custom model ID (empty modelId presets) */}
      {needsModelId && (
        <div className="ob-field">
          <label htmlFor="ob-custom-model">模型名称 <span className="ob-required">*</span></label>
          <input
            id="ob-custom-model"
            type="text"
            className="ob-input"
            data-testid="custom-model-id-input"
            placeholder={isOllama ? 'llama3.2、qwen2.5 等' : '完整 model ID'}
            value={customModelId}
            onChange={e => { setCustomModelId(e.target.value); setTestResult(null); }}
          />
        </div>
      )}

      {/* Base URL (Ollama / custom endpoint) */}
      {showBaseUrl && (
        <div className="ob-field">
          <label htmlFor="ob-base-url">Base URL</label>
          <input
            id="ob-base-url"
            type="text"
            className="ob-input"
            data-testid="custom-base-url-input"
            placeholder={isOllama ? 'http://localhost:11434/v1' : 'https://your-endpoint/v1'}
            value={customBaseUrl || preset?.baseUrl || ''}
            onChange={e => { setCustomBaseUrl(e.target.value); setTestResult(null); }}
          />
        </div>
      )}

      {/* Protocol selector (标准 API 模式下的 OpenAI-compatible provider 才显示) */}
      {mode === 'api' && preset && preset.provider === 'OpenAICompatible' && !isOllama && (
        <div className="ob-field">
          <label>API 协议</label>
          <div className="ob-protocol-tabs">
            {(['OpenAI', 'Anthropic'] as const).map(p => (
              <button
                key={p}
                type="button"
                className={`ob-protocol-tab ${protocol === p ? 'is-active' : ''}`}
                onClick={() => { setProtocol(p); setTestResult(null); }}
              >
                {p === 'OpenAI' ? 'OpenAI 兼容' : 'Anthropic 兼容'}
              </button>
            ))}
          </div>
        </div>
      )}

      {/* API Key */}
      {needsApiKey && (
        <div className="ob-field">
          <label htmlFor="ob-apikey">
            {mode === 'coding-plan' ? 'Coding Plan Key' : 'API Key'}
          </label>
          <input
            id="ob-apikey"
            type="password"
            className="ob-input ob-input--mono"
            data-testid="apikey-input"
            placeholder={preset?.provider === 'Anthropic' ? 'sk-ant-api03-…' : 'sk-…'}
            value={apiKey}
            onChange={e => { setApiKey(e.target.value); setTestResult(null); }}
          />
          {mode === 'coding-plan' ? (
            <span className="ob-hint">
              前往{' '}
              <a
                href={provider === 'Xiaomi'
                  ? 'https://platform.xiaomimimo.com/#/token-plan'
                  : provider === 'MiniMax'
                  ? 'https://platform.minimaxi.com/subscribe/token-plan?code=JV0dA04FuC&source=link'
                  : 'https://www.bigmodel.cn/glm-coding?ic=HFFPJWPZQN'}
                target="_blank"
                rel="noreferrer"
                className="ob-hint-link"
              >
                {provider === 'Xiaomi'
                  ? '小米 MiMo Token Plan 订阅页'
                  : provider === 'MiniMax'
                  ? 'MiniMax Token Plan 订阅页'
                  : '智谱 Coding Plan 订阅页'}
              </a>{' '}
              获取
            </span>
          ) : (
            <span className="ob-hint">连接测试仅发送 1 token，费用 &lt; $0.0001</span>
          )}
        </div>
      )}

      {/* Advanced (proxy / custom Base URL override for non-local providers) */}
      {showAdvancedOpt && (
        <div className="ob-advanced">
          <button
            type="button"
            className="ob-advanced-toggle"
            onClick={() => setShowAdvanced(v => !v)}
          >
            {showAdvanced ? '▲ 收起' : '▼ 高级选项（代理 / 自定义 Base URL）'}
          </button>
          {showAdvanced && (
            <div className="ob-advanced-body">
              <div className="ob-field">
                <label htmlFor="ob-adv-url">
                  自定义 Base URL
                  <span className="ob-optional">可选</span>
                </label>
                <input
                  id="ob-adv-url"
                  type="text"
                  className="ob-input"
                  data-testid="custom-base-url-input"
                  placeholder={
                    presetBaseUrl
                      ?? (preset?.provider === 'Anthropic'
                          ? 'https://api.anthropic.com（默认）'
                          : 'https://api.openai.com/v1（默认）')
                  }
                  value={customBaseUrl}
                  onChange={e => { setCustomBaseUrl(e.target.value); setTestResult(null); }}
                />
              </div>
            </div>
          )}
        </div>
      )}

      {/* Test + Save */}
      {preset && (
        <div className="ob-actions">
          {!testResult?.ok && (
            <button
              className="ob-test-btn"
              data-testid="test-connection-btn"
              onClick={() => void handleTest()}
              disabled={testing || !canTest}
            >
              {testing ? (
                <><span className="ob-spinner" /> 测试中…</>
              ) : '测试连接'}
            </button>
          )}

          {testResult && (
            <div
              className={`ob-result ${testResult.ok ? 'is-ok' : 'is-err'}`}
              data-testid="connection-result"
            >
              {testResult.ok
                ? `✓ 连接成功（${testResult.latencyMs}ms）`
                : `✗ ${testResult.error === 'authentication_error' ? 'Key 认证失败' : `连接失败：${testResult.error}`}`
              }
            </div>
          )}

          {testResult?.ok && (
            <button
              className="ob-save-btn"
              onClick={() => void handleSave()}
              disabled={saving}
            >
              {saving ? '保存中…' : '开始使用 KodaClaw →'}
            </button>
          )}

          {saveError && (
            <div className="ob-result is-err" data-testid="save-error">✗ {saveError}</div>
          )}
        </div>
      )}

      {/* Skip */}
      <button className="ob-skip" onClick={onSkip}>
        跳过，我自己配置
      </button>
    </div>
  );
}
