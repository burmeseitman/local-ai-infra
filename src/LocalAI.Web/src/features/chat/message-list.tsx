import { useEffect, useRef, type ReactNode } from "react";
import type { ChatMessage } from "./chat-state";
import { MessageItem } from "./message-item";

interface MessageListProps {
  messages: ChatMessage[];
  canRetry: boolean;
  onRetry: () => void;
  emptyState: ReactNode;
}

const STICK_THRESHOLD_PX = 80;

export function MessageList({ messages, canRetry, onRetry, emptyState }: MessageListProps) {
  const scrollRef = useRef<HTMLDivElement>(null);
  const stickToBottom = useRef(true);
  const lastContentLength = messages.at(-1)?.content.length ?? 0;

  // Follow streaming output unless the user scrolled up to read earlier messages.
  useEffect(() => {
    const el = scrollRef.current;
    if (el && stickToBottom.current) {
      el.scrollTop = el.scrollHeight;
    }
  }, [messages.length, lastContentLength]);

  const onScroll = () => {
    const el = scrollRef.current;
    if (el) {
      stickToBottom.current = el.scrollHeight - el.scrollTop - el.clientHeight < STICK_THRESHOLD_PX;
    }
  };

  return (
    <div className="messages" ref={scrollRef} onScroll={onScroll} aria-live="polite" aria-relevant="additions">
      {messages.length === 0 ? (
        emptyState
      ) : (
        <div className="messages__inner">
          {messages.map((m, i) => (
            <MessageItem
              key={m.id}
              message={m}
              onRetry={canRetry && i === messages.length - 1 ? onRetry : undefined}
            />
          ))}
        </div>
      )}
    </div>
  );
}
