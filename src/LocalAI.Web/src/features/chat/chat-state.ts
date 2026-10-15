import type { CompletionStats, StoredMessage } from "../../types/api";

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

// ---- Conversation-level state (server-backed history) ----

export interface ConversationState {
  /** The conversation whose messages are held in `messages`; null for an unsaved new chat. */
  conversationId: string | null;
  messages: ChatMessage[];
  loadError: { id: string; error: ChatError } | null;
}

export type ConversationAction =
  | ChatAction
  /** Start showing a different conversation (clears messages). */
  | { type: "switch"; id: string | null }
  /** The current unsaved chat was just created on the server under `id` (keeps messages). */
  | { type: "assign"; id: string }
  | { type: "loaded"; id: string; messages: ChatMessage[] }
  | { type: "loadFailed"; id: string; error: ChatError };

export const initialConversationState: ConversationState = { conversationId: null, messages: [], loadError: null };

export function conversationReducer(state: ConversationState, action: ConversationAction): ConversationState {
  switch (action.type) {
    case "switch":
      return { conversationId: action.id, messages: [], loadError: null };
    case "assign":
      return { ...state, conversationId: action.id };
    case "loaded":
      return { conversationId: action.id, messages: action.messages, loadError: null };
    case "loadFailed":
      return { ...state, loadError: { id: action.id, error: action.error } };
    default:
      return { ...state, messages: chatReducer(state.messages, action) };
  }
}

export function fromStored(message: StoredMessage): ChatMessage {
  return {
    id: message.id,
    role: message.role === "assistant" ? "assistant" : "user",
    content: message.content,
    status: message.status,
    model: message.model ?? undefined,
    stats: message.stats,
    error: message.error
      ? { code: message.error.code, message: message.error.message ?? "The generation failed.", retryable: true }
      : undefined,
  };
}
