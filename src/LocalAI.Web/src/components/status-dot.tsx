export type Tone = "ok" | "warn" | "error" | "busy" | "idle";

interface StatusDotProps {
  tone: Tone;
  label?: string;
}

/** Colored indicator that always pairs color with text for accessibility. */
export function StatusDot({ tone, label }: StatusDotProps) {
  return (
    <span className={`status status--${tone}`}>
      <span className="status__dot" aria-hidden="true" />
      {label && <span className="status__label">{label}</span>}
    </span>
  );
}
