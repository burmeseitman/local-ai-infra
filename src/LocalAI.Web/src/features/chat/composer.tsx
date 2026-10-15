import { useState, type FormEvent, type KeyboardEvent } from "react";
import { SendIcon, StopIcon } from "../../components/icons";

interface ComposerProps {
  maxLength: number;
  isStreaming: boolean;
  disabledReason: string | null;
  onSend: (text: string) => boolean;
  onStop: () => void;
}

export function Composer({ maxLength, isStreaming, disabledReason, onSend, onStop }: ComposerProps) {
  const [text, setText] = useState("");
  const trimmed = text.trim();
  const tooLong = text.length > maxLength;
  const canSend = !isStreaming && !disabledReason && trimmed.length > 0 && !tooLong;

  const submit = (event?: FormEvent) => {
    event?.preventDefault();
    if (canSend && onSend(text)) {
      setText("");
    }
  };

  const onKeyDown = (event: KeyboardEvent<HTMLTextAreaElement>) => {
    if (event.key === "Enter" && !event.shiftKey && !event.nativeEvent.isComposing) {
      event.preventDefault();
      submit();
    }
  };

  return (
    <form className="composer" onSubmit={submit}>
      <label htmlFor="prompt" className="visually-hidden">
        Message
      </label>
      <textarea
        id="prompt"
        className="composer__input"
        value={text}
        rows={3}
        placeholder={disabledReason ?? "Ask the local model anything…"}
        disabled={Boolean(disabledReason) && !isStreaming}
        onChange={(e) => setText(e.target.value)}
        onKeyDown={onKeyDown}
        aria-invalid={tooLong}
        aria-describedby="composer-hint"
      />
      <div className="composer__bar">
        <span id="composer-hint" className={`composer__hint${tooLong ? " composer__hint--error" : ""}`}>
          {tooLong
            ? `Message is ${text.length - maxLength} characters over the ${maxLength.toLocaleString()} limit.`
            : "Enter to send · Shift+Enter for a new line"}
          <span className="composer__count">
            {text.length.toLocaleString()} / {maxLength.toLocaleString()}
          </span>
        </span>
        {isStreaming ? (
          <button type="button" className="btn btn--danger" onClick={onStop}>
            <StopIcon />
            <span>Stop</span>
          </button>
        ) : (
          <button type="submit" className="btn btn--primary" disabled={!canSend}>
            <SendIcon />
            <span>Send</span>
          </button>
        )}
      </div>
    </form>
  );
}
