import { Notice } from "../components/notice";
import { PlusIcon } from "../components/icons";
import { Composer } from "../features/chat/composer";
import { MessageList } from "../features/chat/message-list";
import type { ChatController } from "../features/chat/use-chat";
import { ModelSelector } from "../features/models/model-selector";
import { NoModelsHelp } from "../features/models/no-models-help";
import type { PlatformStatus } from "../features/system/platform-status";
import type { AsyncResource } from "../hooks/use-async-resource";
import type { ModelListResponse } from "../types/api";

interface ChatPageProps {
  chat: ChatController;
  models: AsyncResource<ModelListResponse>;
  selectedModel: string | null;
  onSelectModel: (name: string) => void;
  status: PlatformStatus;
  maxMessageLength: number;
}

export function ChatPage({ chat, models, selectedModel, onSelectModel, status, maxMessageLength }: ChatPageProps) {
  const modelList = models.data?.models ?? [];
  const noUsableModels = models.data !== null && !modelList.some((m) => m.isAvailable);
  const providerDown = status.provider.tone === "error" || models.error?.code === "provider_unavailable";

  let disabledReason: string | null = null;
  if (status.api.tone === "error") {
    disabledReason = "The API is unreachable.";
  } else if (providerDown) {
    disabledReason = "Ollama is unreachable.";
  } else if (models.initialLoading) {
    disabledReason = "Loading models…";
  } else if (!selectedModel) {
    disabledReason = "Install a model to start chatting.";
  }

  return (
    <section className="page page--chat" aria-labelledby="chat-title">
      <header className="page-header">
        <div>
          <h1 id="chat-title" className="page-title">
            Chat
          </h1>
          <p className="page-subtitle">
            {selectedModel ? (
              <>
                Talking to <code>{selectedModel}</code> on your local Ollama instance.
              </>
            ) : (
              "No model selected."
            )}
          </p>
        </div>
        <div className="toolbar">
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
            onClick={chat.reset}
            disabled={chat.messages.length === 0}
            title="Clear the conversation"
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

      <MessageList
        messages={chat.messages}
        canRetry={chat.canRetry}
        onRetry={chat.retry}
        emptyState={
          <div className="empty">
            <p className="empty__title">Start a conversation</p>
            <p className="muted">
              Prompts are sent to the API, which streams the reply from a model running on this machine. Nothing is sent
              to a cloud AI service. Conversations live only in this browser tab.
            </p>
          </div>
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
  );
}
