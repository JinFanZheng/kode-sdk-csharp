# Tool Events 功能实现总结

## 概述

已成功在 Kode.Agent.Boilerplate 项目中实现了将 tool 调用过程实时传递给前端的功能。通过 Server-Sent Events (SSE) 流，前端可以实时监控 agent 的工具使用情况。

## 修改的文件

### 1. Models/OpenAiModels.cs
**新增内容**:
- `OpenAiToolCall` - 表示 tool 调用信息
- `OpenAiToolFunction` - 表示 tool 函数信息
- `OpenAiToolEvent` - 表示 tool 事件（start/end/error）

**修改内容**:
- `OpenAiStreamDelta` - 添加了 `ToolCalls` 属性以支持 tool 调用信息

### 2. AssistantService.cs
**修改内容**:
- `StreamResponseAsync` 方法现在处理三种额外的事件类型：
  - `ToolStartEvent` - tool 开始执行
  - `ToolEndEvent` - tool 执行完成
  - `ToolErrorEvent` - tool 执行错误

每个事件都会被序列化为 JSON 并通过 SSE 发送给前端。

## 新增的文档

### 1. TOOL_EVENTS_GUIDE.md
详细的前端集成指南，包括：
- 所有事件类型的 JSON 格式说明
- Tool 状态枚举说明
- React/TypeScript 和原生 JavaScript 的使用示例
- 常见使用场景

### 2. TESTING_TOOL_EVENTS.md
测试指南，包括：
- 快速启动测试的步骤
- curl 命令示例
- Node.js 测试脚本
- 验证要点清单

## 功能特性

### 实时事件流
- ✅ **Tool 开始**: 当 agent 开始调用工具时立即通知前端
- ✅ **Tool 完成**: 当工具执行完成时通知前端，包含执行时长
- ✅ **Tool 错误**: 当工具执行出错时通知前端，包含错误信息
- ✅ **文本内容**: 保持原有的流式文本响应功能

### 事件信息
每个 tool 事件包含：
- `tool_call_id` - 工具调用的唯一标识
- `tool_name` - 工具名称
- `state` - 工具状态（Pending/Executing/Completed/Failed/Denied/Sealed）
- `duration_ms` - 执行时长（仅在 end/error 事件中）
- `error` - 错误信息（仅在 error 事件中）
- `timestamp` - 时间戳

## 使用示例

### 发送请求
```bash
POST /v1/chat/completions
Content-Type: application/json

{
  "model": "claude-3-5-sonnet-20241022",
  "messages": [
    {"role": "user", "content": "请读取 README.md 文件"}
  ],
  "stream": true
}
```

### 接收响应
```
data: {"event":"tool:start","tool_call_id":"toolu_abc","tool_name":"read_file",...}
data: {"choices":[{"delta":{"content":"正在"}}]}
data: {"event":"tool:end","tool_call_id":"toolu_abc","duration_ms":150,...}
data: {"choices":[{"delta":{"content":"读取"}}]}
data: [DONE]
```

## 前端集成

前端需要：
1. 建立 SSE 连接
2. 解析每行 `data:` 开头的 JSON
3. 通过 `event` 字段区分 tool 事件和文本内容
4. 更新 UI 显示 tool 调用进度

详见 `TOOL_EVENTS_GUIDE.md` 中的完整示例代码。

## 兼容性

- ✅ 向后兼容：不影响现有的文本流功能
- ✅ OpenAI 兼容：遵循 OpenAI Chat Completion API 格式
- ✅ 扩展性：可以轻松添加更多事件类型

## 下一步

1. 测试功能：参考 `TESTING_TOOL_EVENTS.md`
2. 集成到前端：参考 `TOOL_EVENTS_GUIDE.md`
3. 根据需要扩展更多事件类型（如 thinking 事件、permission 事件等）

## 注意事项

1. 所有 tool 事件都通过 SSE 流异步发送
2. 前端需要正确解析 SSE 格式（`data:` 前缀）
3. Tool 事件与文本内容交错发送，需要分别处理
4. 建议在前端实现 UI 来显示实时的 tool 调用状态
