/**
 * The per-provider credential form variants for the Model Credentials settings UI.
 *
 * Deliberately a small, HARDCODED config rather than a manifest framework: today's providers are all
 * shaped `{ secret key } + { optional base URL }` (or keyless / gateway variants), so a typed table is the
 * honest amount of abstraction. A manifest engine waits until a third genuinely-divergent provider shape
 * lands. Each field's `key` maps 1:1 onto `AddModelCredentialInput` so the form renderer needs no mapping.
 */

export type ProviderFormFieldKey = "apiKey" | "baseUrl";

export interface ProviderFormField {
  key: ProviderFormFieldKey;
  label: string;
  /** Secret → masked input, write-only on edit. Non-secret (base URL) → plaintext, shown verbatim. */
  secret: boolean;
  required: boolean;
  placeholder?: string;
  /** Tucked behind an "advanced" disclosure (an optional base-URL override for a direct provider). */
  advanced?: boolean;
}

export interface ProviderForm {
  /** The tag stored on the credential + matched by the harness projector. */
  provider: string;
  /** Human label for the provider card / form header. */
  label: string;
  /** No API key at all (a local model reached over base URL) — the form renders no secret field. */
  keyless: boolean;
  fields: readonly ProviderFormField[];
}

const API_KEY = (placeholder: string, label = "API key"): ProviderFormField =>
  ({ key: "apiKey", label, secret: true, required: true, placeholder });

const OPTIONAL_BASE_URL = (placeholder: string): ProviderFormField =>
  ({ key: "baseUrl", label: "Base URL", secret: false, required: false, advanced: true, placeholder });

const REQUIRED_BASE_URL = (placeholder: string): ProviderFormField =>
  ({ key: "baseUrl", label: "Base URL", secret: false, required: true, placeholder });

export const PROVIDER_FORMS: readonly ProviderForm[] = [
  { provider: "Anthropic", label: "Anthropic", keyless: false, fields: [API_KEY("sk-ant-…"), OPTIONAL_BASE_URL("https://api.anthropic.com")] },
  { provider: "OpenAI", label: "OpenAI", keyless: false, fields: [API_KEY("sk-…"), OPTIONAL_BASE_URL("https://api.openai.com/v1")] },
  { provider: "OpenRouter", label: "OpenRouter", keyless: false, fields: [API_KEY("sk-or-…"), OPTIONAL_BASE_URL("https://openrouter.ai/api/v1")] },
  { provider: "Ollama", label: "Ollama (local)", keyless: true, fields: [REQUIRED_BASE_URL("http://localhost:11434")] },
  // The catch-all gateway: an OpenAI/Anthropic-compatible endpoint (a LiteLLM/vLLM proxy fronting Qwen,
  // DeepSeek, …). Auth is a bearer token, and the base URL is mandatory (there's no default endpoint).
  { provider: "Custom", label: "Custom gateway", keyless: false, fields: [API_KEY("Gateway auth token", "Auth token"), REQUIRED_BASE_URL("https://your-gateway/v1")] },
];

/** Look up a provider's form variant (case-insensitive). Undefined for an unknown tag. */
export function providerForm(provider: string): ProviderForm | undefined {
  return PROVIDER_FORMS.find(f => f.provider.toLowerCase() === provider.toLowerCase());
}
