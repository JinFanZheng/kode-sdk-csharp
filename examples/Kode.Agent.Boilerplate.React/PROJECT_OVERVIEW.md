# Kode.Agent.Boilerplate.React - 项目概览

## 项目信息

- **名称**: Kode.Agent.Boilerplate.React
- **类型**: React + TypeScript + Vite 单页应用
- **目的**: 为 Kode.Agent.Boilerplate 提供现代化的聊天界面

## 技术栈

### 核心框架
- **React 19.0.0** - 最新的 React 版本
- **TypeScript 5.6.2** - 类型安全
- **Vite 6.0.5** - 快速的构建工具

### UI 框架
- **Tailwind CSS 4.1.0** - 实用优先的 CSS 框架
- **Shadcn UI** - 高质量的 React 组件库
- **Radix UI** - 无样式的可访问组件基础
- **Lucide React** - 美观的图标库

### 开发工具
- **ESLint** - 代码质量检查
- **TypeScript ESLint** - TypeScript 专用规则
- **PostCSS** - CSS 处理

## 项目结构

```
Kode.Agent.Boilerplate.React/
├── src/
│   ├── components/
│   │   ├── ui/                    # Shadcn UI 基础组件
│   │   │   ├── button.tsx         # 按钮组件
│   │   │   ├── card.tsx           # 卡片组件
│   │   │   ├── input.tsx          # 输入框组件
│   │   │   ├── textarea.tsx       # 文本域组件
│   │   │   └── scroll-area.tsx    # 滚动区域组件
│   │   ├── ChatPanel.tsx          # 聊天面板（右侧）
│   │   └── SessionList.tsx        # 会话列表（左侧）
│   ├── contexts/
│   │   └── ChatContext.tsx        # 全局聊天状态管理
│   ├── services/
│   │   └── api.ts                 # API 服务层
│   ├── types/
│   │   └── index.ts               # TypeScript 类型定义
│   ├── lib/
│   │   └── utils.ts               # 工具函数
│   ├── App.tsx                    # 根组件
│   ├── main.tsx                   # 应用入口
│   └── index.css                  # 全局样式
├── public/                         # 静态资源
├── .vscode/                        # VS Code 配置
├── package.json                    # 项目依赖
├── vite.config.ts                  # Vite 配置
├── tailwind.config.ts              # Tailwind 配置
├── tsconfig.json                   # TypeScript 配置
├── components.json                 # Shadcn UI 配置
└── README.md                       # 项目文档
```

## 核心功能模块

### 1. 状态管理 (ChatContext)
- 使用 React Context API 管理全局状态
- 会话（Session）管理：创建、选择、删除
- 消息（Message）管理：添加、更新
- localStorage 持久化存储

### 2. API 服务 (ApiService)
- 与 Kode.Agent.Boilerplate 后端通信
- 支持流式（SSE）和非流式响应
- 自动处理会话 ID
- 错误处理和重试机制

### 3. UI 组件

#### SessionList（会话列表）
- 显示所有聊天会话
- 创建新会话
- 选择当前会话
- 删除会话
- 显示会话标题和消息数量

#### ChatPanel（聊天面板）
- 显示当前会话的消息历史
- 用户和助手消息的差异化展示
- 实时流式响应
- 消息输入和发送
- 加载状态指示
- 自动滚动到最新消息

### 4. 类型系统
- Message: 消息类型（用户/助手）
- Session: 会话类型
- OpenAI 兼容的请求/响应类型

## 数据流

```
用户输入消息
    ↓
ChatPanel 组件
    ↓
ChatContext (addMessage)
    ↓
ApiService.sendMessageStream
    ↓
后端 API (/v1/chat/completions)
    ↓
SSE 流式响应
    ↓
ChatContext (updateLastMessage)
    ↓
UI 实时更新
```

## 后端集成

### API 端点
- `POST /v1/chat/completions` - 新会话聊天
- `POST /{sessionId}/v1/chat/completions` - 继续现有会话

### 请求格式
```json
{
  "model": "claude-sonnet-4",
  "messages": [
    {"role": "user", "content": "Hello"}
  ],
  "stream": true
}
```

### 响应格式
- 流式：SSE 格式的增量更新
- 非流式：完整的 JSON 响应
- 响应头：`X-Session-Id` 包含会话 ID

## 本地开发

### 安装
```bash
npm install
```

### 开发
```bash
npm run dev
```
访问 `http://localhost:3000`

### 构建
```bash
npm run build
```

### 预览
```bash
npm run preview
```

## 配置

### Vite 代理
在开发模式下，`/v1` 请求会被代理到 `http://localhost:5000`

### 环境变量
可以通过 `.env` 文件配置：
```
VITE_API_BASE_URL=http://localhost:5000
```

## 最佳实践

### 代码组织
- 组件职责单一
- 使用 TypeScript 确保类型安全
- 提取可复用的工具函数
- 使用 Context 避免 props drilling

### 状态管理
- 本地状态用 useState
- 全局状态用 Context
- 副作用用 useEffect
- localStorage 持久化重要数据

### 样式
- 使用 Tailwind CSS 实用类
- 通过 CSS 变量支持主题
- 响应式设计
- 可访问性（Accessibility）

### 性能
- React.memo 避免不必要的重渲染
- useCallback 缓存回调函数
- 虚拟滚动（如需要）
- 代码分割（如需要）

## 扩展点

### 添加新功能
1. **文件上传** - 添加文件上传组件
2. **代码高亮** - 集成代码高亮库
3. **Markdown 渲染** - 渲染 Markdown 格式消息
4. **多模型切换** - 支持选择不同的 AI 模型
5. **导出对话** - 导出聊天历史为 JSON/Markdown

### 优化方向
1. **PWA** - 添加 Service Worker 支持离线使用
2. **国际化** - 支持多语言
3. **暗色模式** - 完善主题切换
4. **性能监控** - 集成性能监控工具
5. **单元测试** - 添加组件测试

## 常见问题

### Q: 如何修改 API 地址？
A: 修改 `vite.config.ts` 中的 proxy 配置。

### Q: 会话数据存储在哪里？
A: localStorage，键名为 `kode-agent-sessions`。

### Q: 如何添加新的 Shadcn 组件？
A: 运行 `npx shadcn@latest add [component-name]`。

### Q: 如何自定义主题颜色？
A: 修改 `src/index.css` 中的 CSS 变量。

## 维护者

与 Kode.Agent 项目相同

## 许可证

与父项目相同
