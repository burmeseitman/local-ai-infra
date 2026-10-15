import { CopyButton } from "../../components/copy-button";

/** Explains how to install a model; the app itself never downloads models. */
export function NoModelsHelp({ suggested }: { suggested: string | null }) {
  const model = suggested ?? "qwen2.5:0.5b";
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
        Small models such as <code>qwen2.5:0.5b</code> (~400 MB) or <code>llama3.2:1b</code> (~1.3 GB) run on CPU-only
        machines with 8 GB RAM. When running Ollama natively, use <code>ollama pull {model}</code>. Then refresh the
        model list.
      </p>
    </div>
  );
}
