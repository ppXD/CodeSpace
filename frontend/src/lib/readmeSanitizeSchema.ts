import { defaultSchema, type Schema } from "hast-util-sanitize";

/**
 * The one allowlist for a repository's README / markdown HTML, shared by both render paths: the client-side <Markdown>
 * (rehype-sanitize) and the provider-rendered HTML (prepareProviderHtml). Starts from rehype-sanitize's GitHub-based
 * default (which already strips <script>, event handlers, and javascript: URLs) and adds back the cosmetic bits real
 * READMEs rely on: `align` for centering, image sizing, and <picture>/<source>.
 */
export const readmeSanitizeSchema: Schema = {
  ...defaultSchema,
  tagNames: [...(defaultSchema.tagNames ?? []), "picture", "source"],
  attributes: {
    ...defaultSchema.attributes,
    "*": [...(defaultSchema.attributes?.["*"] ?? []), "align"],
    img: [...(defaultSchema.attributes?.img ?? []), "width", "height", "align", "loading"],
    source: ["srcSet", "srcset", "media", "type", "sizes"],
    div: [...(defaultSchema.attributes?.div ?? []), "align"],
    p: [...(defaultSchema.attributes?.p ?? []), "align"],
    h1: [...(defaultSchema.attributes?.h1 ?? []), "align"],
    h2: [...(defaultSchema.attributes?.h2 ?? []), "align"],
    h3: [...(defaultSchema.attributes?.h3 ?? []), "align"],
  },
};
