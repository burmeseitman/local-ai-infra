import type { Conversation, ConversationSummary, StreamDoneEvent } from "../types/api";
import { streamSse, type StreamHandlers } from "./chat-stream";
import { getJson, sendJson } from "./http";

export async function listConversations(signal?: AbortSignal): Promise<ConversationSummary[]> {
  return (await getJson<{ conversations: ConversationSummary[] }>("/api/conversations", signal)).conversations;
}

export function getConversation(id: string, signal?: AbortSignal): Promise<Conversation> {
  return getJson<Conversation>(`/api/conversations/${encodeURIComponent(id)}`, signal);
}

export async function createConversation(title?: string): Promise<Conversation> {
  return (await sendJson<Conversation>("POST", "/api/conversations", title ? { title } : {}))!;
}

export async function renameConversation(id: string, title: string): Promise<void> {
  await sendJson("PATCH", `/api/conversations/${encodeURIComponent(id)}`, { title });
}

export async function deleteConversation(id: string): Promise<void> {
  await sendJson("DELETE", `/api/conversations/${encodeURIComponent(id)}`);
}

/** Sends only the new message; the server supplies history and persists both sides. */
export function streamConversationMessage(
  id: string,
  body: { model: string; content: string; useDocuments?: boolean },
  handlers: StreamHandlers,
  signal: AbortSignal,
): Promise<StreamDoneEvent> {
  return streamSse(`/api/conversations/${encodeURIComponent(id)}/messages/stream`, body, handlers, signal);
}

/** Replaces the last assistant reply with a new generation. */
export function streamRegenerate(
  id: string,
  body: { model: string; useDocuments?: boolean },
  handlers: StreamHandlers,
  signal: AbortSignal,
): Promise<StreamDoneEvent> {
  return streamSse(`/api/conversations/${encodeURIComponent(id)}/regenerate/stream`, body, handlers, signal);
}
