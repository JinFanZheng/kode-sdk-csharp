export interface Message {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  timestamp: number;
  toolCalls?: ToolCall[];
}

export interface Session {
  id: string;
  title: string;
  messages: Message[];
  createdAt: number;
  updatedAt: number;
}

export interface OpenAIChatMessage {
  role: string;
  content: string;
}

export interface OpenAIChatRequest {
  model?: string;
  messages: OpenAIChatMessage[];
  stream: boolean;
  temperature?: number;
  max_tokens?: number;
}

export interface OpenAIChatResponse {
  id: string;
  object: string;
  created: number;
  model: string;
  choices: Array<{
    index: number;
    message: {
      role: string;
      content: string;
    };
    finish_reason: string;
  }>;
  usage: {
    prompt_tokens: number;
    completion_tokens: number;
    total_tokens: number;
  };
}

export interface OpenAIStreamChunk {
  id: string;
  object: string;
  created: number;
  model: string;
  choices: Array<{
    index: number;
    delta: {
      role?: string;
      content?: string;
    };
    finish_reason?: string;
  }>;
}

export interface ToolEvent {
  id: string;
  event: 'tool:start' | 'tool:end' | 'tool:error';
  tool_call_id: string;
  tool_name: string;
  state: string;
  error?: string;
  duration_ms?: number;
  timestamp: number;
}

export interface ToolCall {
  id: string;
  name: string;
  state: 'start' | 'running' | 'completed' | 'error';
  startTime: number;
  endTime?: number;
  duration?: number;
  error?: string;
}
