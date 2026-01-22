# Tool Events 前端集成完成

## 修改的文件

### 1. `src/types/index.ts`
添加了新的类型定义：
- `ToolEvent` - 表示后端发送的 tool 事件
- `ToolCall` - 表示前端跟踪的 tool 调用状态

### 2. `src/services/api.ts`
- 在 `sendMessageStream` 方法中添加了 `onToolEvent` 回调参数
- 在 SSE 解析逻辑中添加了 tool 事件的识别和处理
- Tool 事件通过检查 `chunk.event` 字段来区分

### 3. `src/components/ChatPanel.tsx`
- 添加了 `toolCalls` 状态来跟踪当前的 tool 调用
- 添加了 tool 事件处理逻辑，根据事件类型更新 tool 状态
- 在消息列表中添加了 tool 调用的可视化显示
- Tool 显示包括：
  - 🔵 蓝色 - Tool 正在运行
  - ✅ 绿色 - Tool 执行完成
  - ❌ 红色 - Tool 执行错误
  - 显示执行时长和错误信息

## 使用方法

1. 确保后端服务正在运行：
   ```powershell
   cd C:\Code\featbit\featbit-front-agent-api\examples\Kode.Agent.Boilerplate
   $env:ASPNETCORE_ENVIRONMENT='Development'
   dotnet run
   ```

2. 启动前端开发服务器：
   ```powershell
   cd C:\Code\featbit\featbit-front-agent-api\examples\Kode.Agent.Boilerplate.React
   npm run dev
   ```

3. 在浏览器中访问 `http://localhost:5173`

4. 发送一个需要调用 tool 的消息，例如：
   - "tell me how to use featbit .net sdk"
   - "列出当前目录的文件"
   - "读取 README.md 的内容"

## 预期效果

当 agent 调用工具时，你会在消息流中看到实时的 tool 调用状态：

```
用户消息: tell me how to use featbit .net sdk

[正在思考...]

🔧 Tool Calls:
  ▶️ mcp__featbit__search_documentation [运行中...]
  ✅ mcp__featbit__search_documentation [完成] - 150ms
  ▶️ mcp__featbit__generate_integration_code [运行中...]
  ✅ mcp__featbit__generate_integration_code [完成] - 89ms

AI响应: [流式文本响应...]
```

## 事件流程

1. 用户发送消息
2. 后端 agent 开始处理
3. 当 agent 调用 tool 时：
   - 后端发送 `tool:start` 事件 → 前端显示蓝色运行中状态
   - Tool 执行中...
   - 后端发送 `tool:end` 事件 → 前端更新为绿色完成状态（含时长）
   - 如果出错，发送 `tool:error` → 前端显示红色错误状态（含错误信息）
4. Agent 返回文本响应
5. 完成

## 技术细节

### SSE 数据格式

**文本内容：**
```json
data: {
  "id": "chatcmpl-xxx",
  "object": "chat.completion.chunk",
  "choices": [{"delta": {"content": "Hello"}}]
}
```

**Tool 事件：**
```json
data: {
  "id": "chatcmpl-xxx",
  "event": "tool:start",
  "tool_call_id": "call_xxx",
  "tool_name": "read_file",
  "state": "Pending",
  "timestamp": 1234567890
}
```

前端通过检查 `event` 字段来区分 tool 事件和文本内容。
