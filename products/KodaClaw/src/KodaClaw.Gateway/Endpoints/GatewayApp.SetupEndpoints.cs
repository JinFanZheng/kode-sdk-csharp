using KodaClaw.Contracts;
using KodaClaw.Gateway;
using KodaClaw.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

public static partial class GatewayApp
{
    private sealed record SetupCompleteRequest(
        string?  Provider,      // "Anthropic" | "OpenAI" | "OpenAICompatible" | "AnthropicCompatible"
        string?  ModelId,
        string?  ApiKey,
        string?  BaseUrl,
        string?  DisplayName);

    private static void MapSetupEndpoints(WebApplication app)
    {
        // GET /setup — server-rendered setup wizard
        app.MapGet("/setup", () =>
            Results.Content(SetupHtml, "text/html; charset=utf-8"));

        // POST /setup/complete — writes model registry from provided config
        app.MapPost("/setup/complete", async (
            SetupCompleteRequest request,
            ConfigBootstrapWriter writer,
            IProviderAccountRepository accountRepo,
            OnboardingStateService onboardingStateService,
            IWorkspaceService workspaceService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Provider))
                return Results.BadRequest(new
                {
                    code    = "setup.provider_required",
                    message = "Provider is required."
                });

            if (string.IsNullOrWhiteSpace(request.ModelId))
                return Results.BadRequest(new
                {
                    code    = "setup.model_required",
                    message = "ModelId is required."
                });

            if (!Enum.TryParse<ModelProviderKind>(request.Provider, ignoreCase: true, out var providerKind))
                return Results.BadRequest(new
                {
                    code    = "setup.invalid_provider",
                    message = $"Unknown provider '{request.Provider}'. Valid: Anthropic, OpenAI, OpenAICompatible, AnthropicCompatible."
                });

            // API key is required for hosted providers; optional for local (Ollama / localhost)
            var isLocal = IsLocalBaseUrl(request.BaseUrl);
            if (!isLocal && string.IsNullOrWhiteSpace(request.ApiKey))
                return Results.BadRequest(new
                {
                    code    = "setup.key_required",
                    message = "API key is required for hosted providers."
                });

            await writer.WriteIfAbsentAsync(
                provider:          providerKind,
                modelId:           request.ModelId,
                apiKey:            request.ApiKey,
                baseUrl:           request.BaseUrl,
                displayName:       request.DisplayName,
                cancellationToken: cancellationToken);

            // Mark onboarding as complete so the React app doesn't show its own wizard
            await onboardingStateService.CompleteAsync(cancellationToken);

            // Mark workspace bootstrap as complete so /api/system/bootstrap-state returns Normal mode
            var appConfig = await workspaceService.LoadAppConfigAsync(cancellationToken);
            if (!appConfig.BootstrapCompleted)
            {
                await workspaceService.SaveAppConfigAsync(
                    appConfig with { BootstrapCompleted = true },
                    cancellationToken);
            }

            var accounts = await accountRepo.ListAccountsAsync(cancellationToken);
            return Results.Ok(new
            {
                success      = true,
                accountCount = accounts.Count,
                message      = "Setup complete. KodaClaw is ready."
            });
        });
    }

    private static bool IsLocalBaseUrl(string? baseUrl) =>
        !string.IsNullOrWhiteSpace(baseUrl) &&
        (baseUrl.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
         baseUrl.Contains("127.0.0.1") ||
         baseUrl.Contains("::1"));

    // ── Setup Wizard HTML ──────────────────────────────────────────────────────

    private const string SetupHtml = """
        <!DOCTYPE html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>KodaClaw — 配置模型</title>
        <style>
        *,*::before,*::after{box-sizing:border-box;margin:0;padding:0}
        html,body{height:100%;overflow:hidden}
        body{font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",sans-serif;background:#0f0f10;color:#f2f2f3;font-size:14px;line-height:1.5}

        /* ── Shell ── */
        .shell{display:grid;grid-template-columns:320px 1fr;height:100vh;overflow:hidden}

        /* ── Left panel ── */
        .left{position:relative;background:#0c0c0f;overflow:hidden;display:flex;flex-direction:column}
        .left::before{content:'';position:absolute;inset:0;
          background-image:radial-gradient(circle,rgba(245,158,11,.16) 1px,transparent 1px);
          background-size:24px 24px;pointer-events:none}
        .left-glow{position:absolute;bottom:-80px;left:-40px;width:280px;height:280px;
          background:radial-gradient(circle,rgba(245,158,11,.2) 0%,transparent 70%);pointer-events:none}
        .left-inner{position:relative;z-index:1;display:flex;flex-direction:column;flex:1;padding:44px 36px 36px}
        .brand-mark{width:36px;height:36px;border:1.5px solid rgba(245,158,11,.5);border-radius:8px;
          display:flex;align-items:center;justify-content:center;font-size:13px;font-weight:600;
          color:#f59e0b;letter-spacing:.04em;margin-bottom:14px;font-family:ui-monospace,monospace}
        .brand-name{font-size:19px;font-weight:600;color:#f2f2f3;letter-spacing:-.02em;margin-bottom:5px;
          font-family:ui-monospace,monospace}
        .brand-sub{font-size:11px;color:rgba(155,155,155,.75);letter-spacing:.01em;font-family:ui-monospace,monospace}
        .divider{width:28px;height:1px;background:rgba(245,158,11,.28);margin:28px 0}
        .features{list-style:none;display:flex;flex-direction:column;gap:22px}
        .features li{display:flex;gap:12px;align-items:flex-start}
        .feat-icon{color:#f59e0b;font-size:13px;flex-shrink:0;margin-top:1px;opacity:.8}
        .features strong{display:block;font-family:ui-monospace,monospace;font-size:11.5px;font-weight:500;
          color:#d4d4d4;letter-spacing:.01em;margin-bottom:2px}
        .features em{display:block;font-style:normal;font-family:ui-monospace,monospace;font-size:11px;
          font-weight:300;color:rgba(155,155,155,.65);line-height:1.45}
        .left-footer{margin-top:auto;display:flex;align-items:center;gap:10px}
        .ver{font-family:ui-monospace,monospace;font-size:11px;color:rgba(155,155,155,.4)}
        .badge{font-family:ui-monospace,monospace;font-size:10px;color:rgba(245,158,11,.5);
          border:1px solid rgba(245,158,11,.18);padding:2px 7px;border-radius:999px;letter-spacing:.04em}

        /* ── Right panel ── */
        .right{background:#0f0f10;overflow-y:auto;display:flex;align-items:flex-start;
          justify-content:center;padding:52px 52px 52px}
        .form-wrap{width:100%;max-width:520px}
        .step-title{font-size:24px;font-weight:700;letter-spacing:-.4px;color:#f2f2f3;margin-bottom:7px}
        .step-desc{font-size:13px;color:#9b9b9b;margin-bottom:22px}

        /* ── Mode toggle ── */
        .mode-tabs{display:flex;gap:0;margin-bottom:20px;border:1.5px solid #2a2a2e;border-radius:8px;overflow:hidden}
        .mode-tab{flex:1;padding:9px 14px;background:#1a1a1c;color:#9b9b9b;border:none;cursor:pointer;
          font-size:13px;font-weight:500;transition:background .15s,color .15s;
          display:flex;align-items:center;justify-content:center;gap:8px}
        .mode-tab:hover:not(.active){background:#242427;color:#d4d4d4}
        .mode-tab.active{background:rgba(245,158,11,.1);color:#f59e0b;font-weight:600}
        .mode-tab+.mode-tab{border-left:1.5px solid #2a2a2e}
        .cp-pill{font-size:10px;font-weight:700;background:rgba(245,158,11,.15);color:#f59e0b;
          border:1px solid rgba(245,158,11,.2);padding:1px 7px;border-radius:999px;letter-spacing:.03em}

        /* ── Coding Plan info box ── */
        .cp-info{background:#1a1a1c;border:1px solid rgba(245,158,11,.18);border-radius:8px;
          padding:11px 14px;margin-bottom:18px;display:none}
        .cp-info.visible{display:block}
        .cp-info-text{font-size:12px;color:#9b9b9b;line-height:1.55}

        /* ── Provider tabs ── */
        .provider-tabs{display:flex;gap:6px;flex-wrap:wrap;margin-bottom:22px}
        .pvd-tab{padding:5px 15px;border:1.5px solid #2a2a2e;border-radius:999px;background:#1a1a1c;
          color:#9b9b9b;font-size:13px;font-weight:500;cursor:pointer;
          transition:border-color .15s,color .15s,background .15s;white-space:nowrap}
        .pvd-tab:hover{border-color:#3a3a3f;color:#f2f2f3}
        .pvd-tab.active{border-color:#f59e0b;background:rgba(245,158,11,.1);color:#f59e0b;font-weight:600}

        /* ── Model list ── */
        .model-list{display:flex;flex-direction:column;gap:6px;margin-bottom:22px}
        .model-row{display:flex;align-items:center;gap:11px;padding:11px 13px;
          border:1.5px solid #2a2a2e;border-radius:8px;cursor:pointer;background:#1a1a1c;
          transition:border-color .15s,background .15s}
        .model-row:hover{border-color:#3a3a3f;background:#242427}
        .model-row.selected{border-color:#f59e0b;background:rgba(245,158,11,.08)}
        .model-row input[type=radio]{width:15px;height:15px;accent-color:#f59e0b;flex-shrink:0;cursor:pointer}
        .model-info{flex:1;min-width:0}
        .model-name{font-size:13.5px;font-weight:600;color:#f2f2f3;display:flex;align-items:center;gap:7px}
        .rec-badge{font-size:10px;font-weight:700;background:#f59e0b;color:#0f0f10;
          padding:1px 7px;border-radius:999px;letter-spacing:.02em}
        .model-desc{font-size:12px;color:#6b6b6b;margin-top:1px}
        .model-ctx{font-size:11px;color:#6b6b6b;font-family:ui-monospace,monospace;
          background:#242427;padding:2px 7px;border-radius:999px;flex-shrink:0}

        /* ── Fields ── */
        .field{display:flex;flex-direction:column;gap:5px;margin-bottom:16px}
        .field label{font-size:13px;font-weight:600;color:#d4d4d4;display:flex;align-items:center;gap:5px}
        .req{color:#dc2626}
        .opt{font-size:11px;color:#6b6b6b;font-weight:400}
        .inp{width:100%;padding:9px 12px;border:1.5px solid #2a2a2e;border-radius:8px;
          background:#1a1a1c;color:#f2f2f3;font-size:13px;outline:none;
          transition:border-color .15s,box-shadow .15s}
        .inp:focus{border-color:#f59e0b;box-shadow:0 0 0 3px rgba(245,158,11,.12)}
        .inp.mono{font-family:ui-monospace,monospace;letter-spacing:.02em}
        .hint{font-size:11.5px;color:#6b6b6b}

        /* ── Advanced ── */
        .adv-toggle{background:none;border:none;font-size:12px;color:#6b6b6b;
          cursor:pointer;padding:0;transition:color .15s;margin-bottom:12px}
        .adv-toggle:hover{color:#9b9b9b}
        .adv-body{padding:13px 15px;background:#1a1a1c;border:1px solid #2a2a2e;
          border-radius:8px;margin-bottom:16px}
        .adv-body .field:last-child{margin-bottom:0}

        /* ── Actions ── */
        .actions{display:flex;flex-direction:column;gap:9px;margin-top:6px}
        .submit-btn{padding:11px 26px;background:#f59e0b;color:#0f0f10;border:none;border-radius:8px;
          font-size:14px;font-weight:700;cursor:pointer;letter-spacing:-.1px;align-self:flex-start;
          transition:background .15s;display:inline-flex;align-items:center;gap:7px}
        .submit-btn:hover:not(:disabled){background:#d97706}
        .submit-btn:disabled{opacity:.5;cursor:not-allowed}
        .spin{display:inline-block;width:13px;height:13px;border:2px solid rgba(0,0,0,.2);
          border-top-color:#0f0f10;border-radius:50%;animation:spin .6s linear infinite}
        @keyframes spin{to{transform:rotate(360deg)}}
        .msg{padding:9px 13px;border-radius:8px;font-size:13px;font-weight:500;display:none}
        .msg.ok{background:rgba(22,163,74,.12);color:#16a34a;border:1px solid rgba(22,163,74,.2);display:block}
        .msg.err{background:rgba(220,38,38,.1);color:#dc2626;border:1px solid rgba(220,38,38,.2);display:block}
        .skip-btn{background:none;border:none;color:#6b6b6b;font-size:12px;cursor:pointer;
          padding:0;margin-top:24px;align-self:flex-start;transition:color .15s}
        .skip-btn:hover{color:#9b9b9b}

        @media(max-width:700px){
          .shell{grid-template-columns:1fr}
          .left{display:none}
          .right{padding:36px 22px}
        }
        </style>
        </head>
        <body>
        <div class="shell">

          <!-- Left: brand -->
          <aside class="left">
            <div class="left-inner">
              <div class="brand-mark">K</div>
              <div class="brand-name">KodaClaw</div>
              <div class="brand-sub">运行在你本机的 Agent OS</div>
              <div class="divider"></div>
              <ul class="features">
                <li><span class="feat-icon">◈</span><span>
                  <strong>对话即工作流</strong>
                  <em>说一句话，Koda 安排好一切</em>
                </span></li>
                <li><span class="feat-icon">◈</span><span>
                  <strong>工作区是文件</strong>
                  <em>人格、记忆、规则，纯文本可读可改</em>
                </span></li>
                <li><span class="feat-icon">◈</span><span>
                  <strong>MCP 无限扩展</strong>
                  <em>接入任何工具和外部服务</em>
                </span></li>
              </ul>
              <div class="left-footer">
                <span class="ver">v0.1.0</span>
                <span class="badge">local-first</span>
              </div>
            </div>
            <div class="left-glow"></div>
          </aside>

          <!-- Right: config -->
          <main class="right">
            <div class="form-wrap">
              <h1 class="step-title">配置 AI 模型</h1>
              <p class="step-desc">选择提供商，填入密钥，即可开始使用 KodaClaw</p>

              <!-- Mode toggle -->
              <div class="mode-tabs">
                <button class="mode-tab active" data-mode="api" onclick="selectMode('api')">
                  标准 API
                </button>
                <button class="mode-tab" data-mode="coding-plan" onclick="selectMode('coding-plan')">
                  Coding Plan <span class="cp-pill">国内友好</span>
                </button>
              </div>

              <!-- Coding Plan info -->
              <div class="cp-info" id="cp-info">
                <p class="cp-info-text">
                  Coding Plan 使用 <strong style="color:#d4d4d4">Anthropic 兼容接口</strong>，
                  适合已订阅智谱 / MiniMax / 小米 <em>Token Plan</em> 的用户。
                  工具调用能力更强，无需代理直连国内节点。
                </p>
              </div>

              <!-- Provider tabs -->
              <div class="provider-tabs" id="provider-tabs"></div>

              <!-- Model list -->
              <div class="model-list" id="model-list"></div>

              <!-- Custom model ID (Ollama / empty modelId) -->
              <div class="field" id="field-custom-model" style="display:none">
                <label for="custom-model-id">模型名称 <span class="req">*</span></label>
                <input id="custom-model-id" class="inp mono" type="text"
                  placeholder="llama3.2、qwen2.5:14b …"
                  oninput="onFieldChange()">
              </div>

              <!-- API Key -->
              <div class="field" id="field-apikey" style="display:none">
                <label for="apikey-input">API Key <span id="key-req-mark" class="req">*</span></label>
                <input id="apikey-input" class="inp mono" type="password"
                  placeholder="sk-…" autocomplete="new-password" spellcheck="false"
                  oninput="onFieldChange()">
                <span class="hint" id="key-hint"></span>
              </div>

              <!-- Base URL (required field for local/custom providers) -->
              <div class="field" id="field-baseurl-required" style="display:none">
                <label for="baseurl-req-input">Base URL</label>
                <input id="baseurl-req-input" class="inp mono" type="text"
                  placeholder="http://localhost:11434/v1"
                  oninput="onFieldChange()">
              </div>

              <!-- Advanced (optional base URL override) -->
              <div id="field-advanced" style="display:none">
                <button class="adv-toggle" type="button" onclick="toggleAdvanced()">
                  <span id="adv-label">▼ 高级选项（代理 / 自定义 Base URL）</span>
                </button>
                <div class="adv-body" id="adv-body" style="display:none">
                  <div class="field">
                    <label for="baseurl-adv-input">
                      自定义 Base URL <span class="opt">可选</span>
                    </label>
                    <input id="baseurl-adv-input" class="inp mono" type="text"
                      placeholder="" oninput="onFieldChange()">
                  </div>
                </div>
              </div>

              <!-- Submit -->
              <div class="actions">
                <button class="submit-btn" id="submit-btn" onclick="doSetup()" disabled>
                  完成设置 →
                </button>
                <div class="msg" id="msg"></div>
              </div>

              <button class="skip-btn" onclick="doSkip()">跳过，我自己配置</button>
            </div>
          </main>
        </div>

        <script>
        // ── Provider / model data ───────────────────────────────────────────────
        // [modelId, displayName, description, ctx_k, isRecommended]

        var PROVIDERS_API = [
          { id:'Anthropic', label:'Anthropic', kind:'Anthropic',
            keyHint:'sk-ant-api03-…',
            models:[
              ['claude-sonnet-4-20250514', 'Claude Sonnet 4.5', '推荐 · 性能与成本最优', 200, true],
              ['claude-opus-4-20250514',   'Claude Opus 4',     '最强推理能力', 200, false],
              ['claude-haiku-4-5-20251001','Claude Haiku 4.5',  '高速 · 低成本', 200, false],
            ],
            baseUrl:null, showBaseUrl:false, optKey:false },

          { id:'OpenAI', label:'OpenAI', kind:'OpenAI',
            keyHint:'sk-…',
            models:[
              ['gpt-4o',      'GPT-4o',      '推荐 · 多模态旗舰', 128, true],
              ['gpt-4o-mini', 'GPT-4o mini', '快速 · 低成本', 128, false],
              ['o4-mini',     'o4-mini',     '推理模型', 200, false],
            ],
            baseUrl:null, showBaseUrl:false, optKey:false },

          { id:'DeepSeek', label:'DeepSeek', kind:'OpenAICompatible',
            keyHint:'sk-…',
            models:[
              ['deepseek-chat',     'DeepSeek V3', '推荐 · 中英文旗舰', 64, true],
              ['deepseek-reasoner', 'DeepSeek R1', '推理模型', 64, false],
            ],
            baseUrl:'https://api.deepseek.com/v1', showBaseUrl:false, optKey:false },

          { id:'GLM', label:'智谱 GLM', kind:'OpenAICompatible',
            keyHint:'your-api-key',
            models:[
              ['glm-4-flash',  'GLM-4 Flash',  '推荐 · 免费快速', 128, true],
              ['glm-4-plus',   'GLM-4 Plus',   '高性能旗舰', 128, false],
              ['glm-z1-flash', 'GLM-Z1 Flash', '推理模型 · 免费', 32, false],
            ],
            baseUrl:'https://open.bigmodel.cn/api/paas/v4', showBaseUrl:false, optKey:false },

          { id:'MiniMax', label:'MiniMax', kind:'OpenAICompatible',
            keyHint:'eyJ…',
            models:[
              ['MiniMax-M2.7',    'MiniMax M2.7', '推荐 · 旗舰推理（230B）', 200, true],
              ['MiniMax-Text-01', 'Text-01',       '超长上下文 1M', 1000, false],
            ],
            baseUrl:'https://api.minimaxi.com/v1', showBaseUrl:false, optKey:false },

          { id:'Kimi', label:'Kimi', kind:'OpenAICompatible',
            keyHint:'sk-…',
            models:[
              ['kimi-k2.5',       'Kimi K2.5',      '推荐 · 旗舰推理', 256, true],
              ['moonshot-v1-8k',  'Moonshot v1 8K', '快速', 8, false],
              ['moonshot-v1-32k', 'Moonshot v1 32K','长上下文', 32, false],
            ],
            baseUrl:'https://api.moonshot.cn/v1', showBaseUrl:false, optKey:false },

          { id:'Xiaomi', label:'小米 MiMo', kind:'OpenAICompatible',
            keyHint:'your-api-key',
            models:[
              ['MiMo-V2-Pro',  'MiMo-V2-Pro',  '推荐 · 超长上下文（1M）', 1000, true],
              ['MiMo-V2-Omni', 'MiMo-V2-Omni', '全模态 · 文本/图像/视频', 262, false],
            ],
            baseUrl:'https://api.xiaomimimo.com/v1', showBaseUrl:false, optKey:false },

          { id:'Ollama', label:'Ollama', kind:'OpenAICompatible',
            keyHint:'ollama（可不填）',
            models:[],
            baseUrl:'http://localhost:11434/v1', showBaseUrl:true, optKey:true },
        ];

        // Coding Plan — Anthropic 兼容接口，适合 Token Plan 订阅用户
        var PROVIDERS_CP = [
          { id:'GLM', label:'智谱 GLM', kind:'AnthropicCompatible',
            keyHint:'your-api-key',
            models:[
              ['glm-5.1',     'GLM-5.1',     '推荐 · 对标 Claude Opus', 200, true],
              ['GLM-5-Turbo', 'GLM-5-Turbo', '高性能旗舰', 200, false],
            ],
            baseUrl:'https://open.bigmodel.cn/api/anthropic', showBaseUrl:false, optKey:false },

          { id:'MiniMax', label:'MiniMax', kind:'AnthropicCompatible',
            keyHint:'eyJ…',
            models:[
              ['MiniMax-M2.7',          'MiniMax M2.7',   '推荐 · 旗舰推理', 200, true],
              ['MiniMax-M2.5',          'MiniMax M2.5',   '均衡 · 高性价比', 200, false],
              ['MiniMax-M2.7-highspeed','M2.7 极速版',     '极速推理', 200, false],
              ['MiniMax-M2.5-highspeed','M2.5 极速版',     '极速均衡', 200, false],
            ],
            baseUrl:'https://api.minimaxi.com/anthropic', showBaseUrl:false, optKey:false },

          { id:'Xiaomi', label:'小米 MiMo', kind:'AnthropicCompatible',
            keyHint:'your-api-key',
            models:[
              ['MiMo-V2-Pro',  'MiMo-V2-Pro',  '推荐 · 超长上下文（1M）', 1000, true],
              ['MiMo-V2-Omni', 'MiMo-V2-Omni', '全模态 · 文本/图像/视频', 262, false],
            ],
            baseUrl:'https://token-plan-cn.xiaomimimo.com/anthropic', showBaseUrl:false, optKey:false },
        ];

        // ── State ───────────────────────────────────────────────────────────────
        var st = { mode:'api', pvd:null, model:null, customModel:'', apiKey:'', baseUrl:'', advOpen:false };

        function currentProviders(){ return st.mode === 'coding-plan' ? PROVIDERS_CP : PROVIDERS_API; }

        // ── Boot ────────────────────────────────────────────────────────────────
        (function init(){ renderProviderTabs(); })();

        // ── Mode selection ──────────────────────────────────────────────────────
        function selectMode(m){
          st.mode = m; st.pvd = null; st.model = null; st.customModel = ''; st.apiKey = ''; st.baseUrl = '';
          document.querySelectorAll('.mode-tab').forEach(function(b){
            b.classList.toggle('active', b.dataset.mode === m);
          });
          document.getElementById('cp-info').classList.toggle('visible', m === 'coding-plan');
          renderProviderTabs();
          document.getElementById('model-list').innerHTML = '';
          show('field-apikey', false); show('field-custom-model', false);
          show('field-baseurl-required', false); show('field-advanced', false);
          validate();
        }

        function renderProviderTabs(){
          var tabs = document.getElementById('provider-tabs');
          tabs.innerHTML = '';
          currentProviders().forEach(function(p){
            var btn = document.createElement('button');
            btn.className = 'pvd-tab';
            btn.textContent = p.label;
            btn.dataset.id = p.id;
            btn.onclick = function(){ selectProvider(p.id); };
            tabs.appendChild(btn);
          });
        }

        // ── Provider selection ──────────────────────────────────────────────────
        function selectProvider(pid){
          st.pvd = pid; st.model = null; st.customModel = ''; st.apiKey = ''; st.baseUrl = '';
          document.querySelectorAll('.pvd-tab').forEach(function(b){
            b.classList.toggle('active', b.dataset.id === pid);
          });
          var pvd = currentProviders().find(function(p){ return p.id === pid; });
          renderModels(pvd);
          renderFields(pvd);
          validate();
        }

        function renderModels(pvd){
          var list = document.getElementById('model-list');
          list.innerHTML = '';
          if(!pvd || pvd.models.length === 0) return;
          pvd.models.forEach(function(m){
            var id=m[0], name=m[1], desc=m[2], ctx=m[3], rec=m[4];
            var row = document.createElement('label');
            row.className = 'model-row';
            row.innerHTML =
              '<input type="radio" name="model" value="'+id+'">' +
              '<div class="model-info">' +
                '<div class="model-name">'+name+(rec?'<span class="rec-badge">推荐</span>':'')+'</div>' +
                '<div class="model-desc">'+desc+'</div>' +
              '</div>' +
              (ctx >= 100 ? '<span class="model-ctx">'+ctx+'K</span>' : '');
            row.querySelector('input').onchange = function(){
              document.querySelectorAll('.model-row').forEach(function(r){ r.classList.remove('selected'); });
              row.classList.add('selected');
              st.model = id;
              validate();
            };
            if(rec && !st.model){
              st.model = id;
              row.classList.add('selected');
              row.querySelector('input').checked = true;
            }
            list.appendChild(row);
          });
        }

        function renderFields(pvd){
          if(!pvd) return;
          var isOllama = pvd.id === 'Ollama';
          show('field-custom-model', isOllama);
          if(isOllama) document.getElementById('custom-model-id').value = '';

          show('field-apikey', true);
          document.getElementById('apikey-input').placeholder = pvd.keyHint;
          document.getElementById('apikey-input').value = '';
          document.getElementById('key-hint').textContent =
            pvd.optKey ? '本地模型无需 API Key，可留空' : '连接测试仅发送 1 token，费用 < $0.0001';
          document.getElementById('key-req-mark').style.display = pvd.optKey ? 'none' : '';

          var showReqUrl = pvd.showBaseUrl;
          show('field-baseurl-required', showReqUrl);
          if(showReqUrl){
            var inp = document.getElementById('baseurl-req-input');
            inp.value = pvd.baseUrl || '';
            inp.placeholder = pvd.baseUrl || 'http://…/v1';
            st.baseUrl = pvd.baseUrl || '';
          } else {
            st.baseUrl = '';
          }

          show('field-advanced', !pvd.showBaseUrl);
          document.getElementById('baseurl-adv-input').placeholder =
            pvd.baseUrl ? pvd.baseUrl + '（默认）' : 'https://…/v1（可选）';
          document.getElementById('baseurl-adv-input').value = '';
          if(st.advOpen){ closeAdvanced(); }
        }

        // ── Field change handlers ───────────────────────────────────────────────
        function onFieldChange(){
          st.apiKey      = document.getElementById('apikey-input').value.trim();
          st.customModel = document.getElementById('custom-model-id').value.trim();
          var reqUrl = document.getElementById('baseurl-req-input');
          var advUrl = document.getElementById('baseurl-adv-input');
          st.baseUrl = advUrl.value.trim() || reqUrl.value.trim() || '';
          validate();
        }

        function toggleAdvanced(){
          st.advOpen = !st.advOpen;
          document.getElementById('adv-body').style.display = st.advOpen ? '' : 'none';
          document.getElementById('adv-label').textContent =
            st.advOpen ? '▲ 收起' : '▼ 高级选项（代理 / 自定义 Base URL）';
        }
        function closeAdvanced(){
          st.advOpen = false;
          document.getElementById('adv-body').style.display = 'none';
          document.getElementById('adv-label').textContent = '▼ 高级选项（代理 / 自定义 Base URL）';
        }

        // ── Validation ──────────────────────────────────────────────────────────
        function validate(){
          var pvd = currentProviders().find(function(p){ return p.id === st.pvd; });
          if(!pvd){ setSubmit(false); return; }
          var modelOk = pvd.models.length === 0 ? !!st.customModel : !!st.model;
          var keyOk   = pvd.optKey || !!st.apiKey;
          setSubmit(modelOk && keyOk);
        }
        function setSubmit(ok){
          document.getElementById('submit-btn').disabled = !ok;
        }

        // ── Submit ──────────────────────────────────────────────────────────────
        async function doSetup(){
          var pvd = currentProviders().find(function(p){ return p.id === st.pvd; });
          if(!pvd) return;

          var modelId     = pvd.models.length === 0 ? st.customModel : st.model;
          var displayName = (pvd.models.find(function(m){ return m[0] === modelId; }) || [])[1] || modelId;
          var baseUrl     = (document.getElementById('baseurl-adv-input').value.trim() ||
                             document.getElementById('baseurl-req-input').value.trim() ||
                             pvd.baseUrl || null);

          var btn = document.getElementById('submit-btn');
          var msg = document.getElementById('msg');
          btn.disabled = true;
          btn.innerHTML = '<span class="spin"></span> 保存中…';
          msg.className = 'msg'; msg.textContent = '';

          try {
            var r = await fetch('/setup/complete', {
              method:'POST',
              headers:{'Content-Type':'application/json'},
              body: JSON.stringify({
                provider:    pvd.kind,
                modelId:     modelId,
                apiKey:      st.apiKey || null,
                baseUrl:     baseUrl,
                displayName: displayName,
              })
            });
            var d = await r.json().catch(function(){ return {}; });
            if(r.ok){
              msg.className = 'msg ok';
              msg.textContent = d.message || '设置完成！正在跳转…';
              setTimeout(function(){ window.location.replace('/'); }, 800);
            } else {
              msg.className = 'msg err';
              msg.textContent = d.message || ('服务器错误 ' + r.status);
              btn.disabled = false;
              btn.innerHTML = '完成设置 →';
            }
          } catch(e) {
            msg.className = 'msg err';
            msg.textContent = '网络错误：' + e.message;
            btn.disabled = false;
            btn.innerHTML = '完成设置 →';
          }
        }

        function doSkip(){
          window.location.replace('/');
        }

        function show(id, visible){
          document.getElementById(id).style.display = visible ? '' : 'none';
        }
        </script>
        </body>
        </html>
        """;
}
