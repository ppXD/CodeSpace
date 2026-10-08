import { render } from "@testing-library/react";
import ReactMarkdown from "react-markdown";
import { describe, expect, it } from "vitest";

import { MarkdownImage } from "./MarkdownImage";

/** Production .tsx sources under src/, keyed by path — what the guards below scan. */
function productionSources(): Array<[string, string]> {
  const sources = import.meta.glob("../../**/*.tsx", { eager: true, import: "default", query: "?raw" }) as Record<string, string>;

  return Object.entries(sources).filter(([path]) => !path.includes(".test."));
}

/** Each slice of `source` that starts at `open` and runs to the next `close` — one JSX element's tag (and body). */
function slices(source: string, open: string, close: string): string[] {
  const found: string[] = [];

  for (let at = source.indexOf(open); at >= 0; at = source.indexOf(open, at + open.length)) {
    const end = source.indexOf(close, at);
    found.push(source.slice(at, end < 0 ? undefined : end + close.length));
  }

  return found;
}

describe("MarkdownImage", () => {
  it("renders a markdown image from its own URL, asked for without a Referer", () => {
    const { container } = render(<ReactMarkdown components={{ img: MarkdownImage }}>{"![logo](https://tracker.example/pixel.png)"}</ReactMarkdown>);
    const img = container.querySelector("img")!;

    expect(img.getAttribute("src")).toBe("https://tracker.example/pixel.png");
    expect(img.getAttribute("alt")).toBe("logo");
    expect(img.getAttribute("referrerpolicy")).toBe("no-referrer");
    expect(img.hasAttribute("node")).toBe(false);
  });

  it("is the img renderer of every markdown view, so no outside image host learns which CodeSpace deployment the viewer is on", () => {
    const views = productionSources().flatMap(([path, source]) => slices(source, "<ReactMarkdown", "</ReactMarkdown>").map(view => [path, view] as const));
    const bare = views.filter(([, view]) => !view.includes("img: MarkdownImage")).map(([path]) => path);

    expect(views.length).toBeGreaterThan(0);
    expect(bare).toEqual([]);
  });

  it("leaves no other <img> in the app sending the deployment's origin as its Referer", () => {
    const tags = productionSources().flatMap(([path, source]) => slices(source, "<img ", "/>").map(tag => [path, tag] as const));
    const leaking = tags.filter(([, tag]) => !tag.includes('referrerPolicy="no-referrer"')).map(([path, tag]) => `${path}: ${tag}`);

    expect(tags.length).toBeGreaterThan(0);
    expect(leaking).toEqual([]);
  });
});
