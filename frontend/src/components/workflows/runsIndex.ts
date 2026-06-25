/** A friendly source-type label — title-cased from the open `source_type` token (manual / webhook / schedule.cron / …). */
export function sourceLabel(sourceType: string): string {
  if (!sourceType) return "Run";
  return sourceType.charAt(0).toUpperCase() + sourceType.slice(1);
}
