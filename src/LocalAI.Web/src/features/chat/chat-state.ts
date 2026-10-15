import type { ChatMessageDto, CompletionStats } from "../../types/api";

export type MessageStatus = "complete" | "streaming" | "stopped" | "error";

export interface ChatError {
  code: string;
  message: string;
  retryable: boolean;
}

export interface ChatMessage {
  id: string;
  role: "user" | "assistant";
  content: string;
  status: MessageStatus;
  model?: string;
  error?: ChatError;
  stats?: CompletionStats | null;
  finishReason?: string | null;
}

export type ChatAction =
  | { type: "submit"; user: ChatMessage; assistant: ChatMessage }
  | { type: "restart"; replaceId: string; assistant: ChatMessage }
  | { type: "delta"; id: string; text: string }
  | { type: "done"; id: string; stats: CompletionStats | null; finishReason: string | null }
  | { type: "stopped"; id: string }
  | { type: "error"; id: string; error: ChatError }
  | { type: "reset" };

function update(messages: ChatMessage[], id: string, patch: (m: ChatMessage) => ChatMessage): ChatMessage[] {
  return messages.map((m) => (m.id === id ? patch(m) : m));
}

export function chatReducer(messages: ChatMessage[], action: ChatAction): ChatMessage[] {
  switch (action.type) {
    case "submit":
      return [...messages, action.user, action.assistant];
    case "restart":
      return messages.map((m) => (m.id === action.replaceId ? action.assistant : m));
    case "delta":
      return update(messages, action.id, (m) => ({ ...m, content: m.content + action.text }));
    case "done":
      return update(messages, action.id, (m) => ({
        ...m,
        status: "complete",
        stats: action.stats,
        finishReason: action.finishReason,
      }));
    case "stopped":
      return update(messages, action.id, (m) => ({ ...m, status: "stopped" }));
    case "error":
      // Already-received text is kept; the error is shown alongside it.
      return update(messages, action.id, (m) => ({ ...m, status: "error", error: action.error }));
    case "reset":
      return [];
  }
}

/**
 * Builds the history sent to the API: completed turns plus partial replies that were
 * stopped by the user. Failed or empty assistant turns are omitted.
 */
export function toHistory(messages: ChatMessage[]): ChatMessageDto[] {
  return messages
    .filter((m) =>
      m.role === "user" ? true : (m.status === "complete" || m.status === "stopped") && m.content.trim() !== "",
    )
    .map((m) => ({ role: m.role, content: m.content }));
}
