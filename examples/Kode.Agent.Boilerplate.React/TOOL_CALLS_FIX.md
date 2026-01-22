# Tool Calls 持久化修复说明

## 问题描述

Tool calls 在流式响应过程中能够实时显示，但在消息完成后没有正确保存到历史消息中。

## 根本原因

使用闭包捕获的 `toolCalls` 状态值在异步回调中可能是旧值，导致在 `onComplete` 时保存的是空数组或旧数据。

## 解决方案

使用 `useRef` 来保存最新的 tool calls 数据：

### 1. 添加 Ref

```tsx
const toolCallsRef = useRef<ToolCall[]>([]);
```

### 2. 同步更新 Ref

在每次更新 `toolCalls` 状态时，同步更新 ref：

```tsx
// onToolEvent
(event) => {
  setToolCalls(prev => {
    // ... 处理事件逻辑
    
    toolCallsRef.current = updated;  // ✅ 同步更新 ref
    return updated;
  });
}
```

### 3. 在回调中使用 Ref

在 `onComplete` 和 `onError` 中使用 ref 获取最新值：

```tsx
// onComplete
(sessionId) => {
  const finalToolCalls = toolCallsRef.current;  // ✅ 从 ref 读取最新值
  if (finalToolCalls.length > 0) {
    updateLastMessage('', finalToolCalls);
  }
  // 清空状态和 ref
  setToolCalls([]);
  toolCallsRef.current = [];
}
```

```tsx
// onError
(error) => {
  const finalToolCalls = toolCallsRef.current;  // ✅ 即使出错也保存 tool calls
  if (finalToolCalls.length > 0) {
    updateLastMessage(`\n\n[Error: ${error.message}]`, finalToolCalls);
  } else {
    updateLastMessage(`\n\n[Error: ${error.message}]`);
  }
  // 清空状态和 ref
  setToolCalls([]);
  toolCallsRef.current = [];
}
```

## 调试日志

添加了以下日志来帮助诊断问题：

```tsx
// 每次收到 tool event
console.log('[ChatPanel] Tool event:', event);

// 每次更新 toolCallsRef
console.log('[ChatPanel] Updated toolCallsRef:', updated);

// 在 onComplete 中保存时
console.log('[ChatPanel] Saving tool calls to message:', finalToolCalls);

// 在 onError 中保存时
console.log('[ChatPanel] Saving tool calls on error:', finalToolCalls);
```

## 测试步骤

### 1. 启动服务

**后端：**
```powershell
cd C:\Code\featbit\featbit-front-agent-api\examples\Kode.Agent.Boilerplate
$env:ASPNETCORE_ENVIRONMENT='Development'
dotnet run
```

**前端：**
```powershell
cd C:\Code\featbit\featbit-front-agent-api\examples\Kode.Agent.Boilerplate.React
npm run dev
```

### 2. 测试正常流程

1. 打开浏览器控制台（F12）
2. 发送消息：`tell me how to use featbit .net sdk`
3. 观察控制台日志：
   ```
   [ChatPanel] Tool event: {event: "tool:start", ...}
   [ChatPanel] Updated toolCallsRef: [{id: "call_xxx", name: "mcp__featbit__search_documentation", ...}]
   [ChatPanel] Tool event: {event: "tool:end", ...}
   [ChatPanel] Updated toolCallsRef: [{id: "call_xxx", state: "completed", duration: 4632, ...}]
   [ChatPanel] Saving tool calls to message: [{...}]
   ```
4. 查看消息卡片，确认显示了 tool calls
5. 向上滚动查看历史消息，确认 tool calls 仍然显示
6. 刷新页面，确认从 localStorage 恢复的消息包含 tool calls

### 3. 测试错误场景

1. 模拟网络错误或后端错误
2. 观察控制台日志：
   ```
   [ChatPanel] Saving tool calls on error: [{...}]
   ```
3. 确认即使出错，tool calls 也被保存到消息中

### 4. 验证持久化

1. 发送几条需要 tool 的消息
2. 关闭浏览器标签页
3. 重新打开应用
4. 选择之前的会话
5. 确认所有历史消息的 tool calls 都正确显示

## 预期效果

### 实时显示（流式过程中）

在消息列表下方单独显示：
```
┌────────────────────────────────────┐
│ 🔧 Tool Calls:                     │
│                                    │
│  ┌──────────────────────────────┐ │
│  │ 🔄 mcp__featbit__search...   │ │  ← 蓝色，运行中
│  │ Running...                    │ │
│  └──────────────────────────────┘ │
└────────────────────────────────────┘

┌────────────────────────────────────┐
│ 🤖 Assistant                       │
│ [正在生成回复...]                   │
└────────────────────────────────────┘
```

### 持久化显示（消息完成后）

在消息卡片内部显示：
```
┌────────────────────────────────────┐
│ 🤖 Assistant                       │
│                                    │
│ 🔧 Tool Calls:                     │
│  ┌──────────────────────────────┐ │
│  │ ✅ mcp__featbit__search...   │ │  ← 绿色，已完成
│  │ 4632ms                        │ │
│  └──────────────────────────────┘ │
│                                    │
│ Based on the FeatBit documentation,│
│ here's how to use the .NET SDK...  │
│                                    │
│ 10:30:45 AM                        │
└────────────────────────────────────┘
```

### 错误场景

即使执行失败，也会显示：
```
┌────────────────────────────────────┐
│ 🤖 Assistant                       │
│                                    │
│ 🔧 Tool Calls:                     │
│  ┌──────────────────────────────┐ │
│  │ ❌ read_file                 │ │  ← 红色，失败
│  │ File not found               │ │
│  └──────────────────────────────┘ │
│                                    │
│ [Error: Request failed]            │
│                                    │
│ 10:31:20 AM                        │
└────────────────────────────────────┘
```

## 技术细节

### 为什么需要 useRef？

React 的 `useState` 在异步回调中会形成闭包，捕获的是定义时的状态值。

**问题示例：**
```tsx
const [toolCalls, setToolCalls] = useState([]);

// 在 tool:start 时
setToolCalls([{ id: '1', name: 'tool1' }]);

// 在 tool:end 时（几秒后）
setToolCalls(prev => [...prev, updated]);  // prev 是最新的

// 但在 onComplete 中（异步回调）
console.log(toolCalls);  // ❌ 可能还是 []，因为闭包捕获了旧值
```

**解决方案：**
```tsx
const toolCallsRef = useRef([]);

// 每次更新状态时，同步更新 ref
setToolCalls(prev => {
  const updated = [...prev, newItem];
  toolCallsRef.current = updated;  // ✅ ref 总是最新的
  return updated;
});

// 在异步回调中
console.log(toolCallsRef.current);  // ✅ 获取最新值
```

### 状态 vs Ref

| | useState | useRef |
|---|---|---|
| 触发重渲染 | ✅ 是 | ❌ 否 |
| 闭包问题 | ❌ 有 | ✅ 无 |
| 适用场景 | UI 显示 | 跨渲染共享数据 |

我们的方案：
- 使用 `useState` 来触发 UI 更新（实时显示 tool calls）
- 使用 `useRef` 来在异步回调中获取最新值（持久化保存）

## 数据流图

```
用户发送消息
    ↓
清空 toolCalls 和 toolCallsRef
    ↓
开始 SSE 流
    ↓
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
收到 tool:start
    ↓
setToolCalls([{...}])          ← 触发重渲染，显示蓝色卡片
    ↓
toolCallsRef.current = [{...}] ← 保存到 ref
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
收到 tool:end
    ↓
setToolCalls([{... state: completed}]) ← 更新为绿色
    ↓
toolCallsRef.current = [{... state: completed}] ← 更新 ref
━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━
流结束 (onComplete)
    ↓
const finalToolCalls = toolCallsRef.current ← ✅ 从 ref 读取最新值
    ↓
updateLastMessage('', finalToolCalls) ← 保存到消息
    ↓
消息持久化到 localStorage
    ↓
清空 toolCalls 和 toolCallsRef
```

## 常见问题

### Q: 为什么不直接在 onComplete 中读取 toolCalls 状态？

A: 因为 `onComplete` 是在 `sendMessageStream` 调用时定义的闭包，它捕获的是调用时的 `toolCalls` 值（空数组），而不是最新值。

### Q: 为什么要同时清空状态和 ref？

A: 
- 清空状态 (`setToolCalls([])`) 是为了不显示实时 tool calls 区域
- 清空 ref (`toolCallsRef.current = []`) 是为了避免下次请求时使用旧数据

### Q: 如果用户快速发送多条消息会怎样？

A: 每条消息都有独立的 SSE 流和独立的 tool calls 追踪，不会互相干扰。每次 `handleSubmit` 开始时都会清空 `toolCalls` 和 `toolCallsRef`。

### Q: 刷新页面后 tool calls 会丢失吗？

A: 不会。Tool calls 保存在 `Message.toolCalls` 字段中，随消息一起存储到 localStorage，刷新后会自动恢复。

## 总结

✅ 使用 `useRef` 解决了闭包捕获旧值的问题
✅ Tool calls 现在能正确保存到历史消息中
✅ 即使出错也会保存 tool calls 信息
✅ 支持刷新后从 localStorage 恢复
✅ 添加了详细的调试日志便于排查问题
