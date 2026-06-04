/**
 * Device-local onboarding state (#191).
 *
 * The one-time welcome modal is server-persisted (User.HasCompletedOnboarding) so
 * it fires once across devices. The lighter-weight bits here are deliberately
 * device-local, so localStorage is the right store:
 *   - per-page first-visit tips (dismissed individually), and
 *   - the "Restart tour" hand-off from Settings → Dashboard.
 *
 * Every accessor is SSR-safe (returns a no-op/false when `window` is absent) and
 * swallows localStorage exceptions (Safari private mode / quota), since a
 * non-persisted tip dismissal is an acceptable degradation, never an error.
 */

const TIP_PREFIX = "cd_tip_";
const RESTART_TOUR_KEY = "cd_restart_tour";

/** Page-tip ids — centralized so "Restart tour" + tests reference the same keys. */
export const TIP_IDS = {
  documents: "documents_v1",
  vendors: "vendors_v1",
  rules: "rules_v1",
} as const;

export function isTipDismissed(id: string): boolean {
  if (typeof window === "undefined") return false;
  try {
    return window.localStorage.getItem(`${TIP_PREFIX}${id}`) === "1";
  } catch {
    return false;
  }
}

export function dismissTip(id: string): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(`${TIP_PREFIX}${id}`, "1");
  } catch {
    /* private-mode / quota — a non-persisted dismiss is acceptable */
  }
}

/** Clears every dismissed page tip so "Restart tour" re-shows them all. */
export function resetOnboardingTips(): void {
  if (typeof window === "undefined") return;
  try {
    const stale: string[] = [];
    for (let i = 0; i < window.localStorage.length; i++) {
      const key = window.localStorage.key(i);
      if (key && key.startsWith(TIP_PREFIX)) stale.push(key);
    }
    stale.forEach((key) => window.localStorage.removeItem(key));
  } catch {
    /* ignore */
  }
}

/** Settings → "Restart tour": flags the dashboard to re-open the welcome modal. */
export function requestTourRestart(): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.setItem(RESTART_TOUR_KEY, "1");
  } catch {
    /* ignore */
  }
}

/**
 * Pure read of the "Restart tour" hand-off flag — safe to call during render (the
 * dashboard does, via a lazy useState initializer). Clearing is a separate step
 * (clearTourRestart) so the read stays side-effect-free.
 */
export function peekTourRestart(): boolean {
  if (typeof window === "undefined") return false;
  try {
    return window.localStorage.getItem(RESTART_TOUR_KEY) === "1";
  } catch {
    return false;
  }
}

/** Clears the "Restart tour" flag once the dashboard has acted on it. */
export function clearTourRestart(): void {
  if (typeof window === "undefined") return;
  try {
    window.localStorage.removeItem(RESTART_TOUR_KEY);
  } catch {
    /* ignore */
  }
}
