import { useCallback, useState } from "react";
import { fetchModels, fetchOverview } from "./api/endpoints";
import { useChat } from "./features/chat/use-chat";
import { pickModel, readStoredModel, storeModel } from "./features/models/model-selection";
import { derivePlatformStatus } from "./features/system/platform-status";
import { useAsyncResource } from "./hooks/use-async-resource";
import { useHashRoute } from "./hooks/use-hash-route";
import { AppLayout } from "./layouts/app-layout";
import { ChatPage } from "./pages/chat-page";
import { ModelsPage } from "./pages/models-page";
import { SystemPage } from "./pages/system-page";

const OVERVIEW_POLL_MS = 10_000;
const MODELS_POLL_MS = 30_000;
const FALLBACK_MAX_MESSAGE_LENGTH = 8_000;

export function App() {
  const route = useHashRoute();
  const overview = useAsyncResource(useCallback((_force: boolean, signal: AbortSignal) => fetchOverview(signal), []), OVERVIEW_POLL_MS);
  const models = useAsyncResource(useCallback((force: boolean, signal: AbortSignal) => fetchModels(force, signal), []), MODELS_POLL_MS);

  const [preferredModel, setPreferredModel] = useState<string | null>(readStoredModel);
  // Derived, never stored: only a model that is actually installed and usable can be selected.
  const selectedModel = models.data ? pickModel(models.data.models, preferredModel) : null;
  const selectModel = useCallback((name: string) => {
    setPreferredModel(name);
    storeModel(name);
  }, []);

  const chat = useChat(selectedModel);
  const status = derivePlatformStatus(overview.data, overview.error, overview.initialLoading);
  const maxMessageLength = overview.data?.limits.maxMessageLength ?? FALLBACK_MAX_MESSAGE_LENGTH;

  return (
    <AppLayout route={route} status={status} generating={chat.isStreaming}>
      {route === "chat" && (
        <ChatPage
          chat={chat}
          models={models}
          selectedModel={selectedModel}
          onSelectModel={selectModel}
          status={status}
          maxMessageLength={maxMessageLength}
        />
      )}
      {route === "models" && (
        <ModelsPage
          models={models}
          selectedModel={selectedModel}
          onSelectModel={selectModel}
          selectionLocked={chat.isStreaming}
        />
      )}
      {route === "system" && <SystemPage overview={overview} status={status} generatingHere={chat.isStreaming} />}
    </AppLayout>
  );
}
