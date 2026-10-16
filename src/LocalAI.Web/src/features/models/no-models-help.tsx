import { CopyButton } from "../../components/copy-button";

/** Explains how to install a model; the app itself never downloads models. */
export function NoModelsHelp({ suggested }: { suggested: string | null }) {
  const model = suggested ?? "qwen2.5:3b";
  const command = `docker compose exec ollama ollama pull ${model}`;
  return (
    <div className="help">
      <p>
        Ollama is running but has no usable model. Models are downloaded by an administrator, not by this
        app. With the Docker setup, run:
      </p>
      <div className="code-line">
        <code>{command}</code>
        <CopyButton text={command} label="Copy" />
      </div>
      <p className="muted">
        {suggested ? (
          <>
            <code>{suggested}</code> is the configured default model.
          </>
        ) : (
          <>No default model is configured.</>
        )}{" "}
        <code>qwen2.5:3b</code> (~1.9 GB) runs on CPU-only machines with 8 GB RAM; <code>qwen2.5:0.5b</code> (~400 MB) is
        faster but much less reliable. On a Mac, native Ollama with <code>qwen2.5:7b</code> uses the GPU and is faster still. When running Ollama natively, use <code>ollama pull {model}</code>. Then refresh the
        model list.
      </p>
    </div>
  );
}
