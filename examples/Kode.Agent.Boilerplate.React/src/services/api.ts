import type { OpenAIChatRequest, OpenAIChatResponse } from '@/types';

const API_BASE_URL = '/v1';

export class ApiService {
  /**
   * Send a chat message (non-streaming)
   */
  static async sendMessage(
    sessionId: string | null,
    messages: Array<{ role: string; content: string }>,
    stream: boolean = false
  ): Promise<OpenAIChatResponse> {
    const url = sessionId 
      ? `/${sessionId}${API_BASE_URL}/chat/completions`
      : `${API_BASE_URL}/chat/completions`;

    const request: OpenAIChatRequest = {
      model: 'claude-sonnet-4',
      messages,
      stream,
    };

    const response = await fetch(url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
      },
      body: JSON.stringify(request),
    });

    if (!response.ok) {
      const error = await response.json().catch(() => ({}));
      throw new Error(error.error?.message || 'Failed to send message');
    }

    // Extract session ID from response headers
    const responseSessionId = response.headers.get('X-Session-Id');
    const data = await response.json();

    return {
      ...data,
      sessionId: responseSessionId,
    };
  }

  /**
   * Send a chat message (streaming)
   */
  static async sendMessageStream(
    sessionId: string | null,
    messages: Array<{ role: string; content: string }>,
    onChunk: (content: string) => void,
    onComplete: (sessionId: string | null) => void,
    onError: (error: Error) => void
  ): Promise<void> {
    const url = sessionId 
      ? `/${sessionId}${API_BASE_URL}/chat/completions`
      : `${API_BASE_URL}/chat/completions`;

    const request: OpenAIChatRequest = {
      model: 'claude-sonnet-4',
      messages,
      stream: true,
    };

    console.log('[API] Sending stream request to:', url);
    console.log('[API] Session ID:', sessionId);
    console.log('[API] Request body:', JSON.stringify(request, null, 2));

    try {
      const response = await fetch(url, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
        },
        body: JSON.stringify(request),
      });

      console.log('[API] Response status:', response.status);
      console.log('[API] Response headers:', Object.fromEntries(response.headers.entries()));
      console.log('[API] Response body exists:', !!response.body);
      console.log('[API] Response bodyUsed:', response.bodyUsed);

      if (!response.ok) {
        const error = await response.json().catch(() => ({}));
        throw new Error(error.error?.message || 'Failed to send message');
      }

      // Extract session ID from response headers
      const responseSessionId = response.headers.get('X-Session-Id');
      console.log('[API] Session ID from response header:', responseSessionId);

      // Read the stream
      const reader = response.body?.getReader();
      if (!reader) {
        console.error('[API] ERROR: No response body! response.body is', response.body);
        throw new Error('No response body');
      }

      console.log('[API] Reader created successfully, starting to read stream...');
      const decoder = new TextDecoder();
      let buffer = '';
      let chunkCount = 0;

      while (true) {
        const { done, value } = await reader.read();
        
        console.log('[API] Read result - done:', done, 'value length:', value?.length || 0);
        
        if (done) {
          console.log('[API] Stream completed, total chunks:', chunkCount);
          onComplete(responseSessionId);
          break;
        }

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n');
        buffer = lines.pop() || '';

        for (const line of lines) {
          const trimmedLine = line.trim();
          if (!trimmedLine || trimmedLine === 'data: [DONE]') {
            continue;
          }

          if (trimmedLine.startsWith('data: ')) {
            try {
              const jsonStr = trimmedLine.slice(6);
              const chunk = JSON.parse(jsonStr);
              const content = chunk.choices?.[0]?.delta?.content;
              
              if (content) {
                chunkCount++;
                onChunk(content);
              }
            } catch (e) {
              console.error('[API] Failed to parse chunk:', e, 'Line:', trimmedLine);
            }
          }
        }
      }
    } catch (error) {
      onError(error instanceof Error ? error : new Error('Unknown error'));
    }
  }
}
