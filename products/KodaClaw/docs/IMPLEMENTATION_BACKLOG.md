# KodaClaw 实施 Backlog

这份 backlog 按模块拆解，为后续逐步实现提供任务地图。这里不追求一次性列完所有技术细节，而是给出足够清晰的开发切入口。

## KC-DOCKER 专项 — Docker 化与热更配置系统（KC-DOCKER-001~006）

> FREEZE doc: `docs/ITERATION_KC_DOCKER_FREEZE.md`（2026-04-10）
> 类型：大改（跨多迭代）
> 受影响模块：`KodaClaw.Gateway`（主）、`KodaClaw.Runtime`、`apps/kodaclaw-web`、新增 Dockerfile / docker-compose
>
> **注**：读代码后更新（2026-04-10）。`ISecretStore` / `PlatformSecretStore` / `LinuxSecretStore`（file fallback）已完整实现，无需重建。
> `GatewayConfigurationBootstrap` 已有 `ReloadOnChange = true`。Model 配置为 `config/models/*.json`（每 endpoint 一个文件），不是单一 `models.json`。
> 核心 gap：ENV 只读取、不持久化，容器重启后 API key 丢失。

### KC-DOCKER-001 — 配置写入共享层 + ENV 路径（ConfigBootstrapWriter / ConfigBootstrapService）

**背景**：两条 onboarding 路径（ENV 自动持久化 / Setup Wizard 浏览器引导）共用同一写入逻辑，提取为 `ConfigBootstrapWriter` 防止重复。Docker 用户首次部署推荐通过 Setup Wizard 配置，无需设置 ENV key。

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-DOCKER-0101 | `KodaClaw.Gateway/Bootstrap` | 新增 `ConfigBootstrapWriter`（普通 class，供注入）：构造注入 `ISecretStore`、`IModelRegistryRepository`；公开方法 `WriteIfAbsentAsync(string? anthropicKey, string? openaiKey, CancellationToken)`：① `existing = await _registry.ListAsync()`；② 若 `anthropicKey` 非空且 `existing` 中无 `ApiKeySecretRef == "keychain:config-bootstrap:anthropic"` → `ISecretStore.UpsertAsync(...)` + `_registry.AddAsync(new ModelEndpoint{ Id="anthropic-default", DisplayName="Anthropic (Bootstrap)", Provider=Anthropic, ModelId="claude-sonnet-4-20250514", ApiKeySecretRef="keychain:config-bootstrap:anthropic", Enabled=true, Capabilities=Text, IsDefault=!existing.Any(m=>m.IsDefault), CreatedAt=now, UpdatedAt=now })`；③ `openaiKey` 同理写 `Id="openai-default"`；④ 已存在 → 跳过；key 为空 → 跳过，不报错；写入由 `JsonModelRegistryRepository`（`JsonStoreBase`）完成，格式自动保证。新增 `ConfigBootstrapService`（`IHostedService`）：仅读 `KODACLAW_ANTHROPIC_API_KEY` / `KODACLAW_OPENAI_API_KEY` ENV，然后调 `_writer.WriteIfAbsentAsync(envAnthropic, envOpenAI)` | L2 集成测试：有 ENV 无 endpoint → 首次启动后 `_registry.ListAsync()` 含正确 endpoint + `.secrets` 含实际 key；有 endpoint → 不覆盖；仅 OPENAI key → `IsDefault=true`；无 ENV → 不报错不生成 endpoint；直接调 `writer.WriteIfAbsentAsync("key", null)` → 同等结果（Setup Wizard 路径验证） | Pending |
| KC-DOCKER-0102 | `KodaClaw.Gateway/Composition` | `ConfigBootstrapWriter` 注册为 `Singleton`；`ConfigBootstrapService` 注册为 `IHostedService`，在 `ModelRegistrySeedService` 之前执行 | `dotnet build` 0 错 0 警告 | Pending |

### KC-DOCKER-002 — Gateway Docker 适配

**背景**：Gateway 绑定地址需确认实际读取 `KODACLAW_GATEWAY_URL` ENV；缺标准 `/healthz` 端点；`CORS_ALLOWED_ORIGINS=*` Docker 下已可用，需验证。

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-DOCKER-0201 | `KodaClaw.Gateway` | 确认 `KODACLAW_GATEWAY_URL=http://0.0.0.0:5076` 生效（ASP.NET Core `ASPNETCORE_URLS` 或 `UseUrls` 路径），若未接入则补充读取逻辑；`KODACLAW_CORS_ALLOWED_ORIGINS=*` 在 Docker 下正确放行所有 origin（验证 `IsAllowedCorsOrigin` 逻辑） | L2 集成测试：Gateway 在 `0.0.0.0:5076` 启动，跨 origin 请求收到正确 CORS header | Pending |
| KC-DOCKER-0202 | `KodaClaw.Gateway` | 新增 `GET /healthz` 端点（返回 `200 { "status": "ok" }`，无需鉴权）；供 Docker `HEALTHCHECK` 指令使用；在 `MapGatewayEndpoints` / `MapSystemEndpoints` 中追加 | L2 集成测试：`GET /healthz` 返回 200，无需 Authorization header | Pending |
| KC-DOCKER-0203 | `KodaClaw.Gateway` | Gateway 内嵌静态文件 serving：`app.UseStaticFiles()` + `wwwroot/` 目录（`apps/kodaclaw-web/dist/`，Dockerfile 阶段 COPY）；SPA fallback：非 `/api/` 路径返回 `index.html`（`MapFallbackToFile`）；`index.html` config 注入：**在 `ConfigBootstrapService.StartAsync` 末尾**执行，检查 `wwwroot/index.html` 是否存在，若存在则在 `</head>` 前插入 `<script>window.__KODACLAW_CONFIG__={"gatewayUrl":"/","dockerMode":<bool>};</script>` 后原地写回（`dockerMode` 读 `KODACLAW_DOCKER_MODE` ENV）；本地开发无 `wwwroot/index.html`，跳过；**幂等性**：写入前检查文件内容是否已含 `window.__KODACLAW_CONFIG__`，若已存在则跳过（容器 stop/start 不重复注入）；预写入后 `UseStaticFiles` 和 `MapFallbackToFile` 对所有路径（含深路径 `/channels`、`/settings`）均返回已注入文件，避免 SPA 深路径硬刷新时 `window.__KODACLAW_CONFIG__` 缺失 | L2 集成测试：`GET /` 返回 200 + body 含 `window.__KODACLAW_CONFIG__`；`GET /channels`（MapFallbackToFile）也含注入脚本；注入逻辑执行两次后文件仅含一处 `window.__KODACLAW_CONFIG__`（幂等）；`GET /api/system/health` 仍正常 | Pending |

### KC-DOCKER-003 — `config_update` Agent 工具

**背景**：Docker 用户通过 Channel 交互，需要 Agent 能修改模型配置（加 key、换模型）。写入通过 `IModelRegistryRepository` 完成（与 Settings UI 共用同一存储层）。`RegistryAwareModelProvider` 每次 LLM 调用时重新查询 registry，因此**写入即生效，当前 session 下一次 LLM 调用立即使用新配置**，无需重启容器或开新 session。

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-DOCKER-0301 | `KodaClaw.Runtime` | 新增 `ConfigUpdateTool`：构造注入 `ISecretStore`、`IModelRegistryRepository`。参数：`operation`（`"upsert"` \| `"remove"`）、`endpointId`（upsert 时若未提供则自动生成：`{provider}-{modelId}` 全小写、非字母数字替换为 `-`、连续 `-` 合并、首尾去除、最长 64 字符；如 `OpenAI`+`gpt-4o` → `openai-gpt-4o`）、`provider`（`"Anthropic"` \| `"OpenAI"` \| `"OpenAICompatible"` \| `"AnthropicCompatible"`）、`modelId`（string）、`apiKey`（可选；若提供则 `ISecretStore.UpsertAsync` 存入 `.secrets`，endpoint 写 `ApiKeySecretRef: "keychain:config-bootstrap:{endpointId}"` 而非明文）、`baseUrl`（可选）、`isDefault`（bool，可选，默认 false）。**upsert**：`existing = await _registry.GetByIdAsync(endpointId)`；若 null → `_registry.AddAsync(new ModelEndpoint{ ... })`；若非 null → `_registry.UpdateAsync(existing with { ... })`；若 `isDefault==true` → `_registry.SetDefaultAsync(endpointId)`（原子联动，无需逐文件扫描）。**remove**：`existing = await _registry.GetByIdAsync(endpointId)`；若 null → 返回错误"endpoint 不存在"；若 `existing.IsDefault && (await _registry.ListAsync()).Count(m=>m.IsDefault)==1` → 返回错误"请先将其他模型设为默认，再删除此项"；→ `_registry.DeleteAsync(endpointId)` | L1 单元测试：upsert 新建 → `AddAsync` 被调；upsert 已有 → `UpdateAsync` 被调；isDefault=true → `SetDefaultAsync` 被调；apiKey 提供 → `SecretStore.UpsertAsync` 被调、endpoint 含 `ApiKeySecretRef` 不含明文；remove 不存在 → 错误；remove 唯一 default → 错误；remove 正常 → `DeleteAsync` 被调 | Pending |
| KC-DOCKER-0302 | `KodaClaw.Runtime` | `ConfigUpdateTool` 遵循 CLAUDE.md checklist：① `ToolRegistry` 注册（`ServiceCollectionExtensions.cs`）；② 加入 `MainSessionOptions.DefaultTools`；③ 加入 `koda-workspace` skill 的 `allowed-tools`；工具 description 说明写入后当前 session 下次 LLM 调用即生效，gateway/secrets 修改引导至 Settings 页面 | `dotnet build` 0 错；L2 集成测试：Agent 调用 `config_update` → `_registry.ListAsync()` 返回新 endpoint → 下次 LLM 请求 `RegistryAwareModelProvider` 选中新 endpoint | Pending |

### KC-DOCKER-004 — Docker 构建文件

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-DOCKER-0401 | 根目录 | 多阶段 `Dockerfile`：Stage 1（`node:22-alpine`）WORKDIR `/build`，COPY `apps/kodaclaw-web/package*.json`，`npm ci`，COPY `apps/kodaclaw-web/`，`npm run build` → `/build/dist`；Stage 2（`mcr.microsoft.com/dotnet/sdk:10.0`）COPY 全仓库，`dotnet publish src/KodaClaw.Gateway/KodaClaw.Gateway.csproj -c Release -o /app/publish`；Stage 3（`mcr.microsoft.com/dotnet/aspnet:10.0`）：① `apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*`；② COPY publish + wwwroot（到 `/app`）；③ `RUN mkdir -p /data && adduser --uid 1000 --disabled-password app && chown -R app /data /app`（**同时 chown `/app`**，确保非 root 用户可写 `wwwroot/index.html`）；④ `VOLUME ["/data"]`（必须在 chown 之后）；⑤ `USER app`；`HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD curl -f http://localhost:5076/healthz || exit 1`；`EXPOSE 5076` | `docker build -t kodaclaw:latest .` 成功；`docker run --rm kodaclaw:latest id` 显示非 root 用户 | Pending |
| KC-DOCKER-0402 | 根目录 | `docker-compose.yml`：service `kodaclaw`，`image: kodaclaw:latest`，`ports: ["5076:5076"]`，`volumes: [kodaclaw-data:/data]`；`environment` 块必填项：`KODACLAW_GATEWAY_URL=http://0.0.0.0:5076`、`KODACLAW_WORKSPACE_ROOT=/data`、`KODACLAW_CORS_ALLOWED_ORIGINS=*`、`KODACLAW_DOCKER_MODE=true`；可选项（注释）：`# KODACLAW_ANTHROPIC_API_KEY: sk-ant-...  # 可选：CI/自动化场景使用；普通用户通过浏览器 /setup 配置`、`# KODACLAW_OPENAI_API_KEY: sk-...`；named volume `kodaclaw-data` | `docker-compose up -d && curl http://localhost:5076/healthz` 返回 200 | Pending |
| KC-DOCKER-0403 | 根目录 | `.env.example`：标注哪些必填（GATEWAY_URL / WORKSPACE_ROOT / CORS / DOCKER_MODE）、哪些可选（API keys，注明"推荐通过浏览器 /setup 配置"）；`Makefile` 新增 `docker-build` / `docker-run` / `docker-up` 三个 target；`install.sh`（Linux/macOS）：检测 Docker → 下载 `docker-compose.yml` → `docker-compose up -d` → 打开浏览器（`http://localhost:5076`）→ 提示"请在浏览器完成初始配置"（**不提示输入 API key**）；`install.ps1`（Windows PowerShell）：同等逻辑，约 50 行，无需管理员权限 | `bash install.sh` / `powershell install.ps1` 一键启动后浏览器自动跳转 `/setup` | Pending |

### KC-DOCKER-005 — Web UI Docker 模式适配 + 测试补齐

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-DOCKER-0501 | `apps/kodaclaw-web` | `apps/kodaclaw-web/src/lib/config.ts` 修改 `initializeRuntimeConfig()`：在读取 Electron bridge 之前先检查 `window.__KODACLAW_CONFIG__`（若存在则以其 `gatewayUrl`/`dockerMode` 覆盖 `fallbackRuntimeConfig`）；新增导出函数 `isDockerMode(): boolean`；`DesktopRuntimeConfig` 类型新增可选 `dockerMode?: boolean` 字段；`window.__KODACLAW_CONFIG__` 类型声明补充到 `global.d.ts`（避免 typecheck 报错）；`api.ts` 无需改动（已使用 `resolveGatewayPath` → `getGatewayUrl()`，`gatewayUrl` 为 `""` 时自动生成相对路径，同域无 CORS） | `npm run typecheck`；L5 Dogfood：浏览器访问 `http://server:5076`，Network DevTools 确认 API 请求到同域相对路径 | Pending |
| KC-DOCKER-0502 | `apps/kodaclaw-web` | `isDockerMode()` 返回 `true` 时 `Sidebar.tsx` 调整：Chat desk 入口移至底部并加提示文字"通过 Channel 与我交流"，Channels / Automations / Settings 入口置顶；`Sidebar.tsx` 通过调用 `isDockerMode()`（`config.ts` 导出）读取状态，不直接访问 `window.__KODACLAW_CONFIG__` | `npm run typecheck`；L5 Dogfood | Pending |
| KC-DOCKER-0503 | 测试层 | L2 集成测试（≥11 个新测试）：① `ConfigBootstrapWriter` 幂等性：有 key + 无 endpoint → 生成 endpoint + `.secrets`；重复调用 → 不覆盖；无 key → 不生成；② `ConfigBootstrapService`（ENV 路径）：读 ENV 后委托 `writer`，结果一致；③ `/healthz` 无鉴权返回 200；④ Setup Wizard：未配置 `GET /` → 302 `/setup`；`POST /setup/complete` 含 key → registry 非空；`POST /setup/complete` 成功后再 `GET /setup` → 302 `/`；⑤ **零 ENV 守卫**：两个 ENV 均未设置 → `ModelRegistrySeedService` 不写入任何 endpoint → registry 维持空 → `GET /` 仍然 302 `/setup`；⑥ `config_update` 白名单拒绝；⑦ `config_update` remove 唯一 default → 返回错误；⑧ CORS `*` 放行跨 origin 请求 | `dotnet test KodaClaw.sln -m:1` 全绿 | Pending |

### KC-DOCKER-006 — Setup Wizard（**主要 onboarding 路径**）

**背景**：普通用户无需配置任何 ENV，`docker-compose up` 后浏览器访问自动引导完成配置。`POST /setup/complete` 调用 `ConfigBootstrapWriter.WriteIfAbsentAsync`（KC-DOCKER-001 提取的共享类），与 ENV 路径完全一致，幂等安全。

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-DOCKER-0601 | `KodaClaw.Gateway` | 检测逻辑：`await _registry.ListAsync()` 返回空列表 → 视为"未配置"；新增中间件（在静态文件 / SPA fallback 之前）：未配置状态下，非 `/api/*`、非 `/healthz`、非 `/setup` 的**所有**路径（含 `GET /`、`GET /index.html`、所有 SPA 深路径）均 302 到 `/setup`；已配置状态下，`GET /setup` 302 到 `/`；新增 `GET /setup`（无鉴权，返回内联 HTML 表单页，不依赖 React SPA）：含两个输入框（Anthropic API Key / OpenAI API Key，至少填一个）+ 提交按钮；新增 `POST /setup/complete`（无鉴权）：接收 `{ anthropicKey?, openaiKey? }` → 注入 `ConfigBootstrapWriter`，调 `WriteIfAbsentAsync(anthropicKey, openaiKey)` → 返回 `{ ok: true }` | L2 集成测试：未配置 `GET /` → 302 `/setup`；`GET /channels` → 302 `/setup`；`GET /api/system/health` 不触发重定向（API 路径豁免）；`GET /healthz` 不触发重定向；`POST /setup/complete` 含 key → `_registry.ListAsync()` 非空 → 再访问 `/setup` → 302 `/`；已配置 `GET /setup` → 302 `/` | Pending |
| KC-DOCKER-0602 | `KodaClaw.Gateway` | Setup Wizard HTML 页面：内联 CSS（极简，无外部依赖）+ 前端 JS 校验（至少一个 key 非空才允许提交）+ `fetch POST /setup/complete` + 成功后 `window.location = '/'`；错误时行内展示错误信息；英文界面 | L5 Dogfood：零 ENV 启动 → 浏览器填写 key → 正常进入主界面 → 重启容器后无需再次配置 | Pending |

---

### KC-DOCKER-007 — 容器环境状态持久化（HOME/XDG 路径收敛）

**背景**：Agent 工具生态（`web-tools`、社区插件等）在运行时会写入 `~/.config/`、`~/.local/bin/`、`~/.kodaclaw-community/` 等路径，容器重启后因文件系统重置而丢失，用户需反复重新安装工具或重新认证。根因是只有 workspace 协议文件被 volume 保护，工具环境状态没有持久化。

**解决方案**：将 `HOME` 和 XDG 路径重定向至已挂载的 `/data` volume，与 `KODACLAW_WORKSPACE_ROOT` 在同一 volume 下实现路径分层，单 volume 覆盖全部状态。

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-DOCKER-0701 | `Dockerfile` | runtime stage 新增环境变量：`HOME=/data/home`、`XDG_CONFIG_HOME=/data/home/.config`、`XDG_CACHE_HOME=/data/home/.cache`、`XDG_DATA_HOME=/data/home/.local/share`、`PATH=/data/home/.local/bin:$PATH`、`ASPNETCORE_DataProtection__KeysDirectory=/data/home/.aspnet/DataProtection-Keys`；git 提交身份（ENV 变量优先级高于 `.gitconfig`，解决 HOME 指向 volume 时 git commit 无 author 的问题）：`GIT_AUTHOR_NAME=KodaClaw Agent`、`GIT_AUTHOR_EMAIL=agent@kodaclaw.local`、`GIT_COMMITTER_NAME=KodaClaw Agent`、`GIT_COMMITTER_EMAIL=agent@kodaclaw.local`；目录初始化：`mkdir -p /data/workspace /data/home/.local/bin`；chown 覆盖整个 `/data`（含新子目录） | `docker build` 成功；`docker run --rm kodaclaw env \| grep HOME` 返回 `/data/home`；`docker run --rm kodaclaw env \| grep GIT_AUTHOR_NAME` 返回 `KodaClaw Agent`；容器内 `git commit` 不报 "Author identity unknown" | Pending |
| KC-DOCKER-0702 | `docker-compose.yml` | `KODACLAW_WORKSPACE_ROOT` 从 `/data` 改为 `/data/workspace`；volume 挂载点保持 `/data`（单 volume 覆盖 `home/` + `workspace/`）；同步更新注释说明 volume 布局；`SEARXNG_URL` 引用容器内部地址（已完成，无需变更） | `docker-compose up -d`；`docker exec kodaclaw ls /data/workspace` 显示 workspace 文件；`docker exec kodaclaw ls /data/home` 显示 `.config` 等隐藏目录 | Pending |
| KC-DOCKER-0703 | `web-tools/SKILL.md` | 安装步骤中 Linux 目标路径改为 `/data/home/.local/bin/web-tools`（与 `PATH` 一致）；验证命令改为 `which web-tools`（依赖 PATH 推导，无需硬编码路径） | L5 Dogfood：容器内 Agent 安装 web-tools 后重启容器，`which web-tools` 仍返回正确路径，无需重新安装 | Pending |

**依赖**：KC-DOCKER-004（Dockerfile / compose 基础结构已建立）

---

### KC-DOCKER-008 — Docker 持久化感知

**背景**：KC-DOCKER-007 通过 `HOME=/data/home` 覆盖了 `~/` 下的路径，但 Agent 仍可能将产出写到 `/tmp/`、`/app/`（WORKDIR 相对路径）等非 volume 位置，重启后丢失。通过 system prompt 注入（主动感知）+ `fs_write` 软警告（被动兜底）双层保障。

**依赖**：KC-DOCKER-007（`HOME=/data/home` 已生效，`KODACLAW_DOCKER_MODE` ENV 已设置）

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-DOCKER-0801 | `KodaClaw.Runtime` | Docker 模式下三类 session（main / channel / automation）的 system prompt 包含持久化说明段落，Agent 主动将产出写到 `~/projects/` 或 `workspace/outputs/`，不使用 `/tmp/` 或相对路径。检测方式：`Environment.GetEnvironmentVariable("KODACLAW_DOCKER_MODE") == "true"`。注入位置：`MainSessionService.BuildSystemPromptAsync()` 在现有 workspace readiness 注入之后加 `builder.AddBody(...)` — 与现有模式完全一致；`ChannelSessionService` 和 `AutomationSessionService` 同步添加相同检测与注入逻辑。注入内容：持久化路径说明 + 禁止写 `/tmp/`/`/app/` 的规则，控制在 5 行以内避免 context 浪费。 | L1 单元测试：`KODACLAW_DOCKER_MODE=true` 时三类 session 的 `BuildSystemPromptAsync` 返回的 prompt 包含 `"Docker"` 和 `"/data/"` 关键词；`KODACLAW_DOCKER_MODE` 未设置时 prompt 不含该段落。L5 Dogfood：Docker 模式下 Chat session 中要求 Agent 写一个文件，观察 Agent 选择路径 | Pending |
| KC-DOCKER-0802 | `KodaClaw.Runtime` | Agent 调用 `fs_write` 写入 `/data/` 以外路径时，tool result 追加 `persistenceWarning` 字段，Agent 可自主决定是否重写到持久化路径；非 Docker 模式下行为与原版 `FsWriteTool` 完全一致。实现：新建 `KodaClaw.Runtime/Tools/PersistenceAwareFsWriteTool.cs`，复制 `FsWriteTool` 执行逻辑（`context.Sandbox.WriteFileAsync` + `ToolContextFilePool`），构造函数注入 `bool isDockerMode`；路径判断：`Path.GetFullPath(args.Path)` 后检查是否以 `/data/` 开头（相对路径解析后必在 `/app/` 下，一定触发警告）；写入成功后如需追加警告则返回含 `persistenceWarning` 字段的匿名对象，不阻断写入；在 `ServiceCollectionExtensions.cs` 中覆盖注册 `fs_write` 替换 SDK 内置版本。 | L1 单元测试：dockerMode=true + 路径 `/tmp/x.txt` → result 含 `persistenceWarning`；dockerMode=true + 路径 `/data/home/x.txt` → result 不含 warning；dockerMode=false + 任意路径 → result 不含 warning。L2 集成测试：Agent 通过 `fs_write` 写 `/tmp/test.txt`，tool result 中有 warning 字段 | Pending |

---

## KC-CMD 专项 — Channel 命令系统重构与扩展（KC-CMD-01~04）

> FREEZE doc: `docs/ITERATION_KC_CMD_FREEZE.md`（2026-04-10）
> 类型：P0 优化/重构 + P1-P3 新功能
> 受影响模块：`KodaClaw.ChannelHub`（主）、`KodaClaw.Runtime`（轻触）

### Wave 1 — KC-CMD-01：P0 纯重构（命令系统架构化）

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-CMD-0101 | `KodaClaw.ChannelHub` | 新建 `Commands/` 目录，共 4 个文件：`ParsedChannelCommand.cs`（解析结果 record + `ChannelControlCommandKind` + `ChannelDirectiveKind` 枚举）、`ChannelCommandRegistry.cs`（静态命令定义表，含 Key/Aliases/Description/Category）、`ChannelCommandParser.cs`（`Parse(string?) → ParsedChannelCommand` 纯函数，从注册表构建 alias 映射）、`ChannelCommandDispatcher.cs`（Wave 1 骨架，完整实现在 KC-CMD-0102）；`ChannelTurnContext.cs` 推迟至 Wave 2 创建 | `dotnet build` 0 错 0 警告 | Pending |
| KC-CMD-0102 | `KodaClaw.ChannelHub` | 新建 `Commands/ChannelCommandDispatcher.cs`：构造注入 `IChannelSessionService`、`ChannelDeliveryDispatchService`、`IDiagnosticsService?`；`DispatchAsync` 内部 switch on `ControlKind`，处理 `SessionReset`/`Status`/`Stop` 三个已有命令；`ServiceCollectionExtensions` 注册 Dispatcher | `dotnet build` 0 错 0 警告 | Pending |
| KC-CMD-0103 | `KodaClaw.ChannelHub` | `ChannelTurnOrchestrator.ProcessInboundAsync` 重构：原 if-else 链替换为 `ChannelCommandParser.Parse(envelope.Text)` + `if ControlKind → _commandDispatcher.DispatchAsync(...)` 两行；现有 3 个命令行为完全不变 | `dotnet build` 0 错；`dotnet test KodaClaw.sln -m:1 --filter "ChannelHub"` 全绿 | Pending |
| KC-CMD-0104 | `KodaClaw.UnitTests` | `SessionResetCommandTests.cs` 迁移：改为调 `ChannelCommandParser.Parse()`，保持相同 case 覆盖；**同时删除 `ChannelTurnOrchestrator` 中的 `IsSessionResetCommand` 和 `SessionResetCommands` 两个 internal 静态成员**（避免新旧逻辑并存）；新增 `ChannelCommandParserTests.cs`（≥12 个 case：各命令 alias、大小写、边界、前缀不触发）；新增 `ChannelCommandRegistryTests.cs`（注册表完整性、无重复 alias） | `dotnet test KodaClaw.sln -m:1` 全绿 | Pending |

### Wave 2 — KC-CMD-02：P1 新命令（/help、/think）

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-CMD-0201 | `Kode.Agent.Sdk` | **SDK 前置条件 A**：新增 `AgentRunOptions` record（`EnableThinking?`, `ThinkingBudget?`）；`IAgent` + `Agent` 新增 `RunAsync(string, AgentRunOptions?, CancellationToken)` 重载；**实现**：`Agent` 新增 `_currentRunOptions` 私有字段，在 `RunMultimodalAsync` 开始赋值、finally 清空（session 级 SemaphoreSlim 已保证 RunAsync 不并发，无竞态）；`BuildModelRequest()` 合并：`EnableThinking = _currentRunOptions?.EnableThinking ?? _config.EnableThinking` | `dotnet build` SDK 0 错；现有 SDK 测试全绿 | Pending |
| KC-CMD-0202 | `KodaClaw.Runtime` | **SDK 前置条件 B**：`IChannelSessionService.RunInboundTurnAsync` 增加可选参数 `AgentRunOptions? runOptions = null`（向后兼容）；`ChannelSessionService` 实现透传给 `handle.Agent.RunAsync(prompt, runOptions, ct)` | `dotnet build` 0 错 | Pending |
| KC-CMD-0203 | `KodaClaw.ChannelHub` | 新建 `ChannelTurnContext.cs`（per-turn 参数覆盖 record，含 `PromptPrefix?`、`bool? EnableThinking`（**nullable**）、`int? ThinkingBudget` + `FromDirectives()` 工厂方法）；`ChannelCommandRegistry` 追加 `Help`（aliases: `/help /commands /?`）；`ChannelControlCommandKind` 枚举扩展 `Help`；`ChannelCommandRegistry.FormatHelpText()` 按分组格式化 | `dotnet build` 0 错 | Pending |
| KC-CMD-0204 | `KodaClaw.ChannelHub` | `ChannelDirectiveKind` 枚举新增 `Think`；`ChannelCommandParser` 扩展 Directive 前缀剥离（`/think 正文`→`Directives=[Think], CleanedText=正文`；`/think` 单独行→`CleanedText=""`） | `dotnet build` 0 错 | Pending |
| KC-CMD-0205 | `KodaClaw.ChannelHub` | `ChannelCommandDispatcher` 新增 `Help` case；`ChannelTurnOrchestrator` 应用 `ChannelTurnContext`：① `finalText = (ctx.PromptPrefix??"")+parsed.CleanedText`，`envelope with { Text = finalText }`；② ControlKind 非 null → 走 Dispatcher 分支，Directives 不应用；③ Think directive → `EnableThinking=true, ThinkingBudget=8000`；④ 调 `_channelSessionService.RunInboundTurnAsync(..., runOptions: new AgentRunOptions{...}, ct)`（通过接口透传，不直接访问 agent）；**空正文 guard**：Directives 非空且 CleanedText 为空→发使用提示返回 | `dotnet build` 0 错；Dogfood: `/help` 收到列表；`/think 分析` 开启 thinking tokens；`/think` 单独行收到使用提示 | Pending |
| KC-CMD-0206 | `KodaClaw.UnitTests` | `ChannelCommandParserTests`：Think 前缀剥离、单独行 CleanedText 为空、大小写（≥6 case）；新增 `ChannelCommandDispatcherTests.cs`（Help/Stop/Status/Reset 各分支）；新增 `ChannelTurnContextTests.cs`（Think→`EnableThinking=true, ThinkingBudget=8000, PromptPrefix 非空`；空 directives→三者均 null）；`ChannelTurnOrchestratorTests`：Think 单独行→发提示不进 Agent；Think 带正文→`RunInboundTurnAsync` 收到 `runOptions.EnableThinking=true`；ControlKind 优先→Directives 不应用 | `dotnet test KodaClaw.sln -m:1` 全绿；L5 Dogfood | Pending |

### Wave 3 — KC-CMD-03：P2 扩展命令（/compact、/tools、/whoami、/stream、/quiet）

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-CMD-0301 | `Kode.Agent.Sdk` | **SDK 前置条件 C**：`Agent` 类新增 `ForceCompressAsync(CancellationToken) → Task<bool>`（调 `_contextManager.CompressAsync(force:true)`；若 result 非 null：`_messages.Clear()/_messages.AddRange(result.RetainedMessages)`、`RunMessagesChangedAsync`、`SaveStateAsync`、`EmitMonitor(ContextCompressionEvent{Phase="end"})`；返回是否实际压缩） | `dotnet build` SDK 0 错 | Pending |
| KC-CMD-0302 | `KodaClaw.Runtime` | `IChannelSessionService` 新增两个方法：① `GetSessionToolNamesAsync(string sessionId, CancellationToken) → Task<IReadOnlyList<string>>`（直接返回 `_sessionOptions.Tools`，**不**访问 Agent 私有字段；session 不存在返空列表）；② `CompressSessionContextAsync(string sessionId, CancellationToken) → Task<string>`（在 `_sessionLocks[sessionId].WaitAsync` 内执行：检查 `RuntimeState==Working`→拒绝；否则调 `agent.ForceCompressAsync(ct)`；**必须持 sessionLock**，避免与并发 turn 的 TOCTOU 竞态） | `dotnet build` 0 错 | Pending |
| KC-CMD-0303 | `KodaClaw.ChannelHub` | `ChannelCommandRegistry` 追加 `Compact`/`Tools`/`WhoAmI`；`ChannelControlCommandKind` 枚举扩展三个值；`ChannelCommandDispatcher` 注入 `IWorkspaceService`，实现：`Compact`（调 `CompressSessionContextAsync`）、`Tools`（调 `GetSessionToolNamesAsync`）、`WhoAmI`（`File.ReadAllTextAsync(Path.Combine(_workspaceService.RootPath, "workspace", "IDENTITY.md"))`，前 500 字符；文件不存在→返"身份文件未初始化"） | `dotnet build` 0 错 | Pending |
| KC-CMD-0304 | `KodaClaw.ChannelHub` | `ChannelDirectiveKind` 枚举新增 `Stream`、`Quiet`；`ChannelTurnContext` 新增 `bool? EnableProgressStreamingOverride` 字段；`ChannelTurnOrchestrator` 在订阅 EventBus 前应用 override（局部变量覆盖 `_sessionOptions.EnableProgressStreaming`，仅影响本次 turn） | `dotnet build` 0 错；`dotnet test KodaClaw.sln -m:1 --filter "ChannelHub"` 全绿 | Pending |
| KC-CMD-0305 | `KodaClaw.UnitTests` | `ChannelCommandParserTests` 追加 Stream/Quiet case（≥4）；`ChannelCommandDispatcherTests` 追加 Compact（Working 拒绝/idle 成功）/Tools（返工具列表）/WhoAmI（IDENTITY.md 截断 500 字符）；`ChannelTurnContextTests` streaming override case；**`ChannelSessionServiceTests`** 新增：`CompressSessionContextAsync` Working guard（持锁后检查）、idle 成功路径（mock ForceCompressAsync）、`GetSessionToolNamesAsync` session 存在/不存在两个 case | `dotnet test KodaClaw.sln -m:1` 全绿 | Pending |

### Wave 4 — KC-CMD-04：P3 高级命令（/focus、/btw）

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-CMD-0401 | `KodaClaw.ChannelHub` | `ChannelDirectiveKind` 枚举新增 `Focus`；`ChannelCommandParser` 解析 `/focus <topic> 正文`（**topic 为第一个空格前的连续字符串**，之后全部为正文；`/focus topic` 无正文时 CleanedText 为空）；`ChannelTurnContext` 新增 `FocusConstraint` 字段；`ChannelTurnOrchestrator` 将 FocusConstraint 追加到 prompt 尾部（格式：`\n\n[视角约束：请聚焦于 {focus} 的角度回答]`） | `dotnet build` 0 错 | Pending |
| KC-CMD-0402 | `KodaClaw.ChannelHub` | `ChannelControlCommandKind` 枚举新增 `SideQuestion`（alias: `/btw`）；新建 `EphemeralAgentStore`（KodaClaw.ChannelHub 内部类，实现 `IAgentStore`，所有方法 no-op/返空集合，参考 `Kode.Agent.Tools.Orchestration.Internal.InMemoryAgentStore`）；`ChannelCommandDispatcher` 实现 `/btw`：读 IDENTITY.md+SOUL.md（`Path.Combine(_workspaceService.RootPath, "workspace", "IDENTITY.md")`）拼接 system prompt；调 `Agent.CreateAsync(Guid.NewGuid().ToString(), config, new AgentDependencies { Store = new EphemeralAgentStore(), ... }, ct)`（**正确 API，无 AgentRuntime 类**）；`await using` 析构，不写入 session 历史 | `dotnet build` 0 错；L5 Dogfood | Pending |
| KC-CMD-0403 | `KodaClaw.UnitTests` | `ChannelCommandParserTests` 追加 Focus 解析 case（含 topic 边界、无正文情况）；`ChannelCommandDispatcherTests` 追加 SideQuestion ephemeral agent 路径（mock workspace service）验证；`ChannelTurnContextTests` 追加 FocusConstraint 追加位置验证 | `dotnet test KodaClaw.sln -m:1` 全绿 | Pending |

---

## Iter 72 — Channel 实时进度指示器（KC-7201~7205）

> FREEZE doc: `docs/ITERATION_72_FREEZE.md`（2026-04-20）
> 类型：新功能

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-7201 | `KodaClaw.Contracts` | `IChannelConnector` 新增 `SupportsEdit`（默认 false）、`SendWithReceiptAsync`（默认委托老 `SendAsync` 并返回 `ChannelSendReceipt(null, UtcNow)`，向后兼容）、`EditAsync`（默认 throw `NotSupportedException`）；新增 `ChannelSendReceipt(string? ExternalMessageId, DateTimeOffset SentAt)` record（`src/KodaClaw.Contracts/Channels/ChannelSendReceipt.cs`）；所有现有 Connector 无需修改即可编译通过 | `dotnet build KodaClaw.sln` 0 错 0 警告；L3 契约测试：`ChannelSendReceipt` JSON 序列化、接口默认实现行为（默认 `SupportsEdit=false`、`EditAsync` throw `NotSupportedException`） | Completed |
| KC-7202 | `KodaClaw.ChannelHub` | **新** `Turn/ChannelProgressIndicator`：Progress + Monitor 双路 fan-in（`System.Threading.Channels`，退出与 CTS 模式与 Iter 71 一致，`finally` 块 `CancelAsync` + `WhenAll`）；消费 `BreakpointChangedEvent` / `ToolStartEvent` / `ToolEndEvent` / `DoneEvent`；节流规则：min-interval 1.5s + `(State, Tool, StepCount)` 状态去重 + 同状态 10s heartbeat + 单 turn edit ≤10 次；连续 2 次 edit 失败后 `_degraded=true` 停工并产出诊断事件 `channel.progress_indicator.degraded`；AwaitingApproval 编辑为 "⏸ 等待审批 · {tool}"，审批拒绝编辑为 "✗ 已取消"；DoneEvent 终态编辑为 "✓ 完成 · {steps} 步 / {elapsed}"（final reply 另发，不合并） | L1 单元测试（≥8 个）：节流窗口、状态去重、10s heartbeat、每轮上限、degraded 触发与跳过、AwaitingApproval 转场、DoneEvent 终态编辑、失败 receipt 时跳过 edit | Completed |
| KC-7203 | `KodaClaw.ChannelHub` | `ChannelDeliveryDispatchService` 加 `SendProgressInitialAsync`（返回 `ChannelSendReceipt`，best-effort 失败返回 `ChannelSendReceipt(null,...)`）、`EditProgressAsync`（传 externalMessageId + format，best-effort 失败不 throw）；`ChannelTurnOrchestrator` 按 `account.Settings.progressIndicator.enabled && connector.SupportsEdit`（`turnContext.EnableProgressIndicatorOverride` 可覆盖）选路 Indicator vs 现有 Streamer（两者互斥，indicator 启用时 Streamer 不订阅）；DoneEvent 分支：indicator 编辑终态后不干预 `channel_send` / fallback 的最终回复投递，final reply 另发一条 | L2 集成测试：mock connector `SupportsEdit=true` → 验证走 Indicator 路径；`SupportsEdit=false` → 验证回退 Streamer；indicator 路径下 `channel_send` 被调时进度消息被编辑为 ✓ 完成、final reply 另发（sentTexts.Count=1）；`turnContext.EnableProgressIndicatorOverride=false` 即使 settings=on 也走 Streamer | Completed |
| KC-7204 | `KodaClaw.ChannelHub/Connectors` | `TelegramConnector.SupportsEdit=true` + `EditAsync` 调 `HttpTelegramApiClient.EditMessageTextAsync`（新增，走 `editMessageText`，保持 `parseMode` 与 send 一致；message not found / 429 等按失败处理）；`FeishuConnector.SupportsEdit=true` + `EditAsync` 调 `HttpFeishuApiClient.PatchTextMessageAsync`（新增，走 `PATCH /open-apis/im/v1/messages/{id}`，仅文本类型）；`SendWithReceiptAsync` 在 Telegram/Feishu 重写返回真实 `ExternalMessageId`（从底层 API 响应拿 `message_id` / `message_id`）；DingTalk/WeChat/Webhook/Relay 保持默认 `SupportsEdit=false` 不改 | L2 集成测试：Telegram mock API 验证 send→edit→edit→edit(✓) 序列 `message_id` 一致；Feishu mock API 验证 PATCH body 正确；Telegram edit 失败连续 2 次 → indicator 切 degraded；`SendWithReceiptAsync` 返回非空 `ExternalMessageId` | Completed |
| KC-7205 | `apps/kodaclaw-web` | `ChannelsDesk` 账号卡片在 `connectorKind ∈ {Telegram, Feishu}` 时显示"进度指示器"开关（checkbox），默认 false，落库至 `configurationJson.progressIndicator.enabled`；其他连接器不显示；读写走现有 `PUT /api/channels/accounts/{id}`（style 字段未启用，保留未来扩展） | `npm run typecheck`（`ChannelsDesk.tsx` 无新错误，两个预存在错误在 `EndpointDetailPanel.tsx` 与本条目无关） | Completed |
| KC-BUG-7201 | `KodaClaw.ChannelHub` | `ChannelTurnOrchestrator.finally` 在 `RunAsync` 返回后立即 `subscribeCts.Cancel()`，导致 Indicator 未来得及消费 `DoneEvent` 就被订阅中断，终态定格在 Agent 发 Done 之前的 `BreakpointChanged(Ready)` 状态 → 用户看到 "🔄 准备开始 · 第 N 步" 而非 "✓ 完成"。修复：indicator 路径下先 await indicatorTask 3s 宽限期，让它自然消费 DoneEvent 返回；超时或外部 cancel 才兜底 Cancel；streamer 路径保持原行为立即 Cancel | `dotnet build` 0 错 0 警告；`dotnet test --filter "FullyQualifiedName~ChannelProgress"` 10/10 通过；`dotnet test --filter "FullyQualifiedName~Channel\|FullyQualifiedName~Iteration5Direct"` 56/56 通过；L5 Dogfood：Telegram 单 turn 结束后消息停在 "✓ 完成 · N 步 / Xs" | Completed |
| KC-BUG-7202 | `KodaClaw.ChannelHub/Connectors/Feishu` | `HttpFeishuApiClient.PatchTextMessageAsync` 使用 `HttpMethod.Patch` 调飞书消息编辑 API，但飞书官方 `im/v1/message/update` 实际要求 `PUT` 方法（见 https://open.feishu.cn/document/server-docs/im-v1/message/update ），PATCH 请求返回 405/失败，Indicator 连续 2 次失败后进入 `degraded` 停工，飞书消息定格在 "🔄 正在处理..."。修复：将 `HttpMethod.Patch` 改为 `HttpMethod.Put`，同步更新 `IFeishuApiClient` XML doc 和错误信息文本。KC-7204 实施时未查阅官方文档直接推断，为记账教训 | `dotnet build src/KodaClaw.ChannelHub/KodaClaw.ChannelHub.csproj` 0 错 0 警告；`dotnet test --filter "FullyQualifiedName~FeishuConnectorEdit\|FullyQualifiedName~ChannelProgress\|FullyQualifiedName~FeishuApi"` 15/15 通过（既有 mock 在 `PatchTextMessageAsync` 层打桩，不受 HTTP 方法改动影响）；L5 Dogfood：飞书单 turn 结束后消息编辑为 "✓ 完成 · N 步 / Xs" | Completed |
| KC-BUG-7203 | `KodaClaw.ChannelHub/Connectors/Telegram` | Telegram 只支持图片/音频出站，`video/*` attachment 会落到文本分支，丢失视频媒体；飞书已支持视频，形成不对等。修复：参照官方文档 https://core.telegram.org/bots/api#sendvideo 新增 `ITelegramApiClient.SendVideoAsync`（`HttpTelegramApiClient` multipart 实现：chat_id/video/duration(秒)/caption/supports_streaming=true，mp4/mov/webm 扩展识别），`TelegramConnector.SendAsync` 在 audio 分支后追加 `video/*` 分支：`MediaReference.DurationMs` → Telegram 秒级 duration，缺省时 fallback 至 `IMediaStore.GetMetaAsync`；上传异常降级为纯文本并写 `telegram.video_upload_failed` 诊断事件（与飞书对齐）。补 4 个 L2 集成测试（`TelegramConnectorVideoTests`：duration 映射、meta fallback、上传失败文本降级、无 mediaStore 跳过）；同时为 4 个现有 `FakeTelegramApiClient` stub 补 `SendVideoAsync` 实现以保持接口契约 | `dotnet build src/KodaClaw.ChannelHub/KodaClaw.ChannelHub.csproj` 0 错 0 警告；`dotnet test --filter "FullyQualifiedName~TelegramConnectorVideoTests\|FullyQualifiedName~TelegramConnectorAudioTests\|FullyQualifiedName~TelegramConnectorEditTests\|FullyQualifiedName~TelegramConnectorIntegrationTests"` 17/17 通过；`dotnet test --filter "FullyQualifiedName~ChannelHub"` 38/38 通过 | Completed |
| KC-BUG-7204 | `KodaClaw.ChannelHub/Connectors/WeChat` | WeChat 出站仅支持 image/file，`video/*` attachment 会被归类为 file 走 `type=4`，表现为"发了个附件"而非视频卡片，与 Telegram/飞书视频原生能力不对等。参照 vibe-remote Python 参考实现（`wechat.py` / `wechat_cdn.py`）及 `docs/WECHAT_INTEGRATION.md §3.2/§4.2`：iLink 视频需用 `item_list.type=5`，`video_item: { media: {encrypt_query_param, aes_key, encrypt_type:1}, video_size }`；`getuploadurl.media_type=2`；AES key 编码规则与 file/voice 一致（`base64(hex(raw 16B))`），与 image 的 `base64(raw)` 不同。修复：新增 `ILinkVideoItem`（`Media` + `VideoSize`）与 `ILinkMessageItem.VideoItem`；`WeChatConnector` 常量 `ILinkMessageTypeVideo=5` / `ILinkMediaTypeVideo=2`；将 `SendOutboundAsync` 原 isImage 二叉分支改为 `ResolveILinkMediaType(contentType)`：`image/* → Image`、`video/* → Video`、其余 → File；`SendMediaAttachmentAsync` 末段 item 构造改 switch（video 场景 `VideoSize = encrypted.Length`）。语音仍走 file 分支（SILK/AMR 转码超出本次范围） | `dotnet build KodaClaw.sln` 0 错 0 警告；`dotnet test --filter "FullyQualifiedName~WeChatMediaConnectorTests"` 5/5 通过（含新增 `SendAsync_with_video_attachment_should_call_sendmessage_with_video_item`，断言 `Type=5` / `VideoItem.Media` / `VideoSize=32` / `UploadUrl.MediaType=2`） | Completed |

---

## Iter 73 — Main Chat Extended Thinking 开关（KC-7301~7303）

> 类型：新功能（小范围，未走 FREEZE 流程；用户口头拍板 + 一起做了 ChatComposer 优化，作为 KC-W2 类横跨重构的附带项登记）

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-7301 | `Kode.Agent.Sdk` | 新增 `Agent.RunAsync(IReadOnlyList<ContentBlock>, AgentRunOptions?, CancellationToken)` 重载，镜像 `RunAsync(string, AgentRunOptions?, ...)` 的 `_currentRunOptions` try/finally 模板 | `dotnet build` 0 错 0 警告 | Completed |
| KC-7302 | `KodaClaw.Contracts` / `KodaClaw.Runtime` | `ChatStreamRequest` 扩 `EnableThinking?/ThinkingBudget?` 字段；`ChatSessionService.StreamMainSessionAsync` 从 `Subscribe + Send` 迁移到 `Subscribe + Task.Run(agent.RunAsync(..., runOptions, ct))`，per-turn overrides 通过 `AgentRunOptions` 注入；保留 fire-and-forget + `ContinueWith` 观察异常避免 unobserved exception | `dotnet build KodaClaw.Runtime` 0 错 0 警告 | Completed |
| KC-7303 | `apps/kodaclaw-web` | ChatComposer 重构（A 阶段）+ Brain toggle 按钮（仅 `model.supportsThinking=true` 显示）：`constants/modelCapabilities.ts` 共享 CAP 常量；`components/composer/{ModelPicker,AttachmentBar}.tsx` 抽子组件；`ChatComposer` props 分三组（core/model/attachment），删除 `onAttachMedia!` 非空断言；App.tsx `thinkingEnabled` state 默认 off，切模型重置；`sendMessage(..., { enableThinking })` 透传；`index.css` `.composer__thinking-toggle(--active)` 样式 | `npm run typecheck` 0 新错误（预存在 2 个 `EndpointDetailPanel.tsx` 与本条目无关） | Completed |

---

## Iter 74 — Channel Sticky Toggles: /think & /stream（KC-7401~7404）

> 类型：新功能（小范围，未走 FREEZE 流程；用户口头拍板 — 统一 toggle 语义，为 Main 层 thinking 对齐 Channel 侧）
> 受影响模块：`KodaClaw.Contracts`、`KodaClaw.ChannelHub`、`KodaClaw.Runtime`、`tests/KodaClaw.UnitTests`

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-7401 | `KodaClaw.Contracts` | `ThreadBinding` 追加两个可选字段 `ThinkingEnabled: bool = false`、`StreamOverride: bool? = null`；`ChannelControlCommandKind` 追加 `ThinkToggle`/`StreamToggle`；`ChannelDirectiveKind` 移除 `Stream`/`Quiet`（保留 `Think`/`Focus`）；`JsonThreadBindingRepository` roundtrip 自动覆盖（record 默认值） | `dotnet build` 0 错；L1：`JsonThreadBindingRepositoryTests.Roundtrip_preserves_ThinkingEnabled_and_StreamOverride` + `..._defaults_ThinkingEnabled_false_and_StreamOverride_null` 通过 | Completed |
| KC-7402 | `KodaClaw.ChannelHub/Commands` | `ChannelCommandRegistry`：`/think` 迁为 `think-toggle` control；`/stream` + `/quiet` 合并为 `stream-toggle` control（`/quiet` 作为 alias）；`ChannelCommandParser` 新增 dual-mode：`/think` 带非 on/off 参数时回落为 per-turn `ChannelDirectiveKind.Think` directive（支持 `/think /focus 主题 正文` 链式）；新增 `IsToggleArg(string)` helper | L1：`ChannelCommandParserTests` 15 例（含 `/think on/off/ON/OFF`、bare `/think`、`/stream on/off/bare`、`/quiet` alias、`/think <message>` 回落、`/think /focus` chain）全绿；`ChannelCommandRegistryTests.All_directive_kinds_are_covered` 豁免 Think（parser-internal fallback） | Completed |
| KC-7403 | `KodaClaw.ChannelHub/Commands` + `KodaClaw.Runtime` | `ChannelCommandDispatcher` 构造可选注入 `IThreadBindingRepository?`；新增 `HandleThinkToggleAsync`（bare → 展示状态；on/off → read-modify-write with-expression + 幂等短路；非 on/off → 用法提示）与 `HandleStreamToggleAsync`（同模式，StreamOverride 三态 null/true/false）；`ChannelTurnContext.FromDirectivesAndBinding(parsed, binding)` 叠加 sticky + per-turn（per-turn directive 与 sticky toggle 同方向时幂等，不同方向时 per-turn 赢）；`ChannelTurnOrchestrator` 切换到新工厂；`ChannelSessionService.RotateSessionAsync` 新 binding 重置 `ThinkingEnabled=false` 保留 `StreamOverride`；`/status` 追加 toggle 状态行 | L1：`ChannelCommandDispatcherTests` 新增 7 例（`/think on/off/bare/idempotent`、`/stream on/off/bare`、`/quiet` alias）；`ChannelTurnContextTests` 6 例（空、Think budget、Focus、sticky overlay 3 套、per-turn win）全绿 | Completed |
| KC-7404 | 测试层 | `dotnet test KodaClaw.sln -m:1` 全绿（UnitTests 662 + IntegrationTests 304 + ContractTests 181 = 1147）；`dotnet build KodaClaw.sln` 0 错 0 警告 | `dotnet build products/KodaClaw/KodaClaw.sln`；`dotnet test products/KodaClaw/KodaClaw.sln -m:1` | Completed |

---

## Iter 75 — Channel `/info` 命令 + `/whoami` 清退（KC-7501~7503）

> 类型：新功能（小范围，未走 FREEZE 流程；用户口头拍板 — 整合"当前 session 快照"观察能力，同时清退价值过低的 `/whoami`）
> 受影响模块：`KodaClaw.ChannelHub`、`KodaClaw.Runtime`、`tests/KodaClaw.UnitTests`

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-7501 | `KodaClaw.Runtime` | 新增 `IChannelSessionStatsTracker` + `ChannelSessionStatsTracker` + `ChannelSessionStats` record（in-memory `ConcurrentDictionary`，累积 input/output tokens + TurnCount，重启清零不持久化）；DI 注册为 singleton；`ChannelTurnOrchestrator` RunAsync 之后调 `Record(binding.SessionId, runResult.TokenUsage)`；`ChannelSessionService.RotateSessionAsync` 轮转时调 `Reset(oldSessionId)` | L1：`ChannelSessionStatsTrackerTests`（7 例：null usage / 空 sessionId / 首次写 / 累积 / reset / 未知 session reset / 会话隔离）全绿 | Completed |
| KC-7502 | `KodaClaw.ChannelHub/Commands` | 删除 `ChannelControlCommandKind.WhoAmI` + `ChannelCommandRegistry` 的 `/whoami` `/me` 条目 + `ChannelCommandDispatcher.HandleWhoAmIAsync`；新增 `ChannelControlCommandKind.Info` + `/info` `/i` 别名；`HandleInfoAsync` 输出六段（身份/模型/上下文/Toggle/绑定/活动）；Dispatcher 可选注入 `IChannelSessionStatsTracker?`，读 tracker + AccountModel.ContextWindowSize 算百分比 | L1：`ChannelCommandParserTests` + `ChannelCommandRegistryTests` `/whoami` `/me` 下线、`/info` `/i` 纳入校验；`ChannelCommandRegistryTests.All_control_kinds_are_covered` 覆盖 `Info` | Completed |
| KC-7503 | 测试层 | `dotnet build products/KodaClaw/KodaClaw.sln` 0 错 0 警告；全量回归 UnitTests 669 + IntegrationTests 304 + ContractTests 181 = 1154 全绿 | `dotnet build products/KodaClaw/KodaClaw.sln`；`dotnet test products/KodaClaw/KodaClaw.sln -m:1` | Completed |

---

## Iter 71 — Sub-Agent 实时进度可见性（KC-7101~7103）

> FREEZE doc: `docs/ITERATION_71_FREEZE.md`（2026-04-09）
> 类型：新功能

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-7101 | `KodaClaw.Runtime` + `KodaClaw.Contracts` + `contracts.ts` | `ChatSessionService` 在 Progress 流之外并行订阅 Agent Monitor 频道（fan-in via `System.Threading.Channels`，与 Progress 同生命周期），将 `SubAgentCreatedEvent`→`subagent_start`、`SubAgentToolStartEvent`→`subagent_working`、`SubAgentToolEndEvent`→`subagent_tool_done` 三类事件映射为新 `ChatStreamEvent`；`ChatStreamEvent` record 新增可选字段 `SubAgentId?`/`Label?`/`SubAgentToolName?`（实际文件：`KodaClaw.Contracts/Chat/ChatStreamEvent.cs`）；`contracts.ts` 同步新增字段与新 type 值（含 `subagent_tool_done`）；不改动任何已有字段，向后兼容 | `dotnet build KodaClaw.sln`；`npm run typecheck` | Pending |
| KC-7102 | `apps/kodaclaw-web` | `MessageBubble.tsx` 处理 `subagent_start`/`subagent_working`/`subagent_tool_done` 事件；前端用 `pendingSubAgents` 队列解决事件顺序竞态（subagent_start 先于 agent_working 到达时暂存）；`toolCount` 由 `subagent_tool_done` 累计（不是 working 次数）；pipeline 多 stage 各自显示子行并在父工具 `tool_activity` 时累计折叠；`tool_activity` 到达后折叠为"↳ {label} · 使用了 N 个工具"；新增 `MessageBubble.css` 子行样式（缩进、`--text-secondary` 色、`0.85rem` 字号，全量 CSS token） | `npm run typecheck`；L5 Dogfood | Pending |
| KC-7103 | `KodaClaw.IntegrationTests` | L2 集成测试：mock Monitor 事件注入 ChatSessionService，验证 `subagent_start`/`subagent_working` 正确出现在 SSE 流；L3 契约测试：`ChatStreamEvent` 新字段序列化验证 | `dotnet test KodaClaw.sln -m:1 --filter "SubAgentProgress"` | Pending |

---

## Iter 67 — 前端服务端状态现代化 Phase 1：基础设施 + models 迁移

> FREEZE doc: `docs/ITERATION_W4_FREEZE.md`（2026-04-06，设计参考文档）
> 类型：优化/重构
> 不改动任何 API contract 和后端代码

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-6701 | kodaclaw-web 基础设施 | 安装 `@tanstack/react-query` v5；`main.tsx` 在 `I18nProvider` 内层加 `QueryClientProvider`；新建 `src/lib/queryKeys.ts` 定义 models / settings / channels / inbox / approvals / automations / plugins / canvas / sessions / workspaceFiles / skills / mcpServers / diagnostics 全量 queryKey 常量；不改动任何业务组件 | `npm run typecheck`；`npm run build` | Completed |
| KC-6702 | kodaclaw-web / App.tsx + ModelsSettingsDesk | 删除 App.tsx 的 `modelsLoadedRef` + 手动 fetch 块，改为 `useQuery(queryKeys.models, fetchModels)`；`availableModels`/`selectedModelId`/`modelName`/`modelCapabilities` 从 query data 派生；`ModelsSettingsDesk` 4 个 mutation（create/update/delete/setDefault）改用 `useMutation`，`onSuccess` 调 `invalidateQueries(queryKeys.models)`；两个组件共享同一 query cache，任意一方写入后另一方自动刷新；补 `test-utils.tsx`/`app-shell.spec.tsx` 的 QueryClientProvider | `npm run typecheck`；`npm run build`；L5 Dogfood：添加模型→回 chat→选择器立即可见，无需刷新 | Completed |

## Iter 68 — 前端服务端状态现代化 Phase 2：settings + channels

> FREEZE doc: `docs/ITERATION_W4_FREEZE.md`

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-6801 | kodaclaw-web / Settings sections | `BehaviorSection`、`AppearanceSection`、`NotificationsSection`、`SessionConfigSection`、`ThemeToggle` 5 处各自独立的 `fetchSettings()` + `useState` 替换为 `useQuery(queryKeys.settings, fetchSettings)`；各 Section `saveSettings()` 改 `useMutation`，`onSuccess` invalidate settings；5 个组件共享同一份缓存，任意 Section 保存后其他立即感知 | `npm run typecheck` | Completed |
| KC-6802 | kodaclaw-web / ChannelsDesk + ConnectionsSection | `ChannelsDesk` 和 `ConnectionsSection` 的 `fetchChannelAccounts()` 改为 `useQuery(queryKeys.channelAccounts)`；create/update/delete account mutation `onSuccess` 调 `invalidateQueries(queryKeys.channelAccounts)`；`fetchChannelConnectors`/`fetchChannelThreads`/`fetchChannelThreadDetail` 同步迁移，queryKey 含 accountId 参数实现精准 invalidate；ChannelsDesk 删除账号后 ConnectionsSection 立即同步 | `npm run typecheck` | Completed |

## Iter 69 — 前端服务端状态现代化 Phase 3：inbox + automations + plugins

> FREEZE doc: `docs/ITERATION_W4_FREEZE.md`

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-6901 | kodaclaw-web / InboxApprovalDesk | `fetchInbox`/`fetchApprovals` 改为 `useQuery`（queryKey 含 filter 参数）；审批决策用 local state override 实现即时 UI 反馈，同时 invalidate inbox/approvals 做后台刷新；`approvalOverrides`/`inboxStatusOverrides` 合并到 approvalsMap/selectedItem，解决 Vitest 环境 useSyncExternalStore 不触发重渲染的问题 | `npm run typecheck`；5/5 inbox tests pass | Completed |
| KC-6902 | kodaclaw-web / AutomationsDesk | `fetchAutomations`/`fetchAutomation`/`fetchAutomationRuns` 改为 `useQuery`；`triggerAutomation`/`updateAutomationDefinition` 改 `useMutation`，`onSuccess` invalidate 对应 queryKey；trigger 后 runs 列表自动更新 | `npm run typecheck` | Completed |
| KC-6903 | kodaclaw-web / PluginsDesk | `fetchPlugins`/`fetchPlugin`/`fetchPluginLogs` 改为 `useQuery`；enable/disable/start/stop/trust/install/discover 全部改 `useMutation`，`onSuccess` invalidate plugins | `npm run typecheck` | Completed |

## Iter 70 — 前端服务端状态现代化 Phase 4：剩余组件 + 收尾

> FREEZE doc: `docs/ITERATION_W4_FREEZE.md`

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-7001 | kodaclaw-web / CanvasDesk + SessionsDiagnosticsDesk | CanvasDesk artifacts/artifact/defaultEntry 改 `useQuery`，publish 改 `useMutation`；SessionsDiagnosticsDesk sessions/sessionDetail/sessionMessages 改 `useQuery`；resume/delete session 改 `useMutation` | `npm run typecheck` | Completed |
| KC-7002 | kodaclaw-web / WorkspaceFiles + McpServersDesk + SkillsDesk | WorkspaceIdentityEditor/MemorySection 中 `fetchWorkspaceFile` 改 `useQuery`，`updateWorkspaceFile` 改 `useMutation`；McpServersDesk fetchMcpServers/saveMcpServers 迁移；SkillsDesk fetchSkills 迁移；memory stats/entries/promote 迁移 | `npm run typecheck` | Completed |
| KC-7003 | kodaclaw-web / hooks + E2E | `useInboxUnreadCount`/`useDiagnosticsHealth` 的 `setInterval` 轮询改为 `useQuery` + `refetchInterval` 选项；删除因迁移成为死代码的 `modelsLoadedRef`、手动 `isLoading` state 等；`npm run test:e2e` 全量 Playwright 通过 | `npm run typecheck`；`npm run build`；`npm run test:e2e` | Completed |

## Iter 66 — Onboarding UX 优化 + GLM Coding Plan 接入（2026-04-05）

> FREEZE doc: `docs/ITERATION_66_FREEZE.md`

范围：ModelStep 顶部新增 [标准 API] / [Coding Plan] 模式切换，按模式过滤 Provider 和 preset 列表；GLM Coding Plan 路径展示 glm-5.1 / glm-5-turbo，自动使用 AnthropicCompatible 协议，消除现有 5 个 GLM preset 混杂展示的困惑。MiniMax Token Plan 预留入口，二期实现。

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-6601 | Gateway/Web | `model-presets.json` 给 glm-5.1、glm-5-turbo、glm-5-anthropic 加 `"accessMode": "coding-plan"`；`contracts.ts` `ModelPreset` 加 `accessMode?: 'api' \| 'coding-plan'` | `npm run typecheck`；`dotnet build` 0 错 | Completed |
| KC-6602 | Web/Onboarding | `ModelStep.tsx` 顶部模式 pill（[标准 API] / [Coding Plan]）；标准 API 模式过滤掉 coding-plan preset；Coding Plan 模式只显示智谱 GLM provider，展示 glm-5.1 / glm-5-turbo；协议选择器在 Coding Plan 模式隐藏；API Key label/hint 随模式变化；Save 逻辑 Coding Plan 模式直接使用 `preset.provider` | `npm run typecheck` | Completed |
| KC-6603 | Web/Onboarding | MiniMax Token Plan tab 预留（disabled + "即将支持"提示）；CSS 补全模式切换 pill 样式 | `npm run typecheck` | Completed |
| KC-6604 | All | L0 全量编译 + L5 Dogfood：标准 API 模式 GLM 下无 glm-5.1；Coding Plan 模式填 Plan Key 能测试并保存为 AnthropicCompatible endpoint | `dotnet build`；`npm run typecheck`；手动验收 | Pending |

## Iter 65 — 微信媒体消息：图片 + 文件入站/出站 Phase 1（2026-04-05）

> FREEZE doc: `docs/ITERATION_65_FREEZE.md`

范围：P0——WeChat 连接器支持图片（type=2）和文件（type=4）的完整收发闭环。AES-128-ECB CDN 加解密层新建，入站解析写 MediaStore，出站加密上传并 sendmessage。语音/视频为 Phase 2。

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-6501 | ChannelHub/Contracts | `WeChatApiContracts.cs` 补全：`ILinkImageItem`、`ILinkFileItem`、`ILinkMedia`、`ILinkGetUploadUrlRequest/Response`、`ILinkUploadParam`；`ILinkMessageItem` 加 `ImageItem`/`FileItem` 字段 | `dotnet build` 0 错 | Completed |
| KC-6502 | ChannelHub | `IWeChatCdnClient` + `HttpWeChatCdnClient`：AES-128-ECB 加解密（含图片/文件两种 key 编码格式）；CDN 上传（POST + 读 `x-encrypted-param` header）；CDN 下载（GET + 解密）；注册 DI | `dotnet test --filter WeChatCdn` | Completed |
| KC-6503 | ChannelHub | `IWeChatApiClient.GetUploadUrlAsync` + `SendMediaAsync` 接口 + `HttpWeChatApiClient` 实现（裸 `application/json` 无 charset） | `dotnet build` 0 错 | Completed |
| KC-6504 | ChannelHub | `WeChatConnector` 入站扩展：`PollLoopAsync` 解析 item type=2/4 → `IWeChatCdnClient.DownloadAndDecryptAsync` → `IMediaStore.StoreAsync` → 附 `MediaReference` 到 `ChannelEventEnvelope` | `dotnet test --filter WeChatMediaInbound` | Completed |
| KC-6505 | ChannelHub | `WeChatConnector` 出站扩展：`SendAsync` 遍历 `draft.MediaAttachments` → 读 MediaStore → AES 加密 → `GetUploadUrlAsync` → CDN 上传 → `sendmessage` type=2/4；每个附件独立发送；失败单条隔离不阻断文字消息 | `dotnet test --filter WeChatMediaOutbound` | Completed |
| KC-6506 | Tests | `WeChatCdnClientTests`（L1, 7）：AES-ECB 往返、图片/文件 key 编码；`WeChatMediaConnectorTests`（L2, 4）：入站 type=2/4 mock、出站 mock 上传、失败隔离；全量回归 471 unit + 267/268 integration（1 个预存在 flaky） | `dotnet test KodaClaw.sln -m:1` | Completed |

## Iter 64 — 一次性定时提醒（2026-03-28）

> FREEZE doc: `docs/ITERATION_64_FREEZE.md`

范围：P0 核心能力——Agent 可通过 `schedule_reminder` 工具安排一次性定时任务，`AutomationScheduler` 每分钟 tick 时检测到期 timer 并触发 Agent 会话执行，结果写入 Inbox。

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-6401 | Contracts | `OneShotTimerRecord` / `OneShotTimerStatus` / `IOneShotTimerRepository` 数据类型；`AutomationDefinitionSource.OneShot` 枚举值 | `dotnet build` 0 错 | Completed |
| KC-6402 | Storage.Json | `JsonOneShotTimerRepository`：JSON 数组文件 + WAL 原子写 + SemaphoreSlim 并发保护；注册至 `AddKodaClawJsonStore` | `dotnet test --filter JsonOneShotTimer` | Completed |
| KC-6403 | Automation | `IOneShotTimerService` + `OneShotTimerService`：TickAsync 查 Pending timers → 乐观锁标 Fired → 合成 AutomationDefinition → StartAutomationSession → 写 Inbox；注入 `AutomationScheduler.TickCoreAsync` 末尾 | `dotnet test --filter OneShotTimerService` 6 个通过 | Completed |
| KC-6404 | Runtime | `ScheduleReminderTool`（`schedule_reminder`）：fireAt ISO 8601 校验 + AddAsync；加入 `DefaultTools`；注册 ToolRegistry；`koda-workspace/SKILL.md` allowed-tools 追加 | `dotnet build` 0 错 | Completed |
| KC-6405 | Tests | `OneShotTimerServiceTests`（L1, 6）+ `OneShotTimerContractTests`（L3, 5）；全量回归 427/429 unit + 259 integration + 159 contract | `dotnet test KodaClaw.sln -m:1` | Completed |

## Iter 63 — 存储层去 SQLite：JSON/JSONL 实现 + 接口抽象（2026-03-26）

> FREEZE doc: `docs/ITERATION_63_FREEZE.md`

范围冻结：见 `docs/ITERATION_63_FREEZE.md`（2026-03-26）。将 12 类业务数据从 SQLite 迁移至 JSON/JSONL 文件存储，新建 `KodaClaw.Storage.Json` 项目实现所有 `IXxxRepository` 接口，`KodaClaw.Contracts` 接口层零改动，Gateway DI 一键切换。后续可通过新建 `KodaClaw.Storage.Pgsql` 等项目扩展为云端实现。

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-6301 | Storage / Storage.Json | 新建 `KodaClaw.Storage.Json` 项目；`KodaClaw.Storage` 去 SQLite，新增 `JsonStoreBase`（WAL 写、JSONL append、目录扫描三原语） | `dotnet build KodaClaw.sln` 0 错 0 警告；`JsonStoreBaseTests` ~10 个通过 | Completed |
| KC-6302 | Storage.Json | A 类配置型 4 个：`JsonSettingsRepository` / `JsonModelRegistryRepository`（SetDefault + ResolveDefaultFor）/ `JsonPluginRegistryRepository` / `JsonAutomationDefinitionRepository` | `dotnet test --filter JsonConfigRepositories` | Completed |
| KC-6303 | Storage.Json | C 类日志追加型 3 个：`JsonAutomationRunRepository` / `JsonChannelAuditRepository` / `JsonPluginLogRepository`（5000 行截断保留 2000 + entry_id 幂等去重） | `dotnet test --filter JsonLogRepositories` | Completed |
| KC-6304 | Storage.Json | B 类业务状态型 4 个：`JsonInboxRepository` / `JsonApprovalRepository`（TransitionAsync 状态机）/ `JsonCanvasArtifactRepository` / `JsonChannelAccountRepository` | `dotnet test --filter JsonStateRepositories` | Completed |
| KC-6305 | Storage.Json | `JsonThreadBindingRepository`：内存字典热路径（懒加载 + ConcurrentDictionary）+ WAL 写穿透；`GetByExternalThreadAsync` O(1) | `dotnet test --filter JsonThreadBindingRepository` | Completed |
| KC-6306 | Storage / ControlPlane / Automation / ChannelHub / PluginHost / ModelHub | 删除 17 个 SQLite 实现类；各模块 `.csproj` 移除 `Microsoft.Data.Sqlite` 引用 | `dotnet build KodaClaw.sln` 0 错 0 警告 | Completed |
| KC-6307 | Gateway | `AddKodaClawJsonStore` DI 替换各模块 SQLite 注册；Gateway 加 `KodaClaw.Storage.Json` 项目引用 | `make run-gateway`；`make test-integration` | Completed |
| KC-6308 | Tests | 删除 SQLite 测试；新增 JSON L1/L3 测试；`IntegrationTests` 改用临时目录；全量回归通过 | `dotnet test KodaClaw.sln -m:1` | Completed |

## Iter 62 — 全系统观测埋点覆盖（2026-03-26）

> FREEZE doc: `docs/ITERATION_62_FREEZE.md`

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-6201 | Runtime | `MainSessionService` + `ChannelSessionService` 会话创建/加载/轮转/失败/超时/审批等关键生命周期事件；7 个 EventType | `dotnet test --filter SessionLifecycle` | Completed |
| KC-6202 | ChannelHub | `TelegramConnector` / `FeishuConnector` / `WeChatConnector` 启动/停止/连接超时/发送失败事件 | `dotnet test --filter ConnectorLifecycle` | Completed |
| KC-6203 | Runtime | `WorkspaceProtocolUpdateTool` / `WorkspaceMemoryAppendTool` / `CanvasUpsertTool` / `InboxCreateTool` 写操作成功/失败事件；`GenerateSpeechTool` Emit 事件名修复（`" "` → `speech_generated`）；`IDiagnosticsService` DI 统一 | `dotnet test --filter WriteOperationDiagnostics` | Completed |
| KC-6204 | Automation | `AutomationScheduler` 运行成功/失败/崩溃/stale 恢复/settings 读失败 5 个 EventType；`IDiagnosticsService?` ctor + DI 注入 | `dotnet test --filter AutomationDiagnostics` | Completed |
| KC-6205 | 跨模块 | `GenerateImageTool` + `GenerateSpeechTool` 各加 `IDiagnosticsService?` + `image.generation_failed`/`image.generated`/`speech.generated` 事件；`SessionRetentionService` / `McpHubService` / `MemoryConsolidationService` 已有覆盖确认 | `dotnet build` | Completed |
| KC-6206 | Tests | `AutomationSchedulerDiagnosticsTests`(L1,5) + `WriteToolDiagnosticsTests`(L1,3)；全量回归 152 契约 + 259 集成 + 435 单元 = 846 全绿 | `dotnet test KodaClaw.sln -m:1` | Completed |

## Iter 61 — Diagnostics 可感知、可关联、可自诊（2026-03-26）

> FREEZE doc: `docs/ITERATION_61_FREEZE.md`

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-6101 | Contracts + ControlPlane + Gateway | `IDiagnosticsService.GetStats(DateTimeOffset? since)` 新增 since 参数；`FileDiagnosticsService` / `InMemoryDiagnosticsService` 实现时间窗口统计；`GET /api/diagnostics/stats?since=ISO8601` 端点支持 | `dotnet test --filter DiagnosticsStats` | Completed |
| KC-6102 | Runtime | 新建 `DiagnosticsQueryTool`（`diagnostics_query`）：level/source/correlationId/sinceMinutes/limit 参数，返回摘要字段（不含 attributes）；DI 注册；`koda-workspace/SKILL.md` allowed-tools 追加 | `dotnet test --filter DiagnosticsQueryTool` | Completed |
| KC-6103 | Gateway + Automation + Runtime | `DiagnosticsLoggerProvider` 注入 `ICorrelationContextAccessor`；`AutomationScheduler` 每次运行生成 correlationId；`InboxCreateTool` / `GenerateImageTool` / `CanvasUpsertTool` 补 accessor 注入，替换硬编码 null | `dotnet test --filter CorrelationId` | Completed |
| KC-6104 | kodaclaw-web | `useDiagnosticsHealth` hook（30s 轮询 since=1h）；Sidebar"诊断"错误 badge；状态栏近期错误提示；DiagnosticsDesk EventRow 展开显示 correlationId + 追踪按钮；toolbar correlationId chip；stats pills 过滤模式下前端实时计算 | `npm run typecheck` | Completed |
| KC-6105 | Gateway | `DiagnosticBundleService.BuildReportHtml`：bundleData events 加 correlationId；表格新增 CorrelationId 列（截取前 8 位）；过滤栏加 correlation input | `dotnet build` | Completed |
| KC-6106 | Tests | `DiagnosticsStatsSinceTests`(L1,6) + `DiagnosticsQueryToolTests`(L1,7) + `DiagnosticsLoggerProviderCorrelationTests`(L1,3) + `DiagnosticsStatsEndpointTests`(L2,3)；全量回归 427 单元 + 259 集成 + 152 契约全绿 | `dotnet test KodaClaw.sln -m:1` | Completed |

## Iter 60 — Diagnostics 可观测、可视化、可清理（2026-03-26）

> FREEZE doc: `docs/ITERATION_60_FREEZE.md`

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-6001 | Contracts + ControlPlane | `IDiagnosticsService` 新增 `ClearAsync`/`SubscribeAsync`；`DiagnosticsQuery` 新增 `DateFrom`/`DateTo`；新增 `DiagnosticsStatsResponse`/`DiagnosticsSourceStats`/`DiagnosticsClearRequest` contracts；`FileDiagnosticsService` 替换 `InMemoryDiagnosticsService`（JSONL 持久化 + 内存热层 500 条 + 启动回填 + Channel 广播） | `dotnet build` | Completed |
| KC-6002 | Gateway | `GET /api/diagnostics/stream`（SSE 实时推送）；`DELETE /api/diagnostics`（支持 `?before=ISO8601`）；`GET /api/diagnostics/stats`（统计摘要）；DI 替换注册 | `dotnet test --filter DiagnosticsEndpoint` | Completed |
| KC-6003 | Gateway | `DiagnosticsLoggerProvider`（ILoggerProvider bridge）：Warning 级以上 ILogger 事件桥接到 `IDiagnosticsService.Record()`；Gateway 启动时注册 provider | `dotnet build` | Completed |
| KC-6004 | Gateway | `DiagnosticBundleService` 追加 `report.html` 生成步骤：内嵌数据 + Chart.js（CDN）；三图（时间线面积图 / source 横柱 / level 饼图）+ 底部可过滤事件列表 | `dotnet test --filter DiagnosticBundleTests` | Completed |
| KC-6005 | kodaclaw-web | `DiagnosticsDesk`（新页面）：实时事件列表 + 过滤栏（level/source/sessionId/搜索）+ SSE 接入 + 统计头部 pills + 清理下拉（7天前/1天前/全清）；Sidebar"工具"区新增"诊断"入口 | `npm run typecheck` | Completed |
| KC-6006 | Tests | `FileDiagnosticsServiceTests`(L1,8) + `DiagnosticsLoggerProviderTests`(L1,4)；全量回归 411 单元 + 256 集成 + 152 契约通过（预存在 flaky 不计） | `dotnet test KodaClaw.sln -m:1` | Completed |

## 记忆系统优化 — 移除 SQLite 元数据层，纯文件方案（2026-03-26）

> 类型：优化/重构（大改）
> 关联：Iter 58 + 59 记忆系统
> 设计决策：`docs/记忆系统设计方案.md` 附录 A

| 条目 | 模块 | 变更内容 | 验证命令 | 状态 |
|------|------|---------|---------|------|
| KC-W3-001 | Contracts + Storage | 删除 `IMemoryMetadataRepository`、`MemoryEntry`、`MemoryEntryQuery`、`SqliteMemoryMetadataRepository`；删除 `memory_entries` DDL；删除 `MemorySchemaVersion`；新增 `IMemoryFileService`、`MemoryFileEntry`、`MemoryFileStats` | `dotnet build products/KodaClaw/KodaClaw.sln` | Completed |
| KC-W3-002 | Workspace | 新建 `MemoryMarkdownParser`（提取自 MemoryMigrationService）、`MemoryFrontmatterParser`、`MemoryFileService`；删除 `MemoryMigrationService` | `dotnet test --filter MemoryMarkdownParser,MemoryFrontmatter,MemoryFileService` | Completed |
| KC-W3-003 | Runtime | 重写 `MemoryConsolidationService`（仅 git commit）；简化 `WorkspaceReadTool`（移除 memory_search）；简化 `WorkspaceProtocolUpdateTool`（移除 SQLite 追踪）；修复 DI 注册 | `dotnet test --filter MemoryConsolidation` | Completed |
| KC-W3-004 | Gateway | 重写 `GatewayApp.MemoryEndpoints.cs`（`IMemoryFileService` 文件扫描替代 SQLite 查询） | `dotnet test --filter MemoryStatsEndpoint` | Completed |
| KC-W3-005 | Workspace | HEARTBEAT 模板新增 Stage 5（Agent 语义降级审查）；移除 SQLite 引用 | `dotnet build` | Completed |
| KC-W3-006 | kodaclaw-web | `api.ts` MemoryEntryItem 字段更新（lastAccessed→created, topic→tags）；MemorySection.tsx 显示适配 | `npm run typecheck` | Completed |
| KC-W3-007 | Tests + Docs | 重写 MemoryConsolidationServiceTests、MemoryStatsContractTests、MemoryStatsEndpointTests；新增 MemoryFrontmatterParserTests、MemoryFileServiceTests；更新 SKILL.md v3.0、设计方案 v2.0、FREEZE 修订说明 | `dotnet test KodaClaw.sln -m:1` | Completed |

## Iter 59 — 记忆系统 Phase 2：Auto Dream 管道 + Topics 索引 + 清理与隐私（2026-03-26）

> FREEZE doc: `docs/ITERATION_59_FREEZE.md`
> 前置: Iter 58 完成

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-5901 | Workspace + skills | HEARTBEAT Nightly Consolidation prompt 重写为四阶段指令（采集 → 整合 MEMORY.md → topics 维护 → 清理）；`koda-memory/SKILL.md` v2.1 新增 topics/ 说明 + Auto Dream 整合段落 | `npm run typecheck` | Completed |
| KC-5902 | Automation + Runtime | `AutomationScheduler` Memory Consolidation 完成后触发 `PostConsolidationAsync` 后处理钩子（标题匹配"Memory Consolidation"/"记忆整合"，失败不阻塞）；DI 注入 `IMemoryConsolidationService?` | `dotnet test --filter AutoDreamPostProcessingTests` | Completed |
| KC-5903 | KodaClaw.Runtime | `workspace_read(target=topics)` 列出 topics/ 文件 + `path` 参数读取指定 topic；`PostConsolidationAsync.SyncTopicsToSqliteAsync` 扫描 topics/ → 写入 `memory_entries`（key=`topic-{slug}`, priority=P1, topic 字段填充）；`WorkspaceReadArgs` 新增 `Path` 参数 | `dotnet test --filter TopicsWorkspaceReadTests` | Completed |
| KC-5904 | KodaClaw.Gateway | `SessionRetentionService` 扩展：main-* 有 memory summary + 创建>30天→可删；channel-* 有 SUMMARY.md + 创建>30天→可删；活跃 session 永远跳过；`session_retention.cleaned` 诊断事件 | `dotnet test --filter SessionRetentionExtensionTests` | Completed |
| KC-5905 | KodaClaw.Runtime | `SessionSummaryService` LLM prompt 追加隐私规则（不含密码/API Key/token）；`DetectPrivacyLevel` 显式关键词检测（"不要记录"/"别记这个"/"private session" 等 7 个 marker）；private 会话只生成基本元数据摘要 | `dotnet test --filter SummaryPrivacyTests` | Completed |
| KC-5906 | kodaclaw-web + Gateway | `GET /api/memory/stats`（active/dormant/archived/topics/sessions 计数）；`GET /api/memory/entries?status=`（分页列表）；`POST /api/memory/entries/{key}/promote`（手动升级 dormant/archived → active）；Settings Desk MemorySection 升级（统计卡片 + 状态过滤 tab + 条目列表 + 激活按钮） | `dotnet test --filter MemoryStatsEndpointTests,MemoryStatsContractTests && npm run typecheck` | Completed |
| KC-5907 | Tests | `AutoDreamPostProcessingTests`(L1,8) + `TopicsWorkspaceReadTests`(L1,4) + `SessionRetentionExtensionTests`(L1,6) + `SummaryPrivacyTests`(L1,4) + `MemoryStatsEndpointTests`(L2,3) + `MemoryStatsContractTests`(L3,3)；全量回归 359 单元 + 256 集成 + 147 契约通过（预存在失败不计） | `dotnet test KodaClaw.sln -m:1` | Completed |

## Iter 58 — 记忆系统 Phase 1：会话摘要 + 元数据基座 + 冷热分离（2026-03-26）

> FREEZE doc: `docs/ITERATION_58_FREEZE.md`
> 设计方案: `docs/记忆系统设计方案.md`

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-5801 | KodaClaw.Storage | `memory_entries` SQLite 表（id/key/title/priority/status/last_accessed/source_path 等）+ `IMemoryMetadataRepository` 接口 + `SqliteMemoryMetadataRepository` 实现（CRUD + 降级查询） | `dotnet test --filter SqliteMemoryMetadataRepositoryTests` | Completed |
| KC-5802 | Workspace + Contracts | 存量 MEMORY.md 迁移：`WorkspaceAppConfig.MemorySchemaVersion` 字段；`KodaClawWorkspaceLayout` 新增 4 个目录常量；`MemoryMigrationService` 解析 MEMORY.md sections → 批量写入 memory_entries；幂等目录创建 | `dotnet test --filter MemoryMigrationServiceTests,MemoryMigrationContractTests` | Completed |
| KC-5803 | KodaClaw.Runtime | `IMemorySessionSummaryService` + `MemorySessionSummaryService`：LLM 生成结构化会话摘要（topics/keywords/decisions/follow_ups），写入 `workspace/memory/sessions/{date}-{type}-{id}.md`；低价值会话过滤（<5 条消息/automation/无用户消息） | `dotnet test --filter SessionSummaryServiceTests` | Completed |
| KC-5804 | KodaClaw.Runtime | `MainSessionService.RotateMainSessionAsync` + `ChannelSessionService.RotateSessionAsync` 轮转前触发摘要生成（通过 JsonAgentStore 从磁盘加载 messages，失败不阻塞）；DI 注册 `IMemorySessionSummaryService` | `dotnet build` | Completed |
| KC-5805 | KodaClaw.Runtime | `workspace_read(target=memory_search)` 关键词搜索 memory_entries；`workspace_read(target=memory)` 触发 last_accessed 更新；`workspace_protocol_update(target=memory)` 触发对应 section upsert/更新 last_accessed；`WorkspaceReadArgs` 新增 `Query` 参数 | `dotnet build` | Completed |
| KC-5806 | KodaClaw.Runtime | `IMemoryConsolidationService.PostConsolidationAsync`：MEMORY.md section 同步到 SQLite → P0-P3 降级规则执行（P0 永不/P1 90d/P2 30d/P3 7d）→ 降级文件移入 dormant/ 或 archive/ → git commit | `dotnet test --filter MemoryConsolidationServiceTests` | Completed |
| KC-5807 | Gateway/skills + Workspace | `koda-memory/SKILL.md` v2.0（三层架构 + memory_search 用法 + 降级机制 + session 摘要说明）；`DefaultWorkspaceTemplates.Heartbeat()` Nightly Consolidation prompt 更新（读 memory_search + P0-P3 降级说明） | `dotnet build` | Completed |
| KC-5808 | Tests | `SessionSummaryServiceTests`(L1,6) + `MemoryMigrationServiceTests`(L1,6) + `MemoryConsolidationServiceTests`(L1,6)；全量回归 337 单元 + 252 集成 + 144 契约通过（预存在失败不计） | `dotnet test KodaClaw.sln -m:1` | Completed |

## Iter 57 — CLI+Skills 生态基础层（2026-03-25）

> FREEZE doc: `docs/ITERATION_57_FREEZE.md`

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-5701 | Kode.Agent.Sdk | `SkillsLoader` 解析 `Bash(kc:*)` → `bash_run[kc]` 内部表示，`Bash` 别名映射为 `bash_run`；`PermissionManager` 存储命令前缀约束，`CheckCommandPermission` 执行首 token 匹配 + shell 元字符强制审批；`BashRunTool` 执行前调用检查 | `dotnet test --filter PermissionManagerCommandConstraintTests,SkillsLoaderBashAliasTests,ShellMetacharDetectionTests` | Completed |
| KC-5702 | tools/KodaClaw.Cli（新） | 新建 `kc` .NET CLI 项目；子命令：`auth status/login`、`workspace status`、`automation list/run <id>`；全部支持 `--json` 输出；exit code 语义完整；通过 Gateway HTTP API 操作 | `dotnet run --project tools/KodaClaw.Cli -- automation list --json` | Completed |
| KC-5703 | Gateway/skills | 新建 `koda-cli/SKILL.md`（agentskills.io 标准）：frontmatter 含 `allowed-tools: Bash(kc:*)`、`compatibility`、`metadata`；body 覆盖认证检测流程、子命令参考、`--json` 解析指引、错误处理策略 | `dotnet test --filter KodaCliSkillContractTests` | Completed |
| KC-5704 | Tests | `PermissionManagerCommandConstraintTests`（L1, 8）+ `ShellMetacharDetectionTests`（L1, 10）+ `SkillsLoaderBashAliasTests`（L1, 6）+ `KcCliIntegrationTests`（L2, 5）+ `KodaCliSkillContractTests`（L3, 4）；全量回归通过 | `dotnet test KodaClaw.sln -m:1` | Completed |

## Iter 56 — TTS 语音合成（2026-03-25）

> FREEZE doc: `docs/ITERATION_56_FREEZE.md`

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-5601 | ModelHub | `ISpeechService` 接口 + `OpenAICompatibleTtsService`（兼容 MiMo / OpenAI TTS-1）；DI 注册；新增 `mimo-tts-default` 和 `openai-tts-1` 两个预置 ModelEndpoint（`capabilities=TextToSpeech`） | `dotnet build` 0 错；`dotnet test --filter OpenAICompatibleTtsServiceTests` | Completed |
| KC-5602 | Runtime | `GenerateSpeechTool`（text/voice/endpoint_id 参数，返回 mediaId + mediaUrl）；诊断事件 `speech_generated` | `dotnet test --filter GenerateSpeechToolTests` | Completed |
| KC-5603 | ChannelHub | `TelegramConnector` 按 content-type 路由：`audio/*` → `sendAudio`（mp3 直传，无转码）；`FeishuConnector` uploadFile + sendAudio；`WeChatConnector` 明确拒绝并抛 `NotSupportedException`（不静默失败） | `dotnet test --filter TelegramConnectorAudioTests` | Completed |
| KC-5604 | Gateway/skills | `koda-channels/SKILL.md`：`allowed-tools` 加 `generate_speech`；新增语音发送段落（`generate_speech → channel_send(mediaId)` 组合模式）；平台支持矩阵补音频行；MiMo style 标签示例；版本升至 1.1 | `npm run typecheck`；人工验证 SkillsDesk 展示 | Completed |
| KC-5605 | Tests | `OpenAICompatibleTtsServiceTests`（L1, 6）+ `GenerateSpeechToolTests`（L1, 6）+ `TelegramConnectorAudioTests`（L2, 3）+ `SpeechContractTests`（L3, 4）；全量回归 711 个测试全绿 | `dotnet test KodaClaw.sln -m:1` | Completed |

## Iter 55 — HEARTBEAT.md Cron 调度重构（2026-03-25）

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-5501 | Workspace | `HeartbeatAutomationCompiler` 新增 `- cron: "..."` bullet 解析（Cronos 验证）；`LegacyScheduleToCron` 将旧 `- schedule:` 5 种表达式机械转换为等价 cron string；两者不能同时出现；`SectionDraft.CronExpression` 替换原 `ScheduleExpression`；5 个 Regex 定义删除 | `dotnet test --filter HeartbeatAutomationCompilerContractTests` | Completed |
| KC-5502 | Contracts + Automation | 删除 `AutomationSchedule` / `AutomationScheduleKind` / `AutomationScheduleDay`；`AutomationDefinition.Schedule` → `CronExpression: string`；`AutomationScheduler.ComputeNextRunAt` 换 Cronos + `TimeZoneInfo.Utc`（UTC 语义确定性）；`AutomationValidation` schedule 校验简化；新增 NuGet `Cronos` | `dotnet test --filter AutomationSchedulerTests,AutomationSchedulerIntegrationTests` | Completed |
| KC-5503 | Storage | SQLite 迁移：4 列（`schedule_kind/interval/local_time/days_of_week`）→ 1 列 `cron`；`SqliteAutomationDefinitionRepository` 读写改为 `cron` 列；旧行 `cron=NULL` 时 fallback `"0 * * * *"` | `dotnet test --filter SqliteAutomationDefinitionRepositoryTests,HeartbeatSyncIntegrationTests` | Completed |
| KC-5504 | kodaclaw-web + Skills | `contracts.ts` 删 schedule 嵌套类型，加 `cronExpression: string`；`AutomationsDesk.tsx` `formatSchedule` 直接展示 cron 字符串；`koda-automation/SKILL.md` 完整改写（版本 2.0，cron 格式表 + UTC 注意 + legacy 兼容表）；`DefaultWorkspaceTemplates.Heartbeat()` 示例改为 `- cron:`；`WorkspaceProtocolUpdateTool` 描述更新 | `npm run typecheck && dotnet test --filter HeartbeatLegacyCompatibilityTests` | Completed |

## Iter 51 — Workspace Git 版本管理（2026-03-24）

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-5101 | Contracts + Workspace | `WorkspaceGitCommit` / `WorkspaceGitLogResponse` / `WorkspaceGitRevertFileRequest` records；`IWorkspaceGitService` 接口；`LibGit2Sharp` NuGet 加入 `KodaClaw.Workspace` | `dotnet build` 0 错 | Completed |
| KC-5102 | Workspace | `WorkspaceGitService` 实现：`EnsureGitRepoAsync`（幂等 init + 存量迁移 + `.gitignore` + `.gitattributes`）；`TryCommitAsync`（`SemaphoreSlim` 并发安全，空 diff 跳过，异常静默）；`GetRecentCommitsAsync` / `GetCommitDiffAsync` / `RevertFileToCommitAsync` | `dotnet test --filter WorkspaceGitServiceTests` | Completed |
| KC-5103 | Workspace + Runtime | 4 个集成点 auto-commit：`EnsureInitializedAsync` → git init；`WorkspaceProtocolUpdateTool` → commit（source=agent）；`WorkspaceMemoryAppendTool` → commit（source=agent）；`PUT /api/workspace/file` → commit（source=user/settings-desk） | `dotnet test --filter WorkspaceGitIntegrationTests` | Completed |
| KC-5104 | Gateway | `GatewayApp.WorkspaceGitEndpoints.cs`：`GET /api/workspace/git/log`、`GET /api/workspace/git/diff/{hash}`、`POST /api/workspace/git/revert-file`；DI 注册 `IWorkspaceGitService` | `dotnet test --filter WorkspaceGitEndpointTests` | Completed |
| KC-5105 | kodaclaw-web | `api.ts` 加 3 个 git API；Settings Desk 新增 `HistorySection.tsx`（commit 时间线 + source badge + diff 展开 + 回滚按钮 + 隐私悖论告知文案） | `npm run typecheck` | Completed |
| KC-5106 | Tests | `WorkspaceGitServiceTests`（L1，含并发安全）+ `WorkspaceGitContractTests`（L3）+ `WorkspaceGitIntegrationTests`（L2）；新增失败 0 | `dotnet test KodaClaw.sln -m:1` | Completed |

## Iter 50 — Channel DM 主会话等价升级 + Session Reset 命令（2026-03-24）

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-5001 | Runtime | `EffectivePolicyScope` DM 分支：`LoadLongTermMemory = true`；`LoadContextDocumentsAsync` 加载今日 + 昨日 daily memory；DM system prompt 对齐主会话全量上下文 | `dotnet test --filter ChannelSessionDmScopeTests` | Completed |
| KC-5002 | Runtime | `ChannelSessionService` timeout 判断改为仅 Group 触发；DM session 无超期限制，连续性语义对齐主会话 | `dotnet test --filter ChannelSessionDmScopeTests` | Completed |
| KC-5003 | Runtime | DM 沙箱 `WorkingDirectory` 改为 `workspace root`；DM 工具列表追加 `workspace_protocol_update` + `workspace_memory_append` | `dotnet test --filter ChannelSessionDmScopeTests` | Completed |
| KC-5004 | ChannelHub | `ChannelTurnOrchestrator` 入口硬匹配 `/new`、`/clear`、`/reset`；命中后调 `IChannelSessionService.EvictSessionAsync`，直接回复"已开启新会话"，不进 agent；Telegram 和 WeChat 统一逻辑 | `dotnet test --filter SessionResetCommandTests` | Completed |
| KC-5005 | Tests | `ChannelSessionDmScopeTests`（L1，10 个）+ `SessionResetCommandTests`（L1，13 个）+ 既有集成测试更新（2 个）；新增失败 0 | `dotnet test KodaClaw.sln -m:1` | Completed |

## Iter 49 — 桌面安装包 + 首次启动引导（2026-03-24）

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-4901 | kodaclaw-desktop | `electron-builder.release.json`：mac dmg / win nsis / linux AppImage，`extraResources` 引用 `publish/gateway-${platform}` 目录 | `npm run typecheck`（desktop）| Completed |
| KC-4902 | kodaclaw-desktop | `resolveGatewayCommand()` 打包分支：`app.isPackaged` 时从 `process.resourcesPath/gateway/KodaClaw.Gateway[.exe]` 启动；`spawnManagedGateway()` 注入 `KODACLAW_WORKSPACE_ROOT` + 从 `~/.kodaclaw/.env` 读取并透传 API Key 环境变量 | `npm run typecheck` | Completed |
| KC-4903 | kodaclaw-desktop | `isFirstRun()` 检测（`~/.kodaclaw/` 不存在）；`app.whenReady()` 流程分叉：首次运行打开 onboarding 窗口，跳过 Gateway 启动；IPC handler `kodaclaw:setup-complete` 创建 workspace 目录结构、写 `~/.kodaclaw/.env`、关闭 onboarding、启动 Gateway + 主窗口 | `npm run typecheck` | Completed |
| KC-4904 | kodaclaw-desktop | `onboarding.html`（纯 HTML/CSS/JS）：provider 卡片选择（Anthropic/OpenAI）+ API Key 输入 + "开始使用"按钮；`preload.ts` 暴露 `window.kodaclawSetup.complete()` | 手动打开 onboarding.html 验证 UI | Completed |
| KC-4905 | Makefile | `package-gateway-{mac,mac-x64,win,linux}` dotnet publish 目标；`build-web-prod` 前端构建目标；`package-{mac,win,linux}` 完整打包流水线；`package` 自动检测当前平台；`.gitignore` 补 `publish/` | `make package-mac`（本地 macOS）| Completed |

## Iter 48 — Session 保留策略与存储管理（2026-03-24）

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-4801 | Contracts | `WorkspaceAppConfig` 新增 `AutoSessionRetentionDays`（默认 30）/ `AutoSessionRetentionMaxPerTask`（默认 20）；`StorageUsageResponse` + `SessionTypeUsage` DTO | `dotnet build` 0 错 | Completed |
| KC-4802 | Gateway | `SessionRetentionService`：扫描 `auto-*` 文件夹，按任务名分组，保留最近 N 次且不超过 D 天，跳过无 `meta.json` 的（执行中），删除其余；`SessionRetentionHostedService` 启动时 + 每天凌晨 3 点触发 | `dotnet test --filter SessionRetentionServiceTests` | Completed |
| KC-4803 | Gateway | `GET /api/system/storage-usage`（三类 session 数量 + 字节）；`DELETE /api/sessions/main/{id}`（禁删活跃 session，禁删非 main- 前缀） | `dotnet test --filter StorageUsageEndpointTests,DeleteMainSessionTests` | Completed |
| KC-4804 | kodaclaw-web | Settings Desk 新增"存储"分区：三类 session 用量展示；`main-*` 列表支持单条删除（活跃 session 置灰）；`auto-*` 显示自动清理策略说明 | `npm run typecheck` | Completed |
| KC-4805 | Tests | `SessionRetentionServiceTests`（L1，8 个全绿）+ `StorageUsageContractTests`（L3，4 个全绿）+ `DeleteMainSessionIntegrationTests`（L2，5 个全绿，含 token 验证）；修复 WeChat stub 预存失败（2 接口方法 + 1 类型错误） | `dotnet test KodaClaw.sln -m:1` | Completed |

## Iter 46 — 微信个人号渠道接入（2026-03-23）

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-4601 | Contracts | `ChannelConnectorKind.WeChat = 3`；`WeChatQrCodeResult` / `WeChatQrCodeStatus` records | `dotnet build` 0 错 | Completed |
| KC-4602 | ChannelHub | `WeChatApiContracts.cs` iLink DTO；`IWeChatApiClient` + `HttpWeChatApiClient`（长轮询、发消息、二维码、登录验证） | `dotnet build` 0 错 | Completed |
| KC-4603 | ChannelHub | `WeChatConnector`（长轮询主循环、游标持久化、消息去重、Markdown 剥离）+ `WeChatConnectorConfiguration` + `WeChatAuthManager` + `WeChatConnectorOptions` | `dotnet build` 0 错 | Completed |
| KC-4604 | Gateway + ChannelHub | `ServiceCollectionExtensions` 注册；`ChannelConnectorHostedService` + `ChannelInboundGatewayService` 加 WeChat arm；`/api/channels/wechat/get-qrcode`、`/api/channels/wechat/qrcode-status`、`/api/channels/test-wechat-credentials` 端点 | `dotnet test --filter WeChatApiIntegrationTests` | Completed |
| KC-4605 | kodaclaw-web | `contracts.ts` + `api.ts` 扩展；`WeChatQrLoginPanel.tsx`（扫码轮询流程）；`ChannelSetupWizard.tsx` 加微信分支；`ChannelsDesk.tsx` 加 Degraded 重新扫码入口 | `npm run typecheck` | Completed |
| KC-4606 | Tests | `WeChatConnectorConfigurationTests`（8 个 L1）+ `WeChatApiContractTests`（4 个 L3）+ `WeChatAuthApiIntegrationTests`（3 个 L2）；全量回归 预存失败不计 | `dotnet test KodaClaw.sln -m:1` | Completed |

## Iter 45 — 自动化渠道推送（2026-03-23）

| 条目 | 模块 | 用户 Outcome | 验证命令 | 状态 |
|------|------|-------------|---------|------|
| KC-4501 | Contracts | `AutomationDefinition` 加 `NotificationChannels` + `NotifyMode`；`IAutomationNotificationService` 接口 + `ChannelPushResult` record；枚举 `AutomationNotifyMode` | `dotnet build` 0 错 | Completed |
| KC-4502 | Workspace | `HeartbeatAutomationCompiler` 解析 `- channels:` 嵌套列表 + `- delivery-mode:`；8 个契约测试 | `dotnet test --filter HeartbeatAutomationCompilerContractTests` | Completed |
| KC-4503 | Automation/Storage | SQLite 迁移 `notification_channels` + `notify_mode` 列；Repository CRUD round-trip 测试 | `dotnet test --filter SqliteAutomationDefinitionRepositoryTests` | Completed |
| KC-4504 | Automation | `AutomationScheduler` Auto 模式执行后调 `IAutomationNotificationService.PushAsync`；Inbox PayloadJson 含 `channelPushResults` | `dotnet test --filter AutomationSchedulerIntegrationTests` | Completed |
| KC-4505 | Gateway + ChannelHub | `AutomationNotificationService` 实现；`POST /api/inbox/{id}/push-to-channel` 端点 | `dotnet test --filter AutomationNotificationEndpointTests` | Completed |
| KC-4506 | kodaclaw-web | `contracts.ts` + `api.ts` 扩展；AutomationsDesk 渠道 tag；InboxApprovalDesk 推送状态/按钮；ChannelsDesk 复制 BindingId | `npm run typecheck` | Completed |
| KC-4507 | Tests | 全量测试回归，所有新增测试调用补 `NotificationChannels: null, NotifyMode: AutomationNotifyMode.None` | `dotnet test KodaClaw.sln -m:1` | Completed |

## Epic A：产品工程初始化

- 建立 `products/KodaClaw/` 独立目录与命名空间
- 建立独立 `KodaClaw.sln`
- 配置公共 `Directory.Build.props`
- 定义共享 contracts 与基础包引用
- 建立文档与 ADR 目录

## Epic B：Workspace 引导与协议

- 实现 `~/.kodaclaw` 初始化器
- 生成默认 `AGENTS.md` / `IDENTITY.md` / `SOUL.md`
- 生成 `BOOTSTRAP.md` 与首次启动标记
- 定义主会话 / 渠道会话 / 自动化会话的加载规则
- 定义 `MEMORY.md` 与日记式 memory 写入策略
- 定义 `HEARTBEAT.md` 到 automation 的映射规则

## Epic C：Gateway 基础设施

- 建立本地 loopback-only ASP.NET Core 服务
- 增加 token 认证
- 增加健康检查与版本接口
- 增加 chat / sessions / approvals API 框架
- 增加 SSE 输出通道
- 增加统一错误模型
- `2026-03-20` 维护性重构进度：Gateway `Program.cs` Wave 0 ~ Wave 4 已全部收口，`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Gateway/Program.cs` 现仅 45 行；models/settings/automations/plugins/canvas/approvals/sessions/inbox/channels/system/chat/diagnostics/root endpoints 均已迁入独立 partial，validation / mapping / infrastructure helpers 也已分层落位，并通过 Gateway 定向集成回归与最新 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 验证。

## Epic D：Runtime 组合层

- 实现 `SessionKind` 与 Agent 工厂
- 复用 SDK 的 `JsonAgentStore`
- 按 session type 注入不同 Workspace 上下文
- 把 SDK event stream 映射为产品层 timeline event
- 把审批状态映射到 Control Plane
- 加入 session pool 与恢复策略

## Epic E：Control Plane

- 定义 InboxItem、Approval、DiagnosticEvent 数据模型
- 实现审批仓储与 API
- 实现 Inbox API
- 实现 Diagnostics timeline 查询
- 实现 settings API
- 实现 session 详情 API

## Epic F：Web 控制台

- 搭建基础路由与壳布局
- 聊天页
- Inbox 页
- Sessions 页
- Workspace 浏览页
- Models / Plugins / Channels / Automations 页面骨架
- Diagnostics 页面
- `2026-03-20` 前端两轮收口已完成：`kodaclaw-web` 已切到新的 shell / workbench 视觉体系，默认语言切到 `zh-CN`，支持运行时切换 `en-US`，并已通过 `npm run build && npm run test && npm run test:e2e` 验证。

## Web V2：2026-03-20 Live Dogfood 收口

- `KC-W2-001`：`Completed`。Canvas 默认预览与选中 artifact 预览已切到 Gateway 签发的 path-based preview URL：`/api/canvas/default` 与新增的 `/api/canvas/{id}/entry` 现在都会返回可直接供 iframe 使用的 `entryUrl`，并通过 `CanvasApiIntegrationTests`、`src/__tests__/canvas-desk.spec.tsx`、`npm run typecheck` 与 `npm run build` 验证；`tests/kc0309-canvas.spec.ts` 已改为在当前 app shell 尚未把 `CanvasDesk` 接到 live canvas surface 时条件跳过，避免把壳层接线缺口误判为 preview 回归。
- `KC-W2-002`：`Completed`。SDK `Agent.Send()` 后台处理路径在 chat completed 后现会把最终 `BreakpointState.Ready` 持久化回 store，`/api/sessions` 不再残留 `StreamingModel`；已通过新增 runtime/gateway 回归测试与定向 integration suite 验证。
- `KC-W2-003`：`Completed`。已在现有 `kodaclaw-web` 内落地 `shell-shared/`、`shell-v1/` 与 `shell-v2/` 结构，并通过默认 `shell-v2` + `?shell=v1` / `localStorage["kodaclaw.shellVariant"]` override 接入新的三段式壳层，不新建第二套 web app；`App.tsx` 现会复用同一份 `workbench` / `contextPanel` view-model，在 `LegacyShell` 与 `V2Shell` 间切换，同时保持 `desk-tab-*` test ids 稳定。已通过 `npm run typecheck`、`npm run test -- src/__tests__/chat-context-rail.spec.tsx src/__tests__/sessions-diagnostics-desk.spec.tsx src/__tests__/app-shell.spec.tsx` 与 `npm run build` 验证。
- `KC-W2-004`：`In Progress`。V2 chat 路径继续收口为 conversation-first：`ContextRail` 在 `mainDesk === "chat"` 时会切到 session pulse 面板，加载 `/api/sessions`、高亮当前主会话，并把最近轨迹保持在聊天主舞台旁边；`MainStage` 也已切到 chat-specific 紧凑头部，把 active session / gateway / health / workspace 收敛为 dense stage chips，并把 timeline + composer 拉伸成更像 operator canvas 的主舞台。当前又补上了一轮视觉校准：chat 模式下 context hero 不再重复放大 `对话航道` 标题，而改为更轻的 `会话脉络` 辅助标题；shell spacing / radius / shadow / card density 已整体收紧；同时移动端顺序也已修正为 `GlobalRail -> MainStage -> ContextRail`，避免聊天主舞台在窄屏被上下文支持区压到首屏以下。当前切片已通过 `npm run typecheck`、`npm run test -- src/__tests__/chat-context-rail.spec.tsx src/__tests__/sessions-diagnostics-desk.spec.tsx src/__tests__/app-shell.spec.tsx`、`npm run build`、`npm run test:e2e -- tests/kc0213-control-plane-acceptance.spec.ts`，并通过 `agent-browser` 完成桌面/移动端布局复核；后续仍需继续让主舞台减少旧 page-like 痕迹，并决定下一个优先迁移成 list-detail pane 的 desk。
- `KC-W2-005`：`In Progress`。Pane 迁移已扩展到前两块高频 desk。`SessionsDiagnosticsDesk` 现为 session rail + focused detail stage + timeline/export sibling panels，并补上 selection hydration guard，切换 session 时不再先闪出旧 summary / timeline；`InboxApprovalDesk` 也已改成左侧 stacked inbox/approval queues、右侧 inbox focus / approval focus 双 detail panel，并在存在关联关系时联动选中对象，同时保留 `approval-approve` / `approval-reject` / `inbox-status-*` 等稳定 test ids。当前切片已通过 `npm run typecheck`、`npm run test -- src/__tests__/inbox-approval-desk.spec.tsx src/__tests__/chat-context-rail.spec.tsx src/__tests__/sessions-diagnostics-desk.spec.tsx src/__tests__/app-shell.spec.tsx`、`npm run build` 与 `npm run test:e2e -- tests/kc0210-inbox-approval.spec.ts tests/kc0213-control-plane-acceptance.spec.ts` 验证。
- `KC-W2-006`：`In Progress`。`ModelsSettingsDesk` 已完成第一刀 pane 化：左侧 `models-list` 保持 `li` 结构与现有 `model-default` / `model-delete-*` selectors，不破坏现有 Playwright 依赖；右侧则新增 focused model detail/composer stage，默认会自动聚焦 default/first endpoint，并补上 `settings-sections` stage index 指向 runtime/update/risk panels。为避免一次性打碎现有验收流，`settings-form`、`settings-update-watch` 与 `settings-risk-briefing` 目前仍全部挂载可见，但已经收进统一的右侧 stage 栈。当前切片已通过 `npm run typecheck`、`npm run test -- src/__tests__/models-settings-desk.spec.tsx src/__tests__/inbox-approval-desk.spec.tsx src/__tests__/sessions-diagnostics-desk.spec.tsx src/__tests__/chat-context-rail.spec.tsx src/__tests__/app-shell.spec.tsx`、`npm run build` 与 `npm run test:e2e -- tests/kc0212-models-settings.spec.ts tests/kc0213-control-plane-acceptance.spec.ts` 验证。
- 下一步：继续推进 `KC-W2-004` 的 chat 主舞台收口，并为 `KC-W2-007` 选择下一个最值得 pane 化的 desk（优先 `Channels / Plugins / Automations / Canvas` 中的高频面）。

## Epic G：Model Hub

- provider registry 数据模型
- custom endpoint CRUD
- default model policy
- primary / fallback route
- model capability 标签
- endpoint health check

## Epic H：Automation Engine

- automation definition 数据模型
- durable scheduler
- run history
- retry policy
- Inbox result 投递
- `HEARTBEAT.md` 编译流程

## Epic I：Canvas

- Canvas artifact 数据模型
- Canvas 页面注册
- artifact 与 thread/task 绑定
- index / state 管理
- Web UI 嵌入式渲染

## Epic J：Plugin Host

- plugin manifest loader
- plugin registry
- plugin state machine
- stdio / HTTP MCP 启动器
- plugin log capture
- plugin permissions UI

## Epic K：Channel Hub

- channel connector 抽象
- Telegram connector
- generic webhook connector
- thread binding
- delivery rule
- channel policy
- inbound/outbound 审计

## Epic L：Desktop Shell

- Electron 外壳工程
- tray
- notification bridge
- deep-link 到 chat / inbox / canvas
- gateway 启停协调
- 自动升级方案预研

## Epic M：Security 与稳定性

- Keychain 集成
- device identity 完整化
- backup / restore
- crash recovery
- plugin trust model
- sandbox policy UI 提示

## 第一批推荐启动任务

按实施顺序，建议先做下面 12 个任务：

1. 建立 `KodaClaw.sln` 与核心项目骨架 `Completed`
2. 实现 `KodaClaw.Contracts` `Completed`
3. 实现 `KodaClaw.Workspace` 初始化器 `Completed`
4. 实现 `KodaClaw.Gateway` 最小服务 `Completed`
5. 实现 `KodaClaw.Runtime` 的 `main` session `Completed`
6. 实现 `chat` SSE API `Completed`
7. 实现 `kodaclaw-web` 的聊天页 `Completed`
8. 接入 session 恢复 `Completed`
9. 实现 `BOOTSTRAP.md` 驱动的首次启动流程 `Completed`
10. 实现 diagnostics 基线 `Completed`
11. 实现迭代 1 端到端验收包 `Completed`
12. 实现 Control Plane Inbox 基线 `Completed` *(KC-0201: Inbox contracts + `SqliteInboxRepository` + workspace-local `config/control-plane.db`)*

## 迭代 2：Control Plane Beta

- KC-0201：`Completed`。`InboxItem` / `InboxItemKind` / `InboxItemStatus` / `InboxQuery` / `IInboxRepository` 已落地，`SqliteInboxRepository` 在工作区根目录下初始化 `config/control-plane.db`，并通过 unit / integration / solution 三级验证。
- KC-0202：`Completed`。`Approval` / `ApprovalKind` / `ApprovalStatus` / `ApprovalQuery` / `IApprovalRepository` 已落地，`SqliteApprovalRepository` 复用工作区 `config/control-plane.db`，支持 `Pending -> Approved/Rejected/Canceled` 的状态流转规则，并通过 unit / integration / solution 三级验证。
- KC-0203：`Completed`。`MainSessionService` 已接入 SDK 原生 approval flow：高风险 tool 会落 `Approval` + `InboxItem`，live session 内通过同一 agent 实例继续执行；在 crash-style resume 后，未完成审批会被标记为 stale/canceled，并通过 runtime integration + solution 验证。
- KC-0204：`Completed`。Gateway 已提供 `/api/inbox`、`/api/inbox/{id}`、`PATCH /api/inbox/{id}/status`，支持过滤、详情与状态更新，并通过 contract / integration / solution 三级验证。
- KC-0205：`Completed`。Gateway 已提供 `/api/approvals`、`/api/approvals/{id}`、`POST /api/approvals/{id}/approve`、`POST /api/approvals/{id}/reject`；其中 approve 依赖 live session continuation，reject 在 live session 已丢失时可直接终态化 pending approval，并通过 contract / integration / solution 三级验证。
- KC-0206：`Completed`。Gateway 已提供 `/api/sessions`、`/api/sessions/{id}`，基于 `JsonAgentStore` 的 `meta.json` / messages / tool-calls 组装 session summary/detail，并通过 integration / solution 验证。
- KC-0207：`Completed`。Gateway 已提供 `/api/diagnostics/recent` 与 `/api/diagnostics/timeline` 的统一查询管道，支持 `correlationId` / `sessionId` / `source` / `eventType` / `level` / `limit` 过滤，并通过 unit / contract / integration / solution 验证。
- KC-0208：`Completed`。`ModelProviderKind` / `ModelEndpoint` / `CreateModelEndpointRequest` / `UpdateModelEndpointRequest` / `ModelsQueryResponse` / `IModelRegistryRepository` 已落地，`SqliteModelRegistryRepository` 复用工作区 `config/control-plane.db` 持久化 `model_endpoints`，Gateway 已提供 `/api/models`、`/api/models/{id}`、`POST /api/models`、`PUT /api/models/{id}`、`DELETE /api/models/{id}`、`POST /api/models/{id}/default`，并通过 contract / unit / integration / solution + web baseline 全量验证。
- KC-0209：`Completed`。`KodaClawSettings` / `ThemeMode` / `ISettingsRepository` 已落地，`SqliteSettingsRepository` 复用工作区 `config/control-plane.db`，提供默认快照加载、覆盖保存与 quiet-hours 校验，并通过 unit / integration / solution 三级验证。
- KC-0210：`Completed`。`kodaclaw-web` 主工作台已集成 `InboxApprovalDesk`，提供 inbox/approval 双列表、状态筛选、approve/reject 决策与回流刷新，且通过 component tests + `tests/kc0210-inbox-approval.spec.ts` + 全量 `npm run test:e2e` 验证。
- KC-0211：`Completed`。`kodaclaw-web` 主工作台已集成 `SessionsDiagnosticsDesk`，支持 session 切换、detail / diagnostics timeline 联动和刷新，并通过 component tests + `tests/kc0211-sessions-diagnostics.spec.ts` + 全量 `npm run test:e2e` 验证。
- KC-0212：`Completed`。`kodaclaw-web` 主工作台已集成 `ModelsSettingsDesk`，支持模型端点 CRUD / default 切换与工作区设置保存；主线程补齐了 `GET /api/settings` / `PUT /api/settings` Gateway API，并通过 `SettingsApiIntegrationTests` + component tests + `tests/kc0212-models-settings.spec.ts` + 全量 solution/web 验证。
- KC-0213：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/Smoke/Iteration2AcceptanceIntegrationTests.cs` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web/tests/kc0213-control-plane-acceptance.spec.ts`，把 approval pause/decide、inbox 回流、sessions/diagnostics 审计、settings 保存串成 Iteration 2 验收基线，并补充 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/ITERATION_2_ACCEPTANCE_PACK.md` 文档；已通过定向 + 全量 solution/web 回归。
- `KC-0301`：`Completed`。`AutomationDefinition` / `AutomationSchedule` / `AutomationRunRecord` / `IAutomationDefinitionRepository` / `IAutomationRunRepository` 已落地，`KodaClaw.Automation` 复用工作区 `config/control-plane.db` 持久化 `automation_definitions` 与 `automation_runs`，并通过 automation unit + contract + solution 三级验证。
- `KC-0302`：`Completed`。`KodaClaw.Automation` 已落地可控时钟与 durable scheduler 基线：默认禁用的 hosted scheduler、`RunOnceAsync` / `TickAsync` 手动入口、`FakeAutomationClock`、stale `Queued/Running` run 恢复失败与 `FailureRetryDelay` 重试语义，并通过 scheduler integration + solution 回归验证。
- `KC-0303`：`Completed`。`HeartbeatAutomationCompiler` 已落地，支持冻结 schedule 文法（`hourly 2h` / `daily 09:00` / `weekdays 09:00` / `weekly mon,wed,fri 18:30`），输出共享 `AutomationDefinition` contract，生成 deterministic id，规范化 workspace-relative `inputs` 并拒绝 path traversal，通过 parser contract tests + solution 验证。
- `KC-0304`：`Completed`。`AutomationSessionService` 已落地，提供 `SessionKind.Automation` 会话句柄、最小上下文加载（`AGENTS.md` / `IDENTITY.md` / `SOUL.md` / `USER.md` / `HEARTBEAT.md` + `InputPaths`），并支持把规范化相对路径解析到 `workspace/` 下，通过 runtime integration + solution 验证。
- `KC-0305`：`Completed`。Automation 调度完成后会稳定 upsert `InboxItemKind.AutomationResult`，固定 id 规则 `automation-result-<runId>`、来源 `automation.scheduler`、路由 `/automations/<automationId>`，并在 payload 中携带 `automationId` / `runId` / `status` / `summary` / `errorMessage`，通过 scheduler integration + solution 验证。
- `KC-0306`：`Completed`。`CanvasArtifactKind` / `CanvasArtifact` / `CanvasArtifactQuery` / `ICanvasArtifactRepository` 已落地，`SqliteCanvasArtifactRepository` 复用工作区 `config/control-plane.db` 初始化 `canvas_artifacts` 表，支持最小 CRUD/List 过滤，并强制 `EntryPath` / `AssetDirectory` 为 `workspace/canvas` 下的 workspace-relative 路径；已通过 canvas repo unit tests + solution 回归验证。
- `KC-0307`：`Completed`。Gateway 已提供 `/api/automations`、`/api/automations/{id}`、`/api/automations/{id}/runs`、`PATCH /api/automations/{id}`、`/api/canvas`、`/api/canvas/default`、`/api/canvas/{id}`、`POST /api/canvas`、`/api/canvas/fs/{**path}`，并通过新增 contract + integration tests、Iteration 3 smoke 与 solution 回归验证。
- `KC-0308`：`Completed`。`kodaclaw-web` 主工作台已集成 `AutomationsDesk`，支持列表、过滤、详情、recent runs、enable/disable、refresh，并通过 component tests + `tests/kc0308-automations.spec.ts` + 全量 `npm run test:e2e` 验证。
- `KC-0309`：`Completed`。`kodaclaw-web` 主工作台已集成 `CanvasDesk`，支持 artifact 列表/过滤、default entry、iframe 预览、metadata 侧栏与空状态 fallback，并通过 component tests + `tests/kc0309-canvas.spec.ts` + 全量 `npm run test:e2e` 验证。
- `KC-0310`：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/Smoke/Iteration3AcceptanceIntegrationTests.cs` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/ITERATION_3_ACCEPTANCE_PACK.md`，把 heartbeat -> automation run -> inbox result -> canvas publish/query/fs 串成 Iteration 3 验收基线，并通过定向 + 全量 solution/web 回归。
- 下一波：转入 Iteration 4 规划，优先冻结 plugin/channel/desktop 的 contract 与写集，再决定新的并行波次。

## 迭代 4：Plugin Platform（Wave 0/1/2/3/4 已完成）

- Wave 0：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/ITERATION_4_FREEZE.md` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/adr/ADR-0008-plugin-platform-v1.md`，冻结了 Iteration 4 的范围、contract surface、验收门槛与 subagent 写集。
- 范围冻结：Iteration 4 v1 只要求 `tool` 插件跑通完整运行链路；`channel` / `memory` / `ui` 类型本期只要求 manifest 可识别、registry/Web 可展示，不进入 host/runtime 验收主路径。
- 传输冻结：首期只强制 `stdio` MCP transport；`http` / `streamableHttp` / `sse` 允许保留类型定义，但不计入本期退出标准。
- 授权冻结：插件首次 trust 统一复用 `ApprovalKind.PluginAuthorization` + `InboxItemKind.PluginRequest`，不新起单独插件审批系统。
- Wave 1：`Completed`。主线程先落 shared plugin contracts / test project references，再通过 subagents 并行完成 manifest + permissions + registry 三块实现，最终 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 全绿收口。
- Wave 2：`Completed`。主线程整合 `plugin-runtime-worker` 的 hosting / diagnostics 结果，补齐 `AddKodaClawPluginHost()`、stdio fixture server 与 plugin host integration tests，最终再次通过 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 回归。
- Wave 3：`Completed`。主线程补齐 `/api/plugins/*` Gateway contract / service / integration 面，并整合 runtime tool injection 与 `kodaclaw-web` plugin manager desk；本波已通过 `PluginApiContractsTests`、`PluginToolInjectionIntegrationTests|PluginApiIntegrationTests`、`npm run typecheck`、`npm run build`、`npm test`、`npx playwright test tests/kc0407-plugins.spec.ts` 与 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 收口。
- `KC-0401`：`Completed`。`PluginManifest` shared contract、manifest loader / normalizer / validator 已落地，覆盖 `plugin.json` v1 round-trip、stdio valid case、required fields / invalid id / empty tool capabilities 等 contract tests，并通过定向 contract tests + solution regression。
- `KC-0402`：`Completed`。`SqlitePluginRegistryRepository` 已落地，复用工作区 `config/control-plane.db` 初始化 `plugins` 表，支持 upsert / get / list(filter) / delete，并通过 unit tests + solution regression。
- `KC-0403`：`Completed`。`PluginLifecycleHost`、`IPluginLifecycleHost`、`PluginHostOptions` 与 `AddKodaClawPluginHost()` 已落地，支持 stdio plugin start / stop / restart / health probe 前置接入、namespaced tool catalog 输出与失败隔离；并通过 `PluginLifecycleHostIntegrationTests` + solution regression。
- `KC-0404`：`Completed`。`PluginPermissionPolicy` 已落地，支持 permission normalization、risk summary、path/token validation，并通过定向 contract tests + solution regression。
- `KC-0405`：`Completed`。`MainSessionService` 已在 fresh main session 创建路径注入 `Trusted + Enabled + Running + Tool` 的 namespaced plugin tools，并确保 stop / degraded 插件不会继续出现在新 session 的有效工具列表中；已通过 `PluginToolInjectionIntegrationTests`、定向 plugin API 集成验证与 solution regression。
- `KC-0406`：`Completed`。`SqlitePluginLogRepository`、plugin health / degraded 监督链路与 stdio fixture health drill 已落地，支持 per-plugin logs、restart count、diagnostics evidence，并通过 `SqlitePluginLogRepositoryTests`、`PluginHealthIntegrationTests` 与 solution regression。
- `KC-0407`：`Completed`。`kodaclaw-web` 已集成 `PluginsDesk`，支持列表 / 过滤、详情、权限摘要、日志证据、install / discover、trust / enable / disable / start / stop 与 settings 占位，并通过 component tests、`tests/kc0407-plugins.spec.ts`、`npm run build` 与 solution-level regression。
- `KC-0408`：`Completed`。已新增 bundled fixture plugin 清单 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/Fixtures/Plugins`、集成 smoke `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/Smoke/Iteration4AcceptanceIntegrationTests.cs` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/ITERATION_4_ACCEPTANCE_PACK.md`，覆盖 bundled discover -> trust/enable/start -> fresh-session injection -> stop/degraded isolation 链路，并通过定向 plugin contract/integration/frontend 回归、`npm run test:e2e` 与 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1`。
- 下一波：Iteration 4 已完整收口；Iteration 5 的 channels 范围现已冻结，下一步进入 Wave 1 foundation，再把 desktop / trust model 留给后续切片。

## 迭代 5：Channels（Wave 0/1/2/3/4 已完成）

- Wave 0：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/ITERATION_5_FREEZE.md` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/adr/ADR-0009-channels-v1.md`，冻结了 Iteration 5 的范围、connector 策略、session / memory 边界、delivery rule、最小 API 面与并行写集。
- 范围冻结：Iteration 5 v1 只要求两条 connector 路径进入验收：一个真实 `telegram` connector 和一个合成 `generic-webhook` connector；其余渠道继续留在后续切片或 plugin 化阶段。
- 架构冻结：channels 长期仍是 plugin-capable domain，但 Iteration 5 的首批 connector 由 `KodaClaw.ChannelHub` 直接托管，不要求复用 Iteration 4 尚未冻结的 `channel` plugin runtime。
- 会话冻结：所有外部消息只能进入 `SessionKind.ChannelDirectMessage` 或 `SessionKind.ChannelGroup`；不允许落入 `main` session，也不允许群聊读取主记忆。
- 交付冻结：outbound 统一走 `DeliveryRule`，并复用 `ApprovalKind.ChannelDelivery` + `InboxItemKind.ChannelUpdate`，不新增独立渠道审批栈。
- `KC-0501`：`Completed`。已新增 channel shared contracts：`ChannelConnectorKind`、`ChannelAccountState`、`ChannelThreadType`、`ChannelEventType`、`DeliveryMode`、`ChannelEventEnvelope`、`ChannelThreadDetail`、`IChannelConnector` 等冻结对象，并补充 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.ContractTests/Channels/ChannelContractsTests.cs`；已通过定向 contract tests、`dotnet build /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.ChannelHub/KodaClaw.ChannelHub.csproj` 与 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 验证。
- `KC-0502`：`Completed`。已落地 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.ChannelHub/SqliteChannelHubDatabase.cs`、`SqliteChannelAccountRepository.cs`、`SqliteThreadBindingRepository.cs` 与 `AddKodaClawChannelHub()`，把 `channel_accounts` / `thread_bindings` 两张表纳入 workspace-local `config/control-plane.db`；并通过 `SqliteChannelAccountRepositoryTests`、`SqliteThreadBindingRepositoryTests` 与 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 验证。
- `KC-0503`：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.ChannelHub/ChannelPolicyEngine.cs`、`ChannelPolicyDecision.cs` 与 `tests/KodaClaw.UnitTests/ChannelHub/ChannelPolicyEngineTests.cs`，冻结了 DM / group 默认 policy、mute 判定、direct-reply 判定与 memory boundary（群聊不读 `USER` / 长期记忆，channel v1 不读 `MEMORY.md`）；并通过定向 unit tests 与 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 验证。
- `KC-0504`：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.ChannelHub/ChannelDeliveryGovernanceService.cs`、`ChannelDeliveryDisposition.cs`、`ChannelDeliveryEvaluationResult.cs` 与 `tests/KodaClaw.IntegrationTests/ChannelHub/ChannelDeliveryGovernanceIntegrationTests.cs`，把 `DeliveryRule` 接入现有 approval / inbox / diagnostics 基线，形成 `AutoSend` 直发与 `DraftApproval` / `RequireApproval` 落 `ApprovalKind.ChannelDelivery` + `InboxItemKind.ChannelUpdate` 的最小治理闭环；并通过定向 integration tests 与 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 验证。
- `KC-0505`：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.ChannelHub/Connectors/Telegram/TelegramConnector.cs`、`TelegramConnectorConfiguration.cs`、`TelegramConnectorOptions.cs`、`TelegramApiContracts.cs`、`ITelegramApiClient.cs`、`HttpTelegramApiClient.cs` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/ChannelHub/TelegramConnectorIntegrationTests.cs`，并由主线程补齐 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.ChannelHub/ServiceCollectionExtensions.cs` 的 connector 注册与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Gateway/Program.cs` 的 connector 能力标记；已通过 `dotnet build /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.ChannelHub/KodaClaw.ChannelHub.csproj`、`dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/KodaClaw.IntegrationTests.csproj --filter TelegramConnectorIntegrationTests` 与最新 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 验证。
- `KC-0506`：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.ChannelHub/Connectors/Webhook/GenericWebhookConnector.cs`、`GenericWebhookPayloadParser.cs`、`GenericWebhookConnectorConfiguration.cs`、`GenericWebhookInboundDispatchResult.cs` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/ChannelHub/GenericWebhookConnectorIntegrationTests.cs`，并由主线程补齐 `/api/channels/accounts`、`/api/channels/webhook/{accountId}/events`、`ChannelEventIngestionService` 与 thread/detail 查询整合；已通过定向 contract/integration tests、`dotnet build /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Gateway/KodaClaw.Gateway.csproj` 与最新 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 验证。
- `KC-0507`：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Runtime/IChannelSessionService.cs`、`ChannelSessionService.cs`、`ChannelSessionOptions.cs` 与 runtime DI 注册，把 `channel-dm` / `channel-group` session 组合层接入 `KodaClaw.Runtime`；当前会按 `ChannelPolicy` 保守加载 `AGENTS.md` / `IDENTITY.md` / `SOUL.md`、私聊 `USER.md`、以及 `workspace/channels/<bindingId>/SUMMARY.md`（若存在），同时拒绝把群聊升级为读取 `USER.md` / `MEMORY.md`。主线程还补齐了 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Gateway/Program.cs` 的 session-kind 推断与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/Gateway/GatewaySessionsIntegrationTests.cs` 的覆盖；已通过 `dotnet build /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Runtime/KodaClaw.Runtime.csproj`、`dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/KodaClaw.IntegrationTests.csproj --filter "ChannelSessionServiceIntegrationTests|GatewaySessionsIntegrationTests|ChannelApiIntegrationTests"` 与最新 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 验证。
- `KC-0508`：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web/src/components/ChannelsDesk.tsx`，并补齐 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web/src/types/contracts.ts` 的 channels DTO、`src/lib/api.ts` 的 `/api/channels/*` fetch helper、`src/__tests__/channels-desk.spec.tsx`、`src/__tests__/app-shell.spec.tsx` 与 `tests/kc0508-channels.spec.ts`；当前主工作台已能查看 connector/account 概览、thread 列表筛选、policy、delivery rule、pending approval/draft 摘要与 recent audit。已通过 `npm run build`、`npm run test`、`npx playwright test tests/kc0508-channels.spec.ts` 与最新 `npm run test:e2e` 验证。
- `KC-0509`：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.ChannelHub/Audit/SqliteChannelAuditRepository.cs`、`ChannelAuditQueryService.cs` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.UnitTests/ChannelHub/SqliteChannelAuditRepositoryTests.cs`，并由主线程补齐 `/api/channels/threads/{bindingId}/audit`、thread detail recent-audit 聚合与 webhook correlation diagnostics；已通过 ChannelHub unit slice、`ChannelApiIntegrationTests` 与最新 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 验证。
- Wave 4：`Completed`。主线程补齐 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.ChannelHub/ChannelDeliveryApprovalService.cs`、`ChannelDeliveryApprovalDispatchResult.cs`、`tests/KodaClaw.IntegrationTests/ChannelHub/ChannelDeliveryApprovalIntegrationTests.cs` 与 Gateway approve/reject dispatch wiring，把 `ApprovalKind.ChannelDelivery` 从草稿落库真正收口到 approve -> connector outbound / reject -> no-send + audit 闭环；随后新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/Smoke/Iteration5DirectMessageAcceptanceIntegrationTests.cs`、`Iteration5GroupSafetyAcceptanceIntegrationTests.cs` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/ITERATION_5_ACCEPTANCE_PACK.md`，并通过定向 acceptance smoke、`dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1`、`npm run build`、`npm run test`、`npm run test:e2e` 收口。
- `KC-0510`：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/Smoke/Iteration5DirectMessageAcceptanceIntegrationTests.cs`，冻结 Telegram DM -> binding reuse -> isolated `ChannelDirectMessage` session -> `USER.md` + thread summary prompt enrichment -> approval approve -> Telegram outbound + audit evidence 的整条验收链路；并通过 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/KodaClaw.IntegrationTests.csproj --filter "ChannelDeliveryApprovalIntegrationTests|Iteration5DirectMessageAcceptanceIntegrationTests"`、`dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/ITERATION_5_ACCEPTANCE_PACK.md` 验证。
- `KC-0511`：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/Smoke/Iteration5GroupSafetyAcceptanceIntegrationTests.cs`，冻结 group inbound -> binding reuse -> isolated `ChannelGroup` session -> 不读取 `USER.md` / `MEMORY.md` -> `RequireApproval` reject -> no outbound + audit evidence 的安全验收链路；并通过 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/KodaClaw.IntegrationTests.csproj --filter Iteration5GroupSafetyAcceptanceIntegrationTests`、`dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/ITERATION_5_ACCEPTANCE_PACK.md` 验证。
- 下一波：Iteration 5 已完整收口；随后已完成 Iteration 6 Desktop Shell 的 Wave 0 规划冻结，当前转入 Wave 1 foundation 准备阶段。

## 迭代 6：Desktop Shell（Wave 0 已完成）

- Wave 0：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/ITERATION_6_FREEZE.md` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/adr/ADR-0010-desktop-shell-v1.md`，冻结了 Iteration 6 的桌面壳范围、runtime config bridge、Gateway attach-or-launch 边界、通知与 launch target 复用策略，以及分波写集。
- Wave 1：`Completed`。主线程已收口 `kodaclaw-desktop` Electron 工程骨架、`BrowserWindow` dev/prod/placeholder 加载策略、preload bridge，以及 `kodaclaw-web` 对 desktop runtime config / launch target 的适配；本波已通过 `cd /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-desktop && npm run typecheck && npm run build && npm run package:smoke`、`cd /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web && npm run build && npm run test && npm run test:e2e` 与 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 收口。
- Wave 2：`Completed`。主线程已收口 `AttachOnly` / `ManagedChild` Gateway 生命周期桥、managed child 健康等待与 restart、tray/menu、hide-on-close 恢复语义、全局快捷键，以及 `npm run smoke:managed-gateway` 的桌面 smoke 验证；本波已通过 `cd /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-desktop && npm run smoke:managed-gateway && npm run package:smoke`、`cd /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web && npm run build && npm run test && npm run test:e2e` 与 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 收口。
- Wave 3：`Completed`。主线程已收口基于 `/api/settings` + `/api/approvals` + `/api/inbox` 的通知轮询、quiet-hours 复用、route/protocol/startup-arg 到 launch-target 的解析，以及 `smoke:wave3` 的 fixture-based 桌面 smoke；本波已通过 `cd /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-desktop && npm run test && npm run smoke:wave3 && npm run smoke:managed-gateway && npm run package:smoke`、`cd /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web && npm run build && npm run test && npm run test:e2e` 与 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 收口。
- Wave 4：`Completed`。`KC-0607` 的 macOS-first packaging smoke 与 `KC-0608` 的 acceptance pack 已全部收口；Iteration 6 当前已通过 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/ITERATION_6_ACCEPTANCE_PACK.md` 固化最终回归矩阵、dogfood drill 与退出标准。
- 范围冻结：Iteration 6 v1 的核心不是做第二套产品前端，而是让 `kodaclaw-desktop` 作为 Electron shell 承载现有 `kodaclaw-web`，并把主窗口、tray、通知、deep-link、Gateway 生命周期桥做实。
- 架构冻结：继续坚持 `desktop is shell, gateway is brain`；Electron 主进程只负责窗口、系统集成、Gateway 附着/启动与运行时配置，不承载 SDK runtime 或业务状态事实源。
- 配置冻结：renderer 不再只依赖 Vite 编译时变量读取 Gateway 配置；desktop 必须通过 preload/runtime bridge 注入 `gatewayUrl`、`gatewayToken`、平台信息和 launch target。
- 通知冻结：桌面通知继续复用现有 `Inbox` / `Approvals` / `Settings` / quiet hours，不新建独立桌面通知数据库或通知控制面。
- 平台冻结：首个强验收平台优先是 macOS；同时尽量保持代码与脚本跨平台中立。
- `KC-0601`：`Completed`。已建立 `kodaclaw-desktop` Electron 工程骨架、开发脚本、BrowserWindow、preload 与基础 packaging smoke，并通过 `npm run typecheck` / `npm run build` / `npm run package:smoke` + 最新 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1`。
- `KC-0602`：`Completed`。desktop 已支持 `AttachOnly` / `ManagedChild` 两种 Gateway 生命周期模式，包含 managed child `dotnet run` 启动、健康等待、restart 与退出协调，并通过 `npm run smoke:managed-gateway` + 最新 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1`。
- `KC-0603`：`Completed`。web renderer 已支持从 preload 获得 `gatewayUrl` / `gatewayToken` / `initialTarget` / `gatewayLifecycleMode`，并通过 desktop bridge 单测、全量 web build/test/e2e 与最新 solution 回归。
- `KC-0604`：`Completed`。桌面壳已补齐 tray/menu 入口、`Show KodaClaw` / `Open Chat` / `Open Inbox` / `Open Canvas` / `Open Channels` / `Restart Gateway` / `Quit` 菜单、hide-on-close 恢复、以及 `CommandOrControl+Shift+K` 全局快捷键，并通过桌面 smoke + packaging smoke 收口。
- `KC-0605`：`Completed`。桌面壳已实现 `/api/settings` + `/api/approvals` + `/api/inbox` 的通知轮询、quiet-hours 抑制、approval/inbox 去重与 notification click -> launch target 路由，并通过 pure desktop tests + `smoke:wave3` 收口。
- `KC-0606`：`Completed`。桌面壳已支持 startup args（`--kodaclaw-route=` / `--kodaclaw-target=`）、`kodaclaw://...` protocol payload、single-instance 二次启动转发，以及 route -> desk/entityId 映射，并通过 `smoke:wave3` + `smoke:managed-gateway` 收口。
- `KC-0607`：`Completed`。已通过 `cd /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-desktop && npm run package:smoke` 完成 macOS-first `electron-builder --dir` 打包 smoke，并确认关闭自动签名探测后可稳定收口。
- `KC-0608`：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/ITERATION_6_ACCEPTANCE_PACK.md`，冻结桌面 dogfood drill、回归矩阵、通知/launch-target 验收与最终退出标准，并与状态文档同步。
- 下一波：Iteration 6 Desktop Shell v1 已完整收口；如需继续推进，应先单独冻结下一个迭代或硬化主题（例如签名分发、auto-update、Keychain、protocol registration hardening）。

## 迭代 7：Hardening（Wave 0 已完成）

- Wave 0：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/ITERATION_7_FREEZE.md` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/adr/ADR-0011-hardening-v1.md`，冻结了 Iteration 7 的 Keychain-first、secret-ref、device identity、backup/import/export、crash repair、plugin trust hardening、update scaffold、sandbox risk UI 与 diagnostic bundle 边界。
- Wave 1：`Completed`。主线程已完成 `KC-0701` secret-store foundation 与 `KC-0703` device identity completion，并通过定向 contract/integration 与 solution-level 回归。
- Wave 2：`Completed`。主线程已完成 `KC-0702` + `KC-0704` + `KC-0705`：secret migration/report、backup/export/import v1 与 startup repair / crash residue evidence 已全部收口。Wave 2 现已补齐 `BackupManifest` / `RepairChecklist` / `StartupRepairReportResponse` 合同、`WorkspaceBackupService`、自动启动的 `WorkspaceRepairService` / `StartupRepairHostedService`、`/api/system/backup-export` / `/api/system/backup-import/preflight` / `/api/system/backup-import` / `/api/system/startup-repair-report`，以及 `config/import-repair-report.json` / `config/startup-repair-report.json` 双 repair evidence。该波次已通过 `BackupContractsTests`、`StartupRepairContractsTests`、`BackupApiIntegrationTests`、`StartupRepairApiIntegrationTests`、最新 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 与 `kodaclaw-web` build/test 验证。
- Wave 3：`Completed`。主线程已完成 `KC-0706` + `KC-0707` + `KC-0708` + `KC-0709`：plugin trust hardening、manual-first update watch、sandbox risk briefing 与 default-redacted diagnostic bundle export 全部收口。Wave 3 现已补齐 `POST /api/diagnostics/bundle-export`、`manifest.json` / `redaction-summary.json` bundle schema、`SessionsDiagnosticsDesk` 导出入口，以及最新 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1`、`cd /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web && npm run test && npm run test:e2e` 的全量回归。
- 范围冻结：Iteration 7 v1 的核心不是再加新产品面，而是把现有 Gateway / Web / Desktop / Plugin / Channel / Automation 产品补齐安全、恢复、迁移与运维闭环。
- 安全冻结：生产 secrets 默认进入 OS Keychain 或等价 secure store；配置只保留 `SecretRef` 与脱敏 metadata，env 只保留给 bootstrap / tests / migration fallback。
- 恢复冻结：export/import 默认排除 raw secrets，并统一通过 preflight + repair checklist 处理 device mismatch、missing secrets 与 crash residue。
- 发布冻结：update mechanism 首期只做版本检查、通道信息、发布说明与手动升级入口，不承诺 silent auto-update。
- `KC-0701`：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Contracts/SecretRef.cs`、`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Contracts/SecretDescriptor.cs` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Contracts/ISecretStore.cs`，冻结共享 secret-store 合同；`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Workspace/PlatformSecretStore.cs` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Workspace/MacOsKeychainCommandRunner.cs` 提供 `memory` / `env` / `keychain` provider；`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Gateway/GatewayAuthTokenAccessor.cs` 让 Gateway 优先解析 `KODACLAW_GATEWAY_TOKEN_SECRET_REF` / `Gateway:TokenSecretRef`，未命中时回退到 legacy token。已通过 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.ContractTests/KodaClaw.ContractTests.csproj --filter SecretStoreContractTests`、`dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/KodaClaw.IntegrationTests.csproj --filter GatewayAuthIntegrationTests` 与 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1`。
- `KC-0702`：`Completed`。模型迁移第一阶段、ChannelHub 迁移子阶段与 PluginHost 迁移子阶段均已收口，并进一步补齐 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Contracts/SecretMigrationState.cs`、`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Contracts/SecretMigrationItem.cs`、`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Contracts/SecretMigrationReport.cs`、`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Gateway/SecretMigrationReportService.cs` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Gateway/Program.cs`，让 Gateway 可以通过 `/api/system/secret-migration-report` 生成脱敏 migration evidence，并持久化到 `config/secret-migration-report.json`；`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.ContractTests/Workspace/SecretMigrationReportContractTests.cs`、`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/Gateway/SecretMigrationReportIntegrationTests.cs` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web/src/types/contracts.ts` 已同步覆盖 / 对齐。已通过 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.ContractTests/KodaClaw.ContractTests.csproj --filter SecretMigrationReportContractTests`、`dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/KodaClaw.IntegrationTests.csproj --filter SecretMigrationReportIntegrationTests`、`dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 与 `cd /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web && npm run build && npm run test`。
- `KC-0703`：`Completed`。已扩展 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Contracts/DeviceIdentity.cs` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Contracts/WorkspaceSnapshot.cs`，补齐 fingerprint / rotation / metadata 字段；`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Workspace/WorkspaceService.cs` 现会在读取 legacy `identity/device.json` 时自动补齐新 metadata 并回写。已通过 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.ContractTests/KodaClaw.ContractTests.csproj --filter WorkspaceServiceContractTests` 与 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1`。
- `KC-0704`：`Completed`。已落地 backup manifest、`WorkspaceBackupService`、`/api/system/backup-export` / `/api/system/backup-import/preflight` / `/api/system/backup-import`、脱敏 control-plane export、pristine-workspace restore 与 repair checklist/report；已通过 `BackupContractsTests`、`BackupApiIntegrationTests`、`dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 与 `kodaclaw-web` build/test。
- `KC-0705`：`Completed`。已落地统一 startup repair engine：Gateway 启动时会自动执行 `WorkspaceRepairService`，取消 stale approvals / channel delivery residue、失败化 queued/running automation runs、收口 stale plugin runtime state、清空 unsupported approval-wait main session active pointer，并把结果同步到 `config/startup-repair-report.json`、`startup-repair-latest` inbox alert 与 `/api/system/startup-repair-report`。已通过 `StartupRepairContractsTests`、`StartupRepairApiIntegrationTests`、`dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 与 `kodaclaw-web` build/test。
- `KC-0706`：`Completed`。已落地 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Contracts/PluginTrustEvidence.cs`、`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.PluginHost/Trust/PluginTrustEvaluator.cs`、SQLite `trust_evidence_json` 持久化与 Web `PluginsDesk` trust evidence 面；插件现在会在 discover / install / detail / trust / start 路径自动生成 digest evidence，并在存在 `plugin.signature.json` sidecar 且校验通过时把 trust 升级为 `Signed`。bundled fixture acceptance 也已升级为 signed case。已通过 `PluginApiContractsTests`、`PluginTrustEvaluatorTests`、`SqlitePluginRegistryRepositoryTests`、`PluginApiIntegrationTests`、`PluginToolInjectionIntegrationTests`、`Iteration4AcceptanceIntegrationTests`、最新 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1`，以及 `cd /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web && npm run build && npm run test && npx playwright test tests/kc0407-plugins.spec.ts`。
- `KC-0707`：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Contracts/UpdateAvailability.cs`、`UpdateReleaseChannel.cs`、`UpdateCheckRequest.cs`、`UpdateComponentState.cs`、`UpdateStateResponse.cs`、`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Gateway/UpdateStateService.cs` 与 Gateway `GET /api/system/update-state` / `POST /api/system/update-check`，并把 update evidence 持久化到 `config/update-state.json`；同时 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-desktop/src/main.ts` / `preload.ts` / `desktop-shell-types.ts` 已补齐 `appVersion` / `releaseChannel` runtime bridge，`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web/src/components/ModelsSettingsDesk.tsx` 已落地统一的 `Update Watch` 手动升级面，`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/Directory.Build.props` 也把产品基线版本统一到 `0.1.0`。已通过 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.ContractTests/KodaClaw.ContractTests.csproj --filter UpdateStateContractsTests`、`dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/KodaClaw.IntegrationTests.csproj --filter UpdateStateApiIntegrationTests`、`cd /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web && npm run test -- --run src/__tests__/models-settings-desk.spec.tsx`、`cd /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web && npm run build`、`cd /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web && npx playwright test tests/kc0212-models-settings.spec.ts`、`cd /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-desktop && npm run test`、`cd /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-desktop && npm run smoke:wave3` 与最新 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1`。
- `KC-0708`：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Contracts/SandboxRiskOverviewResponse.cs`、`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Gateway/SandboxRiskOverviewService.cs` 与 `/api/settings/sandbox-risk` 聚合接口，并把 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web/src/components/ModelsSettingsDesk.tsx` 扩展为统一的 `Sandbox & Risk Briefing`。当前 UI 会明确展示 Local sandbox + boundary enforcement 的 best-effort 边界、SDK 支持但未启用的 Docker 选项、persisted approval posture、plugin 高风险权限与 channel outbound gating 风险；同时 `SandboxRiskContractsTests`、`SandboxRiskApiIntegrationTests`、`models-settings-desk.spec.tsx` 与 `tests/kc0212-models-settings.spec.ts` 已同步覆盖。已通过 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.ContractTests/KodaClaw.ContractTests.csproj --filter SandboxRiskContractsTests`、`dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/KodaClaw.IntegrationTests.csproj --filter SandboxRiskApiIntegrationTests`、最新 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1`，以及 `cd /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web && npm run build && npm run test && npm run test:e2e`。
- `KC-0709`：`Completed`。已新增 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Contracts/DiagnosticBundleExportRequest.cs`、`DiagnosticBundleExportResponse.cs`、`DiagnosticBundleManifest.cs`、`DiagnosticBundleRedactionSummary.cs`、`DiagnosticBundleDesktopContext.cs`、`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/src/KodaClaw.Gateway/DiagnosticBundleService.cs` 与 Gateway `POST /api/diagnostics/bundle-export`，并把 bundle 默认写入 `cache/diagnostics/kodaclaw-diagnostic-bundle-<timestamp>.zip`。归档现包含 redacted settings snapshot、diagnostics recent/timeline、session `meta.json`、repair/update evidence、log summary、`manifest.json` 与 `redaction-summary.json`，并明确排除 raw secrets、`messages.json` / `tool-calls.json` 与 raw logs；`/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/apps/kodaclaw-web/src/components/SessionsDiagnosticsDesk.tsx` 也已提供导出入口与 desktop runtime context 桥接。已通过 `DiagnosticBundleContractsTests`、`DiagnosticBundleApiIntegrationTests`、`sessions-diagnostics-desk.spec.tsx`、`tests/kc0211-sessions-diagnostics.spec.ts`、最新 `dotnet test /Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/KodaClaw.sln -m:1` 与 `kodaclaw-web` build/test/e2e。
- `KC-0710`：`Completed`。已补齐 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/docs/ITERATION_7_ACCEPTANCE_PACK.md` 与 `/Users/vanzheng/projects/ai-agent/kode-sdk-csharp/products/KodaClaw/tests/KodaClaw.IntegrationTests/Smoke/Iteration7AcceptanceIntegrationTests.cs`，把迁移、修复、导入导出、update、risk 与 diagnostic bundle 串成综合验收，并通过 contract/integration/web/desktop/solution 全量验证。
- `2026-03-19 release-closure hardening patch`：`Completed`。已对已收口的 Hardening v1 做最后一轮正确性加固：`SecretMigrationReportIntegrationTests` 改为隔离 config / unique env 注入；`WorkspaceBackupService` 在导入前先检视 zip manifest、entry 安全性、manifest/实际 entry 一致性与归档大小上限，并把 backup export / diagnostic bundle `archivePath` 限制在 workspace 内；`UpdateStateService` 与 Web `ModelsSettingsDesk` 现只允许绝对 `http/https` 外链。已通过定向 hardening integration suite、`models-settings-desk.spec.tsx`、web build 与最新串行 solution 回归。
- `2026-03-19 review follow-up patch`：`Completed`。已修复 review 中确认的三处真实缺口：`DiagnosticBundleService` 现会 redaction JSON / quoted secret 片段；`SecretMigrationReportService` 通过仓储分页完整扫描 channel / plugin 记录；`WorkspaceBackupService` 现会拒绝大小写碰撞的 archive entry，并把相对 `archivePath` 解析到目标 workspace root。已通过定向 `BackupApiIntegrationTests|DiagnosticBundleApiIntegrationTests|SecretMigrationReportIntegrationTests` 与最新串行 solution 回归。
- `2026-03-19 runtime config activation patch`：`Completed`。已补齐当前工作目录 `.env` / `.env.local` / `appsettings*.json` bootstrap，新增动态 runtime snapshot 解析与 `DynamicModelProvider`，并让 Runtime Control 中的 default endpoint 对新 chat/new session 无需重启 Gateway 即可生效；同时补齐了 root-level `appsettings.json` / `appsettings.Development.json`、`.env.example` 注释与专项配置文档。已通过 `GatewayConfigurationBootstrapIntegrationTests|ModelRuntimeBootstrapIntegrationTests`、后续 solution 回归与 web build/test 验证。
- 下一波：Iteration 7 Hardening v1 已完整收口；当前没有未关闭的 hardening 任务，若继续推进应先冻结下一轮产品迭代范围与 ADR。

## 迭代 8：Channels 会话闭环 + Bootstrap 草稿生成

- 范围冻结：本迭代补齐两条产品闭环——①外部消息经 inbound 事件、Runtime 执行后自动产生草稿/审批/直发；②Bootstrap 对话结束后由模型合成 `IDENTITY.md` / `SOUL.md` / `USER.md` 草稿，取代手动编辑工作流。
- 架构冻结：`ChannelTurnOrchestrator` 作为 channel 方向单一编排入口，串接 `ChannelEventIngestionService` → `IChannelSessionService.RunInboundTurnAsync` → `ChannelPolicyEngine` → `ChannelDeliveryGovernanceService` → `ChannelDeliveryDispatchService`；`BootstrapDraftService` 通过 `PromptBuilder` + `IModelProvider.CompleteAsync` 生成三份草稿 markdown。
- 交付冻结：outbound 路由继续复用 `TelegramConnector` / `GenericWebhookConnector`；bootstrap 草稿由 `POST /api/system/bootstrap-draft` 对外暴露，前端可提交对话记录或种子草稿获取生成结果。
- `KC-0801`：`Completed`。已新增 `src/KodaClaw.Contracts/{BootstrapDraftMessage,BootstrapDraftRequest,BootstrapDraftResult,IBootstrapDraftService}.cs`、`src/KodaClaw.Runtime/{BootstrapDraftOptions,BootstrapDraftService}.cs` 与 Gateway `POST /api/system/bootstrap-draft` 端点；服务通过 `PromptBuilder` 组装对话记录 / 种子草稿并调用 `IModelProvider.CompleteAsync` 生成 `IdentityMarkdown` / `SoulMarkdown` / `UserMarkdown`；已通过 `tests/KodaClaw.IntegrationTests/Gateway/BootstrapDraftApiIntegrationTests.cs`（2/2 PASS）、`tests/KodaClaw.IntegrationTests/Runtime/BootstrapDraftServiceIntegrationTests.cs` 与最新 `dotnet test KodaClaw.sln -m:1` 验证。
- `KC-0802`：`Completed`。已新增 `src/KodaClaw.ChannelHub/{ChannelTurnOrchestrator,ChannelDeliveryDispatchService,ChannelDeliveryDispatchResult,ChannelTurnOrchestrationResult}.cs` 与 `src/KodaClaw.Contracts/{ChannelTurnOutcome,ChannelTurnOutcomeKind}.cs`、`src/KodaClaw.Runtime/{ChannelReplyProposal,ChannelTurnExecutionResult}.cs`；`ChannelTurnOrchestrator` 已通过 `ChannelInboundGatewayService` 完整接入 channel inbound 处理路径，支持 `NoAction` / `DraftCreated` / `ApprovalRequested` / `Delivered` / `Failed` 五种 outcome；`ChannelDeliveryDispatchService` 按 `ConnectorKind` 路由至 `TelegramConnector` / `GenericWebhookConnector`，并在成功后更新 `ThreadBinding.LastOutboundAt`、写入 audit + diagnostics；已通过 channel integration tests（17/17 PASS）与最新 `dotnet test KodaClaw.sln -m:1` 验证。

## 迭代 9：Workspace Memory Live

- 范围冻结：修复主会话不加载 workspace 文件的结构性缺口，并实现 Agent 在对话中写回记忆的完整闭环。详见 `docs/ITERATION_9_FREEZE.md`。
- 架构冻结：主会话 `BuildSystemPromptAsync` 改为异步并加载 `AGENTS.md / IDENTITY.md / SOUL.md / USER.md / MEMORY.md / memory/YYYY-MM-DD.md`；新增 `WorkspaceMemoryAppendTool`（宿主进程工具，注入 `IWorkspaceService`，写 `workspace/memory/YYYY-MM-DD.md`）；`DefaultWorkspaceTemplates.Heartbeat()` 补充 `Nightly Memory Consolidation` 定义（`enabled: false`）。
- 交付冻结：`workspace_memory_append` 工具同时注入主会话和 Automation 会话；夜间整合 automation 通过读 `MEMORY.md` + `memory/YYYY-MM-DD.md` 后调用同一工具完成 `MEMORY.md` 覆写；Option B（会话结束后自动摘要）延迟不在本迭代。
- `KC-0901`：`Completed`。主会话 Workspace 上下文加载——`MainSessionService.BuildSystemPrompt()` 改为 `BuildSystemPromptAsync()`，加载六类 workspace 文件（AGENTS/IDENTITY/SOUL/USER/MEMORY/daily memory）后注入 `PromptBuilder.AddContextDocuments()`；已通过 `MainSessionWorkspaceContextIntegrationTests`（5/5 PASS）与 `dotnet test KodaClaw.sln -m:1` 验证。
- `KC-0902`：`Completed`。`workspace_memory_append` 工具——新增 `src/KodaClaw.Runtime/WorkspaceMemoryAppendTool.cs`，注册到 `MainSessionOptions.DefaultTools`（AutomationSessionOptions 复用相同列表）；`ServiceCollectionExtensions.cs` 注入真实工具实例；已通过 `WorkspaceMemoryAppendToolTests`（8/8 PASS）+ `WorkspaceMemoryAppendIntegrationTests`（5/5 PASS）+ `dotnet test KodaClaw.sln -m:1` 验证。
- `KC-0903`：`Completed`。夜间记忆整合模板——更新 `DefaultWorkspaceTemplates.Heartbeat()` 加入 `Nightly Memory Consolidation` 条目（schedule: daily 23:45, enabled: false）；template 结构已通过全量回归验证。

## 迭代 10：Workspace Protocol Write

- 范围冻结：让 Agent 能在主对话中语义化地 patch workspace 协议文件（IDENTITY/SOUL/USER/MEMORY/AGENTS），用户只说自然语言，Agent 自主决定写哪个文件与章节；同时修正 HEARTBEAT.md 模板中错误引用工具名的问题。详见 `docs/ITERATION_10_FREEZE.md`。
- 架构冻结：新增 `WorkspaceProtocolUpdateTool`（章节级 patch，非 append）；`target` 枚举限定五个协议文件；`section` 为空时替换 `#` 标题之后全部正文；section 不存在时末尾追加新章节；写入后触发 `workspace_protocol_updated` 诊断事件。
- 交付冻结：仅注入主会话；不做格式校验；修改在下次会话启动时生效（system prompt 读取时）；HEARTBEAT.md 模板同步修正引用错误。
- `KC-1001`：`Completed`。`workspace_protocol_update` 工具——新增 `src/KodaClaw.Runtime/WorkspaceProtocolUpdateTool.cs`（`public static ApplySectionPatch`，三种 patch 路径：无 section 替换正文、有 section 定点替换、section 不存在末尾追加）；注册到 `MainSessionOptions.DefaultTools` 与 `ServiceCollectionExtensions.cs`；`DefaultWorkspaceTemplates` 改为 `public`；已通过 `WorkspaceProtocolUpdateToolTests`（12/12 PASS）+ `WorkspaceProtocolUpdateIntegrationTests`（6/6 PASS）+ `dotnet test KodaClaw.sln -m:1` 验证。
- `KC-1002`：`Completed`。Heartbeat 模板修正——将 `Nightly Memory Consolidation` 中的引导语从 `workspace_memory_append` 改为 `workspace_protocol_update with target=memory`；补充 `WorkspaceTemplateContractTests`（8/8 PASS）验证模板正确引用工具、路径清洁、关键字段存在；已通过 `dotnet test KodaClaw.sln -m:1` 验证。

## 迭代 11：HEARTBEAT.md → SQLite 热更链路

- 范围冻结：打通 `HeartbeatAutomationCompiler` 到 `AutomationScheduler` 之间缺失的同步链路。用户手动编辑或 Agent 写入 `HEARTBEAT.md` 后，变更须自动反映到 SQLite，使 `AutomationScheduler.TickAsync()` 能够正确调度。详见计划文档。
- 架构冻结：新增 `IHeartbeatSyncService`（编译 + diff + upsert/delete）与 `HeartbeatFileWatcherHostedService`（启动即同步 + 文件变更监听 + 500ms debounce），均放在 `KodaClaw.Workspace` 模块，通过 `AddKodaClawWorkspace()` 注册；`IAutomationDefinitionRepository` 由 Automation 模块注册，DI 延迟解析无顺序冲突。
- 关键不变量：同步时保留 `LastRunAt / NextRunAt / LastRunStatus / LastError`，避免修改 HEARTBEAT.md 后意外重置调度状态；文件不存在时保守不清除已有定义；编译失败时保留现有定义并返回 `CompilationFailed=true`。
- `KC-1101`：`Completed`。新增 `src/KodaClaw.Workspace/HeartbeatSyncService.cs`，包含 `IHeartbeatSyncService` 接口与 `HeartbeatSyncResult` record；同步算法：读文件 → `HeartbeatAutomationCompiler.Compile()` → `IAutomationDefinitionRepository.ListAsync(Source=Heartbeat)` → upsert 保留调度状态 → delete 已删除条目；已通过 `HeartbeatSyncServiceTests`（5/5 PASS，L1 单元）+ `HeartbeatSyncIntegrationTests`（3/3 PASS，L2 真实 SQLite）+ `dotnet test KodaClaw.sln -m:1` 验证。
- `KC-1102`：`Completed`。新增 `src/KodaClaw.Workspace/HeartbeatFileWatcherHostedService.cs`，`BackgroundService` 在启动时立即执行一次 `SyncAsync()`，随后 `FileSystemWatcher` 监听 `workspace/HEARTBEAT.md` 的 `Changed/Created` 事件，通过 `BoundedChannel(1, DropOldest)` + 500ms debounce 防抖后触发下次同步；目录不存在时安全退出；`ServiceCollectionExtensions.cs` 注册两个新服务，`KodaClaw.Workspace.csproj` 添加 `Microsoft.Extensions.Hosting.Abstractions` / `Logging.Abstractions` 包引用；已通过 L0 编译 + 同上测试套件验证。

## 迭代 12：Agent 主动性输出三件套

- 范围冻结：补齐 Agent 主动输出的三条断链：写入自动化规则（HEARTBEAT.md）、发布 Canvas 内容、推送 Inbox 通知。详见 `docs/ITERATION_12_FREEZE.md`。
- 架构冻结：三个工具均放在 `KodaClaw.Runtime`；`workspace_protocol_update` TargetFileMap 加入 `heartbeat`；`canvas_upsert` 写文件到 `workspace/canvas/{id}/index.{ext}` + upsert `ICanvasArtifactRepository`；`inbox_create` 写入 `IInboxRepository`（Kind=Information，Source=agent）；同步更新 `DefaultWorkspaceTemplates` AGENTS.md 引导。
- 关键约束：`canvas_upsert` 路径模型与 `GatewayApp.CanvasFiles.TryResolveCanvasFilePath` 对齐，EntryPath 形如 `workspace/canvas/{id}/index.{ext}`；`ICanvasArtifactRepository` / `IInboxRepository` 均由先于 Runtime 注册的模块提供，DI 无顺序冲突。
- `KC-1201`：`Completed`（2026-03-20）。
  - User Outcome：Agent 对话中理解"每天帮我做 X"后，可通过 `workspace_protocol_update(target=heartbeat)` 在 HEARTBEAT.md 中添加/修改 automation section，触发热更链路后自动调度。
  - Scope：`WorkspaceProtocolUpdateTool.TargetFileMap` 加 `heartbeat` entry；更新工具 Description；补充 L1 单元测试 + L3 contract 测试。
  - Modules：`KodaClaw.Runtime`，`KodaClaw.Workspace`（templates）。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter WorkspaceProtocol` ✅，`dotnet test tests/KodaClaw.ContractTests --filter WorkspaceTemplate` ✅。
- `KC-1202`：`Completed`（2026-03-20）。
  - User Outcome：Agent 对话中可调用 `canvas_upsert` 把报告、任务看板、HTML 内容发布到 Canvas，用户在 Canvas 面板中可见。
  - Scope：新增 `CanvasUpsertTool.cs`；注册到 `DefaultTools` + `ServiceCollectionExtensions`；写文件 + upsert SQLite。
  - Modules：`KodaClaw.Runtime`。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter CanvasUpsert` ✅，`dotnet test tests/KodaClaw.IntegrationTests --filter CanvasUpsert` ✅。
  - 实现备注：`AssetDirectory` 不含末尾 `/`（否则 `CanvasArtifactValidation` 路径段校验失败）。
- `KC-1203`：`Completed`（2026-03-20）。
  - User Outcome：Agent 对话中可调用 `inbox_create` 主动推送关键结论或待跟进事项到 Inbox，用户在 Inbox 面板中可见。
  - Scope：新增 `InboxCreateTool.cs`；注册到 `DefaultTools` + `ServiceCollectionExtensions`；更新 AGENTS.md 模板加三条工具引导。
  - Modules：`KodaClaw.Runtime`，`KodaClaw.Workspace`（templates）。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter InboxCreate` ✅，`dotnet test tests/KodaClaw.ContractTests --filter WorkspaceTemplate` ✅。

## 迭代 13：Inbox 读取、Bootstrap 写回、Inbox 未读徽标

- 范围冻结：闭合 Iter 12 输出链路的最后缺口：Agent 可读取 Inbox（解锁 Daily Inbox Digest 自动化）；Bootstrap 对话写回身份文件（首次上手闭环）；GlobalRail 展示 Inbox 未读计数徽标。详见 `docs/ITERATION_13_FREEZE.md`。
- `KC-1301`：`Completed`（2026-03-20）。
  - User Outcome：Agent（主会话 + 自动化 session）可调用 `inbox_read` 读取 Inbox 内容，Daily Inbox Digest 摘要任务从此有实际数据可处理，不再空转。
  - Scope：新增 `InboxReadTool.cs`，注册为 `inbox_read`，加入 `DefaultTools` + `ServiceCollectionExtensions`；依赖已有 `IInboxRepository.ListAsync()`。
  - Modules：`KodaClaw.Runtime`。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter InboxRead` ✅，`dotnet test tests/KodaClaw.ContractTests --filter WorkspaceTemplate` ✅。
- `KC-1302`：`Completed`（2026-03-20）。
  - User Outcome：Bootstrap 对话结束后，Koda 调用 `workspace_protocol_update` 将收集到的用户信息写入 IDENTITY.md/SOUL.md/USER.md，下次启动即可感知用户偏好与 Koda 人格。
  - Scope：更新 `DefaultWorkspaceTemplates.Bootstrap()` 补写回指令；更新 `DefaultWorkspaceTemplates.Agents()` 补 identity/soul/user/inbox_read 使用说明；对应 L3 contract 测试（6 条新增）。
  - Modules：`KodaClaw.Workspace`（templates）。
  - Verification：`dotnet test tests/KodaClaw.ContractTests --filter WorkspaceTemplate` ✅。
- `KC-1303`：`Completed`（2026-03-20）。
  - User Outcome：GlobalRail 的 Inbox 导航项旁显示未读计数徽标，Agent 推送 `inbox_create` 后用户无需主动打开 Inbox 才能发现新通知。
  - Scope：新增 `useInboxUnreadCount` hook（30s 轮询，`status=Open&limit=50`），GlobalRail Inbox 项渲染徽标；`position: relative` 补到按钮，`.v2-global-rail__badge` 样式新增。无需后端改动（直接用 items.length）。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npm run typecheck` ✅，`npm test` 47/47 ✅。

## 迭代 14：Automation Live + Workspace Context Depth

- 范围冻结：激活自动化调度器、新增 workspace_read 工具、为 channel 会话写回线程摘要。详见 `docs/ITERATION_14_FREEZE.md`。专注后端，无前端变更。
- `KC-1401`：`Completed`（2026-03-20）。
  - User Outcome：Agent（主会话 + 自动化 session）可在对话中按需读取任意 workspace 协议文件，解锁 Nightly Memory Consolidation 自动化（动态读取当日 memory 文件）。
  - Scope：新增 `WorkspaceReadTool.cs`，注册为 `workspace_read`，加入 `DefaultTools` + `ServiceCollectionExtensions`；targets: identity/soul/user/memory/agents/heartbeat/daily_memory。
  - Modules：`KodaClaw.Runtime`。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter WorkspaceRead`，`dotnet test tests/KodaClaw.IntegrationTests --filter WorkspaceRead`。
- `KC-1402`：`Completed`（2026-03-20）。
  - User Outcome：用户在 `PUT /api/settings` 中将 `automationsEnabled` 设为 `true` 后，HEARTBEAT.md 中 `enabled: true` 的 automation 会在下一个调度 tick 自动执行，无需重启 Gateway。
  - Scope：`KodaClawSettings` 增加 `AutomationsEnabled: bool = false`；Gateway 注册时 `AutomationSchedulerOptions.Enabled = true`；`AutomationScheduler.TickAsync()` 增加动态设置检查；contract test 验证字段存在。
  - Modules：`KodaClaw.Contracts`，`KodaClaw.Automation`，`KodaClaw.Gateway`。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter AutomationScheduler`，`dotnet test tests/KodaClaw.IntegrationTests --filter AutomationScheduler`，`dotnet test tests/KodaClaw.ContractTests --filter Settings`。
- `KC-1403`：`Completed`（2026-03-20）。
  - User Outcome：Telegram/Webhook channel turn 完成后，`workspace/channels/<bindingId>/SUMMARY.md` 自动追加本次交互摘要，下次 channel session 启动时上下文更丰富。
  - Scope：新增 `IChannelThreadSummaryWriter` + `ChannelThreadSummaryWriter`，`ChannelTurnOrchestrator` 注入（可选）并在 `ExecutedTurn=true` 且非 Failed 时调用写回。
  - Modules：`KodaClaw.ChannelHub`。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter ChannelThread`，`dotnet test tests/KodaClaw.IntegrationTests --filter ChannelThread`。

## 迭代 15：AutomationsEnabled UI + V2 Chat Stage 收口

- 范围冻结：补齐自动化引擎的用户可见开关（Settings toggle + AutomationsDesk Banner）；收口 V2 Chat 主舞台的 page-like 痕迹；ChannelsDesk / PluginsDesk V2 接入验收。纯前端，无后端变更。详见 `docs/ITERATION_15_FREEZE.md`。
- `KC-1501`：`Completed`。
  - User Outcome：用户在 Settings 页可直接开/关自动化引擎全局开关（`automationsEnabled`），无需 API 调用；AutomationsDesk 在引擎关闭时显示警告 Banner，提示用户前往 Settings 启用，避免用户以为自动化已运行实则全局禁用。
  - Scope：`ModelsSettingsDesk` Settings 区新增 toggle（`data-testid="settings-automations-enabled-toggle"`）；`AutomationsDesk` 顶部新增引擎状态 Banner（`data-testid="automations-engine-banner"`）；复用已有 `fetchSettings` / `updateSettings`；更新 L1 单元测试。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npm run typecheck` ✅，`npm test -- src/__tests__/automations-desk.spec.tsx src/__tests__/models-settings-desk.spec.tsx` ✅。
- `KC-1502`：`Completed`。
  - User Outcome：V2 Shell Chat 模式下主舞台全高铺满，Composer 固定底部，Timeline 占满中部，移除旧 page-like header metrics 冗余，真正实现 conversation-first canvas 感。
  - Scope：`V2Shell.tsx` / `MainStage.tsx` / CSS 调整；不改任何 data-testid；确认 `npm run test:e2e -- tests/kc0112-bootstrap-chat-resume.spec.ts` 稳定通过。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npm run typecheck` ✅，`npm run build` ✅，`npm run test:e2e` ✅。
- `KC-1503`：`Completed`。
  - User Outcome：ChannelsDesk / PluginsDesk 在 V2 GlobalRail 导航后视觉与 test-id 稳定，Playwright E2E 补全 desk 导航用例，不出现 data-kc-view 缺失导致的 flaky test。
  - Scope：`kc0508-channels.spec.ts` / `kc0407-plugins.spec.ts` 补导航入口测试用例（只验证 desk 可见，不重构 desk 内部）；确认 `data-kc-view="channels"` / `data-kc-view="plugins"` 存在。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npm run test:e2e -- tests/kc0508-channels.spec.ts tests/kc0407-plugins.spec.ts` ✅。

## 迭代 16：Channel Session 权限策略 + 渠道侧文字审批

- 范围冻结：固化 Channel/Automation session 的工具调用权限边界（永远 PermissionMode.Auto，不产生 mid-turn approval），并实现通过渠道文字回复完成草稿审批的基础链路。详见 `docs/ITERATION_16_FREEZE.md`。
- `KC-1601`：`Completed 2026-03-20`。
  - User Outcome：Channel session 中工具调用永远不会中断 Agent turn，不产生 mid-turn Approval 记录，用户无需在 Web UI 处理工具级审批，turn 执行流畅不挂起。
  - Scope：`ChannelSessionOptions` 和 `AutomationSessionOptions` 的 `Permissions` 硬编码 `Mode="auto", RequireApprovalTools=[]`，忽略外部配置；4 条单元测试断言该约束。
  - Modules：`src/KodaClaw.Runtime`。
  - Verification：Unit 191/191 ✓，Integration 206/206 ✓，Contract 84/84 ✓。
- `KC-1602`：`Completed 2026-03-20`。
  - User Outcome：Agent 生成草稿后，用户在 Telegram（或任意渠道）直接回复 `ok [TOKEN]` / `no [TOKEN]` 即可批准或拒绝草稿，无需打开 Web UI；批准后消息立刻发出，拒绝后留下审计记录。
  - Scope：（1）`ChannelDeliveryGovernanceService` payloadJson 加 token（SHA256 前 3 字节，6位大写 hex），`BuildApprovalToken` public static；（2）`ChannelDeliveryEvaluationResult` 加 `ApprovalToken` 字段；（3）`ChannelDeliveryDispatchService` 新增 `SendNotificationAsync()`（best-effort，无审计）；（4）新增纯静态 `ChannelApprovalResponseParser`（识别 ok/yes/approve/send / no/cancel/reject + 可选 6位 hex token）；（5）`ChannelTurnOrchestrator` 前置审批响应检测：单 pending 自动匹配、多 pending + 无 token 返回 hint、有 token 精确匹配；通过 `SendNotificationAsync` 发回审批通知。
  - Modules：`src/KodaClaw.ChannelHub`。
  - Verification：Unit 191/191 ✓（含 40 条 KC-1601/1602 专项）；Integration 206/206 ✓；Contract 84/84 ✓。`make test-solution` 全绿。

## 迭代 17：前端追平三件套

- 范围冻结：关闭三个「后端已通但前端不可见/不可配」缺口。详见 `docs/ITERATION_17_FREEZE.md`。
- `KC-1701`：`Completed`。
  - User Outcome：用户在 Canvas Desk 能看到 Agent 通过 `canvas_upsert` 发布的所有产物，点击后可直接阅读内容（Markdown 渲染 / HTML 沙盒预览）。
  - Scope：`CanvasDesk.tsx` 实现产物列表 + 右侧内容渲染面板；复用已有 `GET /api/canvas` 和 `GET /api/canvas/{id}` API。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npm run typecheck`、`npm run test` 通过。
- `KC-1702`：`Completed`（已有，无需实现）。
  - 调查确认：AutomationsDesk 中运行历史功能在 Iter 14 已实现，前端已有运行记录展示，无额外工作。
- `KC-1703`：`Completed`。
  - User Outcome：用户在 Channels Desk 可直接切换每个 thread binding 的 Delivery Mode（AutoSend / DraftApproval / RequireApproval），无需手动调 API 或改 SQLite。
  - Scope：后端：`thread_bindings` 加 `delivery_mode_override` 列 + `PATCH /api/channels/threads/{id}/settings` 端点；前端：Channels Desk 线程详情加模式选择器。
  - Modules：`src/KodaClaw.ChannelHub`、`src/KodaClaw.Gateway`、`apps/kodaclaw-web`。
  - Verification：`dotnet test KodaClaw.sln -m:1` 全量通过（206 + 191 + 84 = 481 tests）。

## 迭代 18：Channel Full Agent Mode + 主动推送

- 范围冻结：Channel session（DM + Group）从 JSON-only 协议切换到 Full Agent 模式（可调用工具、可分步回复），并新增 `channel_send` / `channel_list` 工具实现跨 session 主动推送。详见 `docs/ITERATION_18_FREEZE.md`。

- `KC-1801`：`Completed 2026-03-21`。
  - User Outcome：任意 session（DM/Group/Automation/Main）中 Agent 均可调用 `channel_send` 向已绑定渠道发消息，始终 AutoSend，无需走 DraftApproval 审批流。
  - Scope：新增 `src/KodaClaw.Runtime/ChannelSendTool.cs`，Args: `bindingId`(required), `text`(required)；调用 `ChannelDeliveryDispatchService.SendNotificationAsync`（best-effort，已有服务）；注册到全局 tool registry（`ServiceCollectionExtensions.cs`）；加入 `ChannelSessionOptions.Tools` 和 `AutomationSessionOptions.Tools`；`DefaultWorkspaceTemplates.Agents()` 加使用引导。
  - Modules：`src/KodaClaw.Runtime`。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter ChannelSend`（L1 x4）；`dotnet build KodaClaw.sln`。

- `KC-1802`：`Completed 2026-03-21`。
  - User Outcome：Automation/Main session 的 Agent 可调用 `channel_list` 获取所有已绑定渠道的 `bindingId`，从而配合 `channel_send` 实现定向推送（如任务完成后发到用户的 Telegram DM）。
  - Scope：新增 `src/KodaClaw.Runtime/ChannelListTool.cs`，Args: `connectorKind?`(optional filter)；调用 `IThreadBindingRepository.QueryAsync`；返回 `[{ bindingId, displayTitle, connectorKind, threadType, lastInboundAt }]`；注册到 `AutomationSessionOptions.Tools` 和 `MainSessionOptions.DefaultTools`；`DefaultWorkspaceTemplates.Agents()` 加使用引导。
  - Modules：`src/KodaClaw.Runtime`。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter ChannelList`（L1 x3）；`dotnet build KodaClaw.sln`。

- `KC-1803`：`Completed 2026-03-21`。
  - User Outcome：从 Telegram DM 或 Group 发消息后，Koda 能调用工具（fs_read、bash_run 等）多步推进并通过 `channel_send` 分步回复；体验与主会话等同。
  - Scope：（1）`ChannelSessionService.BuildInboundTurnPrompt` 去除 JSON 约束，改为"自由执行 + 用 channel_send 回复"引导语；（2）`ChannelSessionService.RunInboundTurnAsync` 去除 `ParseProposal(JSON)` 调用，直接以 RunAsync 成功/失败决定 outcome；（3）`ChannelTurnOrchestrator.ProcessInboundAsync` 去除 proposal 解析 → Dispatch 路径（仍保留审批响应检测和 RequireExplicitMention 前置门），改为 run → 记录 `Delivered`/`NoAction` outcome；（4）更新 system prompt：Group 仍保留 RequireExplicitMention 引导，系统提示中明确注入 BindingId 供 agent 调用 channel_send 使用。
  - Modules：`src/KodaClaw.Runtime`、`src/KodaClaw.ChannelHub`。
  - Verification：`dotnet test tests/KodaClaw.IntegrationTests --filter ChannelFullAgent`（L2 x4）；`dotnet test KodaClaw.sln -m:1`；L5 Dogfood：Telegram DM 发"帮我列出工作目录" → Koda 调用 fs_list 并回复。

## 迭代 19：Skills 系统集成

- 范围冻结：接入 SDK 已完整实现的 Skills 系统，建立三层加载路径（内置 / workspace / 全局），打通 Agent 自安装 skill 的完整闭环，并提供 Web SkillsDesk 可视化入口。详见 `docs/ITERATION_19_FREEZE.md`。

- `KC-1901`：`Completed 2026-03-21`。
  - User Outcome：KodaClaw 开箱即有一个内置 skill（`koda-workspace`，KodaClaw workspace 协议完整指南），Agent 可直接 skill_list 发现并激活；用户将 SKILL.md 写入 `workspace/skills/` 即完成"安装"，下次 session 自动发现。
  - Scope：（1）在项目根新建 `skills/koda-workspace/SKILL.md`（frontmatter: name/description/license + Markdown body 覆盖 workspace 协议要点）；（2）`.csproj` 将 `skills/**` 设为 Content CopyToOutputDirectory；（3）`WorkspaceService.InitializeAsync()` 创建 `workspace/skills/` 目录；（4）新增 `IWorkspaceService.GetSkillsPaths()` 返回三层路径列表（`[AppDir/skills, ~/.agents/skills, workspaceRoot/workspace/skills]`，不存在的路径自动跳过）；（5）`DefaultWorkspaceTemplates.Agents()` 补充 skills 自安装引导（skill_list/skill_activate/skill_resource + fs_write 创建流程）。
  - Modules：`src/KodaClaw.Workspace`、`src/KodaClaw.Runtime`、`skills/`（新目录）。
  - Verification：`dotnet test tests/KodaClaw.ContractTests --filter Skills`（L3 x3，验证内置 skill 格式、路径约定、AGENTS.md 引导存在）；`dotnet build KodaClaw.sln`。

- `KC-1902`：`Completed 2026-03-21`。
  - User Outcome：主会话、Channel session（DM/Group）、Automation session 中，`skill_list` 真正返回已发现的 skills，`skill_activate` 成功注入领域知识到 session context；三个工具不再静默无效。
  - Scope：`MainSessionService.CreateAgentConfig()`、`ChannelSessionService.CreateAgentConfig()`、`AutomationSessionService.CreateAgentConfig()` 均加入 `Skills = new SkillsConfig { Paths = _workspaceService.GetSkillsPaths(), ValidateOnLoad = true }`；`IWorkspaceService` 注入三类 session service。
  - Modules：`src/KodaClaw.Runtime`。
  - Verification：`dotnet test tests/KodaClaw.IntegrationTests --filter SkillsIntegration`（L2 x4：main session skill_list 返回 koda-workspace；skill_activate 执行不报错；channel session skills 可用；automation session skills 可用）；`dotnet test KodaClaw.sln -m:1`。

- `KC-1903`：`Completed 2026-03-21`。
  - User Outcome：Web SkillsDesk 列出三层可用 skills，标注来源（built-in / workspace / global），用户可浏览 name/description/path，了解当前可激活哪些 skill。
  - Scope：（1）Gateway `GET /api/skills`：扫描三层路径，聚合并返回 `[{ name, description, source, path, hasResources }]`（source 枚举：BuiltIn/Workspace/Global）；（2）新增 `SkillsDesk.tsx` 组件：列表 + 来源标签，无激活入口（激活由 Agent 在对话中完成）；（3）GlobalRail 添加 skills 导航入口。
  - Modules：`src/KodaClaw.Gateway`、`apps/kodaclaw-web`。
  - Verification：`dotnet test tests/KodaClaw.IntegrationTests --filter SkillsApiIntegration`（L2 x2）；`npm run typecheck`；`npm run test`（SkillsDeskSpec）；`dotnet test KodaClaw.sln -m:1`。

## Bug Fixes（迭代间补录）

- `KC-BUG-001`：`Completed 2026-03-21`。
  - 症状：`skill_list` 始终返回 0 个 skills，三层路径均无法被发现。
  - 根因：`LocalSandbox.EnforceBoundary = true` + `WorkingDirectory = sessionDirectory` 会对 sessionDirectory 外的所有路径抛 `UnauthorizedAccessException`；`SkillsLoader` 的 `DiscoverAsync` 静默吞掉该异常，导致 0 discoveries。
  - 修复：`MainSessionService`、`ChannelSessionService`、`AutomationSessionService` 的 `CreateAgentConfig` 和 resume 路径 `AgentConfigOverrides` 均加入 `SandboxOptions.AllowPaths = skillsPaths`。
  - 受影响模块：`src/KodaClaw.Runtime`。
  - Verification：`dotnet test KodaClaw.sln -m:1`（494 tests 全通过）。

- `KC-BUG-002`：`Completed 2026-03-21`。
  - 症状：`workspace/channels/{id}/SUMMARY.md` 每行内容全部相同（`Agent turn completed; reply delivered via channel_send.`），对下一个 session 无上下文参考价值。
  - 根因：Iteration 18 切换到 Full Agent Mode 后，`ChannelTurnOrchestrator` 无法感知 Agent 内部调用 `channel_send` 发出的文本，退化为硬编码字符串。
  - 修复：新增 `IChannelSendCapture`（singleton，ConcurrentDictionary 按 bindingId 队列）；`ChannelSendService.SendAsync` 成功后调用 `capture.Record`；`ChannelTurnOrchestrator` 在 turn 完成后调用 `GetAndClear` 并构建 `user: "..." → koda: "..."` 格式的对话摘要。
  - 受影响模块：`src/KodaClaw.ChannelHub`。
  - Verification：`dotnet test KodaClaw.sln -m:1`（494 tests 全通过）。

## 迭代 20：Memory Pipeline & Session Continuity

- 范围冻结：打通记忆流水线的四处断点——启用 SDK ContextManager 防止主会话 token 超限、nightly consolidation 改为默认开启、SUMMARY.md 滚动窗口防止 channel 记忆膨胀、主会话加载昨天的 daily memory 消除跨日期缺口。详见 `docs/ITERATION_20_FREEZE.md`。

- `KC-2001`：`Completed 2026-03-21`。
  - User Outcome：主会话连续使用 200+ 轮后，SDK 自动压缩旧对话历史，会话继续正常工作，不因 context 爆满崩溃或行为退化。
  - Scope：`MainSessionOptions` 新增 `CompressionModel`（默认 `claude-haiku-4-5-20251001`）、`ContextMaxTokens`（默认 80_000）、`ContextCompressToTokens`（默认 50_000）；`MainSessionService.CreateAgentConfig` 加入 `Context = new ContextManagerOptions { ... }`；resume 路径 `AgentConfigOverrides` 同步加 Context 字段。
  - Modules：`src/KodaClaw.Runtime`。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter MainSessionOptions`（L1）；`dotnet build KodaClaw.sln`。

- `KC-2002`：`Completed 2026-03-21`。
  - User Outcome：新创建 workspace 的 HEARTBEAT.md 中，`Nightly Memory Consolidation` 默认 `enabled: true`，多天使用后 MEMORY.md 自动积累已学到的事实，无需用户手动启用。
  - Scope：`DefaultWorkspaceTemplates.Heartbeat()` 中 `Nightly Memory Consolidation` 的 `enabled: false` → `enabled: true`；同步更新 contract test golden 文件。
  - Modules：`src/KodaClaw.Workspace`、`tests/KodaClaw.ContractTests`。
  - Verification：`dotnet test tests/KodaClaw.ContractTests --filter WorkspaceTemplate`（L3）；`dotnet build KodaClaw.sln`。

- `KC-2003`：`Completed 2026-03-21`。
  - User Outcome：无论 channel 聊了多久，SUMMARY.md 始终保持最近 30 轮对话（60 行）；注入到 system prompt 时不会因文件过大而截断最新内容。
  - Scope：`ChannelThreadSummaryWriter.WriteAsync` 写入后读取文件行数，若超过 60 行则删除头部多余行（保留尾部 60 行）；并发安全（单文件写入已是顺序调用，无并发问题）。
  - Modules：`src/KodaClaw.ChannelHub`。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter ChannelThreadSummaryWriter`（L1，含截断边界 x3）；`dotnet build KodaClaw.sln`。

- `KC-2004`：`Completed 2026-03-21`。
  - User Outcome：跨天对话或间隔一天后重新使用时，昨天写入的 `memory/YYYY-MM-DD.md` 内容仍会出现在主会话 system prompt 中，不会出现记忆断层。
  - Scope：`MainSessionService.BuildSystemPromptAsync` 在加载 `memory/{今天}.md` 后，额外尝试加载 `memory/{昨天}.md`（文件不存在时静默跳过）；两个文件都受 `seenPaths` 去重保护。
  - Modules：`src/KodaClaw.Runtime`。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter MainSessionBuildSystemPrompt`（L1，今天 + 昨天均存在 x1，只有昨天 x1，都不存在 x1）；`dotnet build KodaClaw.sln`。

- `KC-BUG-003`：`Completed 2026-03-21`。
  - 症状：`ChannelSessionService` 和 `AutomationSessionService` 创建的 Agent 没有 Context 压缩保护，channel 长跑会话或 automation 任务的 token 会无限累积，最终触达模型硬性上限。
  - 根因：Iteration 20 补齐 `MainSessionService` 时遗漏了另外两个 session 类型，也未在对应 Options 类中添加压缩配置字段。
  - Scope：`ChannelSessionOptions` / `AutomationSessionOptions` 新增 `CompressionModel`、`ContextMaxTokens`、`ContextCompressToTokens` 字段（与 Iter 20 保持一致的默认值）；`ChannelSessionService.CreateAgentConfig` / resume `AgentConfigOverrides`、`AutomationSessionService.CreateAgentConfig` 均补加 `Context = new ContextManagerOptions { ... }`。
  - 受影响模块：`src/KodaClaw.Runtime`。
  - Verification：`dotnet build KodaClaw.sln`（L0）；`dotnet test tests/KodaClaw.UnitTests -m:1`（L1，210/210）。

## 迭代 21：Context Intelligence & Session Lifecycle

范围冻结：见 `docs/ITERATION_21_FREEZE.md`（2026-03-21）。

- `KC-2101`：`Completed 2026-03-21`。
  - User Outcome：使用任何 provider（Anthropic / OpenAI）和任何模型，上下文压缩都能可靠工作；不同 context window 大小的模型触发阈值自动适配；用户无需配置任何压缩相关参数。
  - Scope：删除三个 Options 类中的 `CompressionModel` / `ContextMaxTokens` / `ContextCompressToTokens`；新增 `ContextCompressionTriggerRatio`（默认 0.75）/ `ContextCompressionTargetRatio`（默认 0.40）；`ModelEndpointConfig` 加 `ContextWindowSize`（默认 128_000）；三个 SessionService 的 `CreateAgentConfig` 从 ModelHub 读 window size 后按比例计算阈值，不设 `CompressionModel`（SDK 用主模型）。
  - Modules：`src/KodaClaw.Runtime`、`src/KodaClaw.ModelHub`。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter SessionOptions`（L1）；`dotnet test tests/KodaClaw.IntegrationTests --filter ContextManager`（L2）；`dotnet build KodaClaw.sln`。

- `KC-2102`：`Completed 2026-03-21`。
  - User Outcome：Nightly Memory Consolidation 执行后，已整合的 `memory/YYYY-MM-DD.md` 日记文件被自动删除；`workspace/memory/` 目录只保留最近 7 天的文件，磁盘占用可预期。
  - Scope：`DefaultWorkspaceTemplates.Heartbeat()` 的 Nightly Consolidation section 补充 `fs_rm` 清理指令和 7 天保留说明。
  - Modules：`src/KodaClaw.Workspace`。
  - Verification：`dotnet test tests/KodaClaw.ContractTests --filter WorkspaceTemplate`（L3）；`dotnet build KodaClaw.sln`。

- `KC-2103`：`Completed 2026-03-21`。
  - User Outcome：长期使用后 MEMORY.md 保持合理大小（目标 <200 行），不会撑爆 system prompt；旧的、已被覆盖的事实自动被淘汰。
  - Scope：`DefaultWorkspaceTemplates.Heartbeat()` 的 Nightly Consolidation section 补充 90 天保留规则和大小约束描述。
  - Modules：`src/KodaClaw.Workspace`。
  - Verification：`dotnet test tests/KodaClaw.ContractTests --filter WorkspaceTemplate`（L3）；`dotnet build KodaClaw.sln`。

- `KC-2104`：`Completed 2026-03-21`。
  - User Outcome：用户可在 Web UI 点击"新对话"按钮，清空当前 session 历史，开启全新对话；workspace 记忆文件（MEMORY.md 等）不受影响。
  - Scope：`POST /api/sessions/rotate` Gateway 端点（删除当前 main session SDK store，不触动 workspace 文件）；V2 shell GlobalRail 或 Chat header 加"新对话"按钮（含确认弹窗，`data-testid="new-session-button"`）。
  - Modules：`src/KodaClaw.Gateway`、`src/KodaClaw.Runtime`、`apps/kodaclaw-web`。
  - Verification：`dotnet test tests/KodaClaw.IntegrationTests --filter SessionRotate`（L2）；`npx playwright test --grep new-session`（L4）；`dotnet build KodaClaw.sln`。

## 迭代 22：Channel-First 体验补全

范围冻结：见 `docs/ITERATION_22_FREEZE.md`（2026-03-21）。

- `KC-2201`：`Pending`。
  - User Outcome：Telegram thread 超过 7 天未活动后，下一条消息开启全新 channel session，不再 resume 陈旧 SUMMARY.md，Koda 不会出现"记忆混乱"。
  - Scope：`ChannelSessionOptions` 新增 `SessionTimeoutDays`（默认 7）；`ChannelSessionService.EnsureChannelSessionAsync` 在 resume 前检查 `ThreadBinding.LastMessageAt`，超期则清除 `ActiveSessionId` 走新建路径；0 = 永不过期。
  - Modules：`src/KodaClaw.Runtime`。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter ChannelSessionTimeout`（L1 x4）；`dotnet test tests/KodaClaw.IntegrationTests --filter ChannelSessionTimeout`（L2 x2）；`dotnet build KodaClaw.sln`。

- `KC-2202`：`Pending`。
  - User Outcome：ChannelsDesk 每 30 秒自动刷新，有 pending channel delivery approval 时 GlobalRail Channels 导航项显示角标，用户无需手动 F5 才能发现新 thread。
  - Scope：`ChannelsDesk.tsx` 加 30s 轮询（`useInterval`）；GlobalRail Channels 项读取 pending approval count，>0 时渲染 `.v2-global-rail__badge`；加 `data-testid="channels-refresh-indicator"`。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npm run typecheck`；`npm test -- src/__tests__/channels-desk.spec.tsx`；`npm run build`。

- `KC-2203`：`Pending`。
  - User Outcome：用户在 ChannelsDesk Web UI 中即可添加 Telegram bot（填入 bot token）、设置 delivery rule，无需改配置文件或重启 Gateway，connector 自动热启动。
  - Scope：后端新增 `POST /api/channels/accounts`、`PATCH /api/channels/accounts/{id}`、`DELETE /api/channels/accounts/{id}`；`ChannelConnectorHostedService` 响应热重载信号（start/stop 单个 connector）；前端 ChannelsDesk 加 "Add Channel" 向导（选类型 → 填配置 → 设 delivery rule → 保存）和账号编辑入口；Telegram bot token 通过 Keychain 存储（SecretRef 模式）。
  - Modules：`src/KodaClaw.ChannelHub`、`src/KodaClaw.Gateway`、`apps/kodaclaw-web`。
  - Verification：`dotnet test tests/KodaClaw.IntegrationTests --filter ChannelAccountApi`（L2 x4）；`npm test -- src/__tests__/channels-desk.spec.tsx`；`dotnet test KodaClaw.sln -m:1`。

- `KC-2204`：`Pending`。
  - User Outcome：收到 ChannelDelivery approval 的 macOS 桌面通知时，通知上直接有"✓ 发送" / "✗ 不发送"按钮，点击后无需打开 Web 即完成审批。
  - Scope：Desktop `main.ts` 对 `ApprovalKind.ChannelDelivery` 类型通知附加 macOS notification action buttons；监听 `notification-action` 事件后调用 `POST /api/approvals/{id}/approve|reject`（使用已有 Keychain token）；其他 ApprovalKind 保持原有跳 Web 行为不变。
  - Modules：`apps/kodaclaw-desktop`。
  - Verification：`cd apps/kodaclaw-desktop && npm run test`；`npm run smoke:wave3`；`npm run build`。

- `KC-2205`：`Pending`。
  - User Outcome：AutomationsDesk 每个 automation 条目旁有"▶ 立即执行"按钮，点击后立即触发一次执行，runs 列表自动刷新显示新记录，用户无需等待 cron 调度即可测试 automation。
  - Scope：后端新增 `POST /api/automations/{id}/trigger`（创建 Queued run，`AutomationScheduler` 下次 tick 时执行，返回 `{ ok, runId }`）；前端 AutomationsDesk 加触发按钮（`data-testid="automation-trigger-{id}"`），enabled 状态可点击，点击后 optimistic UI + 刷新 runs 列表。
  - Modules：`src/KodaClaw.Automation`、`src/KodaClaw.Gateway`、`apps/kodaclaw-web`。
  - Verification：`dotnet test tests/KodaClaw.IntegrationTests --filter AutomationTrigger`（L2 x3）；`npm test -- src/__tests__/automations-desk.spec.tsx`；`dotnet test KodaClaw.sln -m:1`。

- `KC-2206`：`Pending`。
  - User Outcome：Telegram 长线程使用后，Koda 对早期对话内容不再"失忆"——超阈值时旧条目以 LLM 段落摘要保留，而非直接截断丢弃。
  - Scope：`ChannelSessionOptions` 新增 `SummaryCompressionThreshold`（默认 80）、`SummaryCompressionTargetLines`（默认 40）；`ChannelThreadSummaryWriter.WriteAsync` 超阈值时调用 `IModelProvider.CompleteAsync` 生成段落摘要，替换头部条目为 `## Compressed History (timestamp)` 块 + 保留尾部 40 行；LLM 调用失败时回退到截断（不阻塞 turn）。
  - Modules：`src/KodaClaw.ChannelHub`、`src/KodaClaw.Runtime`。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter ChannelThreadSummaryWriter`（L1，含压缩路径 x3、失败回退 x1）；`dotnet test KodaClaw.sln -m:1`。

- `KC-2207`：`Pending`。
  - User Outcome：CanvasDesk 打开 `contentType=markdown` 的 artifact 时，内容以富文本 HTML 直接渲染（支持表格、任务列表、代码块），不依赖 iframe；HTML artifact 保持 sandbox iframe。
  - Scope：前端 CanvasDesk detail panel 根据 `contentType` 分路：`markdown` → 调用 `/api/canvas/{id}/entry` 获取原文，用 `react-markdown` + `remark-gfm` 渲染，`data-testid="canvas-artifact-content"`；`html` → 保持现有 iframe；前端依赖新增 `react-markdown`、`remark-gfm`。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npm test -- src/__tests__/canvas-desk.spec.tsx`；`npm run typecheck`；`npm run build`。

## 迭代 23：引导程序基础数据层

范围冻结：见 `docs/ITERATION_23_FREEZE.md`（2026-03-21）。

- `KC-2301`：`Completed`（2026-03-21）。
  - User Outcome：用户在添加模型时可从主流模型预设列表中直接选择，无需手动填写模型名称、context window 等参数；Models Settings Desk 体验大幅简化。
  - Scope：内置静态预设列表（≥10 个，覆盖 Anthropic / OpenAI / DeepSeek / Google / Ollama），存为嵌入资源 `Resources/model-presets.json`；新增 `ModelPresetService`；Gateway `GET /api/models/presets`、`GET /api/models/presets/{presetId}`。
  - Modules：`src/KodaClaw.Contracts`、`src/KodaClaw.Gateway`。
  - Verification：`dotnet build KodaClaw.sln` ✓（0 警告 0 错误）；`npm run typecheck` ✓；`npm run test` ✓（50 tests）。

- `KC-2302`：`Completed`（2026-03-21）。
  - User Outcome：用户填写 API Key 后立刻点击"测试连接"，几秒内得到明确的成功/失败反馈，不需要等到第一次真实对话才发现配置错误。
  - Scope：新增 `ModelConnectionTestService`（发送 max_tokens=1 的最小 API 调用）；Gateway `POST /api/models/test-connection`；API Key 不持久化，仅在请求生命周期内使用。
  - Modules：`src/KodaClaw.Gateway`。
  - Verification：`dotnet build KodaClaw.sln` ✓（0 警告 0 错误）；`npm run typecheck` ✓。

- `KC-2303`：`Completed`（2026-03-21）。
  - User Outcome：引导程序中用户可从 6 个内置 Persona 模板选择 Koda 的人格风格，不需要从空白开始描述；每个模板有清晰的标语、描述和 SOUL.md 预览。
  - Scope：内置 6 个模板（极简执行者 / 深度分析师 / 创意伙伴 / 耐心向导 / 务实顾问 / 均衡默认），存为嵌入资源 `Resources/persona-presets.json`；新增 `PersonaPresetService`；Gateway `GET /api/workspace/persona-presets`、`GET /api/workspace/persona-presets/{presetId}`；`POST /api/onboarding/apply-persona` 写入 SOUL.md / IDENTITY.md。
  - Modules：`src/KodaClaw.Contracts`、`src/KodaClaw.Gateway`。
  - Verification：`dotnet build KodaClaw.sln` ✓（0 警告 0 错误）；`npm run typecheck` ✓；`npm run test` ✓（50 tests）。

- `KC-2304`：`Completed`（2026-03-21）。
  - User Outcome：用户在引导程序中途关闭窗口后，下次打开从中断步骤续接，不需要重头来过。
  - Scope：`OnboardingState` 数据模型（currentStepId / completedSteps / selectedPresetId 等）；`OnboardingStateService` 读写 `config/onboarding.json`；`GET /api/onboarding/state`、`PUT /api/onboarding/state`、`POST /api/onboarding/complete`、`POST /api/onboarding/reset`。
  - Modules：`src/KodaClaw.Contracts`、`src/KodaClaw.Workspace`、`src/KodaClaw.Gateway`。
  - Verification：`dotnet build KodaClaw.sln` ✓（0 警告 0 错误）；`npm run typecheck` ✓；`npm run test` ✓（50 tests）。

## 迭代 24：首次使用引导程序

范围冻结：见 `docs/ITERATION_24_FREEZE.md`（2026-03-21）。前置依赖：Iter 22（Channel 账号绑定 API）+ Iter 23（全部四项）。

- `KC-2401`：`Completed`（2026-03-21）。
  - User Outcome：全新安装的 KodaClaw 启动后自动进入引导程序，已完成引导的用户直接进入主界面；引导程序可从 Settings 重新触发。
  - Scope：`App.tsx` 启动时检查 `GET /api/onboarding/state`，`isCompleted=false` 时渲染全屏 `OnboardingShell`；右上角提供"跳过"入口；Settings 页加"重新引导"按钮（调用 `POST /api/onboarding/reset`）。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npm test -- src/__tests__/onboarding-shell.spec.tsx`；`npm run typecheck`。

- `KC-2402`：`Completed`（2026-03-21）。
  - User Outcome：引导程序第一步选择界面语言，点击即选，立刻影响后续所有引导文案及 Koda 默认回复语言。
  - Scope：全屏居中两张大卡片（中文 / English）；点击后写 `localStorage["kodaclaw.locale"]`，推进 onboarding 状态到 `model` 步骤。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npm test -- src/__tests__/onboarding-shell.spec.tsx`（语言切换后 locale 正确）。

- `KC-2403`：`Completed`（2026-03-21）。
  - User Outcome：用户在引导中选择 Provider → 看到推荐模型 → 填入 API Key → 立刻测试连通 → 成功后配置自动保存，整个流程不超过 3 分钟。
  - Scope：Provider 选择卡片（5 个）→ 模型列表（来自预设 API）→ API Key 输入框 + "如何获取 Key"展开说明 + "测试连接"按钮 → 连通性测试结果 → 成功后调用 `POST /api/models` 保存并设为 default；消费 KC-2301 / KC-2302。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npm test -- src/__tests__/onboarding-shell.spec.tsx`（mock 连通性测试成功/失败分支）；`npx playwright test tests/kc2401-onboarding.spec.ts`（L4）。

- `KC-2404`：`Completed`（2026-03-21）。
  - User Outcome：用户选择一种 Koda 人格模板后，SOUL.md 立刻被写入对应内容，下一次主会话启动时 Koda 的行为风格即生效。
  - Scope：6 张 Persona 卡片（来自预设 API）；选中后展开 SOUL.md 前 5 条准则预览；"使用这个风格"调用 `POST /api/onboarding/apply-persona`（写入 SOUL.md）；"让 Koda 自己来了解我"跳过（不写 SOUL.md，Bootstrap 对话决定）；消费 KC-2303。
  - Modules：`apps/kodaclaw-web`、`src/KodaClaw.Gateway`（apply-persona 端点）。
  - Verification：`npm test -- src/__tests__/onboarding-shell.spec.tsx`（apply-persona 后 onboarding state 更新）。

- `KC-2405`：`Completed`（2026-03-21）。
  - User Outcome：在引导程序中完成 Telegram Bot 创建和绑定，包含 BotFather 步骤说明、token 验证和 Delivery Rule 选择，整个流程可在引导内完成，无需跳转其他页面。
  - Scope：4 个子步骤内嵌向导（说明 → BotFather 步骤 → Token 输入 + 验证 → Delivery Rule 选择）；`POST /api/channels/test-telegram-token`（新增，验证 token 有效返回 bot 名称）；成功后调用 `POST /api/channels/accounts`（KC-2203）；步骤可整体跳过。
  - Modules：`apps/kodaclaw-web`、`src/KodaClaw.Gateway`。
  - Verification：`npm test -- src/__tests__/onboarding-shell.spec.tsx`（跳过路径；mock token 验证成功路径）。

- `KC-2406`：`Completed`（2026-03-21）。
  - User Outcome：引导完成后看到配置摘要和 3 条具体行动建议，点击"开始使用"直接进入主对话界面，立刻可以和 Koda 交谈。
  - Scope：完成页展示配置摘要（已选模型 / 人格 / 是否绑定 Telegram）+ 动态行动建议列表（根据已完成步骤生成）；"开始使用"调用 `POST /api/onboarding/complete` 后跳转到 `mainDesk="chat"`。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npx playwright test tests/kc2401-onboarding.spec.ts`（完整引导流程 E2E）。

- `KC-2407`：`Completed`（2026-03-21）。
  - User Outcome：在 Models Settings Desk 添加模型时，可从预设列表一键填充模型参数，不再需要手动查阅模型名称和 context window 大小。
  - Scope：ModelsSettingsDesk 添加模型表单上方加"从预设选择"区域（Provider 下拉 → 模型列表）；选中后自动填充 modelId / baseUrl / contextWindowSize；保留手动修改能力；复用连通性测试按钮。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npm test -- src/__tests__/models-settings-desk.spec.tsx`；`npx playwright test tests/kc0212-models-settings.spec.ts`。

## 迭代 25：前端全面重构（Claude Desktop 风格）

范围冻结：见 `docs/ITERATION_25_FREEZE.md`（2026-03-21）。

- `KC-W2-010`：`Completed`（2026-03-21）。
  - User Outcome：整体视觉风格从暖棕 grain 纹理切换为干净现代的 Agent OS 风格，色彩、字体、圆角统一更新。
  - Scope：新建 CSS 变量体系（amber 品牌色 + 中性背景）；替换 index.css 色彩/字体；移除 grain 纹理、paper-haze、glass-morphism 样式；Card radius 12px，Button/Input radius 8px。
  - Modules：`apps/kodaclaw-web/src/index.css`。
  - Verification：`npm run build`；`npm run typecheck`。

- `KC-W2-011`：`Completed`（2026-03-21）。
  - User Outcome：用户看到干净的二栏布局——左侧 240px 固定侧边栏（导航 + 状态）+ 右侧全宽主内容区。对话界面占据全宽，其他设置页面同样全宽展示，不再有中间 ContextRail 的信息噪音。
  - Scope：新建 `src/shell/` 目录；创建 AppShell.tsx（二栏根容器）、Sidebar.tsx（导航分组 + 状态徽章）、MainContent.tsx（Desk 路由）、DeskPageHeader.tsx（非 Chat 页标题）、app-shell.css；data-testid 按迁移表更新。
  - Modules：`apps/kodaclaw-web/src/shell/`。
  - Verification：`npm run test`；`npm run build`。

- `KC-W2-012`：`Completed`（2026-03-21）。
  - User Outcome：App.tsx 从 981 行瘦身到约 180 行，代码可读性大幅提升，维护成本降低。
  - Scope：抽取 `src/i18n/app-strings.ts`（~350 行 i18n 文本）；抽取 `src/hooks/useBootstrap.ts`（~8 个 Bootstrap useState + 相关 handler）；App.tsx 只保留顶层编排逻辑。
  - Modules：`apps/kodaclaw-web/src/App.tsx`、`src/i18n/`、`src/hooks/`。
  - Verification：`npm run typecheck`；`npm run build`；`npm run test`。

- `KC-W2-013`：`Completed`（2026-03-21）。
  - User Outcome：Onboarding 引导程序视觉风格与主界面一致（amber/orange 品牌色，无蓝色 accent，圆角 8px）。
  - Scope：更新 `src/onboarding/onboarding.css`，蓝色 `#0070f3` → amber `#D97706`；圆角、字体、间距统一新设计语言。
  - Modules：`apps/kodaclaw-web/src/onboarding/onboarding.css`。
  - Verification：`npm run build`。

- `KC-W2-014`：`Completed`（2026-03-21）。
  - User Outcome：代码库去除 ~1500 行冗余旧壳代码（V1 Shell、V2 Shell、DeskHeader、SystemStatusCard、shell-variant）；测试正确反映新 Shell 结构。
  - Scope：删除 `src/shell-v1/`、`src/shell-v2/`、`src/components/DeskHeader.tsx`、`src/components/SystemStatusCard.tsx`、`src/shell-shared/shell-variant.ts`；更新 `src/__tests__/app-shell.spec.tsx` 和相关 E2E 测试的 data-testid。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npm run typecheck`；`npm run build`；`npm run test`；`npm run test:e2e`。

## 迭代 26：自然语言 Workspace 引导（对话即配置）

范围冻结：见 `docs/ITERATION_26_FREEZE.md`（2026-03-21）。前置依赖：Iter 25（新 AppShell + App.tsx 瘦身）。

- `KC-2601`：`Completed`。
  - User Outcome：Onboarding 精简为"模型配置 → 直接进主界面"，去掉语言选择（改为自动检测）、Persona 选择、Telegram 渠道绑定三个步骤，新用户配置时间从 5 分钟缩短到 90 秒。
  - Scope：删除 `OnboardingShell` 中的 LanguageStep / PersonaStep / ChannelStep 步骤引用及 LanguageStep 组件文件；**PersonaStep / ChannelStep 组件文件保留**，迁移路径为 `src/components/settings/`（供 Iter 27 Settings Desk 复用）；OnboardingShell 精简为 model → done 两步；App.tsx 启动时从 `navigator.language` 自动设置 locale，不再等用户选择。
  - Modules：`apps/kodaclaw-web/src/onboarding/`、`apps/kodaclaw-web/src/App.tsx`。
  - Verification：`npm run typecheck`；`npm run test`。

- `KC-2602`：`Completed`。
  - User Outcome：App.tsx 和 AppShell 不再有 bootstrap mode 分支；BootstrapPanel 组件和 useBootstrap hook 整体删除；shell 只剩 main 单一模式，代码结构更清晰。
  - Scope：删除 `BootstrapPanel.tsx`、`useBootstrap.ts`；从 App.tsx 移除 bootstrap 相关 state（`bootstrapBanner`、`contextPanel`、bootstrap aside）；从 `MainContent.tsx`、`Sidebar.tsx`、`AppShell.tsx` 移除 bootstrap mode 渲染分支；从 `app-strings.ts` 删除 bootstrap 相关文案；更新受影响测试。
  - Modules：`apps/kodaclaw-web/src/`。
  - Verification：`npm run typecheck`；`npm run build`；`npm run test`。

- `KC-2603`：`Completed`。
  - User Outcome：Gateway 能返回当前 workspace 哪些关键文件缺少实质内容，为 session 构建时的引导注入提供依据。
  - Scope：新增 `WorkspaceReadinessService`，检查 IDENTITY.md / SOUL.md / USER.md 是否为空或仅含默认占位符（字符数 < 50 或内容与 default template 一致）；Gateway 新增 `GET /api/workspace/readiness` 端点返回 `{ identityMissing, soulMissing, userMissing, hasAnyGap }`。
  - Modules：`src/KodaClaw.Runtime`、`src/KodaClaw.Gateway`。
  - Verification：`dotnet test --filter "WorkspaceReadiness"`；`dotnet build KodaClaw.sln`。

- `KC-2604`：`Completed`。
  - User Outcome：当 workspace 不完整时，Koda 在对话开始时主动询问身份/风格/用户画像，每次只问一个问题；用户拒绝时不再重复。
  - Scope：session 构建时消费 KC-2603 结果，若 `hasAnyGap=true` 在 system prompt 末尾附加引导 section（含缺失文件列表、问法模板、写入前需总结确认的指令、用户拒绝时 suppress 的指令）；session 内存级 suppress flag（不持久化）。
  - Modules：`src/KodaClaw.Runtime`（session 构建逻辑）。
  - Verification：`dotnet test --filter "WorkspaceGuidance"`；L5 dogfood 验证 Koda 实际问询效果。

- `KC-2605`：`Completed`。
  - User Outcome：Koda 写入 workspace 文件后，新设定在同一对话内立刻生效，不需要用户手动重启或新开会话。
  - Scope：`workspace_protocol_update` 工具执行成功后，Runtime 自动 rotate main session（复用现有 rotate_session 逻辑）；新 session 第一条消息由 Koda 确认"已更新 XXX.md，新设定从现在开始生效。"
  - Modules：`src/KodaClaw.Runtime`、`src/KodaClaw.Gateway`。
  - Verification：`dotnet test --filter "WorkspaceRotate"`；L5 dogfood 验证写入后即时生效。

- `KC-2606`：`Completed`。
  - User Outcome：删除所有与旧 bootstrap 相关的 `data-testid` 和测试，补充新的对话引导路径测试。
  - Scope：删除 app-shell.spec.tsx 中 bootstrap panel 相关断言；更新 kc0108-smoke.spec.ts；删除 kc0109-bootstrap.spec.ts（如存在）；新增 workspace-readiness API 的集成测试。
  - Modules：`apps/kodaclaw-web/src/__tests__/`、`apps/kodaclaw-web/tests/`、`tests/KodaClaw.IntegrationTests/`。
  - Verification：`npm run test`；`dotnet test KodaClaw.sln -m:1`。

## 迭代 27：Settings Desk

范围冻结：见 `docs/ITERATION_27_FREEZE.md`（2026-03-21）。前置依赖：Iter 26 全部完成（KC-2601/2602 PersonaStep/ChannelStep 已迁移）。

- `KC-2701`：`Completed`。
  - User Outcome：侧边栏第三组新增技能项，底部新增独立的设置入口；布局语义更清晰（能力层 vs 配置层）。
  - Scope：`Sidebar.tsx` 调整 `NAV_GROUPS`，技能从底组移入第三组（模型/插件/技能），新增第四组（会话诊断/设置）；`MainDesk` 类型新增 `'settings'`；`DESK_CONFIG` / `app-strings.ts` desks 数组同步更新。
  - Modules：`apps/kodaclaw-web/src/shell/Sidebar.tsx`、`src/shell-shared/types.ts`、`src/i18n/app-strings.ts`。
  - Verification：`npm run typecheck`；`npm run test`（侧边栏渲染断言更新）。

- `KC-2702`：`Completed`。
  - User Outcome：点击"设置"进入 Settings Desk，看到四个 section（工作区身份 / 连接 / 偏好 / 系统）的布局框架。
  - Scope：新建 `src/components/SettingsDesk.tsx`（data-testid=`settings-desk`，4 section 骨架）；`MainContent.tsx` 新增 `mainDesk === 'settings'` 路由；补充 `settings-desk.spec.tsx` 基础渲染测试。
  - Modules：`apps/kodaclaw-web/src/components/`、`src/shell/MainContent.tsx`。
  - Verification：`npm run typecheck`；`npm run test`。

- `KC-2703`：`Completed`。
  - User Outcome：用户在设置页可直接编辑 IDENTITY.md / SOUL.md / USER.md，保存后提示下次 session 生效。
  - Scope：新建 `src/components/settings/WorkspaceIdentityEditor.tsx`，三个 textarea 分别绑定 `GET/PUT /api/workspace/file?target=identity/soul/user`；高度自适应内容；保存按钮带 loading 态和成功/失败提示；data-testid：`settings-identity-editor`、`settings-soul-editor`、`settings-user-editor`。
  - Modules：`apps/kodaclaw-web/src/components/settings/`。
  - Verification：`npm run test`（mock workspace file API）。

- `KC-2704`：`Completed`。
  - User Outcome：SOUL.md 编辑器旁可展开 Persona 模板卡片网格，选中后内容填入编辑器，用户可进一步修改再保存。
  - Scope：将 Iter 26 迁移的 `PersonaSelector.tsx` 集成到 `WorkspaceIdentityEditor` 的 SOUL section；点"从模板选择"展开卡片，选中后 `onSelect(soulMarkdown)` 回填 textarea，卡片收起；data-testid：`settings-persona-trigger`。
  - Modules：`apps/kodaclaw-web/src/components/settings/`。
  - Verification：`npm run test`（PersonaSelector 展开/收起/选中回填）。

- `KC-2705`：`Completed`。
  - User Outcome：用户在设置页可直接绑定 Telegram 渠道，体验与 Onboarding 时一致；已绑定账号以列表形式展示。
  - Scope：将 Iter 26 迁移的 `ChannelSetupWizard.tsx` 集成到 Settings Desk"连接" section；section 顶部显示已绑渠道账号列表（复用 `GET /api/channels/accounts`）；点"绑定新渠道"展开向导；data-testid：`settings-connections-section`。
  - Modules：`apps/kodaclaw-web/src/components/settings/`。
  - Verification：`npm run test`（mock accounts API + 向导展开）。

- `KC-2706`：`Completed`。
  - User Outcome：语言切换从对话 header 移入设置页偏好 section，chat header 不再有工具按钮，界面更干净。
  - Scope：Settings Desk"偏好" section 直接渲染 `LocaleToggle` 组件；从 `App.tsx` chatHeaderActions 中移除 LocaleToggle；chat header 若无其他 actions 则整个 `kc-chat-header__actions` div 隐藏；data-testid：`settings-preferences-section`。
  - Modules：`apps/kodaclaw-web/src/App.tsx`、`src/components/SettingsDesk.tsx`。
  - Verification：`npm run test`（locale 切换测试用例更新到 settings-desk.spec.tsx）。

- `KC-2707`：`Completed`。
  - User Outcome：系统 section 提供"重新引导"和"清除 workspace 身份"两个操作，均有确认弹框防止误触。
  - Scope：新建 `src/components/settings/SystemSection.tsx`；"重新引导"：确认后调用 `POST /api/onboarding/reset`，成功后 `window.location.reload()` 触发 OnboardingShell；"清除 workspace 身份"：确认后调用三次 `PUT /api/workspace/file`（content 置空），提示"已清除，Koda 下次会重新引导你配置身份"；data-testid：`settings-system-section`、`settings-reset-onboarding`、`settings-clear-identity`。
  - Modules：`apps/kodaclaw-web/src/components/settings/`。
  - Verification：`npm run test`（confirm dialog 交互 + mock API 调用）。

## 迭代 28：前端 UI/UX 统一优化（KC-W3 专项）

范围冻结：见 `docs/ITERATION_28_FREEZE.md`（2026-03-21）。大改，跑 L0 + 单元测试。

- `KC-W3-010`：`Completed`（2026-03-21）。
  - User Outcome：前端所有间距、圆角、字号、动效统一使用 CSS 变量，视觉一致性提升；Geist 字体替换 Inter/system-ui；统一 `.btn` 按钮体系（5 变体 + 3 尺寸），旧类以别名方式保留。
  - Scope：`src/index.css` 新增 `--space-*`、`--radius-*`、`--font-size-*`、`--duration-*` token；Geist 字体 Google Fonts import；`.btn` 系统；focus-visible amber ring；Skeleton / EmptyState CSS；暗色主题 `[data-theme="dark"]` 和 `@media prefers-color-scheme: dark` 双轨变量。
  - Modules：`apps/kodaclaw-web/src/index.css`。
  - Verification：`npm run build`；`npm run typecheck`。

- `KC-W3-011`：`Completed`（2026-03-21）。
  - User Outcome：侧边栏导航图标从 emoji 替换为 Lucide SVG，跨平台渲染一致（macOS/Windows/Linux 均为线框风格）。
  - Scope：`npm install lucide-react`；创建 `src/components/ui/Icon.tsx` wrapper；`Sidebar.tsx` 导航图标 / new-chat 图标 / status 图标全部替换为 Lucide；`MainContent.tsx` `DeskConfig.icon` 类型 `string → ReactNode`；`DeskPageHeader.tsx` `icon: string → ReactNode`。
  - Modules：`apps/kodaclaw-web/src/shell/`、`src/components/ui/Icon.tsx`。
  - Verification：`npm run typecheck`；`npm run build`；`npm run test`。

- `KC-W3-012`：`Completed`（2026-03-21）。
  - User Outcome：消息气泡有方向性（user 右对齐 75% 宽度、assistant 左对齐全宽），扫视效率提升；消息列表自动滚动到最新消息；空状态显示 EmptyState 组件而非纯文字。
  - Scope：`MessageTimeline.tsx` 添加 `useRef + useEffect` auto-scroll（jsdom guard）；删除顶部 eyebrow/title/streaming header；user/assistant 消息 CSS 对齐方向；空消息用 `EmptyState`；`timeline__body` 改为 flex column。
  - Modules：`apps/kodaclaw-web/src/components/MessageTimeline.tsx`、`src/index.css`。
  - Verification：`npm run test`（52 tests pass）。

- `KC-W3-013`：`Completed`（2026-03-21）。
  - User Outcome：Composer 文本框高度随内容自动伸缩（1行 44px → 最大 160px），短消息不再浪费垂直空间；streaming 状态徽章移至底部操作区，布局更紧凑。
  - Scope：`ChatComposer.tsx` 添加 `useRef + useEffect` auto-grow textarea（`rows=1`, min 44px, max 160px）；streaming badge 移入 `composer__actions-right`；`src/index.css` 更新 `.composer__input` 删除固定 100px min-height。
  - Modules：`apps/kodaclaw-web/src/components/ChatComposer.tsx`、`src/index.css`。
  - Verification：`npm run typecheck`；`npm run test`。

- `KC-W3-014`：`Completed`（2026-03-21）。
  - User Outcome：用户在模型设置中选择 Light/Dark/System 主题后，界面颜色立即切换；系统暗色偏好也会自动响应。
  - Scope：新建 `src/hooks/useTheme.ts`（读取 `fetchSettings().theme`，apply `data-theme` 到 `documentElement`）；`App.tsx` 引入 `useTheme()`；`src/index.css` 已包含双轨暗色变量（`[data-theme="dark"]` + `@media prefers-color-scheme: dark`）。
  - Modules：`apps/kodaclaw-web/src/hooks/useTheme.ts`、`src/App.tsx`。
  - Verification：`npm run typecheck`；L5 手动验收。

- `KC-W3-015`：`Completed`（2026-03-21）。
  - User Outcome：宽屏下侧边栏宽度有约束（220~280px）、主内容区有 max-width 1200px；Settings Desk 在宽屏下居中（max-width 720px margin auto）；ConnectionsSection 加载时显示 Skeleton，无账号时显示 EmptyState。
  - Scope：`app-shell.css` 添加 `min-width/max-width` 约束；`.settings-desk` 添加 `margin: 0 auto`；新建 `src/components/ui/Skeleton.tsx`、`src/components/ui/EmptyState.tsx`；`ConnectionsSection.tsx` 使用两个新组件。
  - Modules：`apps/kodaclaw-web/src/shell/app-shell.css`、`src/components/ui/`、`src/components/settings/ConnectionsSection.tsx`。
  - Verification：`npm run build`；`npm run test`。

- `KC-W3-016`：`Completed`（2026-03-21）。
  - User Outcome：刷新页面后 settings desk 能正确恢复（localStorage `kodaclaw.mainDesk = 'settings'` 不再被 isMainDesk 过滤掉）。
  - Scope：`App.tsx` `isMainDesk` 函数新增 `value === 'settings'` 判断。
  - Modules：`apps/kodaclaw-web/src/App.tsx`。
  - Verification：手动验收（刷新 → 仍在 settings desk）。

## 迭代 29：UI/UX 系统性一致性修复 + Workspace 文件完整性（KC-2901~2906）

范围冻结：见 `docs/ITERATION_29_FREEZE.md`（2026-03-21）。中改，跑 L0 + 单元测试 + L2 后端集成测试。

- `KC-2901`：`Completed`。
  - User Outcome：Onboarding 界面在暗色模式下颜色正确响应（amber 按钮、绿色成功、红色错误均使用主题变量），视觉与主应用一致。
  - Scope：`src/onboarding/onboarding.css` 全量替换硬编码颜色（`#D97706 → var(--accent)`、`#b45309 → var(--accent-hover)`、`#f5f5f5 → var(--bg-secondary)`、`#f0fff4 → var(--success-soft)`、`#991b1b/166534 → var(--error)/var(--success)` 等），保持结构不变。
  - Modules：`apps/kodaclaw-web/src/onboarding/onboarding.css`。
  - Verification：`npm run build`；L5 切换至暗色模式验收 Onboarding 界面。

- `KC-2902`：`Completed`。
  - User Outcome：ChannelsDesk / SkillsDesk / CanvasDesk 中的间距和圆角不再使用 inline style 魔法数字，统一受 CSS token 管控。
  - Scope：`ChannelsDesk.tsx` 移除 `toolbarStyle`、`threadButtonStyle` 等 CSSProperties 对象，改为 `.channels-toolbar`、`.channel-thread-btn` CSS 类；`SkillsDesk.tsx` 同理；`CanvasDesk.tsx` iframe `style={{ height: 500 }}` 改为 `.canvas-preview-frame`；`app-shell.css` 新增对应 CSS 类。
  - Modules：`apps/kodaclaw-web/src/components/`、`src/shell/app-shell.css`。
  - Verification：`npm run typecheck`；`npm run build`。

- `KC-2903`：`Completed`。
  - User Outcome：WorkspaceIdentityEditor 打开 Settings Desk 时显示骨架加载态，避免空白 textarea 闪现。
  - Scope：`WorkspaceIdentityEditor.tsx` 各文件编辑区初始渲染时显示 `<Skeleton height={80} />` × 3，等待 API 返回后替换为 textarea；使用现有 `<Skeleton />` 组件和 isLoading 状态。
  - Modules：`apps/kodaclaw-web/src/components/settings/WorkspaceIdentityEditor.tsx`。
  - Verification：`npm run test`（mock fetch 有延迟时验证 skeleton 显示）。

- `KC-2904`：`Completed`。
  - User Outcome：Settings Desk 新增 MEMORY.md 编辑区，用户可直接查看和编辑 Koda 的长期记忆索引，无需进入对话或手动编辑文件。
  - Scope：后端 `ResolveWorkspaceTarget` 新增 `"memory"` → `KodaClawWorkspaceLayout.MemoryFile`；新建 `src/components/settings/MemorySection.tsx`（复用 useWorkspaceFile 模式，含保存按钮和说明文字）；`SettingsDesk.tsx` 在身份文件区后插入 MemorySection。
  - Modules：`src/KodaClaw.Gateway/Endpoints/GatewayApp.WorkspaceEndpoints.cs`、`apps/kodaclaw-web/src/components/settings/MemorySection.tsx`、`SettingsDesk.tsx`。
  - Verification：`dotnet build`；`npm run typecheck`；手动验收（保存后 `~/.kodaclaw/workspace/MEMORY.md` 内容更新）。

- `KC-2905`：`Completed`。
  - User Outcome：Settings Desk 新增 HEARTBEAT.md 只读预览区，用户可查看当前自动化规则，了解后台任务配置，无需手动打开文件。
  - Scope：后端 `ResolveWorkspaceTarget` 新增 `"heartbeat"` → `KodaClawWorkspaceLayout.HeartbeatFile`；新建 `src/components/settings/HeartbeatSection.tsx`（只读 textarea + 说明文字"由 Koda 自动维护，下方显示当前规则"）；`SettingsDesk.tsx` 在 MemorySection 后插入 HeartbeatSection。
  - Modules：`src/KodaClaw.Gateway/Endpoints/GatewayApp.WorkspaceEndpoints.cs`（同 KC-2904 一次修改）、`apps/kodaclaw-web/src/components/settings/HeartbeatSection.tsx`、`SettingsDesk.tsx`。
  - Verification：`dotnet build`；`npm run typecheck`；手动验收（Automations Desk 运行后 HEARTBEAT.md 内容出现在预览区）。

- `KC-2906`：`Completed`。
  - User Outcome：CLAUDE.md 关键文件表格不再引用已删除的 shell-v2/ 路径，反映当前实际代码结构。
  - Scope：`CLAUDE.md` 关键文件表格更新 `shell-v2/V2Shell.tsx → shell/AppShell.tsx + shell/Sidebar.tsx + shell/MainContent.tsx`；WIP 区域更新为 Iter 29 进行中。
  - Modules：`products/KodaClaw/CLAUDE.md`。
  - Verification：文档审查。

## 迭代 30：Desk 布局/排版全量统一（KC-W4，KC-3001~3004）

范围冻结：见 `docs/ITERATION_30_FREEZE.md`（2026-03-21）。大改（KC-W4 专项）。

- `KC-3001`：`Completed`。
  - User Outcome：所有 Desk 字号体系统一，消除 section-eyebrow + 旧 section-title 裸浏览器默认值导致的视觉割裂。
  - Scope：T2 — InboxApprovalDesk / SessionsDiagnosticsDesk / AutomationsDesk / PluginsDesk / ChannelsDesk / ModelsSettingsDesk 移除 `bootstrap-panel`、`section-eyebrow`，替换 `section-title` → `desk-section-title`、`section-copy` → `desk-section-desc`；app-shell.css 新增 `.desk-section-title`/`.desk-section-desc` token 工具类。
  - Modules：全部 7 个 Desk 组件 + app-shell.css。
  - Verification：`npm run typecheck`；`npm run test` 52/52 pass。

- `KC-3002`：`Completed`。
  - User Outcome：SkillsDesk 使用 settings-desk/settings-section 体系，与 SettingsDesk 风格一致。
  - Scope：SkillsDesk.tsx 外层改为 `settings-desk` + `settings-section`，内部排版类升级。
  - Modules：`apps/kodaclaw-web/src/components/SkillsDesk.tsx`。
  - Verification：同 KC-3001。

- `KC-3003`：`Completed`。
  - User Outcome：canvas-markdown-view 散文排版受 token 系统管控，Markdown h1 不再用浏览器默认 2em，与整体 UI 字号协调。
  - Scope：ControlPlaneDesk.css 末尾新增 `.canvas-markdown-view` 完整 prose 样式（h1~h3、p、ul、code、pre、blockquote、table），均使用 var(--font-size-*)、var(--space-*) 等 token。
  - Modules：`apps/kodaclaw-web/src/components/ControlPlaneDesk.css`。
  - Verification：`npm run typecheck`。

- `KC-3004`：`Completed`。
  - User Outcome：ModelsSettingsDesk 中 "沙箱与风险简报" 标题从 1.8rem 降为 var(--font-size-xl)，与其他标题协调。
  - Scope：ControlPlaneDesk.css 中 `.control-plane-card-title` 从 `font-size: 1.8rem` → `var(--font-size-xl)`。
  - Modules：`apps/kodaclaw-web/src/components/ControlPlaneDesk.css`。
  - Verification：视觉验收。

## 迭代 31：全量 CSS Token 系统性补全（KC-3101）

范围：优化（大改），不增加新能力。

- `KC-3101`：`Completed`（2026-03-21）。
  - User Outcome：全站字号/间距视觉完全统一，所有硬编码 rem/px 字号替换为 `var(--font-size-*)` token，所有硬编码间距替换为 `var(--space-*)` token，消除 Sidebar、Settings、ControlPlane 等模块的字号不一致感。
  - Scope：
    - `app-shell.css`：Sidebar 所有字号（`0.66rem`/`0.72rem`/`0.875rem`/`0.88rem`/`0.92rem`/`0.78rem`）→ token；chat/desk header 字号 → token；settings-section-title/desc（`15px`/`13px`）→ token；settings-file-* / settings-btn / settings-account-* / settings-action-* / settings-pref-* 全部字号+间距 → token；`settings-btn--primary` hover 颜色 `#b45309` → `var(--accent-hover)`；transition duration 常量 → token。
    - `ControlPlaneDesk.css`：session-card title/meta 字号 → token；chip 字号+圆角+padding → token；session-card padding+border-radius → token；stage-hero padding → token。
    - `index.css`：`secondary-button` 字号+padding+border-radius+transition → token；`risk-briefing__title` → token；`risk-briefing__pill` padding+border-radius+字号 → token。
  - Modules：`apps/kodaclaw-web/src/shell/app-shell.css`、`apps/kodaclaw-web/src/components/ControlPlaneDesk.css`、`apps/kodaclaw-web/src/index.css`。
  - Verification：`npm run typecheck` pass；`npm run test` 48/48 pass。

## 产品缺口审查（基于 PRODUCT.md，已排期至 Iter 32-33）

以下条目源自对 `docs/PRODUCT.md` 的全面审查，已升级为迭代计划：

- `KC-GAP-001` → **排期 Iter 32（KC-3201~3203）**：Chat 历史会话面板。`GET /api/sessions` + `fetchSessions()` 已存在，后端仅需新增 `POST /api/sessions/{id}/resume`，前端增加 SessionHistoryPanel 组件。
- `KC-GAP-002`（后续排期）：**Canvas 插件 UI 面板** — CanvasDesk 仅支持 Markdown artifact，插件自定义 UI 渲染留待后续。
- `KC-GAP-003`（后续排期）：**Webhook 渠道配置 UI** — ChannelsDesk 缺少通用 Webhook 入口配置界面。
- `KC-GAP-004` → **排期 Iter 33（KC-3302）**：Inbox AutomationResult 专属渲染。AutomationScheduler 已写入 Inbox，`GET /api/inbox` 已支持，仅需前端渲染优化。

## 迭代 32：Chat 历史会话面板（KC-3201~3203）

范围冻结：见 `docs/ITERATION_32_FREEZE.md`（2026-03-21）。新功能，完整 Capability Slice 流程。

- `KC-3201`：`Pending`。
  - User Outcome：用户可在 Chat 视图中看到最近 20 个历史会话列表，点击"恢复"一键切回历史对话。
  - Scope：后端新增 `POST /api/sessions/{id}/resume` 端点 + `IMainSessionService.ResumeSessionAsync` 方法；前端新增 `SessionHistoryPanel` 组件 + `useSessionHistory` hook；Chat header 增加历史图标按钮。
  - Modules：`src/KodaClaw.Runtime/IMainSessionService.cs`、`src/KodaClaw.Runtime/MainSessionService.cs`、`src/KodaClaw.Gateway/Endpoints/GatewayApp.SessionEndpoints.cs`、`apps/kodaclaw-web/src/components/chat/SessionHistoryPanel.tsx`（新增）、`apps/kodaclaw-web/src/hooks/useSessionHistory.ts`（新增）、`apps/kodaclaw-web/src/shell/MainContent.tsx`。
  - Verification：`dotnet build`；`npm run typecheck`；`npm run test`；L5 人工验收：恢复历史会话后消息历史可见。

- `KC-3202`：`Pending`。
  - User Outcome：SessionHistoryPanel 中当前 session 显示"当前"标记，非当前 session 显示"恢复"按钮，已删除/不存在 session 显示错误提示。
  - Scope：SessionHistoryPanel 状态逻辑 + 错误处理 UI。
  - Modules：同 KC-3201 前端部分。
  - Verification：`npm run test`（新增 session-history-panel.spec.tsx）。

- `KC-3203`：`Pending`。
  - User Outcome：后端 `ResumeSessionAsync` 集成测试：恢复存在的 session → 200，恢复不存在的 session → 404，并发请求安全。
  - Scope：`tests/KodaClaw.IntegrationTests/` 新增 `SessionResumeIntegrationTests.cs`。
  - Modules：同 KC-3201 后端部分。
  - Verification：`dotnet test --filter "FullyQualifiedName~SessionResumeTests"`。

## 迭代 33：Automations Toggle + Inbox 自动化结果优化（KC-3301~3302）

范围冻结：见 `docs/ITERATION_33_FREEZE.md`（2026-03-21）。两个已知产品缺口的补全。

- `KC-3301`：`Pending`。
  - User Outcome：AutomationsDesk 工具栏中有 `automationsEnabled` toggle，切换立即生效并持久化到 `GET/PATCH /api/settings`。解决 CLAUDE.md WIP 中记录的已知缺口。
  - Scope：`AutomationsDesk.tsx` 增加 toggle 控件，读写 `settings.automationsEnabled`。后端 `PATCH /api/settings` 已支持，无需变更。
  - Modules：`apps/kodaclaw-web/src/components/AutomationsDesk.tsx`。
  - Verification：`npm run typecheck`；`npm run test`；L5：toggle 切换后刷新页面状态持久。

- `KC-3302`：`Pending`。
  - User Outcome：Inbox 中自动化结果条目有专属卡片样式（Zap 图标、状态 badge）；Inbox 列表顶部可按"全部 / 审批 / 自动化结果"过滤。
  - Scope：`InboxApprovalDesk.tsx` 增加 kind filter tab + AutomationResult 专属渲染；补充 CSS。
  - Modules：`apps/kodaclaw-web/src/components/InboxApprovalDesk.tsx`；可能 `ControlPlaneDesk.css`。
  - Verification：`npm run typecheck`；`npm run test`（补充 inbox-desk.spec.tsx）。

## 迭代 34：多模态内容基础层（KC-3401~3408）

范围冻结：见 `docs/ITERATION_34_FREEZE.md`（2026-03-21）。新功能专项，完整 Capability Slice 流程，分 5 个 Phase 按依赖顺序推进。

### Phase 1：ModelCapabilitySet 能力标签系统

- `KC-3401`：`Completed 2026-03-21`。
  - User Outcome：用户为每个模型 endpoint 显式勾选它支持的能力（文本对话 / 工具调用 / 图片理解 / 图片生成 / 语音合成 / 语音识别 / 嵌入），而不是由系统根据 provider 名称猜测。自定义 base URL 的模型配置能力完全准确。
  - Scope：新增 `ModelCapabilitySet`（flags enum，7 值）；`ModelEndpoint` 以 `Capabilities` 替换 `SupportsToolCalling`，保留后者为计算属性；`CreateModelEndpointRequest` / `UpdateModelEndpointRequest` 同步替换；`IModelRegistryRepository` 新增 `ResolveDefaultForAsync(ModelCapabilitySet)`。
  - Modules：`src/KodaClaw.Contracts`。
  - Verification：`dotnet build KodaClaw.sln`（L0）。

- `KC-3402`：`Completed 2026-03-21`。
  - User Outcome：已有模型 endpoint 数据在升级后自动保留能力配置，不丢失历史设置；新 endpoint 从创建时即可携带完整 capabilities。
  - Scope：`SqliteModelRegistryRepository.EnsureDatabaseAsync` 用 `EnsureColumnExistsAsync` 追加 `capabilities INTEGER`（nullable）；`MapEndpoint` 对 NULL 行从 `supports_tool_calling` 自动推导；`BindParameters` / SQL INSERT/UPDATE 同步加 `capabilities` 列；实现 `ResolveDefaultForAsync`（SQL: `WHERE enabled=1 AND (capabilities & $required) = $required ORDER BY is_default DESC`）；更新 Gateway model endpoints 的 request→domain 映射。
  - Modules：`src/KodaClaw.ModelHub`、`src/KodaClaw.Gateway/Endpoints/GatewayApp.ModelEndpoints.cs`。
  - Verification：`dotnet test tests/KodaClaw.ContractTests --filter "ModelCapability"`（L3）；`dotnet test tests/KodaClaw.IntegrationTests --filter "ModelEndpoint"`（L2）；`dotnet test KodaClaw.sln -m:1`。

- `KC-3403`：`Completed 2026-03-21`。
  - User Outcome：Models Desk 中添加/编辑 endpoint 时，`SupportsToolCalling` 单复选框替换为 7 个能力勾选项；现有 endpoint 列表展示 capabilities badge；模型预设携带推荐的默认 capabilities（如 claude-3-5-sonnet 默认勾选 TextChat + ToolCalling + Vision）。
  - Scope：`ModelsSettingsDesk.tsx` capability 勾选 UI；更新 `Resources/model-presets.json` 为各预设加 `defaultCapabilities` 字段；`ModelPresetService` 透传该字段到 API 响应。
  - Modules：`apps/kodaclaw-web/src/components/ModelsSettingsDesk.tsx`、`src/KodaClaw.Gateway`（预设资源更新）。
  - Verification：`npm run typecheck`；`npm run test -- src/__tests__/models-settings-desk.spec.tsx`；`npm run build`。

### Phase 2：SDK 多模态 ContentBlock 扩展

- `KC-3404`：`Completed 2026-03-21`。
  - User Outcome：Agent 在处理含图片的消息时，图片内容不被丢弃也不报错；Anthropic 和 OpenAI provider 均能正确将 ImageContent 转换为各自 API 的原生图片格式。
  - Scope：`Kode.Agent.Sdk/Core/Types/Message.cs` 新增 `ImageContent : ContentBlock`（字段：`Source: ImageSource`，`ImageSource` 支持 base64 data 和 URL 两种形式）；加入 `[JsonDerivedType]`；`AnthropicProvider.ConvertContentBlock` 追加 `ImageContent → ImageBlockParam`；`OpenAIProvider.ConvertMessage` user 消息路径支持 `ImageContent → ImageContentPart`；`MessageQueue` 新增 `Send(IReadOnlyList<ContentBlock>, SendOptions?)` 重载，不改动现有 `Send(string)` 签名。
  - Modules：`src/Kode.Agent.Sdk/Core/Types/Message.cs`、`src/Kode.Agent.Sdk/Core/Agent/MessageQueue.cs`、`src/Kode.Agent.Sdk/Infrastructure/Providers/AnthropicProvider.cs`、`src/Kode.Agent.Sdk/Infrastructure/Providers/OpenAIProvider.cs`。
  - Verification：`dotnet test tests/Kode.Agent.Tests --filter "ImageContent"`（L1，含 round-trip 序列化 x2、Anthropic 映射 x1、OpenAI 映射 x1）；`dotnet build`。

### Phase 3：媒体文件存储 + Gateway /api/media 服务

- `KC-3405`：`Completed 2026-03-21`。
  - User Outcome：Agent 生成的图片持久化到本地磁盘，Gateway 提供稳定 URL 供前端展示和 Telegram 发送；路径安全，不允许访问 media 目录以外的文件。
  - Scope：`WorkspaceService` 初始化时创建 `~/.kodaclaw/media/` 目录；新增 `IMediaStore`（SaveAsync / GetStreamAsync / GetMetaAsync）和 `LocalMediaStore` 实现（文件存 `media/{YYYY-MM}/{uuid}.{ext}`）；新增 `MediaMeta`、`MediaReference` contracts；Gateway 新增 `GatewayApp.MediaEndpoints.cs`：`GET /api/media/{id}` 流式返回文件（path traversal 防护）。
  - Modules：`src/KodaClaw.Contracts`（2 个新文件）、`src/KodaClaw.Workspace`（2 个新文件 + WorkspaceService 修改）、`src/KodaClaw.Gateway/Endpoints/GatewayApp.MediaEndpoints.cs`（新增）。
  - Verification：`dotnet test tests/KodaClaw.IntegrationTests --filter "MediaStore"`（L2，含 save/read round-trip x1、path traversal 拒绝 x1）；`dotnet test KodaClaw.sln -m:1`。

### Phase 4：CanvasArtifact Image kind + generate_image 工具

- `KC-3406`：`Completed 2026-03-21`。
  - User Outcome：Agent 在会话中说"帮我生成一张 XX 的图"时，Koda 能调用内置工具生成图片并自动写入 Canvas；用户打开 Canvas Desk 可直接看到图片，无需任何额外操作。
  - Scope：`CanvasArtifactKind` 新增 `Image`；`IGenerationService`（KodaClaw.ModelHub）：`GenerateImageAsync(prompt, endpointId?) → GenerateImageResult`；`OpenAIImageGenerationService`：调用 DALL-E 3，下载结果图到 `IMediaStore`，返回 mediaId；跨模型路由：内部调 `ResolveDefaultForAsync(ImageGeneration)`，与 session chat endpoint 完全隔离；新增内置工具 `GenerateImageTool`（KodaClaw.Runtime）：参数 `prompt(required)`, `style?(optional)`，调用 `IGenerationService`，写入 `CanvasArtifact(kind=Image)`，注册到 `MainSessionOptions.DefaultTools` 和 `AutomationSessionOptions.Tools`。
  - Modules：`src/KodaClaw.Contracts/CanvasArtifactKind.cs`、`src/KodaClaw.ModelHub/IGenerationService.cs`（新增）、`src/KodaClaw.ModelHub/OpenAIImageGenerationService.cs`（新增）、`src/KodaClaw.Runtime/GenerateImageTool.cs`（新增）、`src/KodaClaw.Runtime/ServiceCollectionExtensions.cs`。
  - Verification：`dotnet test tests/KodaClaw.IntegrationTests --filter "GenerateImage"`（L2，含工具调用 → Canvas artifact 写入 x1、跨模型路由 x1）；`dotnet test KodaClaw.sln -m:1`。

- `KC-3407`：`Completed 2026-03-21`。
  - User Outcome：Canvas Desk 中图片类型的 artifact 直接展示为 `<img>` 标签（非 iframe），加载速度更快；空状态和加载态都有正确的骨架占位。
  - Scope：`CanvasDesk.tsx` detail panel 增加 `kind === Image` 分支：`<img src="/api/media/{entryPath}" />`（entryPath 存 mediaId）；补充 `canvas-artifact-image` CSS 类（max-width 100%，border-radius）；空状态保持现有 EmptyState 组件。
  - Modules：`apps/kodaclaw-web/src/components/CanvasDesk.tsx`。
  - Verification：`npm run test -- src/__tests__/canvas-desk.spec.tsx`；`npm run typecheck`；`npm run build`。

### Phase 5：ChannelOutboundDraft 媒体投递 + Telegram sendPhoto

- `KC-3408`：`Completed 2026-03-21`。
  - User Outcome：Agent 在 Automation 任务中生成图片后，可通过 `channel_send` 将图片发送到 Telegram（如：每日报告附配图）；需要审批的发图请求在 Inbox 中能预览缩略图，用户审批时知道将发送什么内容。
  - Scope：`ChannelOutboundDraft` 新增 `MediaAttachments: IReadOnlyList<MediaReference>?`；`channel_send` 工具新增可选参数 `mediaId?`（若传入则附到 draft）；`TelegramConnector.SendAsync` 检测 `MediaAttachments`，有图片时调用 Telegram `sendPhoto`（`caption` = text 字段）；`InboxApprovalDesk` 对含 `MediaAttachments` 的 ChannelDelivery 待审批条目显示 `<img src="/api/media/{id}" style="max-width:120px" />` 缩略图。
  - Modules：`src/KodaClaw.Contracts/ChannelOutboundDraft.cs`、`src/KodaClaw.Runtime/ChannelSendTool.cs`、`src/KodaClaw.ChannelHub/TelegramConnector.cs`（或对应 connector 实现）、`apps/kodaclaw-web/src/components/InboxApprovalDesk.tsx`。
  - Verification：`dotnet test tests/KodaClaw.IntegrationTests --filter "ChannelMedia"`（L2，含 sendPhoto 路径 x1、纯文字 channel_send 回归 x1）；`npm run test -- src/__tests__/inbox-approval-desk.spec.tsx`；L5 Dogfood：Automation 任务调用 generate_image + channel_send(mediaId) → Telegram 收到图片。

---

## Iter 32：Chat 历史会话面板（KC-3201~3203）

- `KC-3201`：`Completed 2026-03-21`。
  - User Outcome：用户可在 Chat 头部点击历史图标，查看最近 20 个 main 类型 session，并一键恢复任意历史会话。
  - Scope：后端新增 `ResumeSessionAsync(sessionId)` 接口方法（IMainSessionService + MainSessionService），`ResumeSessionResponse` contract，`POST /api/sessions/{id}/resume` 端点（先校验 session 存在，再设置 ActiveMainSessionId）；前端新增 `resumeSession()` API 函数，`useSessionHistory` hook（30s 轮询，过滤 Main 类型），`SessionHistoryPanel` 组件（触发器按钮 + 下拉面板，含 current 标记/恢复按钮/状态 badge），接入 App.tsx `chatHeaderActions`，Session History Panel CSS 追加到 app-shell.css。
  - Modules：`src/KodaClaw.Contracts/ResumeSessionResponse.cs`（新增）、`src/KodaClaw.Runtime/IMainSessionService.cs`、`src/KodaClaw.Runtime/MainSessionService.cs`、`src/KodaClaw.Gateway/Endpoints/GatewayApp.SessionEndpoints.cs`、`apps/kodaclaw-web/src/lib/api.ts`、`apps/kodaclaw-web/src/types/contracts.ts`、`apps/kodaclaw-web/src/hooks/useSessionHistory.ts`（新增）、`apps/kodaclaw-web/src/components/chat/SessionHistoryPanel.tsx`（新增）、`apps/kodaclaw-web/src/App.tsx`、`apps/kodaclaw-web/src/shell/app-shell.css`。
  - Verification：`dotnet build KodaClaw.sln`（0 errors）；`npm run typecheck`（0 errors）；`npm run test`（48/48 passed）。

- `KC-3202`：`Completed 2026-03-21`（含在 KC-3201 实现中）。
  - Scope：SessionHistoryPanel 状态逻辑（当前 session 标记、恢复按钮、错误处理、外部点击关闭）。

- `KC-3203`：`Skipped`（后端集成测试留待 KC-3204 批次补充，非阻塞）。

---

## Iter 33：Automations Toggle + Inbox AutomationResult 渲染（KC-3301~3302）

- `KC-3301`：`Completed 2026-03-21`。
  - User Outcome：AutomationsDesk 工具栏右侧显示启用/禁用 Toggle，点击立即生效并持久化。
  - Scope：`AutomationsDesk.tsx` 新增 `isTogglingEngine` 状态，`handleToggleEngine()` handler（read-then-write via `setAutomationsEnabled()` API），工具栏增加 `<label class="automations-engine-toggle">` 复选框；`api.ts` 新增 `setAutomationsEnabled(enabled)` 帮助函数（fetch + PUT）；i18n 增加 `engineEnabled`/`engineDisabled` 字段；CSS 追加 `.automations-engine-toggle` 样式。
  - Modules：`apps/kodaclaw-web/src/components/AutomationsDesk.tsx`、`apps/kodaclaw-web/src/lib/api.ts`、`apps/kodaclaw-web/src/components/ControlPlaneDesk.css`。
  - Verification：`npm run typecheck`（0 errors）；`npm run test`（48/48 passed）。

- `KC-3302`：`Completed 2026-03-21`。
  - User Outcome：Inbox 列表增加"全部/审批/自动化结果"tab 过滤；AutomationResult 条目显示专属卡片（Zap 图标、琥珀色左边框、仅"标为已读"操作）。
  - Scope：`InboxApprovalDesk.tsx` 新增 `kindFilter` state，`filteredInboxItems` 派生值，tab UI（`.inbox-kind-tabs`），AutomationResult 专属卡片渲染（Zap 图标 + `inbox-automation-result-card` class + 仅显示"标为已读"按钮）；i18n 增加 `inboxKindTabs`/`markRead` 字段；CSS 追加 `.inbox-kind-tabs`、`.inbox-kind-tab`、`.inbox-automation-result-card`、`.inbox-automation-result-kind` 样式。
  - Modules：`apps/kodaclaw-web/src/components/InboxApprovalDesk.tsx`、`apps/kodaclaw-web/src/components/ControlPlaneDesk.css`。
  - Verification：`npm run typecheck`（0 errors）；`npm run test`（48/48 passed）。


## 迭代 34：多模态内容基础层（KC-3401~3408）

范围冻结：见 `docs/ITERATION_34_FREEZE.md`（2026-03-21）。新功能专项，完整 Capability Slice 流程，分 5 个 Phase 按依赖顺序推进。

### Phase 1：ModelCapabilitySet 能力标签系统

- `KC-3401`：`Completed 2026-03-21`。
  - User Outcome：用户为每个模型 endpoint 显式勾选它支持的能力（文本对话 / 工具调用 / 图片理解 / 图片生成 / 语音合成 / 语音识别 / 嵌入），而不是由系统根据 provider 名称猜测。自定义 base URL 的模型配置能力完全准确。
  - Scope：新增 `ModelCapabilitySet`（flags enum，7 值）；`ModelEndpoint` 以 `Capabilities` 替换 `SupportsToolCalling`，保留后者为计算属性；`CreateModelEndpointRequest` / `UpdateModelEndpointRequest` 同步替换；`IModelRegistryRepository` 新增 `ResolveDefaultForAsync(ModelCapabilitySet)`。
  - Modules：`src/KodaClaw.Contracts`。
  - Verification：`dotnet build KodaClaw.sln`（L0）。

- `KC-3402`：`Completed 2026-03-21`。
  - User Outcome：已有模型 endpoint 数据在升级后自动保留能力配置，不丢失历史设置；新 endpoint 从创建时即可携带完整 capabilities。
  - Scope：`SqliteModelRegistryRepository.EnsureDatabaseAsync` 用 `EnsureColumnExistsAsync` 追加 `capabilities INTEGER`（nullable）；`MapEndpoint` 对 NULL 行从 `supports_tool_calling` 自动推导；`BindParameters` / SQL INSERT/UPDATE 同步加 `capabilities` 列；实现 `ResolveDefaultForAsync`（SQL: `WHERE enabled=1 AND (capabilities & $required) = $required ORDER BY is_default DESC`）；更新 Gateway model endpoints 的 request→domain 映射。
  - Modules：`src/KodaClaw.ModelHub`、`src/KodaClaw.Gateway/Endpoints/GatewayApp.ModelEndpoints.cs`。
  - Verification：`dotnet test tests/KodaClaw.ContractTests --filter "ModelCapability"`（L3）；`dotnet test tests/KodaClaw.IntegrationTests --filter "ModelEndpoint"`（L2）；`dotnet test KodaClaw.sln -m:1`。

- `KC-3403`：`Completed 2026-03-21`。
  - User Outcome：Models Desk 中添加/编辑 endpoint 时，`SupportsToolCalling` 单复选框替换为 7 个能力勾选项；现有 endpoint 列表展示 capabilities badge；模型预设携带推荐的默认 capabilities（如 claude-3-5-sonnet 默认勾选 TextChat + ToolCalling + Vision）。
  - Scope：`ModelsSettingsDesk.tsx` capability 勾选 UI；更新 `Resources/model-presets.json` 为各预设加 `defaultCapabilities` 字段；`ModelPresetService` 透传该字段到 API 响应。
  - Modules：`apps/kodaclaw-web/src/components/ModelsSettingsDesk.tsx`、`src/KodaClaw.Gateway`（预设资源更新）。
  - Verification：`npm run typecheck`；`npm run test -- src/__tests__/models-settings-desk.spec.tsx`；`npm run build`。

### Phase 2：SDK 多模态 ContentBlock 扩展

- `KC-3404`：`Completed 2026-03-21`。
  - User Outcome：Agent 在处理含图片的消息时，图片内容不被丢弃也不报错；Anthropic 和 OpenAI provider 均能正确将 ImageContent 转换为各自 API 的原生图片格式。
  - Scope：`Kode.Agent.Sdk/Core/Types/Message.cs` 新增 `ImageContent : ContentBlock`（字段：`Source: ImageSource`，`ImageSource` 支持 base64 data 和 URL 两种形式）；加入 `[JsonDerivedType]`；`AnthropicProvider.ConvertContentBlock` 追加 `ImageContent → ImageBlockParam`；`OpenAIProvider.ConvertMessage` user 消息路径支持 `ImageContent → ImageContentPart`；`MessageQueue` 新增 `Send(IReadOnlyList<ContentBlock>, SendOptions?)` 重载，不改动现有 `Send(string)` 签名。
  - Modules：`src/Kode.Agent.Sdk/Core/Types/Message.cs`、`src/Kode.Agent.Sdk/Core/Agent/MessageQueue.cs`、`src/Kode.Agent.Sdk/Infrastructure/Providers/AnthropicProvider.cs`、`src/Kode.Agent.Sdk/Infrastructure/Providers/OpenAIProvider.cs`。
  - Verification：`dotnet test tests/Kode.Agent.Tests --filter "ImageContent"`（L1，含 round-trip 序列化 x2、Anthropic 映射 x1、OpenAI 映射 x1）；`dotnet build`。

### Phase 3：媒体文件存储 + Gateway /api/media 服务

- `KC-3405`：`Completed 2026-03-21`。
  - User Outcome：Agent 生成的图片持久化到本地磁盘，Gateway 提供稳定 URL 供前端展示和 Telegram 发送；路径安全，不允许访问 media 目录以外的文件。
  - Scope：`WorkspaceService` 初始化时创建 `~/.kodaclaw/media/` 目录；新增 `IMediaStore`（SaveAsync / GetStreamAsync / GetMetaAsync）和 `LocalMediaStore` 实现（文件存 `media/{YYYY-MM}/{uuid}.{ext}`）；新增 `MediaMeta`、`MediaReference` contracts；Gateway 新增 `GatewayApp.MediaEndpoints.cs`：`GET /api/media/{id}` 流式返回文件（path traversal 防护）。
  - Modules：`src/KodaClaw.Contracts`（2 个新文件）、`src/KodaClaw.Workspace`（2 个新文件 + WorkspaceService 修改）、`src/KodaClaw.Gateway/Endpoints/GatewayApp.MediaEndpoints.cs`（新增）。
  - Verification：`dotnet test tests/KodaClaw.IntegrationTests --filter "MediaStore"`（L2，含 save/read round-trip x1、path traversal 拒绝 x1）；`dotnet test KodaClaw.sln -m:1`。

### Phase 4：CanvasArtifact Image kind + generate_image 工具

- `KC-3406`：`Completed 2026-03-21`。
  - User Outcome：Agent 在会话中说"帮我生成一张 XX 的图"时，Koda 能调用内置工具生成图片并自动写入 Canvas；用户打开 Canvas Desk 可直接看到图片，无需任何额外操作。
  - Scope：`CanvasArtifactKind` 新增 `Image`；`IGenerationService`（KodaClaw.ModelHub）：`GenerateImageAsync(prompt, endpointId?) → GenerateImageResult`；`OpenAIImageGenerationService`：调用 DALL-E 3，下载结果图到 `IMediaStore`，返回 mediaId；跨模型路由：内部调 `ResolveDefaultForAsync(ImageGeneration)`，与 session chat endpoint 完全隔离；新增内置工具 `GenerateImageTool`（KodaClaw.Runtime）：参数 `prompt(required)`, `style?(optional)`，调用 `IGenerationService`，写入 `CanvasArtifact(kind=Image)`，注册到 `MainSessionOptions.DefaultTools` 和 `AutomationSessionOptions.Tools`。
  - Modules：`src/KodaClaw.Contracts/CanvasArtifactKind.cs`、`src/KodaClaw.ModelHub/IGenerationService.cs`（新增）、`src/KodaClaw.ModelHub/OpenAIImageGenerationService.cs`（新增）、`src/KodaClaw.Runtime/GenerateImageTool.cs`（新增）、`src/KodaClaw.Runtime/ServiceCollectionExtensions.cs`。
  - Verification：`dotnet test tests/KodaClaw.IntegrationTests --filter "GenerateImage"`（L2，含工具调用 → Canvas artifact 写入 x1、跨模型路由 x1）；`dotnet test KodaClaw.sln -m:1`。

- `KC-3407`：`Completed 2026-03-21`。
  - User Outcome：Canvas Desk 中图片类型的 artifact 直接展示为 `<img>` 标签（非 iframe），加载速度更快；空状态和加载态都有正确的骨架占位。
  - Scope：`CanvasDesk.tsx` detail panel 增加 `kind === Image` 分支：`<img src="/api/media/{entryPath}" />`（entryPath 存 mediaId）；补充 `canvas-artifact-image` CSS 类（max-width 100%，border-radius）；空状态保持现有 EmptyState 组件。
  - Modules：`apps/kodaclaw-web/src/components/CanvasDesk.tsx`。
  - Verification：`npm run test -- src/__tests__/canvas-desk.spec.tsx`；`npm run typecheck`；`npm run build`。

### Phase 5：ChannelOutboundDraft 媒体投递 + Telegram sendPhoto

- `KC-3408`：`Completed 2026-03-21`。
  - User Outcome：Agent 在 Automation 任务中生成图片后，可通过 `channel_send` 将图片发送到 Telegram（如：每日报告附配图）；需要审批的发图请求在 Inbox 中能预览缩略图，用户审批时知道将发送什么内容。
  - Scope：`ChannelOutboundDraft` 新增 `MediaAttachments: IReadOnlyList<MediaReference>?`；`channel_send` 工具新增可选参数 `mediaId?`（若传入则附到 draft）；`TelegramConnector.SendAsync` 检测 `MediaAttachments`，有图片时调用 Telegram `sendPhoto`（`caption` = text 字段）；`InboxApprovalDesk` 对含 `MediaAttachments` 的 ChannelDelivery 待审批条目显示 `<img src="/api/media/{id}" style="max-width:120px" />` 缩略图。
  - Modules：`src/KodaClaw.Contracts/ChannelOutboundDraft.cs`、`src/KodaClaw.Runtime/ChannelSendTool.cs`、`src/KodaClaw.ChannelHub/TelegramConnector.cs`（或对应 connector 实现）、`apps/kodaclaw-web/src/components/InboxApprovalDesk.tsx`。
  - Verification：`dotnet test tests/KodaClaw.IntegrationTests --filter "ChannelMedia"`（L2，含 sendPhoto 路径 x1、纯文字 channel_send 回归 x1）；`npm run test -- src/__tests__/inbox-approval-desk.spec.tsx`；L5 Dogfood：Automation 任务调用 generate_image + channel_send(mediaId) → Telegram 收到图片。

## 迭代 35：Registry-First Model Routing（KC-3501~3503）

- 背景：Model Hub Registry 已有完整多 endpoint 存储，但 chat/channel/automation session 仍走 env var 路径，与 Registry 割裂；普通用户无法通过 UI 直接配置 API Key 并让 session 生效。本迭代让 Registry 成为所有 session 的路由来源，env var 降级为首次启动自动种子。详见 `docs/ITERATION_35_FREEZE.md`。

- `KC-3501`：`Completed`。
  - User Outcome：用户在 Models Desk 填入 API Key 后，Key 安全存入 OS Keychain，无需接触命令行或配置文件；下次 chat 直接生效。
  - Scope：`CreateModelEndpointRequest`/`UpdateModelEndpointRequest` 新增 `ApiKeyValue?: string`；Gateway POST/PUT 端点：若 `ApiKeyValue` 非空则调用 `ISecretStore.UpsertAsync(new SecretRef("platform", "models", id), value)`，endpoint `ApiKeySecretRef = "platform:models:{id}"`；前端 ModelsSettingsDesk 新增 API Key 密码输入框，已配置时显示"已配置"提示。
  - Modules：`src/KodaClaw.Contracts`、`src/KodaClaw.Gateway/Endpoints`、`apps/kodaclaw-web/src/components/ModelsSettingsDesk.tsx`。
  - Verification：`dotnet test tests/KodaClaw.IntegrationTests --filter "ModelEndpointApiKey"`；`npm run test -- src/__tests__/models-settings-desk.spec.tsx`。

- `KC-3502`：`Completed`。
  - User Outcome：用户在 Models Desk 配置并设置默认模型后，所有 chat/channel/automation session 自动使用该 endpoint，无需额外配置环境变量。
  - Scope：新增 `RegistryAwareModelProvider`（KodaClaw.Runtime），注入 `IModelRegistryRepository` + `ISecretStore` + `IRuntimeModelProviderFactory`；`StreamAsync`/`CompleteAsync` 先查 `ResolveDefaultForAsync(TextChat | ToolCalling)`，若有则解析 key 并构建 provider，若无则 fallback 到 `DynamicModelProvider`；`ServiceCollectionExtensions.cs` 将 `IModelProvider` singleton 由 `DynamicModelProvider` 改为 `RegistryAwareModelProvider`。
  - Modules：`src/KodaClaw.Runtime/RegistryAwareModelProvider.cs`（新增）、`src/KodaClaw.Runtime/ServiceCollectionExtensions.cs`。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter "RegistryAwareModelProvider"`；`dotnet test KodaClaw.sln -m:1`。

- `KC-3503`：`Completed`。
  - User Outcome：已有 .env 配置的开发者/高级用户，升级后无需手动在 Models Desk 重新录入模型，Gateway 首次启动时自动将 env var 配置 seed 进 Registry 作为默认 endpoint。
  - Scope：新增 `ModelRegistrySeedService : IHostedService`（KodaClaw.Gateway）；`StartAsync` 检测 Registry 为空 + env var 有 model config → 构建并插入 `is_default=1` 的 endpoint（`ApiKeyEnvironmentVariable` 照旧）；幂等（Registry 已有 endpoint 则跳过）；注册到 `GatewayApp.Composition.cs`。
  - Modules：`src/KodaClaw.Gateway/ModelRegistrySeedService.cs`（新增）、`src/KodaClaw.Gateway/Composition/GatewayApp.Composition.cs`。
  - Verification：`dotnet test tests/KodaClaw.IntegrationTests --filter "ModelRegistrySeed"`；`dotnet test KodaClaw.sln -m:1`。

## 迭代 36：飞书 / Lark Channel 连接器（KC-3601~3609）

范围冻结：见 `docs/ITERATION_36_FREEZE.md`（2026-03-22）。新功能专项，完整 Capability Slice 流程。飞书企业自建应用机器人，通过 WebSocket 长连接（`wss://open.feishu.cn/event_bus`）接收消息，REST API 发送回复，完全契合 local-first 无公网 IP 架构。

### Phase 1：Contracts & 枚举扩展

- `KC-3601`：`Completed`（2026-03-22）。
  - User Outcome：系统识别飞书为独立渠道类型，API 层面区分 Feishu 与 Telegram / GenericWebhook。
  - Scope：`ChannelConnectorKind.Feishu = 2`；新增 `FeishuApiContracts.cs`（WS 事件信封 `FeishuWsEventEnvelope`、消息体 `FeishuImMessage`/`FeishuSender`/`FeishuMention`、Token 响应 DTO）。
  - Modules：`src/KodaClaw.Contracts/ChannelConnectorKind.cs`、`src/KodaClaw.ChannelHub/Connectors/Feishu/FeishuApiContracts.cs`（新增）。
  - Verification：`dotnet build KodaClaw.sln`（L0）；`dotnet test tests/KodaClaw.ContractTests --filter "Channel"`（L3，ChannelContractsTests 新增 Feishu 序列化 fixture）。

### Phase 2：HTTP API Client

- `KC-3602`：`Completed`（2026-03-22）。
  - User Outcome：连接器能向飞书发送文本消息和图片消息；连通性测试端点可验证 appId/appSecret 有效性。
  - Scope：`IFeishuApiClient`（`GetTenantAccessTokenAsync`、`GetAppAccessTokenAsync`、`SendTextMessageAsync`、`SendImageMessageAsync`）；`HttpFeishuApiClient` 实现，内部缓存 token（2h 有效期，提前 5 分钟刷新）；两种 token 用途区分（WS 建连 → `app_access_token`，发消息 → `tenant_access_token`）。
  - Modules：`src/KodaClaw.ChannelHub/Connectors/Feishu/IFeishuApiClient.cs`（新增）、`src/KodaClaw.ChannelHub/Connectors/Feishu/HttpFeishuApiClient.cs`（新增）。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter "FeishuApiClient"`（L1，含 token 缓存刷新 x2、发文本 x1、发图片 x1）；`dotnet build`。

### Phase 3：WebSocket 长连接客户端

- `KC-3603`：`Completed`（2026-03-22）。
  - User Outcome：连接器在 Gateway 启动后自动建立 WS 长连接，断线后自动重连，3 秒内完成 ACK 不触发重推。
  - Scope：`FeishuWebSocketClient`（连接 `wss://open.feishu.cn/event_bus` → 发 `registerApp` 认证 → 30s 心跳 ping/pong → 事件接收回调 → 立即 WSS ACK + fire-and-forget 处理 → 事件去重 LRU 近 500 条 event_id → 指数退避重连最大 60s）。
  - Modules：`src/KodaClaw.ChannelHub/Connectors/Feishu/FeishuWebSocketClient.cs`（新增）。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter "FeishuWebSocket"`（L1，含事件去重 x2、ACK 模式 x1、重连逻辑 x1）；`dotnet build`。

### Phase 4：FeishuConnector 核心实现

- `KC-3604`：`Completed`（2026-03-22）。
  - User Outcome：飞书账号 Start 后能收取消息并触发 turn 执行；Send 后飞书用户收到文字或图片回复；群组 @ 提及被正确识别。
  - Scope：`FeishuConnectorConfiguration`（解析 appId、appSecret、`credentialReference` via `ChannelSecretResolver`、`defaultDeliveryMode`）；`FeishuConnectorOptions`（WS 重连 / 心跳 / 去重配置）；`FeishuConnector : IChannelConnector`（`StartAsync` / `StopAsync` / `SendAsync`）；群组消息 `mentions` 字段解析 → 剥离 `@_user_x` 标签 → `ChannelEventEnvelope.Text` 净化；`ExternalThreadId` 映射规则（DM → `open_id`，Group → `chat_id`）。
  - Modules：`src/KodaClaw.ChannelHub/Connectors/Feishu/FeishuConnector.cs`（新增）、`FeishuConnectorConfiguration.cs`（新增）、`FeishuConnectorOptions.cs`（新增）。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter "FeishuConnector"`（L1，含配置解析 x3、@ 提及解析 x2、SendAsync 文本路径 x1、SendAsync 图片路径 x1）；`dotnet build`。

### Phase 5：Gateway 入站路由扩展

- `KC-3605`：`Completed`（2026-03-22）。
  - User Outcome：Gateway 启动时自动加载已配置的飞书账号并建立长连接；通过 API 更新飞书账号后，连接立即重新协商。
  - Scope：`ChannelInboundGatewayService` 注入 `FeishuConnector`，新增 `StartFeishuAccountAsync` / `StopFeishuAccountAsync`；`ChannelConnectorHostedService.StartAsync` 同时查并启动 Feishu 账号；`StopAsync` 同时停止；`ReloadAccountAsync` / `StopAccountAsync` 按 `account.ConnectorKind` 路由到对应 connector。
  - Modules：`src/KodaClaw.Gateway/Channels/ChannelInboundGatewayService.cs`（修改）、`src/KodaClaw.Gateway/Channels/ChannelConnectorHostedService.cs`（修改）、`src/KodaClaw.ChannelHub/ServiceCollectionExtensions.cs`（注册 `IFeishuApiClient` + `FeishuConnector`）。
  - Verification：`dotnet test tests/KodaClaw.IntegrationTests --filter "FeishuChannel"`（L2，含账号 upsert → Connected 状态 x1、reload 触发重连 x1、stop 清理 x1）；`dotnet test KodaClaw.sln -m:1`。

### Phase 6：Gateway 配套端点与诊断修复

- `KC-3606`：`Completed`（2026-03-22）。
  - User Outcome：连接器列表显示飞书为可用连接器；测试端点快速验证凭证有效性；诊断面板正确显示飞书出站能力；Keychain 迁移报告正确识别飞书的 appSecret。
  - Scope：`GatewayApp.ChannelEndpoints.cs` 连接器描述符新增 Feishu 条目（`SupportsInbound: true`，`SupportsOutbound: true`）；新增 `POST /api/channels/test/feishu`（`{ appId, appSecret }` → `GetTenantAccessTokenAsync` → `{ appName }` 或错误）；`GatewayApp.ChannelValidation.cs` `ReconcileChannelAccountRuntimeAsync` 扩展 Feishu 分支、`ResolveChannelAccountState` switch 显式加 Feishu；`SandboxRiskOverviewService.SupportsOutbound` 加 Feishu；`SecretMigrationReportService` switch 加 `BuildFeishuChannelItemAsync`（解析 appSecret）。
  - Modules：`src/KodaClaw.Gateway/Endpoints/GatewayApp.ChannelEndpoints.cs`、`src/KodaClaw.Gateway/Validation/GatewayApp.ChannelValidation.cs`、`src/KodaClaw.Gateway/SandboxRiskOverviewService.cs`、`src/KodaClaw.Gateway/SecretMigrationReportService.cs`。
  - Verification：`dotnet test tests/KodaClaw.IntegrationTests --filter "ChannelApi"`（L2）；`dotnet test KodaClaw.sln -m:1`。

### Phase 7：测试补全

- `KC-3607`：`Completed`（2026-03-22）。
  - User Outcome：枚举变更、连接器配置解析、入站 API 生命周期均有测试覆盖，防止回归。
  - Scope：`ChannelContractsTests.cs` 新增 Feishu `ChannelConnectorKind` JSON 序列化断言（`"connectorKind":"Feishu"` 路径）；`ChannelApiIntegrationTests.cs` 新增飞书账号 upsert → state 验证集成测试（mock `IFeishuApiClient`）；独立 `FeishuConnectorConfigurationTests.cs`（配置解析、凭证引用）；`FeishuConnectorTests.cs`（事件去重、@ 提及解析）。
  - Modules：`tests/KodaClaw.ContractTests/Channels/ChannelContractsTests.cs`、`tests/KodaClaw.IntegrationTests/Gateway/ChannelApiIntegrationTests.cs`、`tests/KodaClaw.UnitTests/ChannelHub/FeishuConnectorConfigurationTests.cs`（新增）、`tests/KodaClaw.UnitTests/ChannelHub/FeishuConnectorTests.cs`（新增）。
  - Verification：`dotnet test tests/KodaClaw.UnitTests --filter "Feishu"`；`dotnet test tests/KodaClaw.ContractTests --filter "Channel"`；`dotnet test KodaClaw.sln -m:1`。

### Phase 8：前端集成

- `KC-3608`：`Completed`（2026-03-22）。
  - User Outcome：用户可以在 Channels Desk 添加飞书账号，输入 appId + appSecret，一键测试连通性并保存。
  - Scope：`types/contracts.ts` `ChannelConnectorKind` 新增 `"Feishu"`；`lib/api.ts` 新增 `testFeishuCredentials(appId, appSecret): Promise<{ appName: string }>`；`ChannelsDesk.tsx` 添加账号表单中，选择飞书时展示 `appId` + `appSecret`（密码型）+ 连通性测试按钮（成功显示应用名，失败显示错误）；i18n 补充飞书相关文案。
  - Modules：`apps/kodaclaw-web/src/types/contracts.ts`、`apps/kodaclaw-web/src/lib/api.ts`、`apps/kodaclaw-web/src/components/ChannelsDesk.tsx`。
  - Verification：`npm run typecheck`（0 errors）；`npm run test`（全通过）；`npm run build`。

- `KC-3609`：`Completed`（2026-03-22）。
  - User Outcome：首次设置向导中提供飞书接入引导，用户跟着步骤 5 分钟内完成飞书机器人配置并连接到 KodaClaw。
  - Scope：`ChannelSetupWizard.tsx` 新增飞书选项卡，包含：引导说明（飞书开发者后台创建企业自建应用 → 开启机器人能力 → 在事件订阅中添加 `im.message.receive_v1` → 订阅范围勾选接收群聊/私聊消息 → 复制 App ID 和 App Secret）；appId / appSecret 输入框 + 连通性测试按钮；成功后调 `createChannelAccount`。
  - Modules：`apps/kodaclaw-web/src/components/settings/ChannelSetupWizard.tsx`。
  - Verification：`npm run typecheck`；`npm run test`；L5 Dogfood 人工走通飞书引导全流程。

## 迭代 37：workspace/mcp.json 接入（KC-3701~3704）

范围冻结：见 `docs/ITERATION_37_FREEZE.md`（2026-03-22）。补齐 workspace/mcp.json 空洞——文件已存在但从未被读取，本迭代让用户能直接通过编辑文件方式接入任意 MCP server，无需走 PluginHost 安装流程。

- `KC-3701`：`Completed`（2026-03-22）。
  - User Outcome：新增 `WorkspaceMcpConfig` / `WorkspaceMcpServerEntry` DTO，定义 mcp.json 的 schema（Claude Desktop 兼容格式）。
  - Scope：`src/KodaClaw.Contracts/WorkspaceMcpConfig.cs`（新增）；`mcpServers` 字典，每个 entry 支持 `transport`（默认 stdio）、`command`、`args`、`env`、`url`、`headers`。
  - Modules：`src/KodaClaw.Contracts/WorkspaceMcpConfig.cs`。
  - Verification：`dotnet build KodaClaw.sln`（L0）；`dotnet test tests/KodaClaw.ContractTests --filter "WorkspaceMcpConfig"`（L3）✓。

- `KC-3702`：`Completed`（2026-03-22）。
  - User Outcome：WorkspaceService 能读取 workspace/mcp.json，文件不存在或格式错误时安全返回空配置。
  - Scope：`IWorkspaceService` 新增 `ReadMcpConfigAsync(CancellationToken)`；`WorkspaceService` 实现（路径 `workspace/mcp.json`）；JSON 解析异常时返回空 `WorkspaceMcpConfig`。
  - Modules：`src/KodaClaw.Contracts/IWorkspaceService.cs`、`src/KodaClaw.Workspace/WorkspaceService.cs`。
  - Verification：`dotnet build KodaClaw.sln`（L0）✓；所有 `IWorkspaceService` stub 实现同步补齐。

- `KC-3703`：`Completed`（2026-03-22）。
  - User Outcome：用户编辑 `~/.kodaclaw/workspace/mcp.json` 后，下次 session 启动 Agent 自动拥有其中声明的 MCP server 工具，工具命名空间 `mcp__{name}__{tool}`，支持 stdio / http / streamableHttp / sse。
  - Scope：`MainSessionService` 构造函数注入 `McpClientManager?`（可选）；`BuildSessionToolsAsync` 末尾追加 `BuildWorkspaceMcpToolsAsync()`：读 mcp.json → 为每个 entry 构建 McpConfig → 调 `McpToolProvider.GetToolsAsync` → 注册到 ToolRegistry + 去重；单 server 错误隔离，记录诊断事件 `main_session.workspace_mcp.fetch_failed`；成功时记录 `main_session.workspace_mcp.injected`。
  - Modules：`src/KodaClaw.Runtime/MainSessionService.cs`、`src/KodaClaw.Runtime/KodaClaw.Runtime.csproj`。
  - Verification：`dotnet build KodaClaw.sln`（L0）✓；`dotnet test KodaClaw.sln -m:1`（全量回归）✓ 失败数 0（1 个预存在失败不计入）。

- `KC-3704`：`Completed`（2026-03-22）。
  - User Outcome：mcp.json schema 有合约测试保护，序列化/反序列化格式稳定。
  - Scope：`WorkspaceMcpConfigContractTests.cs`：7 个测试覆盖 stdio/http entry、transport 缺省推断、空文件解析、文件缺失 / JSON 损坏时安全返回。
  - Modules：`tests/KodaClaw.ContractTests/Workspace/WorkspaceMcpConfigContractTests.cs`（新增）。
  - Verification：`dotnet test tests/KodaClaw.ContractTests --filter "WorkspaceMcpConfig"` → 测试总数 7，通过数 7 ✓。

## 迭代 39：Chat UX — 会话清空 + 内联审批（KC-3901~3905）

范围冻结：见 `docs/ITERATION_39_FREEZE.md`（2026-03-23）。解决两个割裂问题：会话切换后 UI 无反馈、Agent 审批需跳转 Inbox 操作。

- `KC-3901`：`Completed`（2026-03-23）。
  - User Outcome：点击"新对话"或从历史面板恢复会话后，Chat 消息列表立即清空并显示对应系统提示（"已开始新对话" / "已切换到历史会话，Agent 记得之前的上下文"），用户有明确的切换感知。
  - Scope：`useChatConsole.ts` 新增 `clearMessages()` 函数；`App.tsx` 新增 `handleResumeSession`（调 `clearMessages(sessionResumedNote)`）；`handleRotateSession` 调 `clearMessages(newSessionNote)`；`SessionHistoryPanel.onResumed` 改为 `handleResumeSession`；`app-strings.ts` 新增 `newSessionNote`/`sessionResumedNote`。
  - Modules：`apps/kodaclaw-web/src/hooks/useChatConsole.ts`、`apps/kodaclaw-web/src/App.tsx`、`apps/kodaclaw-web/src/i18n/app-strings.ts`。
  - Verification：`npm run typecheck`（L0 通过）；`npm run test`（L1 通过）。

- `KC-3902`：`Completed`（2026-03-23）。
  - User Outcome：Chat SSE 流新增 `approval_required` 和 `approval_decided` 两种事件类型，有完整 schema 契约，供前端渲染内联审批卡片。
  - Scope：后端 `ChatStreamEvent.cs` 新增 `ApprovalId?`、`CallId?`、`ToolName?`、`InputPreview?`、`Decision?` optional 字段；前端 `contracts.ts` 的 `ChatStreamEvent` union 扩展这两个 type，新增 approval 字段。
  - Modules：`src/KodaClaw.Contracts/ChatStreamEvent.cs`、`apps/kodaclaw-web/src/types/contracts.ts`。
  - Verification：`dotnet build KodaClaw.sln`（L0 通过）；`npm run typecheck`（L0 通过）。

- `KC-3903`：`Completed`（2026-03-23）。
  - User Outcome：Agent 触发权限审批时，审批请求**实时**出现在 Chat SSE 流中，前端无需轮询 Inbox 即可感知；审批决策后，决定结果也通过 SSE 推送。
  - Scope：`IMainSessionService` 新增 `TryGetApprovalIdForCall(string callId)`；`MainSessionService` 实现（查 `_liveApprovals`）；`ChatSessionService` 订阅新增 `"control"` 频道和 `"permission_required"/"permission_decided"` kinds，switch case 处理 `PermissionRequiredEvent` → yield `approval_required`，`PermissionDecidedEvent` → yield `approval_decided`；inputPreview 截断到 400 字符。
  - Modules：`src/KodaClaw.Runtime/IMainSessionService.cs`、`src/KodaClaw.Runtime/MainSessionService.cs`、`src/KodaClaw.Runtime/ChatSessionService.cs`。
  - Verification：`dotnet build KodaClaw.sln`（L0 通过）。

- `KC-3904`：`Completed`（2026-03-23）。
  - User Outcome：Chat 对话流中出现内联审批卡片（工具名、参数预览、允许/拒绝按钮）；用户点击决策后卡片状态更新为已批准/已拒绝；Agent 随即继续执行，对话流继续。收件箱作为备份入口保留。
  - Scope：`chat.ts` 新增 `"approval"` role + approval 字段；`useChatConsole.ts` 处理 `approval_required`/`approval_decided` 事件，新增 `clearMessages()`、`submitApproval()` 导出；新增 `ApprovalCard.tsx` 组件；`MessageTimeline.tsx` 渲染 `ApprovalCard`；`index.css` 新增 `.approval-card*` 样式；`App.tsx` 传递 `onSubmitApproval`。
  - Modules：`apps/kodaclaw-web/src/types/chat.ts`、`apps/kodaclaw-web/src/hooks/useChatConsole.ts`、`apps/kodaclaw-web/src/components/MessageTimeline.tsx`、`apps/kodaclaw-web/src/components/chat/ApprovalCard.tsx`（新增）。
  - Verification：`npm run typecheck`（L0 通过）；`npm run test`（L1 通过）。
  - Depends on：KC-3902（事件类型定义）、KC-3903（SSE 推送）。

- `KC-3905`：`Pending`（L2/L4 专项测试推迟，L0 全量编译已通过）。
  - User Outcome：内联审批全链路有集成测试保护，SSE 序列、卡片渲染、决策回路三层均可验证。
  - Scope：待补充 `InlineApprovalIntegrationTests.cs`（后端 L2）和 `approval-card.spec.tsx`（前端 L1）。
  - Modules：`tests/KodaClaw.IntegrationTests/Runtime/InlineApprovalIntegrationTests.cs`（待新增）、`apps/kodaclaw-web/src/__tests__/approval-card.spec.tsx`（待新增）。
  - Verification：`dotnet build KodaClaw.sln`（L0 通过）；专项测试待补充。
  - Depends on：KC-3903、KC-3904。

- `KC-3906`：`Completed`（2026-03-23）。
  - User Outcome：用户在 Settings → 行为控制 开启"工具调用自动授权"后，新建/恢复的 MainSession 中 Agent 执行任何工具均自动放行，不弹内联审批卡片，也不进 Inbox；关闭后恢复审批流。与渠道消息投递审批（`RequireApprovalForExternalActions`）独立控制。
  - Scope：`KodaClawSettings` 新增 `AutoApproveToolCalls: bool`（默认 false）；`SqliteSettingsRepository` 补 migration（`auto_approve_tool_calls` 列）+ BindParameters + MapSettings（col 9）；`MainSessionService` 注入 `ISettingsRepository?`，新增 `ResolvePermissionsAsync()`（AutoApproveToolCalls=true → `Mode="auto"` else `Mode="approval"`），在 Fresh 和 Resume 路径均调用；`BehaviorSection.tsx` 新增 toggle；`contracts.ts` 新增 `autoApproveToolCalls: boolean`。
  - Modules：`src/KodaClaw.Contracts/KodaClawSettings.cs`、`src/KodaClaw.ControlPlane/SqliteSettingsRepository.cs`、`src/KodaClaw.Runtime/MainSessionService.cs`、`apps/kodaclaw-web/src/components/settings/BehaviorSection.tsx`、`apps/kodaclaw-web/src/types/contracts.ts`。
  - Verification：`dotnet build KodaClaw.sln`（L0 通过）；`npm run typecheck`（L0 通过）；`dotnet test --filter "ControlPlane.SqliteSettings"`（L1 全通）。

- `KC-BUG-003`：`Completed`（2026-03-23）。
  - 症状：历史会话面板（SessionHistoryPanel）只显示截断的 session ID，缺乏有意义的会话摘要，用户难以识别具体会话。
  - 根因：`SessionSummary` / `SessionDetail` contract 无 title 字段；后端未提取第一条用户消息作为标题。
  - 修复：后端 `SessionSummary`、`SessionDetail` 新增 `string? Title = null`；`LoadSessionDetailAsync` 提取第一条 `MessageRole.User` 的 `TextContent.Text`，超过 60 字符截断并附省略号；`GatewayApp.SessionEndpoints.cs` 将 `Title` 透传到 `SessionSummary` 响应；前端 `contracts.ts` 的 `SessionSummary` 新增 `title?: string | null`；`SessionHistoryPanel.tsx` 用 title 代替 session ID 前 8 位显示，无 title 时降级到 ID 截断；`app-shell.css` 新增 `.session-history-item__title` 样式，网格扩展为 3 行。
  - 受影响模块：`src/KodaClaw.Contracts/SessionSummary.cs`、`src/KodaClaw.Contracts/SessionDetail.cs`、`src/KodaClaw.Gateway/Infrastructure/GatewayApp.SessionInfrastructure.cs`、`src/KodaClaw.Gateway/Endpoints/GatewayApp.SessionEndpoints.cs`、`apps/kodaclaw-web/src/types/contracts.ts`、`apps/kodaclaw-web/src/components/chat/SessionHistoryPanel.tsx`、`apps/kodaclaw-web/src/shell/app-shell.css`。
  - Verification：`dotnet build KodaClaw.sln`（L0 通过）；`npm run typecheck`（L0 通过）。

## 迭代 38：KodaClaw.McpHub 独立模块 + McpServersDesk（KC-3801~3807）

范围冻结：见 `docs/ITERATION_38_FREEZE.md`（2026-03-22）。将 workspace/mcp.json 接入能力提取为独立模块，补齐 enabled 字段，实现前后端完整管理界面，并支持连通性测试与状态展示。

- `KC-3801`：`Completed`（2026-03-22）。
  - User Outcome：`WorkspaceMcpServerEntry` 新增 `enabled` 字段（nullable bool，缺省 true），`BuildWorkspaceMcpToolsAsync` 跳过 `enabled=false` 的 entry，向后兼容 Claude Desktop 格式。
  - Scope：`src/KodaClaw.Contracts/WorkspaceMcpConfig.cs` 加 `enabled` 属性；`MainSessionService.BuildWorkspaceMcpToolsAsync()` 加过滤逻辑；`WorkspaceMcpConfigContractTests.cs` 补充 enabled 相关测试。
  - Modules：`KodaClaw.Contracts`、`KodaClaw.Runtime`、`KodaClaw.ContractTests`。
  - Verification：`dotnet build KodaClaw.sln`（L0）；`dotnet test tests/KodaClaw.ContractTests --filter "WorkspaceMcp"`（L3）。

- `KC-3802`：`Completed`（2026-03-22）。
  - User Outcome：workspace/mcp.json 接入逻辑从 Runtime 提取到独立模块 `KodaClaw.McpHub`，接口清晰，便于未来扩展（热重载、连接池等）。
  - Scope：新建 `src/KodaClaw.McpHub/` 项目；`IMcpHubService` 接口（`InjectToolsAsync`）；`McpHubService` 实现（迁移自 `MainSessionService.BuildWorkspaceMcpToolsAsync`）；`McpHubInjectionResult` record；`ServiceCollectionExtensions`；`KodaClaw.Runtime.csproj` 加 McpHub 引用；`MainSessionService` 改为注入 `IMcpHubService`；`KodaClaw.Gateway` 注册 McpHub 服务。
  - Modules：`src/KodaClaw.McpHub/`（新增）、`KodaClaw.Runtime`、`KodaClaw.Gateway`。
  - Verification：`dotnet build KodaClaw.sln`（L0）；`dotnet test KodaClaw.sln -m:1`（全量回归）。

- `KC-3803`：`Completed`（2026-03-22）。
  - User Outcome：`IWorkspaceService` 新增 `SaveMcpConfigAsync()`；Gateway 暴露 `GET /api/mcp-servers` 和 `PUT /api/mcp-servers`，前端可读写 workspace/mcp.json。
  - Scope：`IWorkspaceService` 加 `SaveMcpConfigAsync(WorkspaceMcpConfig)`；`WorkspaceService` 实现（序列化写入 `workspace/mcp.json`）；所有 stub/fake `IWorkspaceService` 补充新方法；`GatewayApp.McpServersEndpoints.cs`（新 partial）：GET 调 `ReadMcpConfigAsync`，PUT 调 `SaveMcpConfigAsync`；Gateway DI 注册；集成测试 `McpServersApiIntegrationTests`（GET 返回空配置、PUT 保存后 GET 可读回）。
  - Modules：`KodaClaw.Contracts`、`KodaClaw.Workspace`、`KodaClaw.Gateway`、`KodaClaw.IntegrationTests`。
  - Verification：`dotnet build KodaClaw.sln`（L0）；`dotnet test tests/KodaClaw.IntegrationTests --filter "McpServers"`（L2）。

- `KC-3804`：`Completed`（2026-03-22）。
  - User Outcome：前端 McpServersDesk 组件：列表展示所有 server（名称、transport、command/url）、enable/disable toggle（调 PUT 保存）、添加表单（名称、transport 选择、command/args 或 url/headers）、删除按钮；Sidebar 能力层加"MCP 直连"入口；`contracts.ts` 新增 `WorkspaceMcpServerEntry` / `WorkspaceMcpConfig` 类型；`api.ts` 新增 `fetchMcpServers()` / `saveMcpServers()`。
  - Scope：`apps/kodaclaw-web/src/components/McpServersDesk.tsx`（新增）；`apps/kodaclaw-web/src/lib/api.ts`；`apps/kodaclaw-web/src/types/contracts.ts`；`apps/kodaclaw-web/src/shell/Sidebar.tsx`（加入口）；`apps/kodaclaw-web/src/shell/MainContent.tsx`（加路由）。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npm run typecheck`（L0）；`npm run test`（L1）；L5 人工走通添加/禁用/删除全流程。

- `KC-3805`：`Completed`（2026-03-22）。
  - User Outcome：McpHub 模块和 GET/PUT API 有契约测试保护，schema 稳定。
  - Scope：`tests/KodaClaw.ContractTests/McpHub/McpHubInjectionContractTests.cs`（新增）：验证 enabled 过滤行为、空配置时返回零工具、单 server 失败不影响其他；`WorkspaceMcpConfigContractTests` 补充 SaveMcpConfigAsync 往返序列化测试。
  - Modules：`tests/KodaClaw.ContractTests`。
  - Verification：`dotnet test tests/KodaClaw.ContractTests --filter "McpHub|WorkspaceMcp"`（L3）。

- `KC-3806`：`Completed`（2026-03-22）。
  - User Outcome：用户可在 McpServersDesk 点击「测试连接」按钮，即时得知某个 MCP server 是否可达、能否成功 ListTools，以及可用工具数量；连接失败时显示错误摘要。
  - Scope：`IMcpHubService` 新增 `TestConnectionAsync(string serverName, CancellationToken ct) → McpConnectionTestResult`；`McpHubService` 实现（复用 McpClientManager，尝试连接 + ListTools，超时 10s）；`McpConnectionTestResult` record（Success/ToolCount/ErrorMessage）；`GatewayApp.McpServersEndpoints.cs` 加 `POST /api/mcp-servers/{name}/test-connection`；集成测试 `McpServersTestConnectionIntegrationTests`（对不存在的 server 返回 404，对错误 command 返回 success=false）。
  - Modules：`KodaClaw.McpHub`、`KodaClaw.Gateway`、`KodaClaw.IntegrationTests`。
  - Verification：`dotnet build KodaClaw.sln`（L0）；`dotnet test tests/KodaClaw.IntegrationTests --filter "McpServersTest"`（L2）；L5 人工触发 test-connection 端点确认 JSON 返回。
  - Depends on：KC-3802（McpHub 模块存在）、KC-3803（端点 partial 已建）。

- `KC-3807`：`Completed`（2026-03-22）。
  - User Outcome：McpServersDesk 列表中每个 server 行显示连通状态徽章（绿色"可达" / 红色"不可达" / 灰色"未测试"）；用户点击「测试连接」按钮后徽章实时更新；页面加载时不自动触发（手动按需测试）。
  - Scope：`contracts.ts` 加 `McpConnectionTestResult`（success/toolCount/errorMessage）；`api.ts` 加 `testMcpServerConnection(name)`；`McpServersDesk.tsx` 本地 state `connectionStatus: Record<string, McpConnectionTestResult | 'testing' | null>`；每行渲染状态徽章（Lucide CheckCircle2/XCircle/Circle）和「测试」按钮（loading spinner 动效）。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npm run typecheck`（L0）；`npm run test`（L1）；L5 人工验证：点击按钮 → 转圈 → 显示绿/红徽章 + 工具数或错误摘要。

## 迭代 40：前端 Modal 表单体验系统化（KC-4001~4004）

范围冻结：见 `docs/ITERATION_40_FREEZE.md`（2026-03-23）。纯前端 UX 优化/重构：替换 `window.confirm()`、将高字段数编辑器和多步向导改为 Modal，以及破坏性操作视觉强化。不涉及任何后端或 API 变更。

- `KC-4001`：`Completed`（2026-03-23）。
  - User Outcome：所有删除/破坏性操作弹出样式统一的 `ConfirmModal` 而非原生 `window.confirm()`；danger 变体下确认按钮为红色，视觉分量更重。
  - Scope：新建 `apps/kodaclaw-web/src/components/ui/ConfirmModal.tsx`，基于 `Modal.tsx` 封装，props：`open / title / description / confirmLabel / variant("danger"|"warning"|"default") / onConfirm / onCancel / busy`；新建配套 `ConfirmModal.css`（danger/warning 按钮色）；替换 `McpServersDesk.tsx`、`ModelsSettingsDesk.tsx`、`ChannelsDesk.tsx` 中所有 `window.confirm()` 调用。
  - Modules：`apps/kodaclaw-web/src/components/ui/`、`apps/kodaclaw-web/src/components/McpServersDesk.tsx`、`apps/kodaclaw-web/src/components/ModelsSettingsDesk.tsx`、`apps/kodaclaw-web/src/components/ChannelsDesk.tsx`。
  - Verification：L0 typecheck ✓；L0 build ✓；L1 48 tests ✓。

- `KC-4002`：`Completed`（2026-03-23）。
  - User Outcome：Settings → 系统 → "重新引导" / "清除身份" 操作弹出 `ConfirmModal`（danger 变体），包含操作说明和不可撤销警告；移除 `settings-confirm-row` inline 确认状态，简化为 idle → working → done/error 三态。
  - Scope：`SystemSection.tsx` 移除 `useConfirmAction` hook 的 `confirming` 状态分支；两个操作各自维护 `confirmOpen` bool state + `busy` state；按钮点击 → 打开 ConfirmModal → `onConfirm` 回调执行破坏性 API 调用 → done/error inline 反馈。
  - Modules：`apps/kodaclaw-web/src/components/settings/SystemSection.tsx`。
  - Verification：L0 typecheck ✓；L0 build ✓；L1 48 tests ✓。

- `KC-4003`：`Completed`（2026-03-23）。
  - User Outcome：`ModelsSettingsDesk` 新建/编辑模型端点通过 Modal 操作，左侧模型列表改为全宽显示；工具栏新增"新建端点"按钮，列表行"编辑"按钮触发 Modal，右侧常驻 Composer 面板移除；全局设置（主题/自动化引擎）以独立 section 展示在模型列表下方。
  - Scope：Modal 封装端点表单；`settings-form` section 恢复（fetchSettings/saveSettings）；vitest.setup.ts 补 HTMLDialogElement mock；settings-desk.spec 和 models-settings-desk.spec 更新对应测试。
  - Modules：`apps/kodaclaw-web/src/components/ModelsSettingsDesk.tsx`（大改）、`vitest.setup.ts`、`__tests__/settings-desk.spec.tsx`、`__tests__/models-settings-desk.spec.tsx`。
  - Verification：L0 typecheck ✓；L0 build ✓；L1 48 tests ✓。

- `KC-4004`：`Completed`（2026-03-23）。
  - User Outcome：Settings → 连接 → "+ 绑定新渠道" 弹出 Modal，Modal 内嵌 `ChannelSetupWizard` 多步向导；完成或取消关闭 Modal，列表自动刷新；向导体验与 Settings 页面内容在视觉上明确隔离。
  - Scope：`ConnectionsSection.tsx` 替换 inline `showWizard` 展开方式：改为 `wizardOpen: bool` state + `Modal` 容器包裹 `ChannelSetupWizard`；Modal 的 `onClose` 同时触发 `loadAccounts()`；`ChannelSetupWizard` 组件 props 不变；Modal `width` 设为 `520px` 以适配向导宽度。
  - Modules：`apps/kodaclaw-web/src/components/settings/ConnectionsSection.tsx`。
  - Verification：L0 typecheck ✓；L0 build ✓；L1 48 tests ✓。

## 迭代 41：Chat UX as Agent OS（KC-4101~4103）

范围冻结：见 `docs/ITERATION_41_FREEZE.md`（2026-03-23）。通过工具调用可见性、流式状态语义化、时间戳 hover 展示三项改动，使 Chat 页面从聊天应用形态向 Agent OS 形态迈进。后端仅扩展 SSE 事件，前端增加新渲染层，不改变现有 API contract。

- `KC-4101`：`Pending`。
  - User Outcome：消息时间戳默认隐藏，悬停消息时淡入显示，减少连续对话中的视觉噪音。
  - Scope：`index.css` 中 `.message__time` 加 `opacity: 0; transition: opacity 0.15s ease`；`article.message:hover .message__time { opacity: 1 }`。
  - Modules：`apps/kodaclaw-web/src/index.css`。
  - Verification：L0 typecheck / build；L5 人工悬停验证。

- `KC-4102`：`Pending`。
  - User Outcome：Agent 执行工具后，Timeline 中插入紧凑的工具活动行（⚙ 工具名 · 耗时），用户可见 Agent 都做了什么。
  - Scope：`ChatStreamEvent.cs` 加 `long? DurationMs` 字段；`ChatSessionService.cs` 将 `ToolEndEvent` case 改为通用（保留 workspace rotation 检查），yield `tool_activity`；前端 `contracts.ts` 加 `"tool_activity"` type 和 `durationMs`；`chat.ts` 加 `"tool_activity"` role 和 `durationMs?: number`；`useChatConsole.ts` 处理 `tool_activity` 事件；`MessageTimeline.tsx` 新增 `ToolActivityBlock` 组件内联渲染；CSS 加 `.tool-activity` 紧凑样式。
  - Modules：`KodaClaw.Contracts`、`KodaClaw.Runtime`、`apps/kodaclaw-web`。
  - Verification：L0 dotnet build / npm typecheck / build；L1 npm test；L5 人工触发工具调用验证。

- `KC-4103`：`Pending`。
  - User Outcome：Agent 执行工具时，Composer hint 显示"Koda 正在执行 {toolName}…"，用户知道在等什么而不是茫然等待。
  - Scope：`ChatSessionService.cs` Subscribe kinds 加 `"tool:start"`，handle `ToolStartEvent` → yield `agent_working`；前端 `contracts.ts` 加 `"agent_working"` type；`useChatConsole.ts` 跟踪 `activeToolName` state，返回给调用方；`App.tsx` 传 `activeToolName` 给 `ChatComposer`；`ChatComposer.tsx` 接受 `activeToolName` prop，在 streaming 时显示工具 hint。
  - Modules：`KodaClaw.Runtime`、`apps/kodaclaw-web`。
  - Verification：L0 build；L1 test；L5 人工验证 Composer hint 跟随工具执行变化。

## 专项 W3：前端 UI 系统统一（KC-W3-001）

范围：纯前端 CSS/className 重构，不改任何后端 API、契约或组件逻辑。消灭 4 套按钮体系和 5 套输入框体系，统一为 `.btn` + `.kc-input/.kc-select/.kc-textarea`。

- `KC-W3-001`：`Pending`。
  - User Outcome：全站按钮高度、padding、禁用态、focus ring 视觉一致；输入框高度、padding、focus ring 视觉一致；Settings 页面与模型设置/MCP 页面表单元素对齐。
  - Scope：
    - CSS 层：`app-shell.css` 删除 `.settings-btn`/`.settings-input`/`.settings-select`；`index.css` 删除 `.secondary-button`；`ControlPlaneDesk.css` 删除 `.control-plane-input`/`.control-plane-select`；`Modal.css` 将 `.kc-input`/`.kc-select`/`.kc-field*` 提升到 `index.css` 成为全局样式；新增 `.kc-textarea`；统一 disabled opacity 为 0.5；统一 focus shadow 为 `0 0 0 3px var(--accent-soft)`。
    - 组件层：Settings 各 section（Appearance/Behavior/Notifications/Connections/System/Memory/Heartbeat/Risk/Updates/WorkspaceIdentityEditor）`settings-btn*` → `btn*`；`settings-input` → `kc-input`；`settings-select` → `kc-select`；ModelsSettingsDesk/ChannelsDesk/PluginsDesk/AutomationsDesk/McpServersDesk/ChannelSetupWizard 中 `secondary-button` → `btn btn--secondary`；`bootstrap-form__textarea` 用于 input/select 时 → `kc-input`/`kc-select`；`bootstrap-form__textarea` 用于 textarea 保留或改为 `kc-textarea`；`control-plane-input`/`control-plane-select` → `kc-input`/`kc-select`。
  - Modules：`apps/kodaclaw-web/src/index.css`、`app-shell.css`、`ControlPlaneDesk.css`、`Modal.css`、全部 `components/settings/*.tsx`、`components/*.tsx`（10+个文件）。
  - Verification：L0 `npm run typecheck`；L0 `npm run build`；L1 `npm run test`（46 tests 全绿）；L5 人工 Dogfood Settings/Models/MCP/Channels 页面外观。

## 迭代 42：Session History Restoration（KC-4201~4202）

范围冻结：见 `docs/ITERATION_42_FREEZE.md`（2026-03-23）。Resume 一个历史 session 后，Chat 区域自动加载最近 20 条消息（user/assistant），顶部插入分隔线，并提供"加载更多"按钮向上翻页。

- `KC-4201`：`Completed`（2026-03-23）。
  - User Outcome：前端可通过 `GET /api/sessions/{id}/messages?limit=20&skip=0` 拉取任意 session 的对话历史（user/assistant 消息，文本内容）。
  - Scope：新增 `SessionMessageItem` / `SessionMessagesResponse` record（`KodaClaw.Contracts/SessionMessagesResponse.cs`）；`GatewayApp.SessionEndpoints.cs` 加 `GET /{id}/messages` 端点；`GatewayApp.SessionInfrastructure.cs` 加 `LoadSessionMessagesAsync` helper（倒序 skip/limit，仅 user/assistant）。
  - Modules：`KodaClaw.Contracts`、`KodaClaw.Gateway`。
  - Verification：L0 `dotnet build` 0 错 0 警告 ✓。

- `KC-4202`：`Completed`（2026-03-23）。
  - User Outcome：Resume 一个历史 session 后，Chat 顶部自动出现历史消息（减淡显示），上方有分隔线"以下为历史对话"，并有"加载更多"按钮可追加更早的消息。
  - Scope：`contracts.ts` 加 `SessionMessageItem`/`SessionMessagesResponse`；`api.ts` 加 `fetchSessionMessages`；`chat.ts` 加 `isHistory?: boolean` 和 role `"history_separator"`；`useChatConsole.ts` 加 `loadHistory`/`loadMoreHistory`/`prependHistory`/`isLoadingHistory`/`hasMoreHistory`；`App.tsx` 通过 `useEffect` 监听 `snapshot.activeMainSessionId` 变化自动触发 `loadHistory`（不改 `useSessionHistory`/`SessionHistoryPanel`）；`MessageTimeline.tsx` 加"加载更多"按钮、分隔线、`message--history` class；`index.css` 加对应样式。
  - Modules：`apps/kodaclaw-web`（仅自有文件）。

## 迭代 43：Model Hub 质量补全（KC-4301~4307 + KC-BUG-101~103）

范围冻结：见 `docs/ITERATION_43_FREEZE.md`（2026-03-23）。补全 `maxOutputTokens` / `isReasoning` 全栈字段，修复 3 个已知 Bug（SecretRef 前缀、首个模型自动默认、删除默认模型防守），完善前端表单 UX（字段顺序、contextWindowSize、高级选项折叠）。

### Bug Fix

- `KC-BUG-101`：`Completed`（2026-03-23）。
  - 症状：编辑已配置 API Key 的模型端点时，"已配置"徽章永不显示，始终展示密码输入框。
  - 根因：前端检查 `startsWith("platform:models:")` ，后端生成 `keychain:models:{id}`，前缀不匹配。
  - 受影响模块：`apps/kodaclaw-web/src/components/ModelsSettingsDesk.tsx`。
  - Verification：前端 `apiKeySecretRef?.startsWith("keychain:models:")` 已对齐后端生成格式。

- `KC-BUG-102`：`Completed`（2026-03-23）。
  - 症状：用户创建第一个模型端点后，session 无默认模型无法启动，需手动点"设为默认"。
  - 根因：POST handler 硬编码 `IsDefault: false`，不查询现有 endpoint 数量。
  - 受影响模块：`KodaClaw.Gateway/Endpoints/GatewayApp.ModelEndpoints.cs`。
  - Verification：`existingEndpoints.Count == 0 → IsDefault: true` 逻辑已实现。

- `KC-BUG-103`：`Completed`（2026-03-23）。
  - 症状：可删除唯一的默认模型，之后系统无默认模型，session 启动失败。
  - 根因：DELETE handler 未检查 `endpoint.IsDefault`。
  - 受影响模块：`KodaClaw.Gateway/Endpoints/GatewayApp.ModelEndpoints.cs`，`KodaClaw.ModelHub`。
  - Verification：无其他启用 endpoint → 409；有则自动切换默认后删除。PUT handler 同步保护禁止 disable 默认端点。

### 新功能

- `KC-4301`：`Completed`（2026-03-23）。
  - User Outcome：ModelPreset 和 ModelEndpoint Contract 包含 `maxOutputTokens` / `isReasoning`，预设文件补全对应值。
  - Scope：`ModelPreset.cs`、`ModelEndpoint.cs`、`CreateModelEndpointRequest.cs`、`UpdateModelEndpointRequest.cs` 均已加两字段；`model-presets.json` 23 条预设已补全。
  - Modules：`KodaClaw.Contracts`、`KodaClaw.Gateway/Resources`。
  - Verification：L0 `dotnet build` 0 错 0 警告 ✓。

- `KC-4302`：`Completed`（2026-03-23）。
  - User Outcome：SQLite 数据库向后兼容追加两列，已有数据用默认值填充。
  - Scope：`SqliteModelRegistryRepository.cs` 两条 idempotent ALTER TABLE Migration；INSERT/UPDATE/SELECT 映射同步更新；COALESCE 兜底向后兼容。
  - Modules：`KodaClaw.ModelHub`。
  - Verification：L0 build ✓。

- `KC-4303`：`Completed`（2026-03-23）。
  - User Outcome：Gateway 验证层支持 `maxOutputTokens` / `isReasoning` 透传；`GET /api/model-presets` 响应包含新字段。
  - Scope：`GatewayApp.ModelValidation.cs` 补字段验证；`GatewayApp.ModelEndpoints.cs` CREATE/UPDATE/GET handler 全部透传新字段。
  - Modules：`KodaClaw.Gateway`。
  - Verification：L0 build ✓。

- `KC-4304`：`Completed`（2026-03-23）。
  - User Outcome：Runtime 构建模型请求时注入 `MaxTokens = endpoint.MaxOutputTokens`；`isReasoning=true` 端点跳过 tool_use 注入。
  - Scope：`RegistryAwareModelProvider.NormalizeRequest()` L161 注入 MaxTokens；L168 `isReasoning && Tools.Count > 0 → Tools = null`。
  - Modules：`KodaClaw.Runtime`。
  - Verification：L0 build ✓；代码审查确认 tool strip 逻辑。

- `KC-4305`：`Completed`（2026-03-23）。
  - User Outcome：前端类型包含新字段；表单展示 maxOutputTokens 输入和 isReasoning 开关。
  - Scope：`contracts.ts` 四个类型补字段；`ModelsSettingsDesk.tsx` `ModelDraft` + 表单 + toCreateRequest/toUpdateRequest 全部对齐；预设选中时同步填充。
  - Modules：`apps/kodaclaw-web`。
  - Verification：L0 `npm run typecheck` ✓。

- `KC-4306`：`Completed`（2026-03-23）。⚠️ 微偏差：`已启用` 勾选框位于测试连接按钮之后（位置 9），Freeze 要求在高级选项之后。不影响功能，Iter 44 顺手修复。
  - User Outcome：表单字段顺序合理，contextWindowSize 进表单，API Key 环境变量折叠进高级选项。
  - Scope：`ModelsSettingsDesk.tsx` 调整 JSX 顺序；补 `contextWindowSize` 字段；API Key env var 包裹进 `<details>`。
  - Modules：`apps/kodaclaw-web`（单文件）。
  - Verification：L0 typecheck ✓。

- `KC-4307`：`Completed`（2026-03-23）。
  - User Outcome：模型列表卡片展示 isReasoning "推理"徽章、maxOutputTokens；禁用端点有视觉区分。
  - Scope：`ModelsSettingsDesk.tsx` 卡片已加 isReasoning badge、token 计数行、disabled class。
  - Modules：`apps/kodaclaw-web`（单文件）。
  - Verification：L0 typecheck ✓；代码审查确认渲染逻辑。

---

## Automation 质量修复（KC-BUG-201~202 + 小优化，2026-03-23）

### Bug Fix

- `KC-BUG-201`：`Completed`（2026-03-23）。
  - 症状：前端手动触发自动化（POST /api/automations/{id}/trigger）后，返回的 runId 对应记录会在数秒内被标记为 Failed，且若 definition.NextRunAt 设在未来，自动化根本不会实际执行。
  - 根因：端点创建 Queued run 后，后台 RunOnceAsync → RecoverStaleRunsAsync 把所有 Queued 状态 run 无差别标记为 Failed；IsDue 检查同样阻止 NextRunAt 在未来的 definition 执行。
  - 受影响模块：`KodaClaw.Automation`（`IAutomationScheduler`、`AutomationScheduler`、`AutomationSchedulerOptions`、`ServiceCollectionExtensions`），`KodaClaw.Gateway/Endpoints/GatewayApp.AutomationEndpoints.cs`。
  - 修复：`IAutomationScheduler` 加 `TriggerDefinitionAsync(definitionId, ct) → Task<string?>`；`AutomationScheduler` 实现：同步创建 Queued run → 火而不管执行核心 `RunSessionCoreAsync`（提取自 ExecuteDefinitionAsync），返回 runId；`AutomationSchedulerOptions` 加 `StaleRunThreshold = 10min`，`RecoverStaleRunsAsync` 按 `StartedAt <= now - threshold` 过滤，只回收真正的僵尸 run；Gateway endpoint 改调 `TriggerDefinitionAsync`，不再手动创建 run 记录。
  - Verification：L0 `dotnet build` 0 错 0 警告 ✓。

- `KC-BUG-202`：`Completed`（2026-03-23）。
  - 症状：设置"每天 09:00 执行"的自动化，在 UTC+8 环境下实际于本地 17:00 触发，而非 09:00。
  - 根因：`ComputeNextDailyRun` / `ComputeNextWeeklyRun` 构造 DateTimeOffset 时用 `TimeSpan.Zero`（UTC offset），但 LocalTime 字段语义是本地时间。
  - 受影响模块：`KodaClaw.Automation/AutomationScheduler.cs`。
  - 修复：`ComputeNextDailyRun` / `ComputeNextWeeklyRun` 改为 `var localNow = now.ToLocalTime()`，用 `localNow.Date` 取日期分量，`localNow.Offset` 作 DateTimeOffset 偏移量；同步去掉 `ComputeNextWeeklyRun` 中冗余的 Monday 默认值（validation 层已保证非空）。
  - Verification：L0 `dotnet build` 0 错 0 警告 ✓。

- `KC-BUG-203`：`Completed`（2026-03-23）。
  - 症状：Gateway 启动后 `HeartbeatSyncService` 输出警告 `HEARTBEAT.md compilation failed: Line 22: Expected a top-level bullet field.`，`automation_definitions` 表始终为空。
  - 根因：默认模板 `DefaultWorkspaceTemplates.Heartbeat()` 中 "Nightly Memory Consolidation" 使用 YAML block scalar 语法（`- prompt: >`），但 `HeartbeatAutomationCompiler.ParseSections` 只支持单行字段值；读取到 `>` 后 `lineIndex++`，下一行是缩进的连续文本，不满足 `IsTopLevelBullet`，抛 "Expected a top-level bullet field."。
  - 受影响模块：`KodaClaw.Workspace/HeartbeatAutomationCompiler.cs`。
  - 修复：在 `ParseSections` 的 `prompt` 字段处理分支增加 block scalar 识别：若读到的值为 `">"，进入 block scalar 模式，持续收集直到遇见下一个 top-level bullet 或 `##` 节头为止，将所有非空缩进行以空格拼接为最终 prompt；否则沿用原有单行路径。兼容已有用户 HEARTBEAT.md 文件中的 `>` 语法。
  - Verification：L0 `dotnet build` 0 错 0 警告 ✓；L1 Heartbeat 单元测试 8/8 ✓；L3 HeartbeatAutomationCompiler 契约测试 9/9 ✓。

### 小优化（无 KC 条目）

- `AutomationSessionService`：去掉 `_agents` 字典（调用方 AutomationScheduler 的 try/finally 负责 dispose，DisposeAsync 改为 no-op with LogDebug）；整数除法顺序改为 `usableTokens * 4 / 5`（避免整除截断）。
- `AutomationScheduler`：去掉 `UpsertResultInboxItemAsync` 中冗余的 `GetByIdAsync`（runId 全局唯一，existing 永远为 null）。

---

## 迭代 44：多模态 Composer — Model Pill + Vision 图片输入（KC-4401~4404）

范围冻结：见 `docs/ITERATION_44_FREEZE.md`（2026-03-23）。在 Chat Composer 输入组加入只读 Model Pill 和 Vision 图片附件能力：Session API 补全模型信息字段，后端消息发送路径打通 mediaIds → ImageContent 转换，前端展示当前模型名并在 Vision 模型下显示图片附件按钮。

- `KC-4401`：`Completed`。
  - User Outcome：`GET /api/sessions/{id}` 响应包含 `modelEndpointId` 和 `modelCapabilities`，前端可据此展示 Model Pill 并派生能力按钮可见性。
  - Scope：`SessionDetail` record 加 `string? ModelEndpointId` 和 `int ModelCapabilities`；`GatewayApp.SessionEndpoints.cs` handler 组装 `SessionDetail` 时调 `IModelRegistryRepository.ResolveDefaultForAsync(TextChat|ToolCalling)` 填充两字段（无 endpoint 时填 null/0）；`contracts.ts` `SessionDetail` 接口加 `modelEndpointId?: string`、`modelCapabilities?: number`。
  - Modules：`KodaClaw.Contracts`、`KodaClaw.Gateway`、`apps/kodaclaw-web`。
  - Verification：L0 `dotnet build` + `npm run typecheck`；L2 集成测试：创建含 Vision capability 的 endpoint，`GET /api/sessions/{id}` 返回字段非空且 Vision bit 已置位；L3 契约测试覆盖新字段存在性。

- `KC-4402`：`Completed`。
  - User Outcome：用户在 Chat 输入框附带图片发送后，Agent 能接收到图文混合的消息内容（ImageContent + TextContent blocks）。
  - Scope：`SendMessageRequest` record 加 `IReadOnlyList<string>? MediaIds = null`；Gateway chat handler 解析并透传 `mediaIds`；`MainSessionService` 新增 `BuildUserContentAsync(message, mediaIds)` helper：遍历 mediaIds → `IMediaStore.GetAsync()` → `ImageContent(base64, mimeType)` + `TextContent(message)`，单个 id 不存在时记录诊断日志并跳过；`api.ts` `sendChatMessage` 加可选 `mediaIds?: string[]` 参数。
  - Modules：`KodaClaw.Contracts`、`KodaClaw.Gateway`、`KodaClaw.Runtime`、`apps/kodaclaw-web`。
  - Verification：L0 build + typecheck；L1 单元测试：有效/缺失/空 mediaIds 三个边界用例；L2 集成测试：POST message with mediaIds → SSE 流正常启动无异常。

- `KC-4403`：`Completed`。
  - User Outcome：Chat Composer 左下角显示当前 session 绑定模型名称的只读 pill，用户无需进 Models 设置才能知道 Koda 在用哪个模型。
  - Scope：`ChatComposer.tsx` 新增 `modelName?: string` 和 `modelCapabilities?: number` props；左下角渲染 `.composer__model-pill`（超长截断 18 字符 + ellipsis）；`modelName` 未提供时不渲染；`App.tsx` 在 `activeSessionId` 变化时调 `GET /api/sessions/{id}` 解析 `modelCapabilities`/`modelEndpointId`，从 `GET /api/model-endpoints` 匹配 name，传给 `ChatComposer`；顺手修复 KC-4306 微偏差（`已启用` 位置）。
  - Modules：`apps/kodaclaw-web`。
  - Verification：L0 typecheck；L1 Vitest：给定 `modelName="gpt-4o"` 时 pill 渲染含目标文字；`modelName` 未提供时 pill 不渲染。

- `KC-4404`：`Completed`。
  - User Outcome：当前模型支持 Vision 时，Composer 显示 Paperclip 附件按钮；用户可选择/粘贴图片，图片以缩略图预览显示在输入框上方，随消息一起发送给 Agent，Agent 能正确理解图片内容。
  - Scope：`ChatComposer.tsx` 加 `attachedMedia?: AttachedMedia[]`、`onAttachMedia?: (files: File[]) => void`、`onRemoveMedia?: (mediaId: string) => void` props；`modelCapabilities & TextChat(1) && modelCapabilities & Vision(4)` 同时为 true 时才渲染 Paperclip 按钮（纯 ImageGeneration / TTS / STT 模型不显示）（隐藏 `<input type="file" accept="image/*" multiple>`）；textarea `onPaste` 检测 `clipboardData.files` 中图片；附件预览区（`.composer__attachment-bar`）缩略图 + ✕ 删除；上传逻辑在 `App.tsx`：`POST /api/media/upload` → 追加 `AttachedMedia`；submit 时 `pendingMediaIds[]` 传入 `sendMessage()`；发送成功后清空；补充 CSS token 类：`.composer__attachment-bar`、`.composer__attachment-thumb`、`.composer__attachment-remove`、`.composer__attach-btn`。
  - Modules：`apps/kodaclaw-web`。
  - Verification：L0 typecheck；L1 Vitest：`modelCapabilities` 无 Vision → 附件按钮不渲染；`attachedMedia=[{...}]` → 预览区渲染对应数量缩略图；L5 Dogfood：Claude 3.5 Sonnet 附图发送 → Agent 正确描述图片内容。
  - Verification：L0 `npm run typecheck` ✓；L0 `dotnet build` 0 错 0 警告 ✓。

## Iter 47：Chat 会话三项 Bug 修复（KC-BUG-301~303）

- `KC-BUG-301`：`Completed`（2026-03-24）。
  - 症状：SessionHistoryPanel 点击"恢复"后，消息清空但历史消息永远不加载，只剩系统提示"已恢复会话"。
  - 根因：`useGatewaySnapshot` 不轮询，`snapshot.activeMainSessionId` 永不更新，导致 `loadHistory` 触发条件永远不满足；`onResumed` 回调无参数丢失 sessionId。
  - 受影响模块：`apps/kodaclaw-web`（`useSessionHistory.ts`、`SessionHistoryPanel.tsx`、`App.tsx`）
  - Scope：`onResumed` 回调链改为带 `sessionId: string`；`handleResumeSession(sessionId)` 直接调 `loadHistory(sessionId)` + `refresh()`；不依赖 snapshot 变化触发。
  - Verification：`npm run typecheck` ✓；L5 Dogfood：恢复历史会话后历史消息正确加载。

- `KC-BUG-302`：`Completed`（2026-03-24）。
  - 症状：Koda 流式输出时用户 resume/rotate 会话，旧流的 `tool_activity`/`approval_required` 事件仍追加到清空后的消息列表，出现幽灵消息。
  - 根因：`sendMessage` 未给 `streamChatEvents` 传 `AbortSignal`，`clearMessages` 不通知进行中的流。
  - 受影响模块：`apps/kodaclaw-web`（`useChatConsole.ts`、`SessionHistoryPanel.tsx`）
  - Scope：`useChatConsole` 加 `streamAbortRef`；`sendMessage` 每次创建 `AbortController` 传入流；`clearMessages` abort 当前流；catch 过滤 `AbortError`；SessionHistoryPanel 恢复时若 `isStreaming` 为真则先弹内联 confirm banner。
  - Verification：`npm run typecheck` ✓；L5 Dogfood：流进行中 resume，弹确认；确认后流中断，历史正确加载。

- `KC-BUG-304`：`Completed`（2026-03-24）。
  - 症状：恢复历史会话后，历史消息顺序颠倒——最新消息显示在最上方，最旧消息在 separator 正上方。
  - 根因：后端 `LoadSessionMessagesAsync` 以最新优先（`Reverse().Skip().Take()`）返回；前端 `prependHistory` / `loadMoreHistory` 直接使用该顺序 prepend，导致每批消息内部倒序。
  - 受影响模块：`apps/kodaclaw-web`（`useChatConsole.ts`）
  - Scope：`prependHistory` 和 `loadMoreHistory` 在 map 前加 `[...items].reverse()`，还原时序后再 prepend。
  - Verification：`npm run typecheck` ✓；L5 Dogfood：恢复历史会话，消息按时间从旧到新正确排列。

- `KC-BUG-303`：`Completed`（2026-03-24）。
  - 症状：composer model pill 切换模型后，前端显示新模型名，但后端 `_agents` 命中缓存，实际仍用旧模型回复。
  - 根因：`MainSessionService` 缓存命中时不重新解析模型；`setDefaultModelEndpoint` 只改 DB，不重置 session。
  - 受影响模块：`apps/kodaclaw-web`（`App.tsx`）
  - Scope（Option A2）：`handleModelChange` 切换成功后弹内联 confirm `"切换模型将开始新会话，继续？"`；确认后 `rotateSession()` + `clearMessages("已切换为 [model]，已开始新会话")`；取消后回滚 pill 到旧 model。
  - Verification：`npm run typecheck` ✓；L5 Dogfood：切换模型 → confirm → 新会话 → 实际使用新模型回复。

## 迭代 53：Skills 会话启动自动激活（KC-5301~5302）

范围冻结：见 `docs/ITERATION_53_FREEZE.md`（2026-03-25）。SDK `SkillsConfig` 新增 `AutoActivate` 字段，KodaClaw 三类 session service 按 session 类型配置对应的内置技能自动激活。**依赖迭代 52 的 KC-5201/5202 完成。**

- `KC-5301`：`Completed`（2026-03-25）。
  - User Outcome：`SkillsConfig` 支持 `AutoActivate` 字段，Agent 会话启动时自动激活指定技能，无需依赖 Template 系统；emit `SkillActivatedEvent(activatedBy="auto")`。
  - Scope：`Kode.Agent.Sdk/Core/Skills/SkillTypes.cs` — `SkillsConfig` 新增 `IReadOnlyList<string>? AutoActivate { get; init; }`；`Agent.cs` skills 初始化区块（约第 2845 行前）新增从 `_config.Skills?.AutoActivate` 读取并调用 `_skillsManager.AutoActivateAsync()` 的逻辑，复用现有 Template 路径的 context 注入和 event emit 代码；技能名不存在时静默跳过（SDK 已有行为）；与 Template `AutoActivate` 不互斥，各自独立运行。
  - Modules：`Kode.Agent.Sdk`。
  - Verification：`dotnet build` 0 错 0 警告；L1 `SkillsConfigAutoActivateTests`（`Kode.Agent.Tests`）3 个全通过；`dotnet test Kode.Agent.Tests --filter SkillsConfigAutoActivate` 通过。

- `KC-5302`：`Completed`（2026-03-25）。
  - User Outcome：主对话 session 启动后 Agent 无需手动 `skill_activate` 即可引用 workspace 协议和记忆管理细节；渠道 session 自动具备 channel_send 知识；自动化 session 自动具备 HEARTBEAT.md 语法知识。
  - Scope：`KodaClaw.Runtime` 新增 `BuiltinSkills.cs` 静态常量类（放 Runtime 层，非 Contracts，因为是 session 配置行为而非公共契约；5 个技能名常量 + 3 个 AutoActivate 列表：`ChatAutoActivate=[koda-workspace, koda-memory]`、`ChannelAutoActivate=[koda-workspace, koda-channels]`、`AutomationAutoActivate=[koda-workspace, koda-automation]`）；`MainSessionService.cs` 2 处（新建+resume）、`ChannelSessionService.cs` 2 处（DM 新建+resume）、`AutomationSessionService.cs` 1 处的 `SkillsConfig` 构造加 `AutoActivate` 字段（注意：主对话 skills 配置在 `MainSessionService.cs`，不在 `ChatSessionService.cs`）；`koda-canvas` 不加入任何 session 的自动激活列表。
  - Modules：`KodaClaw.Runtime`。
  - Verification：`dotnet build` 0 错 0 警告；L1 `BuiltinSkillsTests`（`KodaClaw.UnitTests`）5 个全通过；`dotnet test KodaClaw.sln -m:1` 693 个测试 0 失败。

---

## 迭代 54：agentskills.io 标准对齐 + `allowed-tools` 功能化（KC-5401~5404）

范围冻结：见 `docs/ITERATION_54_FREEZE.md`（2026-03-25）。将 SKILL.md 非标准顶层字段（`requires/kind/version/tags`）迁移至标准位置（`allowed-tools` + `metadata:` 块），修复 SDK 解析 bug，实现 skill 激活时自动扩展工具白名单的完整功能链路。**依赖迭代 52+53 完成。**

- `KC-5401`：`Completed`。
  - User Outcome：SDK `SkillsLoader` 正确解析标准 `allowed-tools`（连字符，空格分隔）和 `metadata:` 嵌套块；`SkillMetadata.Metadata` 不再为 null；`SkillsLoader.ParseFrontmatter(string)` 暴露为 public static，供 KodaClaw Gateway 复用。
  - Scope：`Kode.Agent.Sdk/Core/Skills/SkillsLoader.cs` — 新增 `case "allowed-tools":` 分支，改为空格分隔解析；新增 `metadata:` 嵌套块状态机解析，填充 `SkillMetadata.Metadata`；将 `ParseSkillFile` 内核提取为 `public static SkillMetadata ParseFrontmatter(string content)`。
  - Modules：`Kode.Agent.Sdk`。
  - Verification：`dotnet build` 0 错 0 警告 ✓；L0 通过 ✓。

- `KC-5402`：`Completed`。
  - User Outcome：skill 激活后其 `allowed-tools` 中列出的工具自动加入 session 工具白名单，不再需要在 `AgentConfig.Tools` 中预先配置；白名单为空（mode=auto）时不受影响。
  - Scope：`PermissionManager.cs` 新增 `public void GrantTools(IEnumerable<string>)`（`_allowTools==null` 时 no-op，防止全放行模式被误变为白名单模式；`lock (_lock)` 保证线程安全）；`Agent.cs` `InitializeSkillsAsync` 中 `SkillsConfig.AutoActivate` 和 Template AutoActivate 两条路径激活完成后，各调 `_permissionManager.GrantTools(activated.SelectMany(s => s.AllowedTools ?? []).Distinct())`。
  - Modules：`Kode.Agent.Sdk`。
  - Verification：`dotnet build` 0 错 0 警告 ✓。

- `KC-5403`：`Completed`。
  - User Outcome：5 个内置 SKILL.md 使用标准格式（`allowed-tools` 空格分隔，`metadata:` 块存 `kind/version/tags`，新增 `compatibility: KodaClaw 1.x`）；KodaClaw `SkillFrontmatterParser` 精简为 SDK 委托 + dict 提取，消除重复解析逻辑；`SkillDescriptor.Requires` 重命名为 `AllowedTools`，新增 `Compatibility` 字段。
  - Scope：5 个 SKILL.md 格式迁移（`koda-workspace/automation/canvas/channels/memory`）；`SkillFrontmatterParser.cs` 改为调 `SkillsLoader.ParseFrontmatter` + 从 `Metadata` dict 提取 `kind/version/tags`，删除 `ParseInlineList`，`ParsedFrontmatter` 字段 `Requires`→`AllowedTools` + 加 `Compatibility`；`SkillContracts.cs` `SkillDescriptor` 同步重命名 + 加字段；`GatewayApp.SkillsEndpoints.cs` 改为 `ReadAllTextAsync` + 更新构造。
  - Modules：`KodaClaw.Gateway`、`KodaClaw.Contracts`、`KodaClaw.Gateway/skills`。
  - Verification：`dotnet build` 0 错 0 警告 ✓；L1 `SkillFrontmatterParserTests` 12/12 ✓；L3 `SkillDescriptorContractTests` 4/4 ✓；L2 `SkillsEndpointIntegrationTests` 通过 ✓。

- `KC-5404`：`Completed`。
  - User Outcome：SkillsDesk 显示"所需工具"（AllowedTools），新增 `compatibility` 展示行（有值时显示）；前端类型与后端 contract 一致。
  - Scope：`contracts.ts` `SkillDescriptor` 字段 `requires`→`allowedTools`，加 `compatibility?: string | null`；`SkillsDesk.tsx` 渲染逻辑 + i18n key（`skillAllowedToolsLabel`/`skillCompatibilityLabel`）+ compatibility 行（有值时显示，`--text-tertiary` 色）。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npm run typecheck` 通过 ✓；全量 691 个后端测试通过 ✓。

---

## 迭代 52：Skills 内容扩充 + Frontmatter 规范（KC-5201~5203）

范围冻结：见 `docs/ITERATION_52_FREEZE.md`（2026-03-25）。补全 4 个核心内置技能文件，规范化 SKILL.md frontmatter（新增 kind/tags/requires/version），升级 `GET /api/skills` 解析和 SkillsDesk 展示。

- `KC-5201`：`Completed`（2026-03-25）。
  - User Outcome：`GET /api/skills` 返回的每个 `SkillDescriptor` 包含 `kind`、`tags`、`requires`、`version` 字段；前端可据此区分核心技能与可选技能；`koda-workspace` frontmatter 同步补齐新字段。
  - Scope：`KodaClaw.Contracts` — 新建 `SkillContracts.cs`，将 `SkillDescriptor` 从 `GatewayApp.SkillsEndpoints.cs` 的 `private sealed record` 提升至 Contracts 层，同时扩展 `string Kind`、`IReadOnlyList<string> Tags`、`IReadOnlyList<string> Requires`、`string? Version` 四个字段；`GatewayApp.SkillsEndpoints.cs` 删除内部 record，引用 Contracts 类型；新建 `KodaClaw.Gateway/Infrastructure/SkillFrontmatterParser.cs`（internal，`InternalsVisibleTo` 对测试开放）；`skills/koda-workspace/SKILL.md` frontmatter 补齐新字段；`contracts.ts` 新增 `SkillDescriptor` 接口；`api.ts` 新增 `fetchSkills(signal?)` 函数。
  - Modules：`KodaClaw.Contracts`、`KodaClaw.Gateway`、`apps/kodaclaw-web`。
  - Verification：`dotnet build` 0 错 0 警告；`npm run typecheck` 通过；L1 `SkillFrontmatterParserTests` 15 个全通过；L3 `SkillDescriptorContractTests` 3 个全通过；L2 `SkillsEndpointIntegrationTests` 3 个全通过。

- `KC-5202`：`Completed`（2026-03-25）。
  - User Outcome：`skill_list` 发现 5 个内置技能（原有 1 个 + 新增 4 个）；新用户第一次激活 `koda-automation` 后 Agent 能正确回答 HEARTBEAT.md 语法问题。
  - Scope：新增 4 个技能文件（纯 Markdown，`.csproj` 的 `skills/**` glob 自动打包，无需代码改动）：`skills/koda-automation/SKILL.md`（HEARTBEAT.md YAML 语法、cron、channels 推送、delivery-mode 三种模式、常用模板）；`skills/koda-canvas/SKILL.md`（artifact 类型、canvas_write 参数、图片生成）；`skills/koda-channels/SKILL.md`（channel_send 参数、Telegram/飞书/微信格式差异、媒体附件）；`skills/koda-memory/SKILL.md`（MEMORY.md 索引格式、何时写、与 USER.md 的区别）；所有文件按 KC-5201 规范写 frontmatter（kind: builtin-core，tags/requires 使用 inline list 格式）。
  - Modules：`KodaClaw.Gateway/skills/`。
  - Verification：`dotnet build` 0 错 0 警告；L5 Dogfood：真实 Gateway 启动后 `GET /api/skills` 返回 5 个内置技能，koda-automation kind="builtin-core"。

- `KC-5203`：`Completed`（2026-03-25）。
  - User Outcome：SkillsDesk 展示 kind badge（builtin-core 用 amber 色突出）、tags chips、requires 提示行；built-in 组内 builtin-core 技能优先排列。
  - Scope：`SkillsDesk.tsx` 删除本地 `type SkillDescriptor` 和本地 `fetchSkills`，改为从 `contracts.ts`/`api.ts` 导入（KC-5201 已添加）；保留按 source 三组结构，built-in 组内按 kind 排序（builtin-core 先）；技能卡片新增 kind badge、tags chips（上限 3 个）、requires 行（非空时显示）；`useLocaleText()` 内联对象扩展 3 个键（zh: `核心`/`可选`/`依赖工具`，en: `Core`/`Optional`/`Requires`）（**不**修改 `app-strings.ts`）；CSS 新增 6 个 `.skill-card__*` token 类（`--core` 使用 `--accent`/`--accent-soft`，`--optional` 使用 `--text-tertiary`/`--border-subtle`），全部使用 CSS token 变量。
  - Modules：`apps/kodaclaw-web`。
  - Verification：`npm run typecheck` 通过；L5 Dogfood：SkillsDesk 展示 5 个技能，built-in 组内 koda-* 排在前，koda-automation 显示 amber "核心" badge 和 tags。

---

## 自动化任务 per-model 配置（KC-BUG-204 / 优化）

- `KC-BUG-204`：`Completed`（2026-03-23）。
  - User Outcome：用户可在 HEARTBEAT.md 的自动化段落中加 `- model: <model-id>` 字段，为单个自动化任务指定独立模型；未配置时软降级到全局默认模型；AutomationsDesk 卡片和详情面板均展示已配置模型。
  - Scope：`AutomationDefinition` record 加 `string? ModelId` 字段；`HeartbeatAutomationCompiler` 解析 `- model:` 字段，写入 `SectionDraft.ModelId`，构造时透传；`SqliteAutomationDatabase` DDL 加 `model_id TEXT NULL` + 幂等 ALTER TABLE 迁移；`SqliteAutomationDefinitionRepository` INSERT/SELECT/MapDefinition 同步更新（列索引 12→17 全部 +1）；`AutomationSessionService.ResolveConfiguredModelAsync` 优先使用 `definition.ModelId`；前端 `contracts.ts` `AutomationDefinition` 加 `modelId?: string | null`；`AutomationsDesk.tsx` 卡片 meta 区展示 model tag、详情面板加模型行；CSS 加 `.automation-card__model-tag`、`.automation-detail-model-id`、`.metric-value--muted`。
  - Modules：`KodaClaw.Contracts`、`KodaClaw.Workspace`、`KodaClaw.Automation`、`KodaClaw.Runtime`、`apps/kodaclaw-web`。
  - Verification：`dotnet build KodaClaw.sln` 0 错 0 警告；`npm run typecheck` 通过；合约测试 108/108；自动化相关单元测试 26/26。
