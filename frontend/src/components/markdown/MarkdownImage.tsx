import type { ComponentProps } from "react";
import type { ExtraProps } from "react-markdown";

/**
 * The `img` renderer for markdown written outside the team — READMEs, PR / issue / comment / release bodies. The image
 * still loads from wherever its author pointed it, but with no Referer at all, so that host does not learn which
 * CodeSpace deployment the viewer is on. The page's path (which repository or item was open) is not what this guards:
 * the Referrer-Policy nginx.conf sends, strict-origin-when-cross-origin, already keeps it off every cross-origin request.
 */
export function MarkdownImage({ node: _node, ...props }: ComponentProps<"img"> & ExtraProps) {
  return <img {...props} referrerPolicy="no-referrer" />;
}
