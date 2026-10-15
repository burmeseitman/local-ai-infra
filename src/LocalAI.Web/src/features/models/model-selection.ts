import type { ModelInfo } from "../../types/api";

const STORAGE_KEY = "localai.selectedModel";

/**
 * Chooses the model to use: the user's preference if it is still installed and
 * usable, otherwise the configured default, otherwise the first usable model.
 * Returns null when nothing usable is installed — never a made-up name.
 */
export function pickModel(models: readonly ModelInfo[], preferred: string | null): string | null {
  const usable = models.filter((m) => m.isAvailable);
  if (preferred && usable.some((m) => m.name === preferred)) {
    return preferred;
  }
  return usable.find((m) => m.isDefault)?.name ?? usable[0]?.name ?? null;
}

export function readStoredModel(): string | null {
  try {
    return window.localStorage.getItem(STORAGE_KEY);
  } catch {
    return null; // storage disabled (private mode, policy)
  }
}

export function storeModel(name: string): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, name);
  } catch {
    // Preference simply won't persist.
  }
}
