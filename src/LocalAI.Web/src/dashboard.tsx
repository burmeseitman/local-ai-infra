import { useCallback, useState } from "react";
import { deleteConversation, listConversations, renameConversation } from "./api/conversations";
import { fetchModels, fetchOverview } from "./api/endpoints";
import { useConversation } from "./features/chat/use-conversation";
import { pickModel, readStoredModel, storeModel } from "./features/models/model-selection";
import { derivePlatformStatus } from "./features/system/platform-status";
import { useAsyncResource } from "./hooks/use-async-resource";
import { useHashRoute } from "./hooks/use-hash-route";
import { AppLayout } from "./layouts/app-layout";
import { ChatPage, type HistoryActions } from "./pages/chat-page";
import { ModelsPage } from "./pages/models-page";
import { SystemPage } from "./pages/system-page";
import { AccountPage } from "./pages/account-page";
import { UsersPage } from "./pages/users-page";
import type { AuthUser } from "./types/api";

interface DashboardProps {
  user: AuthUser;
  onSignOut: () => Promise<void>;
}

const OVERVIEW_POLL_MS = 10_000;
const MODELS_POLL_MS = 30_000;
const FALLBACK_MAX_MESSAGE_LENGTH = 8_000;

export function Dashboard({ user, onSignOut }: DashboardProps) {
  const [location, navigate] = useHashRoute();
  const overview = useAsyncResource(useCallback((_force: boolean, signal: AbortSignal) => fetchOverview(signal), []), OVERVIEW_POLL_MS);
  const models = useAsyncResource(useCallback((force: boolean, signal: AbortSignal) => fetchModels(force, signal), []), MODELS_POLL_MS);
  const conversations = useAsyncResource(useCallback((_force: boolean, signal: AbortSignal) => listConversations(signal), []));

  const [preferredModel, setPreferredModel] = useState<string | null>(readStoredModel);
  // Derived, never stored: only a model that is actually installed and usable can be selected.
  const selectedModel = models.data ? pickModel(models.data.models, preferredModel) : null;
  const selectModel = useCallback((name: string) => {
    setPreferredModel(name);
    storeModel(name);
  }, []);

  const reloadConversations = conversations.reload;
  const chat = useConversation({
    conversationId: location.chatId,
    model: selectedModel,
    onCreated: useCallback((id: string) => navigate(`chat/${id}`), [navigate]),
    onChanged: useCallback(() => void reloadConversations(), [reloadConversations]),
  });

  const history: HistoryActions = {
    conversations,
    onNew: () => navigate("chat"),
    onSelect: (id) => navigate(`chat/${id}`),
    onRename: async (id, title) => {
      await renameConversation(id, title);
      await reloadConversations();
    },
    onDelete: async (id) => {
      await deleteConversation(id);
      if (chat.conversationId === id) {
        navigate("chat");
      }
      await reloadConversations();
    },
  };

  const status = derivePlatformStatus(overview.data, overview.error, overview.initialLoading);
  const maxMessageLength = overview.data?.limits.maxMessageLength ?? FALLBACK_MAX_MESSAGE_LENGTH;

  return (
    <AppLayout
      route={location.route}
      user={user}
      onSignOut={onSignOut}
      chatHref={location.chatId ? `#/chat/${location.chatId}` : "#/chat"}
      status={status}
      generating={chat.isStreaming}
    >
      {location.route === "chat" && (
        <ChatPage
          chat={chat}
          history={history}
          models={models}
          selectedModel={selectedModel}
          onSelectModel={selectModel}
          status={status}
          maxMessageLength={maxMessageLength}
        />
      )}
      {location.route === "models" && (
        <ModelsPage
          models={models}
          selectedModel={selectedModel}
          onSelectModel={selectModel}
          selectionLocked={chat.isStreaming}
        />
      )}
      {location.route === "system" && <SystemPage overview={overview} status={status} generatingHere={chat.isStreaming} />}
      {location.route === "users" && (user.role === "admin" ? <UsersPage currentUser={user} /> : <AccountPage user={user} />)}
      {location.route === "account" && <AccountPage user={user} />}
    </AppLayout>
  );
}
