# Testing Tool Events Feature

## Quick Test

### 1. Start the Backend Service

```powershell
cd C:\Code\featbit\featbit-front-agent-api\examples\Kode.Agent.Boilerplate
$env:ASPNETCORE_ENVIRONMENT='Development'
dotnet run
```

### 2. Test with curl

```bash
curl -N http://localhost:5149/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "model": "claude-3-5-sonnet-20241022",
    "messages": [
      {"role": "user", "content": "Please read the README.md file in the current directory and tell me its content"}
    ],
    "stream": true
  }'
```

### 3. Expected Output Example

You should see output similar to the following:

```
data: {"id":"chatcmpl-xxx","event":"tool:start","tool_call_id":"toolu_abc123","tool_name":"read_file","state":"Pending","timestamp":1234567890123}

data: {"id":"chatcmpl-xxx","object":"chat.completion.chunk","created":1234567890,"model":"claude-3-5-sonnet-20241022","choices":[{"index":0,"delta":{"content":"我"},"finish_reason":null}]}

data: {"id":"chatcmpl-xxx","object":"chat.completion.chunk","created":1234567890,"model":"claude-3-5-sonnet-20241022","choices":[{"index":0,"delta":{"content":"正在"},"finish_reason":null}]}

data: {"id":"chatcmpl-xxx","event":"tool:end","tool_call_id":"toolu_abc123","tool_name":"read_file","state":"Completed","duration_ms":150,"timestamp":1234567890273}

data: {"id":"chatcmpl-xxx","object":"chat.completion.chunk","created":1234567890,"model":"claude-3-5-sonnet-20241022","choices":[{"index":0,"delta":{"content":"读取"},"finish_reason":null}]}

...

data: [DONE]
```

### 4. Using Node.js Test Script

Create `test-tool-events.js`:

```javascript
const https = require('http');

const data = JSON.stringify({
  model: 'claude-3-5-sonnet-20241022',
  messages: [
    { role: 'user', content: 'Please list files in current directory, then read the content of README.md' }
  ],
  stream: true
});

const options = {
  hostname: 'localhost',
  port: 5149,
  path: '/v1/chat/completions',
  method: 'POST',
  headers: {
    'Content-Type': 'application/json',
    'Content-Length': data.length
  }
};

const req = https.request(options, (res) => {
  console.log(`Status Code: ${res.statusCode}\n`);

  res.setEncoding('utf8');
  res.on('data', (chunk) => {
    const lines = chunk.split('\n');
    for (const line of lines) {
      if (line.startsWith('data: ')) {
        const data = line.slice(6).trim();
        if (data === '[DONE]') {
          console.log('\n[Completed]');
          continue;
        }

        try {
          const json = JSON.parse(data);
          
          // Tool events
          if (json.event) {
            console.log(`\n[Tool ${json.event}]`, {
              name: json.tool_name,
              state: json.state,
              duration: json.duration_ms ? `${json.duration_ms}ms` : undefined,
              error: json.error
            });
          }
          // Text content
          else if (json.choices) {
            const content = json.choices[0]?.delta?.content;
            if (content) {
              process.stdout.write(content);
            }
          }
        } catch (e) {
          // Ignore parse errors
        }
      }
    }
  });

  res.on('end', () => {
    console.log('\n\nStream ended');
  });
});

req.on('error', (e) => {
  console.error(`Request error: ${e.message}`);
});

req.write(data);
req.end();
```

Run:
```bash
node test-tool-events.js
```

## Verification Points

✅ **Tool Start Event**: Should receive `tool:start` event every time a tool is called
✅ **Tool End Event**: Should receive `tool:end` event after tool execution completes, including duration_ms
✅ **Tool Error Event**: Should receive `tool:error` event if tool execution fails, including error information
✅ **Text Content**: Normal text responses should continue to work properly
✅ **Event Order**: Tool events should be sent before or between corresponding text content
✅ **Completeness**: Every tool:start should have a corresponding tool:end or tool:error

## Log Inspection

In the backend logs, you should see:

```
[Stream] Tool started: read_file (ID: toolu_abc123)
[Stream] Tool completed: read_file (ID: toolu_abc123, Duration: 150ms)
```

Or in error cases:

```
[Stream] Tool error: read_file (ID: toolu_abc123) - File not found
```

## Frontend Integration

Refer to the React and JavaScript examples in `TOOL_EVENTS_GUIDE.md` to integrate tool events into your frontend application.
