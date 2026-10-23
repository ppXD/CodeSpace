export function externalTriggerUrl(endpoint: string): string {
  return new URL(endpoint, import.meta.env.VITE_API_URL || window.location.origin).href;
}
