/**
 * How long ago, in the shortest form that is still unambiguous.
 *
 * Its own module because a card and a badge must not disagree about it, and because exporting a plain function from a
 * component file costs the whole file its fast refresh.
 */
export function timeAgo(observedAt: string): string {
  const minutes = Math.round((Date.now() - new Date(observedAt).getTime()) / 60000);
  if (!Number.isFinite(minutes) || minutes < 1) return "just now";
  if (minutes < 60) return `${minutes} min ago`;

  const hours = Math.round(minutes / 60);
  return hours < 24 ? `${hours} h ago` : `${Math.round(hours / 24)} d ago`;
}
