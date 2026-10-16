import { useCallback, useState } from "react";
import { deleteConversation, listConversations, renameConversation } from "./api/conversations";
import { listDocuments } from "./api/documents";
import { fetchModels, fetchOverview } from "./api/endpoints";
import { useConversation } from "./features/chat/use-conversation";
import { pickModel, readStoredModel, storeModel } from "./features/models/model-selection";
import { derivePlatformStatus } from "./features/system/platform-status";
import { useAsyncResource } from "./hooks/use-async-resource";
import { useHashRoute } from "./hooks/use-hash-route";
import { AppLayout } from "./layouts/app-layout";
import { ChatPage, type HistoryActions } from "./pages/chat-page";
import { DocumentsPage } from "./pages/documents-page";
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
const DOCUMENTS_POLL_MS = 4_000;
const FALLBACK_MAX_MESSAGE_LENGTH = 8_000;
const USE_DOCUMENTS_KEY = "localai.useDocuments";

function readFlag(key: string): boolean {
  try {
    return window.localStorage.getItem(key) === "true";
  } catch {
    return false;
  }
}

function writeFlag(key: string, value: boolean): void {
  try {
    window.localStorage.setItem(key, String(value));
  } catch {
    // preference simply won't persist
  }
}

export function Dashboard({ user, onSignOut }: DashboardProps) {
  const [location, navigate] = useHashRoute();
  const overview = useAsyncResource(useCallback((_force: boolean, signal: AbortSignal) => fetchOverview(signal), []), OVERVIEW_POLL_MS);
  const models = useAsyncResource(useCallback((force: boolean, signal: AbortSignal) => fetchModels(force, signal), []), MODELS_POLL_MS);
  const conversations = useAsyncResource(useCallback((_force: boolean, signal: AbortSignal) => listConversations(signal), []));
  // Polled so indexing progress (processing → ready) shows up without a manual refresh.
  const documents = useAsyncResource(useCallback((_force: boolean, signal: AbortSignal) => listDocuments(signal), []), DOCUMENTS_POLL_MS);
  const readyDocuments = documents.data?.documents.filter((d) => d.status === "ready").length ?? 0;
  const [useDocumentsPreference, setUseDocumentsPreference] = useState(() => readFlag(USE_DOCUMENTS_KEY));
  const useDocuments = useDocumentsPreference && readyDocuments > 0;
  const toggleDocuments = useCallback((value: boolean) => {
    setUseDocumentsPreference(value);
    writeFlag(USE_DOCUMENTS_KEY, value);
  }, []);

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
    useDocuments,
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
          documents={{ ready: readyDocuments, enabled: useDocuments, onToggle: toggleDocuments }}
        />
      )}
      {location.route === "documents" && <DocumentsPage documents={documents} />}
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
