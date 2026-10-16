import { useCallback, useEffect, useReducer, useRef } from "react";
import type { StreamHandlers } from "../../api/chat-stream";
import { createConversation, getConversation, streamConversationMessage, streamRegenerate } from "../../api/conversations";
import { ApiError, isAbortError } from "../../api/http";
import type { StreamDoneEvent } from "../../types/api";
import {
  conversationReducer,
  fromStored,
  initialConversationState,
  type ChatError,
  type ChatMessage,
} from "./chat-state";

let nextId = 0;
const newId = () => `local-${Date.now().toString(36)}-${(nextId += 1)}`;

export function toChatError(error: unknown): ChatError {
  if (error instanceof ApiError) {
    return { code: error.code, message: error.message, retryable: error.retryable };
  }
  return { code: "client_error", message: "Something went wrong in the browser.", retryable: true };
}

export interface ConversationController {
  conversationId: string | null;
  messages: ChatMessage[];
  loading: boolean;
  loadError: ChatError | null;
  isStreaming: boolean;
  canRetry: boolean;
  /** Returns false if nothing was sent (empty, no model, or busy). */
  send: (text: string) => boolean;
  stop: () => void;
  retry: () => void;
}

interface Options {
  /** Conversation selected by the URL; null means a new, unsaved chat. */
  conversationId: string | null;
  model: string | null;
  /** Ground replies in the user's indexed documents. */
  useDocuments: boolean;
  /** Called with a new conversation's id so the URL can follow. */
  onCreated: (id: string) => void;
  /** Called whenever stored conversations changed (to refresh the list). */
  onChanged: () => void;
}

type StartStream = (signal: AbortSignal, handlers: StreamHandlers) => Promise<StreamDoneEvent>;

/**
 * Server-backed chat. The server owns history: this hook sends only the new message,
 * renders the streamed reply optimistically, and reloads history when the URL changes.
 */
export function useConversation({ conversationId: routeId, model, useDocuments, onCreated, onChanged }: Options): ConversationController {
  const [state, dispatch] = useReducer(conversationReducer, initialConversationState);
  const controllerRef = useRef<AbortController | null>(null);
  // Synchronous guard spanning conversation creation and streaming.
  const busyRef = useRef(false);
  const callbacks = useRef({ onCreated, onChanged });
  useEffect(() => {
    callbacks.current = { onCreated, onChanged };
  }, [onCreated, onChanged]);

  const isCurrent = state.conversationId === routeId;

  // Leaving a conversation stops its generation; the server stores it as "stopped".
  useEffect(() => {
    if (!isCurrent) {
      controllerRef.current?.abort();
    }
  }, [isCurrent]);

  useEffect(() => {
    if (!routeId || routeId === state.conversationId) {
      return;
    }
    const controller = new AbortController();
    getConversation(routeId, controller.signal).then(
      (conversation) =>
        dispatch({
          type: "loaded",
          id: conversation.id,
          messages: conversation.messages.filter((m) => m.role !== "system").map(fromStored),
        }),
      (error: unknown) => {
        if (!isAbortError(error)) {
          dispatch({ type: "loadFailed", id: routeId, error: toChatError(error) });
        }
      },
    );
    return () => controller.abort();
  }, [routeId, state.conversationId]);

  const run = useCallback(async (assistantId: string, start: StartStream) => {
    const controller = new AbortController();
    controllerRef.current = controller;
    try {
      const done = await start(controller.signal, {
        onDelta: (text) => dispatch({ type: "delta", id: assistantId, text }),
        onSources: (sources) => dispatch({ type: "sources", id: assistantId, sources }),
      });
      dispatch({ type: "done", id: assistantId, stats: done.stats, finishReason: done.finishReason });
    } catch (error) {
      dispatch(
        isAbortError(error)
          ? { type: "stopped", id: assistantId }
          : { type: "error", id: assistantId, error: toChatError(error) },
      );
    } finally {
      if (controllerRef.current === controller) {
        controllerRef.current = null;
      }
      busyRef.current = false;
      callbacks.current.onChanged();
    }
  }, []);

  const send = useCallback(
    (text: string) => {
      const content = text.trim();
      if (!content || !model || busyRef.current) {
        return false;
      }
      busyRef.current = true;

      let targetId = isCurrent ? state.conversationId : routeId;
      if (!isCurrent) {
        dispatch({ type: "switch", id: routeId });
      }
      const user: ChatMessage = { id: newId(), role: "user", content, status: "complete" };
      const assistant: ChatMessage = { id: newId(), role: "assistant", content: "", status: "streaming", model };
      dispatch({ type: "submit", user, assistant });

      void (async () => {
        if (!targetId) {
          try {
            const created = await createConversation();
            targetId = created.id;
            // Same tick: state and URL agree, so no reload of the (empty) conversation happens.
            dispatch({ type: "assign", id: created.id });
            callbacks.current.onCreated(created.id);
          } catch (error) {
            dispatch({ type: "error", id: assistant.id, error: toChatError(error) });
            busyRef.current = false;
            return;
          }
        }
        const id = targetId;
        await run(assistant.id, (signal, handlers) =>
          streamConversationMessage(id, { model, content, ...(useDocuments ? { useDocuments } : {}) }, handlers, signal),
        );
      })();
      return true;
    },
    [isCurrent, model, routeId, run, state.conversationId, useDocuments],
  );

  const messages = isCurrent ? state.messages : [];
  const isStreaming = messages.some((m) => m.status === "streaming");
  const last = messages.at(-1);
  const canRetry =
    !isStreaming &&
    model !== null &&
    state.conversationId !== null &&
    last?.role === "assistant" &&
    (last.status === "error" || last.status === "stopped");

  const retry = useCallback(() => {
    const failed = state.messages.at(-1);
    const id = state.conversationId;
    if (!model || !id || busyRef.current || failed?.role !== "assistant" || failed.status === "complete") {
      return;
    }
    busyRef.current = true;
    const assistant: ChatMessage = { id: newId(), role: "assistant", content: "", status: "streaming", model };
    dispatch({ type: "restart", replaceId: failed.id, assistant });
    void run(assistant.id, (signal, handlers) =>
      streamRegenerate(id, { model, ...(useDocuments ? { useDocuments } : {}) }, handlers, signal),
    );
  }, [model, run, state.conversationId, state.messages, useDocuments]);

  const stop = useCallback(() => controllerRef.current?.abort(), []);

  useEffect(() => () => controllerRef.current?.abort(), []);

  const loadError = !isCurrent && routeId && state.loadError?.id === routeId ? state.loadError.error : null;
  return {
    conversationId: isCurrent ? state.conversationId : routeId,
    messages,
    loading: routeId !== null && !isCurrent && loadError === null,
    loadError,
    isStreaming,
    canRetry,
    send,
    stop,
    retry,
  };
}
