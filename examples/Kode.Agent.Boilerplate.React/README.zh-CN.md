# Kode.Agent.Boilerplate.React

一个现代化的 React 前端项目，为 Kode.Agent.Boilerplate API 提供类似 ChatGPT 的交互界面。

## 功能特性

✅ **现代技术栈**
- React 19+ 与 TypeScript
- Vite 快速开发和构建
- Tailwind CSS 4.1 样式系统
- Shadcn UI 组件库

✅ **聊天界面**
- 类似 ChatGPT 的用户界面
- 会话管理（创建、选择、删除）
- 实时流式响应
- 消息历史本地持久化存储

✅ **OpenAI 兼容**
- 与 Kode.Agent.Boilerplate API 无缝对接
- 支持流式和非流式响应
- 自动会话 ID 管理

## 界面预览

应用程序包含：
- **左侧边栏**：会话列表，支持创建/删除功能
- **右侧面板**：聊天界面，包含消息历史和输入框

## 前置要求

- Node.js 18+ 或更高版本
- npm、yarn 或 pnpm 包管理器
- 运行中的 [Kode.Agent.Boilerplate](../Kode.Agent.Boilerplate) 后端 API

## 快速开始

### 1. 安装依赖

```bash
cd examples/Kode.Agent.Boilerplate.React
npm install
```

或使用 yarn:
```bash
yarn install
```

或使用 pnpm:
```bash
pnpm install
```

### 2. 配置后端 API

前端默认配置代理请求到 `http://localhost:5000`。如果您的后端运行在不同端口，请在 `vite.config.ts` 中更新代理配置：

```typescript
export default defineConfig({
  // ...
  server: {
    port: 3000,
    proxy: {
      '/v1': {
        target: 'http://localhost:5000', // 修改为您的后端地址
        changeOrigin: true,
      },
    },
  },
})
```

### 3. 启动开发服务器

```bash
npm run dev
```

应用程序将在 `http://localhost:3000` 可用。

### 4. 启动后端 API

确保 Kode.Agent.Boilerplate 后端正在运行：

```bash
cd ../Kode.Agent.Boilerplate
dotnet run
```

## 使用方法

1. **创建新对话**：点击侧边栏中的 `+` 按钮
2. **发送消息**：输入消息并按 Enter 键（或点击发送按钮）
3. **切换对话**：点击侧边栏中的任意会话
4. **删除对话**：鼠标悬停在会话上，点击垃圾桶图标

### 键盘快捷键

- `Enter`：发送消息
- `Shift + Enter`：消息中换行

## 项目结构

```
Kode.Agent.Boilerplate.React/
├── public/                  # 静态资源
├── src/
│   ├── components/          # React 组件
│   │   ├── ui/              # Shadcn UI 组件
│   │   ├── ChatPanel.tsx    # 主聊天界面
│   │   └── SessionList.tsx  # 会话列表侧边栏
│   ├── contexts/            # React 上下文
│   │   └── ChatContext.tsx  # 聊天状态管理
│   ├── lib/                 # 工具函数
│   │   └── utils.ts         # 辅助工具
│   ├── services/            # API 服务
│   │   └── api.ts           # 后端 API 客户端
│   ├── types/               # TypeScript 类型
│   │   └── index.ts         # 类型定义
│   ├── App.tsx              # 根组件
│   ├── main.tsx             # 应用入口点
│   └── index.css            # 全局样式
├── index.html               # HTML 模板
├── package.json             # 依赖项
├── tsconfig.json            # TypeScript 配置
├── tailwind.config.ts       # Tailwind CSS 配置
├── vite.config.ts           # Vite 配置
└── components.json          # Shadcn UI 配置
```

## 配置

### API 端点

API 端点在 `src/services/api.ts` 中配置。默认情况下，它使用 `/v1`，通过 Vite 开发服务器代理到后端。

### 主题自定义

您可以通过编辑 `src/index.css` 中的 CSS 变量来自定义颜色方案：

```css
:root {
  --background: 0 0% 100%;
  --foreground: 222.2 84% 4.9%;
  /* ... 更多变量 */
}
```

## 生产构建

### 构建应用

```bash
npm run build
```

构建输出将在 `dist/` 目录中。

### 预览生产构建

```bash
npm run preview
```

### 部署

构建后的应用是一个静态网站，可以部署到任何静态托管服务：

- Vercel
- Netlify
- GitHub Pages
- AWS S3 + CloudFront
- Azure Static Web Apps

确保配置 API 代理或更新 API 基础 URL 以指向您的生产后端。

## 开发

### 添加新的 Shadcn 组件

您可以根据需要添加更多 Shadcn UI 组件。例如，添加对话框组件：

```bash
npx shadcn@latest add dialog
```

### 代码风格

项目使用：
- ESLint 进行代码检查
- TypeScript 进行类型检查

运行检查：
```bash
npm run lint
```

## 详细功能

### 会话管理

- 会话存储在 localStorage 中以实现持久化
- 每个会话都有一个与后端同步的唯一 ID
- 会话标题从第一条用户消息自动生成

### 消息流式传输

应用使用 Server-Sent Events (SSE) 进行实时流式响应：

1. 用户发送消息
2. 前端向 `/v1/chat/completions` 发送请求，设置 `stream: true`
3. 后端流式传输响应块
4. 前端实时显示响应块

### 错误处理

- 捕获并在聊天中显示网络错误
- 失败的请求显示错误消息
- 即使后端暂时不可用，会话仍然持久化

## 故障排除

### 后端连接问题

如果看到连接错误：

1. 确保后端在 `http://localhost:5000` 上运行
2. 检查 `vite.config.ts` 中的代理配置
3. 检查浏览器控制台是否有 CORS 错误

### 会话不持久化

- 会话存储在 localStorage 中
- 检查浏览器是否启用了 localStorage
- 清除 localStorage 以重置：`localStorage.clear()`

### 构建错误

如果遇到构建错误：

1. 删除 `node_modules` 并重新安装：`rm -rf node_modules && npm install`
2. 清除 Vite 缓存：`rm -rf node_modules/.vite`
3. 检查 TypeScript 错误：`npm run build`

## 许可证

与父项目相同。

## 相关项目

- [Kode.Agent.Boilerplate](../Kode.Agent.Boilerplate) - 后端 API
- [Kode.Agent.Sdk](../../src/Kode.Agent.Sdk) - 核心 SDK
- [Shadcn UI](https://ui.shadcn.com/) - UI 组件
- [Vite](https://vite.dev/) - 构建工具
