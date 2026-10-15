import { useState } from "react";
import { HistoryIcon, PlusIcon, RefreshIcon } from "../components/icons";
import { Notice } from "../components/notice";
import { Composer } from "../features/chat/composer";
import { MessageList } from "../features/chat/message-list";
import type { ConversationController } from "../features/chat/use-conversation";
import { ConversationList } from "../features/conversations/conversation-list";
import { ModelSelector } from "../features/models/model-selector";
import { NoModelsHelp } from "../features/models/no-models-help";
import type { PlatformStatus } from "../features/system/platform-status";
import type { AsyncResource } from "../hooks/use-async-resource";
import type { ConversationSummary, ModelListResponse } from "../types/api";

export interface HistoryActions {
  conversations: AsyncResource<ConversationSummary[]>;
  onNew: () => void;
  onSelect: (id: string) => void;
  onRename: (id: string, title: string) => Promise<void>;
  onDelete: (id: string) => Promise<void>;
}

interface ChatPageProps {
  chat: ConversationController;
  history: HistoryActions;
  models: AsyncResource<ModelListResponse>;
  selectedModel: string | null;
  onSelectModel: (name: string) => void;
  status: PlatformStatus;
  maxMessageLength: number;
}

export function ChatPage({ chat, history, models, selectedModel, onSelectModel, status, maxMessageLength }: ChatPageProps) {
  const [historyOpen, setHistoryOpen] = useState(false);
  const modelList = models.data?.models ?? [];
  const noUsableModels = models.data !== null && !modelList.some((m) => m.isAvailable);
  const providerDown = status.provider.tone === "error" || models.error?.code === "provider_unavailable";
  const title = history.conversations.data?.find((c) => c.id === chat.conversationId)?.title;

  let disabledReason: string | null = null;
  if (status.api.tone === "error") {
    disabledReason = "The API is unreachable.";
  } else if (providerDown) {
    disabledReason = "Ollama is unreachable.";
  } else if (models.initialLoading) {
    disabledReason = "Loading models…";
  } else if (!selectedModel) {
    disabledReason = "Install a model to start chatting.";
  } else if (chat.loading) {
    disabledReason = "Loading conversation…";
  } else if (chat.loadError) {
    disabledReason = "This conversation could not be loaded.";
  }

  const closeAnd = (action: () => void) => () => {
    setHistoryOpen(false);
    action();
  };

  return (
    <div className="chat-layout">
      <div className={`chat-layout__history${historyOpen ? " is-open" : ""}`}>
        <ConversationList
          conversations={history.conversations.data}
          activeId={chat.conversationId}
          loading={history.conversations.initialLoading}
          error={history.conversations.error?.message ?? null}
          disabled={chat.isStreaming}
          onNew={closeAnd(history.onNew)}
          onSelect={(id) => closeAnd(() => history.onSelect(id))()}
          onRename={history.onRename}
          onDelete={history.onDelete}
        />
      </div>

      <section className="page page--chat" aria-labelledby="chat-title">
        <header className="page-header">
          <div className="page-header__title">
            <h1 id="chat-title" className="page-title" title={title}>
              {title ?? "New conversation"}
            </h1>
            <p className="page-subtitle">
              {selectedModel ? (
                <>
                  Talking to <code>{selectedModel}</code> on your local Ollama instance. History is saved on this server.
                </>
              ) : (
                "No model selected."
              )}
            </p>
          </div>
          <div className="toolbar">
            <button
              type="button"
              className="btn history-toggle"
              aria-expanded={historyOpen}
              onClick={() => setHistoryOpen((open) => !open)}
            >
              <HistoryIcon />
              <span>History</span>
            </button>
            <ModelSelector
              models={modelList}
              selected={selectedModel}
              loading={models.refreshing}
              disabled={chat.isStreaming}
              onSelect={onSelectModel}
              onRefresh={() => void models.reload(true)}
            />
            <button
              type="button"
              className="btn"
              onClick={history.onNew}
              disabled={chat.conversationId === null && chat.messages.length === 0}
              title="Start a new conversation"
            >
              <PlusIcon />
              <span>New chat</span>
            </button>
          </div>
        </header>

        {status.api.tone === "error" && (
          <Notice tone="error" title="Cannot reach the API">
            The dashboard could not contact the backend. Check that the <code>api</code> service is running.
          </Notice>
        )}
        {status.api.tone !== "error" && providerDown && (
          <Notice tone="error" title="Ollama is not reachable">
            The inference service may be stopped or still starting. Start it with{" "}
            <code>docker compose up -d ollama</code>; this page updates automatically.
          </Notice>
        )}
        {!providerDown && models.error && models.error.code !== "provider_unavailable" && (
          <Notice tone="warn" title="Could not load models">
            {models.error.message}
          </Notice>
        )}
        {!providerDown && noUsableModels && (
          <Notice tone="warn" title="No model installed">
            <NoModelsHelp suggested={models.data?.defaultModel ?? null} />
          </Notice>
        )}
        {chat.loadError && (
          <Notice
            tone="error"
            title={chat.loadError.code === "conversation_not_found" ? "Conversation not found" : "Could not load conversation"}
            action={
              <button type="button" className="btn btn--sm" onClick={history.onNew}>
                <RefreshIcon />
                <span>Start a new chat</span>
              </button>
            }
          >
            {chat.loadError.message}
          </Notice>
        )}

        <MessageList
          messages={chat.messages}
          canRetry={chat.canRetry}
          onRetry={chat.retry}
          emptyState={
            chat.loading ? (
              <div className="empty">
                <p className="muted">Loading conversation…</p>
              </div>
            ) : (
              <div className="empty">
                <p className="empty__title">Start a conversation</p>
                <p className="muted">
                  Prompts are sent to the API, which streams the reply from a model running on this machine. Nothing is
                  sent to a cloud AI service. Conversations are saved in the API&apos;s local database.
                </p>
              </div>
            )
          }
        />

        <Composer
          maxLength={maxMessageLength}
          isStreaming={chat.isStreaming}
          disabledReason={disabledReason}
          onSend={chat.send}
          onStop={chat.stop}
        />
      </section>
    </div>
  );
}
