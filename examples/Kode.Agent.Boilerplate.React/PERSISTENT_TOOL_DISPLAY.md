# Tool 调用持久化显示功能

## 功能说明

现在 tool 调用不仅在执行过程中实时显示，而且在消息完成后也会**持久化保存**并显示在历史消息中。

## 实现细节

### 1. 数据模型更新

**[src/types/index.ts](src/types/index.ts)**
```typescript
export interface Message {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  timestamp: number;
  toolCalls?: ToolCall[];  // ✨ 新增：保存工具调用信息
}
```

### 2. Context 更新

**[src/contexts/ChatContext.tsx](src/contexts/ChatContext.tsx)**

- 导入 `ToolCall` 类型
- 更新 `updateLastMessage` 签名：
  ```typescript
  updateLastMessage: (content: string, toolCalls?: ToolCall[]) => void;
  ```
- 实现中支持更新 toolCalls：
  ```typescript
  toolCalls: toolCalls !== undefined ? toolCalls : lastMessage.toolCalls
  ```

### 3. ChatPanel 组件更新

**[src/components/ChatPanel.tsx](src/components/ChatPanel.tsx)**

#### 保存 Tool Calls
在流完成时，将 `toolCalls` 状态保存到消息中：
```typescript
// onComplete
(sessionId) => {
  if (sessionId && sessionId !== currentSession.id) {
    setCurrentSessionId(sessionId);
  }
  // 保存 tool calls 到最后一条消息
  if (toolCalls.length > 0) {
    updateLastMessage('', toolCalls);
  }
  setIsLoading(false);
  setToolCalls([]); // 清空临时状态
}
```

#### 渲染历史 Tool Calls
在消息渲染时，显示保存的 toolCalls：
```tsx
{/* Tool Calls Display */}
{message.role === "assistant" && message.toolCalls && message.toolCalls.length > 0 && (
  <div className="mb-3">
    <div className="flex items-center gap-2 text-xs font-medium text-muted-foreground mb-2">
      <Wrench className="w-3 h-3" />
      <span>Tool Calls</span>
    </div>
    <div className="space-y-1.5">
      {message.toolCalls.map((tool) => (
        <div key={tool.id} className={/* 状态颜色 */}>
          {/* 图标、名称、时长/错误 */}
        </div>
      ))}
    </div>
  </div>
)}
```

## 视觉效果

### 实时显示（消息流中）
```
┌────────────────────────────────────┐
│ 🤖 Assistant                       │
│                                    │
│ 🔧 Tool Calls:                     │
│  🔄 mcp__featbit__search... Running│  ← 蓝色，实时更新
│                                    │
│ [正在生成回复...]                   │
└────────────────────────────────────┘
```

### 持久化显示（历史消息）
```
┌────────────────────────────────────┐
│ 🤖 Assistant                       │
│                                    │
│ 🔧 Tool Calls:                     │
│  ✅ mcp__featbit__search... 4632ms │  ← 绿色，已保存
│  ✅ mcp__featbit__generate... 89ms │  ← 绿色，已保存
│                                    │
│ Based on the FeatBit documentation,│
│ here's how to use the .NET SDK...  │
│                                    │
│ 10:30:45 AM                        │
└────────────────────────────────────┘
```

## 数据流程

```
用户发送消息
    ↓
创建空的 assistant 消息
    ↓
开始 SSE 流
    ↓
收到 tool:start 事件 → 更新 toolCalls 状态 → 实时显示蓝色卡片
    ↓
收到 tool:end 事件 → 更新 toolCalls 状态 → 实时更新为绿色
    ↓
收到文本内容 → 更新消息内容
    ↓
流结束 (onComplete)
    ↓
保存 toolCalls 到消息 → updateLastMessage('', toolCalls)
    ↓
清空临时 toolCalls 状态
    ↓
消息持久化到 localStorage
    ↓
刷新页面后，从消息中读取 toolCalls 并渲染
```

## 本地存储

Tool calls 信息随消息一起保存到 localStorage：
```json
{
  "id": "session-123",
  "title": "FeatBit SDK Usage",
  "messages": [
    {
      "id": "msg-1",
      "role": "user",
      "content": "tell me how to use featbit .net sdk",
      "timestamp": 1234567890
    },
    {
      "id": "msg-2",
      "role": "assistant",
      "content": "Based on the documentation...",
      "timestamp": 1234567895,
      "toolCalls": [
        {
          "id": "call_xxx",
          "name": "mcp__featbit__search_documentation",
          "state": "completed",
          "startTime": 1234567891,
          "endTime": 1234567895,
          "duration": 4632
        }
      ]
    }
  ]
}
```

## 优势

✅ **持久化**：Tool 调用信息不会丢失，刷新页面后依然可见
✅ **可追溯**：可以看到每条回复使用了哪些工具
✅ **透明度**：用户了解 AI 如何获取信息并生成回复
✅ **调试友好**：开发者可以检查工具调用历史
✅ **用户体验**：既有实时反馈，又有历史记录

## 状态说明

| 状态 | 图标 | 颜色 | 说明 |
|------|------|------|------|
| running | 🔄 | 蓝色 | 工具正在执行（仅实时显示） |
| completed | ✅ | 绿色 | 工具执行成功，显示执行时长 |
| error | ❌ | 红色 | 工具执行失败，显示错误信息 |

## 测试建议

1. 发送需要调用工具的消息
2. 观察实时的工具调用过程（蓝色→绿色）
3. 等待消息完成
4. 向上滚动查看历史消息，确认工具调用信息仍然显示
5. 刷新页面，确认工具调用信息从 localStorage 正确恢复
6. 创建新会话，确认不同会话的工具调用不会混淆

## 下一步增强建议

🔮 可以考虑的功能：
- 显示工具的输入参数
- 显示工具的返回结果（可折叠）
- 工具调用的时间线可视化
- 导出工具调用日志
- 按工具类型过滤历史消息
