import type {
  ChatRequest,
  StreamDeltaEvent,
  StreamDoneEvent,
  StreamErrorEvent,
  StreamStartEvent,
} from "../types/api";
import { SseParser } from "../utils/sse-parser";
import { API_BASE_URL, ApiError, isAbortError, networkError, toApiError } from "./http";

export interface StreamHandlers {
  onStart?: (event: StreamStartEvent) => void;
  onDelta: (text: string) => void;
}

function parsePayload<T>(data: string): T {
  try {
    return JSON.parse(data) as T;
  } catch {
    throw new ApiError("The server sent a malformed stream event.", "stream_malformed", null, true);
  }
}

/**
 * Streams a chat completion from POST /api/chat/stream.
 * Resolves with the `done` event; rejects with ApiError on failure, or with an
 * AbortError when `signal` is aborted (the caller's "Stop").
 */
export async function streamChat(
  request: ChatRequest,
  handlers: StreamHandlers,
  signal: AbortSignal,
): Promise<StreamDoneEvent> {
  let response: Response;
  try {
    response = await fetch(`${API_BASE_URL}/api/chat/stream`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "text/event-stream" },
      body: JSON.stringify(request),
      signal,
    });
  } catch (error) {
    if (isAbortError(error)) {
      throw error;
    }
    throw networkError();
  }

  if (!response.ok) {
    throw await toApiError(response);
  }
  if (!response.body) {
    throw new ApiError("This browser does not support streaming responses.", "stream_unsupported", null, false);
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  const parser = new SseParser();

  try {
    for (;;) {
      const { value, done } = await reader.read();
      const text = done ? decoder.decode() : decoder.decode(value, { stream: true });

      for (const message of parser.push(text)) {
        switch (message.event) {
          case "start":
            handlers.onStart?.(parsePayload<StreamStartEvent>(message.data));
            break;
          case "delta":
            handlers.onDelta(parsePayload<StreamDeltaEvent>(message.data).content);
            break;
          case "done":
            return parsePayload<StreamDoneEvent>(message.data);
          case "error": {
            const error = parsePayload<StreamErrorEvent>(message.data);
            throw new ApiError(error.message, error.code, response.status, error.retryable);
          }
          default:
            break; // ignore unknown events for forward compatibility
        }
      }

      if (done) {
        break;
      }
    }
  } catch (error) {
    if (isAbortError(error) || error instanceof ApiError) {
      throw error;
    }
    throw new ApiError("The connection was interrupted while streaming.", "stream_interrupted", null, true);
  } finally {
    reader.cancel().catch(() => undefined);
  }

  throw new ApiError("The response ended before the model finished.", "stream_incomplete", null, true);
}
