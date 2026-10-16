import { memo } from "react";
import { CopyButton } from "../../components/copy-button";
import { RefreshIcon } from "../../components/icons";
import { Markdown } from "../../components/markdown";
import { summarizeStats } from "../../utils/format";
import type { MessageSource } from "../../types/api";
import type { ChatMessage } from "./chat-state";

interface MessageItemProps {
  message: ChatMessage;
  onRetry?: () => void;
}

export const MessageItem = memo(function MessageItem({ message, onRetry }: MessageItemProps) {
  const isUser = message.role === "user";
  const streaming = message.status === "streaming";
  const stats = summarizeStats(message.stats);

  return (
    <article
      className={`msg msg--${message.role}`}
      aria-busy={streaming}
      aria-label={isUser ? "Your message" : "Assistant message"}
    >
      <header className="msg__head">
        <span className="msg__role">{isUser ? "You" : "Assistant"}</span>
        {!isUser && message.model && <span className="msg__model">{message.model}</span>}
        {streaming && (
          <span className="msg__live" role="status">
            <span className="pulse" aria-hidden="true" /> Streaming
          </span>
        )}
      </header>

      <div className="msg__body">
        {isUser ? (
          <p className="msg__plain">{message.content}</p>
        ) : message.content ? (
          <Markdown text={message.content} />
        ) : streaming ? (
          <p className="muted">Waiting for the first token… the model may be loading into memory.</p>
        ) : null}
        {streaming && message.content && <span className="cursor" aria-hidden="true" />}
      </div>

      {message.sources && <SourceList sources={message.sources} streaming={streaming} />}

      {message.status === "stopped" && (
        <p className="msg__note">Generation stopped{message.content ? "; partial response kept." : "."}</p>
      )}
      {message.status === "error" && message.error && (
        <div className="msg__error" role="alert">
          <strong>Generation failed.</strong> {message.error.message}
          {message.content && <span className="muted"> Partial response kept above.</span>}
          <span className="msg__code">{message.error.code}</span>
        </div>
      )}

      {!isUser && !streaming && (
        <footer className="msg__foot">
          {message.content && <CopyButton text={message.content} />}
          {onRetry && (
            <button type="button" className="btn btn--ghost btn--sm" onClick={onRetry}>
              <RefreshIcon />
              <span>Retry</span>
            </button>
          )}
          {stats && <span className="msg__stats">{stats}</span>}
        </footer>
      )}
    </article>
  );
});

function SourceList({ sources, streaming }: { sources: MessageSource[]; streaming: boolean }) {
  if (sources.length === 0) {
    return streaming ? null : <p className="msg__note">No matching passages were found in your documents.</p>;
  }
  return (
    <details className="sources" open>
      <summary>
        {sources.length} source{sources.length === 1 ? "" : "s"} from your documents
      </summary>
      <ol className="sources__list">
        {sources.map((s) => (
          <li key={s.number} value={s.number}>
            <span className="sources__title">
              {s.title}
              {s.page != null && <span className="muted"> · p. {s.page}</span>}
            </span>
            <span className="sources__snippet">{s.snippet}</span>
          </li>
        ))}
      </ol>
    </details>
  );
}
