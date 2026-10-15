import { useEffect, useState } from "react";
import { CheckIcon, CopyIcon } from "./icons";

async function copyText(text: string): Promise<void> {
  if (navigator.clipboard?.writeText) {
    await navigator.clipboard.writeText(text);
    return;
  }
  // Fallback for non-secure contexts (e.g. plain HTTP on a LAN address).
  const area = document.createElement("textarea");
  area.value = text;
  area.setAttribute("readonly", "");
  area.style.position = "fixed";
  area.style.opacity = "0";
  document.body.appendChild(area);
  area.select();
  const ok = document.execCommand("copy");
  area.remove();
  if (!ok) {
    throw new Error("Copy failed");
  }
}

export function CopyButton({ text, label = "Copy" }: { text: string; label?: string }) {
  const [state, setState] = useState<"idle" | "copied" | "failed">("idle");

  useEffect(() => {
    if (state === "idle") {
      return;
    }
    const timer = window.setTimeout(() => setState("idle"), 1500);
    return () => window.clearTimeout(timer);
  }, [state]);

  return (
    <button
      type="button"
      className="btn btn--ghost btn--sm"
      onClick={() => copyText(text).then(() => setState("copied"), () => setState("failed"))}
      aria-label={state === "copied" ? "Copied" : `${label} response`}
    >
      {state === "copied" ? <CheckIcon /> : <CopyIcon />}
      <span>{state === "copied" ? "Copied" : state === "failed" ? "Copy failed" : label}</span>
    </button>
  );
}
