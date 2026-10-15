import { useState, type FormEvent } from "react";
import { PencilIcon, PlusIcon, TrashIcon } from "../../components/icons";
import type { ConversationSummary } from "../../types/api";
import { formatRelative } from "../../utils/format";

interface ConversationListProps {
  conversations: ConversationSummary[] | null;
  activeId: string | null;
  loading: boolean;
  error: string | null;
  disabled: boolean;
  onNew: () => void;
  onSelect: (id: string) => void;
  onRename: (id: string, title: string) => Promise<void>;
  onDelete: (id: string) => Promise<void>;
}

export function ConversationList(props: ConversationListProps) {
  const { conversations, activeId, loading, error, disabled, onNew, onSelect } = props;

  return (
    <nav className="history" aria-label="Conversations">
      <div className="history__head">
        <h2 className="history__title">Conversations</h2>
        <button type="button" className="btn btn--sm" onClick={onNew} disabled={disabled && activeId === null}>
          <PlusIcon />
          <span>New chat</span>
        </button>
      </div>

      {error && <p className="history__msg history__msg--error">{error}</p>}
      {!error && loading && !conversations && <p className="history__msg muted">Loading…</p>}
      {conversations?.length === 0 && <p className="history__msg muted">No saved conversations yet.</p>}

      <ul className="history__list">
        {conversations?.map((c) => (
          <ConversationRow
            key={c.id}
            conversation={c}
            active={c.id === activeId}
            locked={disabled}
            onSelect={() => onSelect(c.id)}
            onRename={(title) => props.onRename(c.id, title)}
            onDelete={() => props.onDelete(c.id)}
          />
        ))}
      </ul>
    </nav>
  );
}

interface RowProps {
  conversation: ConversationSummary;
  active: boolean;
  locked: boolean;
  onSelect: () => void;
  onRename: (title: string) => Promise<void>;
  onDelete: () => Promise<void>;
}

function ConversationRow({ conversation, active, locked, onSelect, onRename, onDelete }: RowProps) {
  const [mode, setMode] = useState<"view" | "rename" | "confirmDelete">("view");
  const [draft, setDraft] = useState(conversation.title);
  const [pending, setPending] = useState(false);

  const submitRename = async (event: FormEvent) => {
    event.preventDefault();
    const title = draft.trim();
    if (!title || title === conversation.title) {
      setMode("view");
      return;
    }
    setPending(true);
    try {
      await onRename(title);
      setMode("view");
    } finally {
      setPending(false);
    }
  };

  const confirmDelete = async () => {
    setPending(true);
    try {
      await onDelete();
    } finally {
      setPending(false);
      setMode("view");
    }
  };

  if (mode === "rename") {
    return (
      <li className="history__item is-editing">
        <form className="history__rename" onSubmit={submitRename}>
          <label className="visually-hidden" htmlFor={`rename-${conversation.id}`}>
            Conversation title
          </label>
          <input
            id={`rename-${conversation.id}`}
            className="input"
            value={draft}
            maxLength={120}
            autoFocus
            disabled={pending}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => e.key === "Escape" && setMode("view")}
          />
          <button type="submit" className="btn btn--sm" disabled={pending}>
            Save
          </button>
        </form>
      </li>
    );
  }

  if (mode === "confirmDelete") {
    return (
      <li className="history__item is-confirming">
        <span className="history__confirm">Delete “{conversation.title}”?</span>
        <div className="history__actions">
          <button type="button" className="btn btn--sm btn--danger" onClick={confirmDelete} disabled={pending}>
            Delete
          </button>
          <button type="button" className="btn btn--sm" onClick={() => setMode("view")} disabled={pending}>
            Cancel
          </button>
        </div>
      </li>
    );
  }

  return (
    <li className={`history__item${active ? " is-active" : ""}`}>
      <button
        type="button"
        className="history__select"
        onClick={onSelect}
        aria-current={active ? "page" : undefined}
        disabled={locked && !active}
      >
        <span className="history__name">{conversation.title}</span>
        <span className="history__meta">
          {formatRelative(conversation.updatedAt)} · {conversation.messageCount} msg
          {conversation.lastModel ? ` · ${conversation.lastModel}` : ""}
        </span>
      </button>
      <div className="history__actions">
        <button
          type="button"
          className="btn btn--ghost btn--icon btn--sm"
          aria-label={`Rename ${conversation.title}`}
          title="Rename"
          onClick={() => {
            setDraft(conversation.title);
            setMode("rename");
          }}
        >
          <PencilIcon />
        </button>
        <button
          type="button"
          className="btn btn--ghost btn--icon btn--sm"
          aria-label={`Delete ${conversation.title}`}
          title="Delete"
          disabled={locked && active}
          onClick={() => setMode("confirmDelete")}
        >
          <TrashIcon />
        </button>
      </div>
    </li>
  );
}
