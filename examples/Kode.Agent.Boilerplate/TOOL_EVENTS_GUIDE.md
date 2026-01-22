# Tool Events Streaming Guide

## Overview

This API now supports sending tool invocation events in Server-Sent Events (SSE) streams, allowing the frontend to monitor the agent's tool usage in real-time.

## Event Types

### 1. Text Content Events
Standard OpenAI format text stream:
```json
{
  "id": "chatcmpl-xxx",
  "object": "chat.completion.chunk",
  "created": 1234567890,
  "model": "claude-3-5-sonnet-20241022",
  "choices": [
    {
      "index": 0,
      "delta": {
        "content": "Hello"
      },
      "finish_reason": null
    }
  ]
}
```

### 2. Tool Start Event (tool:start)
Sent when the agent starts calling a tool:
```json
{
  "id": "chatcmpl-xxx",
  "event": "tool:start",
  "tool_call_id": "toolu_abc123",
  "tool_name": "read_file",
  "state": "Pending",
  "timestamp": 1234567890123
}
```

### 3. Tool Completion Event (tool:end)
Sent when tool execution completes:
```json
{
  "id": "chatcmpl-xxx",
  "event": "tool:end",
  "tool_call_id": "toolu_abc123",
  "tool_name": "read_file",
  "state": "Completed",
  "duration_ms": 150,
  "timestamp": 1234567890273
}
```

### 4. Tool Error Event (tool:error)
Sent when tool execution fails:
```json
{
  "id": "chatcmpl-xxx",
  "event": "tool:error",
  "tool_call_id": "toolu_abc123",
  "tool_name": "read_file",
  "state": "Failed",
  "error": "File not found",
  "duration_ms": 50,
  "timestamp": 1234567890173
}
```

## Tool State Descriptions

- **Pending**: Tool invocation registered, waiting for execution
- **Executing**: Tool is currently executing
- **Completed**: Tool execution completed successfully
- **Failed**: Tool execution failed
- **Denied**: Tool invocation denied by permission control
- **Sealed**: Tool execution fully ended and recorded

## Frontend Usage Examples

### React/TypeScript 示例

```typescript
interface ToolEvent {
  id: string;
  event: 'tool:start' | 'tool:end' | 'tool:error';
  tool_call_id: string;
  tool_name: string;
  state: string;
  error?: string;
  duration_ms?: number;
  timestamp: number;
}

interface TextChunk {
  id: string;
  object: 'chat.completion.chunk';
  created: number;
  model: string;
  choices: Array<{
    index: number;
    delta: { content?: string };
    finish_reason: string | null;
  }>;
}

const ChatComponent = () => {
  const [messages, setMessages] = useState<string>('');
  const [toolCalls, setToolCalls] = useState<ToolEvent[]>([]);

  const streamChat = async (userMessage: string) => {
    const response = await fetch('http://localhost:5149/v1/chat/completions', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify({
        model: 'claude-3-5-sonnet-20241022',
        messages: [{ role: 'user', content: userMessage }],
        stream: true,
      }),
    });

    const reader = response.body?.getReader();
    const decoder = new TextDecoder();

    while (true) {
      const { done, value } = await reader!.read();
      if (done) break;

      const chunk = decoder.decode(value);
      const lines = chunk.split('\n');

      for (const line of lines) {
        if (line.startsWith('data: ')) {
          const data = line.slice(6);
          if (data === '[DONE]') continue;

          try {
            const json = JSON.parse(data);
            
            // Check if it's a tool event
            if (json.event) {
              const toolEvent = json as ToolEvent;
              console.log(`Tool ${toolEvent.event}:`, toolEvent.tool_name);
              setToolCalls(prev => [...prev, toolEvent]);
            } 
            // Otherwise it's text content
            else {
              const textChunk = json as TextChunk;
              const content = textChunk.choices[0]?.delta?.content;
              if (content) {
                setMessages(prev => prev + content);
              }
            }
          } catch (e) {
            console.error('Failed to parse SSE data:', e);
          }
        }
      }
    }
  };

  return (
    <div>
      <div className="messages">{messages}</div>
      <div className="tool-calls">
        <h3>Tool Calls:</h3>
        {toolCalls.map((call, idx) => (
          <div key={idx} className={`tool-call ${call.event}`}>
            <span className="tool-name">{call.tool_name}</span>
            <span className="tool-state">{call.state}</span>
            {call.error && <span className="error">{call.error}</span>}
            {call.duration_ms && <span className="duration">{call.duration_ms}ms</span>}
          </div>
        ))}
      </div>
    </div>
  );
};
```

### Vanilla JavaScript Example

```javascript
async function streamChatWithTools(message) {
  const response = await fetch('http://localhost:5149/v1/chat/completions', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      model: 'claude-3-5-sonnet-20241022',
      messages: [{ role: 'user', content: message }],
      stream: true,
    }),
  });

  const reader = response.body.getReader();
  const decoder = new TextDecoder();

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;

    const chunk = decoder.decode(value);
    const lines = chunk.split('\n');

    for (const line of lines) {
      if (line.startsWith('data: ')) {
        const data = line.slice(6).trim();
        if (data === '[DONE]') continue;

        try {
          const json = JSON.parse(data);
          
          if (json.event) {
            // Tool events
            console.log(`[${json.event}] ${json.tool_name} (${json.state})`);
            if (json.error) {
              console.error(`  Error: ${json.error}`);
            }
            if (json.duration_ms) {
              console.log(`  Duration: ${json.duration_ms}ms`);
            }
          } else if (json.choices) {
            // Text content
            const content = json.choices[0]?.delta?.content;
            if (content) {
              process.stdout.write(content);
            }
          }
        } catch (e) {
          console.error('Parse error:', e);
        }
      }
    }
  }
}
```

## Use Cases

1. **Real-time Progress Display**: Show which tools the agent is currently calling in the UI
2. **Performance Monitoring**: Track execution time for each tool
3. **Error Handling**: Capture and display tool execution errors in real-time
4. **Debug Information**: Help developers understand the agent's decision-making process

## Notes

1. All events are sent through SSE stream with `data:` prefix
2. Tool events are interleaved with text content events
3. Tool events can be distinguished from text content via the `event` field
4. Tool event order guarantee: start → (executing) → end/error
5. The same tool invocation's `tool_call_id` remains consistent across start/end/error events
