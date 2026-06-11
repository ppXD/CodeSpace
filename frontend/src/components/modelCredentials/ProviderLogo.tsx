import type { JSX } from "react";

/**
 * Per-provider brand mark for the Model Credentials cards. Each entry is a simplified, originally-drawn
 * emblem in the provider's brand colour, sitting in a soft tinted tile — enough to make a card scannable
 * at a glance without shipping pixel-exact trademarked artwork. Keyed by the lowercased provider tag (the
 * same tag stored on the credential), with a neutral fallback for any provider not listed here. To swap in
 * an official logo later, replace just that entry's `mark`.
 */
interface Brand {
  color: string;
  bg: string;
  mark: JSX.Element;
}

const BRANDS: Record<string, Brand> = {
  // Anthropic — the "spark" starburst, in clay.
  anthropic: {
    color: "#CC785C",
    bg: "rgba(204,120,92,.14)",
    mark: (
      <g stroke="currentColor" strokeWidth="2.4" strokeLinecap="round">
        <path d="M12 3.5v17" />
        <path d="M4.6 7.75 19.4 16.25" />
        <path d="M4.6 16.25 19.4 7.75" />
      </g>
    ),
  },
  // OpenAI — a six-petal rosette evoking the knot mark, in teal.
  openai: {
    color: "#10A37F",
    bg: "rgba(16,163,127,.14)",
    mark: (
      <g fill="none" stroke="currentColor" strokeWidth="1.6">
        <ellipse cx="12" cy="12" rx="4" ry="8.5" />
        <ellipse cx="12" cy="12" rx="4" ry="8.5" transform="rotate(60 12 12)" />
        <ellipse cx="12" cy="12" rx="4" ry="8.5" transform="rotate(120 12 12)" />
      </g>
    ),
  },
  // OpenRouter — a routing node branching to two endpoints, in indigo.
  openrouter: {
    color: "#6366F1",
    bg: "rgba(99,102,241,.14)",
    mark: (
      <g stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" fill="currentColor">
        <path d="M8 12h4.5" fill="none" />
        <path d="M12.5 12 16 6.8M12.5 12 16 17.2" fill="none" />
        <circle cx="6.2" cy="12" r="2.1" />
        <circle cx="17.6" cy="6.4" r="2.1" />
        <circle cx="17.6" cy="17.6" r="2.1" />
      </g>
    ),
  },
  // Ollama — the rounded mascot silhouette with its two upright ears, near-black.
  ollama: {
    color: "#2B2B2B",
    bg: "rgba(43,43,43,.12)",
    mark: (
      <path
        fill="currentColor"
        d="M8.7 3.4c.55 0 .96.46 1.02 1l.18 1.62a8.2 8.2 0 0 1 4.2 0l.18-1.62c.06-.54.47-1 1.02-1 .62 0 1.08.56.98 1.18l-.36 2.16c1.5.92 2.5 2.5 2.5 4.36v4.4c0 1.7-1.38 3.1-3.08 3.1H8.66c-1.7 0-3.08-1.4-3.08-3.1v-4.4c0-1.86 1-3.44 2.5-4.36l-.36-2.16c-.1-.62.36-1.18.98-1.18Z"
      />
    ),
  },
  // Custom gateway — a plug into a compatible endpoint, in slate.
  custom: {
    color: "#64748B",
    bg: "rgba(100,116,139,.14)",
    mark: (
      <g fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
        <path d="M9 3.5v3.5M15 3.5v3.5" />
        <path d="M6.5 7h11v3.2a5.5 5.5 0 0 1-11 0Z" />
        <path d="M12 15.7V20.5" />
      </g>
    ),
  },
};

const FALLBACK: Brand = {
  color: "#64748B",
  bg: "rgba(100,116,139,.14)",
  mark: (
    <g fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round">
      <path d="M14.5 8.5a3.5 3.5 0 1 0-4.9 3.2L5 16.3V20h3.7l.6-.6v-1.8h1.8l1-1 .2-.2a3.5 3.5 0 0 0 2.2-7.9Z" />
    </g>
  ),
};

export function ProviderLogo({ provider, size = 20 }: { provider: string; size?: number }) {
  const brand = BRANDS[provider.toLowerCase()] ?? FALLBACK;
  return (
    <span className="mc-logo" style={{ background: brand.bg, color: brand.color }}>
      <svg width={size} height={size} viewBox="0 0 24 24" aria-hidden="true" focusable="false">{brand.mark}</svg>
    </span>
  );
}
