import { useRef, useState, type DragEvent } from "react";
import { deleteDocument, uploadDocument } from "../api/documents";
import { ApiError } from "../api/http";
import { CopyButton } from "../components/copy-button";
import { RefreshIcon, UploadIcon } from "../components/icons";
import { Notice } from "../components/notice";
import { StatusDot } from "../components/status-dot";
import type { AsyncResource } from "../hooks/use-async-resource";
import type { DocumentInfo, DocumentListResponse } from "../types/api";
import { formatBytes, formatRelative } from "../utils/format";

interface DocumentsPageProps {
  documents: AsyncResource<DocumentListResponse>;
}

/** The user's private document library for "chat with your documents". */
export function DocumentsPage({ documents }: DocumentsPageProps) {
  const data = documents.data;
  const [uploads, setUploads] = useState<{ name: string; error?: string }[]>([]);
  const [dragging, setDragging] = useState(false);
  const input = useRef<HTMLInputElement>(null);
  const canUpload = data?.embeddingModelInstalled ?? false;
  const accept = (data?.supportedExtensions ?? [".pdf", ".txt", ".md"]).join(",");

  const upload = async (files: FileList | File[]) => {
    for (const file of Array.from(files)) {
      setUploads((u) => [...u, { name: file.name }]);
      try {
        await uploadDocument(file);
        setUploads((u) => u.filter((x) => x.name !== file.name));
      } catch (error) {
        const message = error instanceof ApiError ? error.message : "Upload failed.";
        setUploads((u) => u.map((x) => (x.name === file.name ? { ...x, error: message } : x)));
      }
      await documents.reload();
    }
  };

  const onDrop = (event: DragEvent) => {
    event.preventDefault();
    setDragging(false);
    if (canUpload && event.dataTransfer.files.length > 0) {
      void upload(event.dataTransfer.files);
    }
  };

  const pullCommand = `docker compose exec ollama ollama pull ${data?.embeddingModel ?? "nomic-embed-text"}`;

  return (
    <section className="page" aria-labelledby="documents-title">
      <header className="page-header">
        <div>
          <h1 id="documents-title" className="page-title">
            Documents
          </h1>
          <p className="page-subtitle">
            Upload files, then turn on <strong>Use my documents</strong> in Chat to get answers with citations. Files are
            processed on this machine and visible only to you.
          </p>
        </div>
        <button type="button" className="btn" onClick={() => void documents.reload()} disabled={documents.refreshing}>
          <RefreshIcon className={documents.refreshing ? "spin" : undefined} />
          <span>Refresh</span>
        </button>
      </header>

      {documents.error && (
        <Notice tone="error" title="Could not load documents">
          {documents.error.message}
        </Notice>
      )}

      {data && !data.embeddingModelInstalled && (
        <Notice tone="warn" title="Embedding model not installed">
          <p>
            Documents are indexed with the local embedding model <code>{data.embeddingModel}</code> (~270 MB), which is not
            installed yet. Install it once:
          </p>
          <div className="help">
            <div className="code-line">
              <code>{pullCommand}</code>
              <CopyButton text={pullCommand} />
            </div>
          </div>
          <p className="muted">With native Ollama, run <code>ollama pull {data.embeddingModel}</code>. Then refresh this page.</p>
        </Notice>
      )}

      <div
        className={`dropzone${dragging ? " is-dragging" : ""}${canUpload ? "" : " is-disabled"}`}
        onDragOver={(e) => {
          e.preventDefault();
          setDragging(canUpload);
        }}
        onDragLeave={() => setDragging(false)}
        onDrop={onDrop}
      >
        <UploadIcon className="dropzone__icon" />
        <p className="dropzone__text">
          Drag PDF, TXT or Markdown files here, or{" "}
          <button type="button" className="link-button" onClick={() => input.current?.click()} disabled={!canUpload}>
            choose files
          </button>
          .
        </p>
        <p className="muted small">
          Up to {formatBytes(data?.maxFileBytes ?? 10 * 1024 * 1024)} each. Scanned PDFs (images without a text layer) are not supported.
        </p>
        <input
          ref={input}
          className="visually-hidden"
          type="file"
          multiple
          accept={accept}
          aria-label="Upload documents"
          disabled={!canUpload}
          onChange={(e) => {
            if (e.target.files) {
              void upload(e.target.files);
            }
            e.target.value = "";
          }}
        />
      </div>

      {uploads.length > 0 && (
        <ul className="upload-list">
          {uploads.map((u) => (
            <li key={u.name} className={u.error ? "is-error" : undefined} role={u.error ? "alert" : "status"}>
              <strong>{u.name}</strong>: {u.error ?? "uploading…"}
              {u.error && (
                <button type="button" className="btn btn--ghost btn--sm" onClick={() => setUploads((list) => list.filter((x) => x !== u))}>
                  Dismiss
                </button>
              )}
            </li>
          ))}
        </ul>
      )}

      {data && data.documents.length === 0 && <p className="muted">No documents yet.</p>}
      {data && data.documents.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th scope="col">Document</th>
                <th scope="col">Status</th>
                <th scope="col" className="num">
                  Size
                </th>
                <th scope="col" className="num">
                  Passages
                </th>
                <th scope="col">Added</th>
                <th scope="col">
                  <span className="visually-hidden">Actions</span>
                </th>
              </tr>
            </thead>
            <tbody>
              {data.documents.map((d) => (
                <DocumentRow key={d.id} document={d} onDeleted={() => void documents.reload()} />
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  );
}

function DocumentRow({ document: d, onDeleted }: { document: DocumentInfo; onDeleted: () => void }) {
  const [confirming, setConfirming] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const remove = async () => {
    try {
      await deleteDocument(d.id);
      onDeleted();
    } catch (e) {
      setError(e instanceof ApiError ? e.message : "Delete failed.");
    }
  };

  return (
    <tr>
      <td>
        <div className="model-name">{d.title}</div>
        <div className="muted small mono">{d.fileName}</div>
      </td>
      <td>
        {d.status === "ready" && <StatusDot tone="ok" label="Ready" />}
        {d.status === "processing" && <StatusDot tone="busy" label="Indexing…" />}
        {d.status === "failed" && (
          <div>
            <StatusDot tone="error" label="Failed" />
            <div className="small muted">{d.error}</div>
          </div>
        )}
        {error && <div className="small" role="alert">{error}</div>}
      </td>
      <td className="num mono">{formatBytes(d.sizeBytes)}</td>
      <td className="num mono">{d.status === "ready" ? d.chunkCount : "—"}</td>
      <td>{formatRelative(d.createdAt)}</td>
      <td className="actions">
        {confirming ? (
          <span className="inline-form">
            <button type="button" className="btn btn--sm btn--danger" onClick={() => void remove()}>
              Delete
            </button>
            <button type="button" className="btn btn--sm btn--ghost" onClick={() => setConfirming(false)}>
              Cancel
            </button>
          </span>
        ) : (
          <button type="button" className="btn btn--sm" aria-label={`Delete ${d.title}`} onClick={() => setConfirming(true)}>
            Delete
          </button>
        )}
      </td>
    </tr>
  );
}
