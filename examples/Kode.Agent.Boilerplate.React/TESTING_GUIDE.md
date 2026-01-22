# Tool Events 测试指南

## 快速测试步骤

### 1. 启动后端服务

```powershell
cd C:\Code\featbit\featbit-front-agent-api\examples\Kode.Agent.Boilerplate
$env:ASPNETCORE_ENVIRONMENT='Development'
dotnet run
```

**预期输出：**
```
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://localhost:5000
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
```

### 2. 启动前端服务

打开**新的** PowerShell 终端：

```powershell
cd C:\Code\featbit\featbit-front-agent-api\examples\Kode.Agent.Boilerplate.React
npm run dev
```

**预期输出：**
```
  VITE v5.x.x  ready in xxx ms

  ➜  Local:   http://localhost:5173/
  ➜  Network: use --host to expose
```

### 3. 打开浏览器

访问 `http://localhost:5173`

### 4. 测试 Tool Events

#### 测试 1：FeatBit 文档搜索

在聊天框中输入：
```
tell me how to use featbit .net sdk
```

**预期看到：**
- 🔧 Tool Calls 区域出现
- 看到蓝色的工具运行状态
- 工具完成后变成绿色，显示执行时长
- Agent 返回关于 FeatBit .NET SDK 的回答

#### 测试 2：文件系统操作（如果有相关工具）

```
列出当前目录的文件
```

或

```
读取 README.md 的内容
```

#### 测试 3：多个工具调用

```
search featbit documentation about feature flags and then show me code examples
```

这可能会触发多个工具调用，你会看到多个工具卡片同时或依次出现。

## 调试检查点

### 后端日志检查

在后端终端中，你应该看到类似这样的日志：

```
[Stream] 📝 Processing event: ToolStartEvent
[Stream] ✅ Tool started: mcp__featbit__search_documentation (ID: call_xxx)
[Stream] Sending tool:start event: {"id":"chatcmpl-xxx","event":"tool:start",...}
[Stream] 📊 SSE event sent. Type: tool:start, Content length: 234 chars

[Stream] 📝 Processing event: ToolEndEvent
[Stream] ✅ Tool completed: mcp__featbit__search_documentation (Duration: 4632ms)
[Stream] Sending tool:end event: {"id":"chatcmpl-xxx","event":"tool:end",...}
```

### 前端控制台检查

打开浏览器开发者工具（F12），在 Console 中你应该看到：

```
[API] Tool event received: {event: "tool:start", tool_call_id: "call_xxx", ...}
[API] Tool event received: {event: "tool:end", tool_call_id: "call_xxx", ...}
```

### 前端 UI 检查

在消息流区域，你应该看到：

```
┌────────────────────────────────────┐
│ 🔧 Tool Calls:                     │
│                                    │
│  ┌──────────────────────────────┐ │
│  │ 🔄 mcp__featbit__search...   │ │  ← 蓝色背景（运行中）
│  │ Running...                    │ │
│  └──────────────────────────────┘ │
│                                    │
│  ┌──────────────────────────────┐ │
│  │ ✅ mcp__featbit__search...   │ │  ← 绿色边框（完成）
│  │ Completed - 4632ms           │ │
│  └──────────────────────────────┘ │
└────────────────────────────────────┘
```

## 常见问题排查

### 问题 1：前端看不到 Tool Events

**检查项：**
1. 后端日志中是否有 "Sending tool:start event" 字样？
   - 如果没有：工具可能没有被调用，尝试不同的问题
   - 如果有：继续下一步

2. 浏览器控制台有 "[API] Tool event received" 日志吗？
   - 如果没有：检查网络连接，SSE stream 是否正常
   - 如果有：继续下一步

3. 检查 ChatPanel.tsx 的 `onToolEvent` 回调是否被调用
   - 在 `onToolEvent` 函数开头添加 `console.log('Tool event handler:', event);`

### 问题 2：Tool 状态卡片样式不正确

确保 TailwindCSS 正常工作：
```bash
# 重新构建
npm run dev
```

### 问题 3：后端没有发送 Tool Events

检查 `AssistantService.cs` 中的事件处理器：
```csharp
else if (envelope.Event is ToolStartEvent toolStart)
{
    // 确保这段代码存在
}
```

### 问题 4：SSE 连接断开

检查：
1. 后端是否正在运行？
2. CORS 配置是否正确？
3. 网络代理设置？

## 成功标准

✅ 后端日志显示 tool 事件被发送
✅ 前端控制台显示 tool 事件被接收
✅ UI 中出现彩色的 tool 调用卡片
✅ 卡片状态正确更新（蓝色→绿色/红色）
✅ 显示工具执行时长
✅ 如果出错，显示错误信息

## 性能指标

正常情况下：
- Tool 启动延迟：< 100ms
- Tool 执行时间：根据工具类型，通常 100ms - 5000ms
- UI 更新延迟：< 50ms

## 下一步

如果测试通过，你可以：
1. 自定义 tool 卡片的样式和布局
2. 添加更多工具执行的详细信息
3. 添加工具参数和结果的展示
4. 实现工具调用历史记录
