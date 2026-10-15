import { useCallback, useEffect, useReducer, useRef } from "react";
import { streamChat } from "../../api/endpoints";
import { ApiError, isAbortError } from "../../api/http";
import type { ChatMessageDto } from "../../types/api";
import { chatReducer, toHistory, type ChatError, type ChatMessage } from "./chat-state";

let nextId = 0;
const newId = () => `m${Date.now().toString(36)}-${(nextId += 1)}`;

function toChatError(error: unknown): ChatError {
  if (error instanceof ApiError) {
    return { code: error.code, message: error.message, retryable: error.retryable };
  }
  return { code: "client_error", message: "Something went wrong in the browser.", retryable: true };
}

export interface ChatController {
  messages: ChatMessage[];
  isStreaming: boolean;
  /** Returns false if the message was not sent (empty, no model, or a generation is running). */
  send: (text: string) => boolean;
  stop: () => void;
  retry: () => void;
  reset: () => void;
  canRetry: boolean;
}

export function useChat(model: string | null): ChatController {
  const [messages, dispatch] = useReducer(chatReducer, []);
  // Synchronous guard against double submission (state updates are async).
  const controllerRef = useRef<AbortController | null>(null);

  const run = useCallback(async (history: ChatMessageDto[], assistantId: string, targetModel: string) => {
    const controller = new AbortController();
    controllerRef.current = controller;
    try {
      const done = await streamChat(
        { model: targetModel, messages: history },
        { onDelta: (text) => dispatch({ type: "delta", id: assistantId, text }) },
        controller.signal,
      );
      dispatch({ type: "done", id: assistantId, stats: done.stats, finishReason: done.finishReason });
    } catch (error) {
      if (isAbortError(error)) {
        dispatch({ type: "stopped", id: assistantId });
      } else {
        dispatch({ type: "error", id: assistantId, error: toChatError(error) });
      }
    } finally {
      if (controllerRef.current === controller) {
        controllerRef.current = null;
      }
    }
  }, []);

  const send = useCallback(
    (text: string) => {
      const content = text.trim();
      if (!content || !model || controllerRef.current) {
        return false;
      }
      const user: ChatMessage = { id: newId(), role: "user", content, status: "complete" };
      const assistant: ChatMessage = { id: newId(), role: "assistant", content: "", status: "streaming", model };
      const history = [...toHistory(messages), { role: "user" as const, content }];
      dispatch({ type: "submit", user, assistant });
      void run(history, assistant.id, model);
      return true;
    },
    [messages, model, run],
  );

  const isStreaming = messages.some((m) => m.status === "streaming");
  const last = messages.at(-1);
  const canRetry =
    !isStreaming &&
    model !== null &&
    last?.role === "assistant" &&
    (last.status === "error" || last.status === "stopped");

  const retry = useCallback(() => {
    const failed = messages.at(-1);
    if (!model || controllerRef.current || failed?.role !== "assistant" || failed.status === "complete") {
      return;
    }
    const assistant: ChatMessage = { id: newId(), role: "assistant", content: "", status: "streaming", model };
    const history = toHistory(messages.slice(0, -1));
    dispatch({ type: "restart", replaceId: failed.id, assistant });
    void run(history, assistant.id, model);
  }, [messages, model, run]);

  const stop = useCallback(() => controllerRef.current?.abort(), []);

  const reset = useCallback(() => {
    controllerRef.current?.abort();
    controllerRef.current = null;
    dispatch({ type: "reset" });
  }, []);

  // Abort any in-flight generation when the chat is torn down.
  useEffect(() => () => controllerRef.current?.abort(), []);

  return {
    messages,
    isStreaming,
    send,
    stop,
    retry,
    reset,
    canRetry,
  };
}
