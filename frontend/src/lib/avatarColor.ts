/**
 * A stable avatar colour per user — the same id always maps to the same swatch, so each person is
 * recognisable across the chat log (the Slack / Discord / Space convention for telling speakers
 * apart). The palette is deliberately desaturated so it spreads across the hue wheel for
 * distinguishability while still sitting inside the warm theme rather than fighting it.
 */

export interface AvatarSwatch {
  bg: string;
  fg: string;
}

// Muted tint (bg) + a darker same-hue ink (fg) so a bold initial stays legible on each.
const PALETTE: readonly AvatarSwatch[] = [
  { bg: "#EAD9CC", fg: "#9A5A33" }, // clay
  { bg: "#D9E2D2", fg: "#4F6B45" }, // sage
  { bg: "#D5DFE8", fg: "#3F5B73" }, // slate blue
  { bg: "#E7DAE6", fg: "#6E4F70" }, // mauve
  { bg: "#ECE0C6", fg: "#8A6A24" }, // wheat
  { bg: "#ECD8DA", fg: "#9A4F58" }, // rose
  { bg: "#D2E2DF", fg: "#3E6B63" }, // teal
  { bg: "#DEDCEC", fg: "#524E7C" }, // periwinkle
];

export function avatarColor(userId: string): AvatarSwatch {
  let hash = 0;
  for (let i = 0; i < userId.length; i++) hash = (hash * 31 + userId.charCodeAt(i)) >>> 0;

  return PALETTE[hash % PALETTE.length];
}
