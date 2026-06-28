import { useEffect, useState } from "react";

/**
 * Returns `value` after it has stopped changing for `delayMs` — debounces a fast-changing input (e.g. a search
 * box) so dependent fetches fire once the user pauses typing, not on every keystroke. The latest value always
 * wins: each change cancels the prior pending timer.
 */
export function useDebounced<T>(value: T, delayMs: number): T {
  const [debounced, setDebounced] = useState(value);

  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), delayMs);
    return () => clearTimeout(timer);
  }, [value, delayMs]);

  return debounced;
}
