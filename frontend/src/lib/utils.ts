import { type ClassValue, clsx } from "clsx";
import { twMerge } from "tailwind-merge";

/**
 * shadcn/ui canonical class merger. Conditional classes + Tailwind conflict resolution
 * in one helper — every component in src/components/ui imports this.
 */
export function cn(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}
