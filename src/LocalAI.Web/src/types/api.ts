// Mirrors the API contracts in src/LocalAI.Api/Contracts (see docs/api.md).

export type ChatRole = "system" | "user" | "assistant";

export interface ChatMessageDto {
  role: ChatRole;
  content: string;
}

export interface ChatRequest {
  model: string;
  messages: ChatMessageDto[];
  temperature?: number;
  maxTokens?: number;
}

export interface CompletionStats {
  promptTokens: number | null;
  completionTokens: number | null;
  totalDurationMs: number | null;
  loadDurationMs: number | null;
  generationDurationMs: number | null;
  tokensPerSecond: number | null;
}

export interface StreamStartEvent {
  requestId: string;
  model: string;
  createdAt: string;
  conversationId: string | null;
}

export interface StreamDeltaEvent {
  content: string;
}

export interface StreamDoneEvent {
  finishReason: string | null;
  stats: CompletionStats | null;
}

export interface StreamErrorEvent {
  code: string;
  message: string;
  retryable: boolean;
}

export interface ModelInfo {
  name: string;
  displayName: string;
  tag: string | null;
  family: string | null;
  digest: string | null;
  sizeBytes: number | null;
  modifiedAt: string | null;
  parameterSize: string | null;
  quantizationLevel: string | null;
  isRemote: boolean;
  isAvailable: boolean;
  isDefault: boolean;
}

export interface ModelListResponse {
  provider: string;
  models: ModelInfo[];
  defaultModel: string | null;
  fetchedAt: string;
}

export interface ProviderStatus {
  provider: string;
  reachable: boolean;
  version: string | null;
  latencyMs: number | null;
  error: string | null;
  checkedAt: string;
  lastSuccessfulCheckAt: string | null;
  installedModelCount: number | null;
  usableModelCount: number | null;
  defaultModel: string | null;
  defaultModelInstalled: boolean;
}

export interface RecentFailure {
  at: string;
  code: string;
  model: string | null;
}

export interface InferenceStats {
  since: string;
  totalRequests: number;
  succeeded: number;
  failed: number;
  cancelled: number;
  activeGenerations: number;
  lastSuccessAt: string | null;
  lastFailureAt: string | null;
  lastCompletionModel: string | null;
  lastCompletion: CompletionStats | null;
  recentFailures: RecentFailure[];
}

export interface Limits {
  maxMessageLength: number;
  maxConversationLength: number;
  maxMessages: number;
  defaultMaxTokens: number;
  maxTokensLimit: number;
  maxConcurrentGenerations: number;
  inferenceTimeoutSeconds: number;
  allowRemoteModels: boolean;
}

export interface SystemOverview {
  api: {
    status: string;
    version: string;
    environment: string;
    startedAt: string;
    uptimeSeconds: number;
  };
  provider: ProviderStatus;
  inference: InferenceStats;
  limits: Limits;
  generatedAt: string;
}

/** RFC 7807 Problem Details with the API's extension members. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  code?: string;
  retryable?: boolean;
  traceId?: string;
  errors?: Record<string, string[]>;
}

export type MessageStatus = "complete" | "stopped" | "error";

export interface StoredMessage {
  id: string;
  sequence: number;
  role: ChatRole;
  content: string;
  status: MessageStatus;
  model: string | null;
  error: { code: string; message: string | null } | null;
  stats: CompletionStats | null;
  createdAt: string;
}

export interface ConversationSummary {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  messageCount: number;
  lastModel: string | null;
}

export interface Conversation {
  id: string;
  title: string;
  createdAt: string;
  updatedAt: string;
  messages: StoredMessage[];
}
