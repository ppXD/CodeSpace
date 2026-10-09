import { describe, expect, it } from "vitest";

import { prepareProviderHtml } from "./providerHtml";

const GH = "https://github.com/SolarifyDev/Squid";
const REF = "main";

/** Parse the output the way the browser does when ProviderHtml injects it into a <div>. */
function inject(html: string): HTMLElement {
  const host = document.createElement("div");
  host.innerHTML = html;
  return host;
}

const allElements = (root: HTMLElement): Element[] => Array.from(root.querySelectorAll("*"));

// Elements that can run script, load a document, submit, restyle the page or carry SVG/MathML URL semantics. None of
// them belongs in a README card, whatever the provider sent.
const FORBIDDEN_ELEMENTS = ["script", "style", "iframe", "frame", "object", "embed", "svg", "math", "area", "map", "form", "button", "base", "link", "meta", "animate", "set", "use", "template", "noscript", "video", "audio", "textarea", "select"];

const forbiddenElements = (root: HTMLElement) => allElements(root).map(el => el.localName).filter(name => FORBIDDEN_ELEMENTS.includes(name));

const eventHandlers = (root: HTMLElement) => allElements(root).flatMap(el => el.getAttributeNames().filter(name => name.toLowerCase().startsWith("on")).map(name => `${el.localName}[${name}]`));

const styleAttributes = (root: HTMLElement) => allElements(root).filter(el => el.hasAttribute("style")).map(el => el.localName);

/** A URL as the browser's parser sees its scheme: ASCII whitespace and C0 control characters dropped, case folded. */
const schemeAsBrowserReadsIt = (url: string) => Array.from(url).filter(c => c.charCodeAt(0) > 0x20).join("").toLowerCase();

/** Every attribute value — each candidate of a comma list — whose scheme, as a browser reads it, would execute or embed a document. */
const executableUrls = (root: HTMLElement) =>
  allElements(root).flatMap(el =>
    el.getAttributeNames().flatMap(name =>
      (el.getAttribute(name) ?? "")
        .split(",")
        .map(candidate => schemeAsBrowserReadsIt(candidate))
        .filter(candidate => /^(javascript|vbscript|data):/.test(candidate))
        .map(() => `${el.localName}[${name}]`)));

describe("prepareProviderHtml — URL resolution", () => {
  it("returns empty string for empty input", () => {
    expect(prepareProviderHtml("", GH, REF, "")).toBe("");
  });

  it("resolves a relative image src to the provider raw URL", () => {
    const html = prepareProviderHtml(`<p><img src="./docs/logo.png"></p>`, GH, REF, "");
    expect(html).toContain("https://raw.githubusercontent.com/SolarifyDev/Squid/main/docs/logo.png");
  });

  it("resolves a relative image against the README's own folder", () => {
    const html = prepareProviderHtml(`<img src="logo.png">`, GH, REF, "packages/ui");
    expect(html).toContain("https://raw.githubusercontent.com/SolarifyDev/Squid/main/packages/ui/logo.png");
  });

  it("resolves a relative link href to the provider blob view and opens it in a new tab", () => {
    const html = prepareProviderHtml(`<a href="CONTRIBUTING.md">Contributing</a>`, GH, REF, "");
    expect(html).toContain("https://github.com/SolarifyDev/Squid/blob/main/CONTRIBUTING.md");
    expect(html).toContain('target="_blank"');
    expect(html).toContain("noopener");
  });

  it("leaves absolute URLs untouched", () => {
    const html = prepareProviderHtml(`<a href="https://example.com">x</a><img src="https://cdn.example.com/a.png">`, GH, REF, "");
    expect(html).toContain('href="https://example.com"');
    expect(html).toContain('src="https://cdn.example.com/a.png"');
  });

  it("keeps an in-page anchor in the card, pointed at the name as the card carries it, without forcing a new tab", () => {
    const html = prepareProviderHtml(`<a href="#install">Install</a>`, GH, REF, "");
    expect(html).toContain('href="#user-content-install"');
    expect(html).not.toContain("target");
  });

  it("leaves a bare # link as it is", () => {
    expect(prepareProviderHtml(`<a href="#">top</a>`, GH, REF, "")).toContain('href="#"');
  });

  it("strips <script> tags", () => {
    const html = prepareProviderHtml(`<p>hi</p><script>alert(1)</script>`, GH, REF, "");
    expect(html).not.toContain("script");
    expect(html).toContain("hi");
  });

  it("strips event-handler attributes", () => {
    const html = prepareProviderHtml(`<img src="x.png" onerror="alert(1)">`, GH, REF, "");
    expect(html).not.toContain("onerror");
  });

  it("drops javascript: hrefs", () => {
    const html = prepareProviderHtml(`<a href="javascript:alert(1)">x</a>`, GH, REF, "");
    expect(html).not.toContain("javascript:");
  });

  it("resolves every candidate in a <source> srcset", () => {
    // <picture><source srcset> is how a README switches its logo for dark mode; <img srcset> is not on the allowlist.
    const html = prepareProviderHtml(`<picture><source srcset="a.png 1x, b.png 2x"></picture>`, GH, REF, "docs");
    expect(html).toContain("https://raw.githubusercontent.com/SolarifyDev/Squid/main/docs/a.png 1x");
    expect(html).toContain("https://raw.githubusercontent.com/SolarifyDev/Squid/main/docs/b.png 2x");
  });

  it("drops a srcset candidate with a non-http scheme instead of resolving it as a repo path", () => {
    const html = prepareProviderHtml(`<picture><source srcset="javascript:alert(1) 1x, data:image/svg+xml 2x, https://cdn.example.com/a.png 3x, b.png 4x"></picture>`, GH, REF, "");

    expect(inject(html).querySelector("source")?.getAttribute("srcset")).toBe("https://cdn.example.com/a.png 3x, https://raw.githubusercontent.com/SolarifyDev/Squid/main/b.png 4x");
  });

  it("asks for every image without a Referer, so an outside image host does not learn which CodeSpace deployment the viewer is on", () => {
    const root = inject(prepareProviderHtml(`<p><img src="https://tracker.example/pixel.png"><img src="logo.png"></p>`, GH, REF, ""));

    expect(Array.from(root.querySelectorAll("img")).map(img => img.getAttribute("referrerpolicy"))).toEqual(["no-referrer", "no-referrer"]);
  });
});

describe("prepareProviderHtml — the provider's HTML is cut to an allowlist before it is embedded", () => {
  // Each payload class a provider whose sanitizer was bypassed — or a provider instance a team Admin pointed at their
  // own host — could return. The bar is not "the known-bad string is gone" but "nothing in the injected DOM can run,
  // navigate to script, embed a document, or restyle the page".
  const payloads: Array<[string, string]> = [
    ["a <script> element", `<script>alert(1)</script>`],
    ["markup smuggled through <noscript>", `<noscript><p title="</noscript><img src=x onerror=alert(1)>"></noscript>`],
    ["markup inside <template>", `<template><img src=x onerror=alert(1)></template>`],
    ["an img onerror handler", `<img src="x.png" onerror="alert(1)">`],
    ["an anchor onclick handler", `<a href="#a" onclick="alert(1)">x</a>`],
    ["a details ontoggle handler", `<details open ontoggle="alert(1)"><summary>s</summary></details>`],
    ["an svg onload handler", `<svg onload="alert(1)"></svg>`],
    ["a javascript: href", `<a href="javascript:alert(1)">x</a>`],
    ["a mixed-case scheme", `<a href="JaVaScRiPt:alert(1)">x</a>`],
    ["a tab-split scheme", `<a href="java&#x09;script:alert(1)">x</a>`],
    ["a control-character prefix", `<a href="&#x01;javascript:alert(1)">x</a>`],
    ["leading whitespace", `<a href=" javascript:alert(1)">x</a>`],
    ["an entity-encoded scheme", `<a href="&#106;avascript:alert(1)">x</a>`],
    ["a data: document href", `<a href="data:text/html,<script>alert(1)</script>">x</a>`],
    ["a vbscript: href", `<a href="vbscript:msgbox(1)">x</a>`],
    ["a javascript: img src", `<img src="javascript:alert(1)">`],
    ["a data: SVG img src", `<img src="data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=">`],
    ["an img longdesc", `<img src="a.png" longdesc="javascript:alert(1)">`],
    ["source srcset candidates", `<picture><source srcset="javascript:alert(1) 1x, data:image/png;base64,AA 2x"><img src="a.png"></picture>`],
    ["a blockquote cite", `<blockquote cite="javascript:alert(1)">q</blockquote>`],
    ["an image-map area href", `<map name="m"><area href="javascript:alert(1)" shape="rect" coords="0,0,1,1"></map><img src="a.png" usemap="#m">`],
    ["a form action", `<form action="javascript:alert(1)"><input type="submit"></form>`],
    ["a button formaction", `<button formaction="javascript:alert(1)">b</button>`],
    ["an input formaction", `<input type="image" src="a.png" formaction="javascript:alert(1)">`],
    ["an iframe src and srcdoc", `<iframe src="javascript:alert(1)" srcdoc="<script>alert(1)</script>"></iframe>`],
    ["an object data", `<object data="javascript:alert(1)"></object>`],
    ["an embed src", `<embed src="javascript:alert(1)">`],
    ["a video poster and src", `<video poster="javascript:alert(1)" src="javascript:alert(1)"></video>`],
    ["a base href", `<base href="javascript:alert(1)//">`],
    ["a meta refresh", `<meta http-equiv="refresh" content="0;url=javascript:alert(1)">`],
    ["a stylesheet link", `<link rel="stylesheet" href="javascript:alert(1)">`],
    ["an SVG xlink:href", `<svg><a xlink:href="javascript:alert(1)"><text x="0" y="10">x</text></a></svg>`],
    ["an SVG use href", `<svg><use href="data:image/svg+xml,<svg id='x' xmlns='http://www.w3.org/2000/svg'><image href='1' onerror='alert(1)'/></svg>#x"/></svg>`],
    ["a SMIL animate values", `<svg><a><animate attributeName="href" values="javascript:alert(1)"/><text x="0" y="10">x</text></a></svg>`],
    ["a SMIL set to", `<svg><a><set attributeName="href" to="javascript:alert(1)"/><text x="0" y="10">x</text></a></svg>`],
    ["a MathML href", `<math><mi href="javascript:alert(1)">x</mi></math>`],
    ["a style attribute", `<p style="position:fixed;inset:0;background:url(javascript:alert(1))">x</p>`],
    ["a <style> element", `<style>*{background:url("javascript:alert(1)")}</style>`],
  ];

  it.each(payloads)("leaves %s inert", (_, payload) => {
    const root = inject(prepareProviderHtml(`<p>before</p>${payload}<p>after</p>`, GH, REF, ""));

    expect(forbiddenElements(root)).toEqual([]);
    expect(eventHandlers(root)).toEqual([]);
    expect(styleAttributes(root)).toEqual([]);
    expect(executableUrls(root)).toEqual([]);
    expect(root.textContent).toContain("before");
    expect(root.textContent).toContain("after");
  });

  it("keeps what a provider-rendered README is made of", () => {
    // Trimmed from GitHub's /markdown output: heading anchor with its octicon, task list, table, details, a dark-mode
    // <picture>, a centered and sized logo, a fenced code block.
    const github = [
      `<div class="markdown-heading"><h2 class="heading-element">Install</h2><a id="user-content-install" class="anchor" href="#install"><svg class="octicon octicon-link" viewBox="0 0 16 16" width="16" height="16" aria-hidden="true"><path d="m7.775 3.275"></path></svg></a></div>`,
      `<p align="center"><img src="docs/logo.png" width="120" alt="Logo"></p>`,
      `<picture><source media="(prefers-color-scheme: dark)" srcset="docs/logo-dark.png"><img src="docs/logo-light.png" alt="Mode"></picture>`,
      `<ul class="contains-task-list"><li class="task-list-item"><input type="checkbox" class="task-list-item-checkbox" disabled checked> done</li></ul>`,
      `<table><thead><tr><th>Key</th></tr></thead><tbody><tr><td>Value</td></tr></tbody></table>`,
      `<details><summary>More</summary><p>Hidden</p></details>`,
      `<div class="highlight highlight-source-shell"><pre>dotnet <span class="pl-c1">build</span></pre></div>`,
    ].join("");

    const root = inject(prepareProviderHtml(github, GH, REF, ""));

    expect(root.querySelector("h2")?.textContent).toBe("Install");
    expect(root.querySelector("p[align=center] img")?.getAttribute("width")).toBe("120");
    expect(root.querySelector("p[align=center] img")?.getAttribute("src")).toBe("https://raw.githubusercontent.com/SolarifyDev/Squid/main/docs/logo.png");
    expect(root.querySelector("picture source")?.getAttribute("media")).toBe("(prefers-color-scheme: dark)");
    expect(root.querySelector("picture source")?.getAttribute("srcset")).toBe("https://raw.githubusercontent.com/SolarifyDev/Squid/main/docs/logo-dark.png");
    expect(root.querySelector("li input[type=checkbox]")?.hasAttribute("checked")).toBe(true);
    expect(root.querySelector("li input[type=checkbox]")?.hasAttribute("disabled")).toBe(true);
    expect(root.querySelector("td")?.textContent).toBe("Value");
    expect(root.querySelector("details summary")?.textContent).toBe("More");
    expect(root.querySelector("pre")?.textContent).toBe("dotnet build");
  });
});

describe("prepareProviderHtml — in-page links land on their target", () => {
  // The sanitizer prefixes every id / name / aria reference with `user-content-` so a README cannot shadow one of the
  // app's own ids. Each provider names its anchors differently, and a footnote or heading link only works if the
  // fragment it points at still matches a name after that prefixing.

  /** Each in-page reference in the injected DOM — a `#fragment` link or an aria-describedby / aria-labelledby id. */
  const inPageReferences = (root: HTMLElement): string[] => [
    ...Array.from(root.querySelectorAll("a[href^='#']")).map(a => decodeURIComponent(a.getAttribute("href")!.slice(1))),
    ...allElements(root).flatMap(el => ["aria-describedby", "aria-labelledby"].flatMap(attr => el.getAttribute(attr)?.split(/\s+/) ?? [])),
  ];

  /** The references the browser would find no element for: no element with that id, no <a> with that name. */
  const deadReferences = (root: HTMLElement): string[] => {
    const names = new Set(allElements(root).flatMap(el => [el.id, el.localName === "a" ? el.getAttribute("name") : null]));

    return inPageReferences(root).filter(name => !names.has(name));
  };

  it("keeps GitHub's footnotes and heading anchor working — ids and links that already carry the prefix", () => {
    // Trimmed from GitHub's /markdown output: ids and footnote hrefs already prefixed; the heading anchor's href is not.
    const github = [
      `<div class="markdown-heading"><h2 class="heading-element">Install</h2><a id="user-content-install" class="anchor" href="#install"></a></div>`,
      `<p>Text<sup><a href="#user-content-fn-1-abc" id="user-content-fnref-1-abc" data-footnote-ref="" aria-describedby="footnote-label">1</a></sup></p>`,
      `<section data-footnotes="" class="footnotes"><h2 id="footnote-label" class="sr-only">Footnotes</h2><ol>`,
      `<li id="user-content-fn-1-abc"><p>The note. <a href="#user-content-fnref-1-abc" data-footnote-backref="" aria-label="Back to reference 1" class="data-footnote-backref">↩</a></p></li>`,
      `</ol></section>`,
    ].join("");

    const root = inject(prepareProviderHtml(github, GH, REF, ""));

    expect(inPageReferences(root)).toEqual(["user-content-install", "user-content-fn-1-abc", "user-content-fnref-1-abc", "user-content-footnote-label"]);
    expect(deadReferences(root)).toEqual([]);
  });

  it("keeps GitLab's footnotes and heading anchor working — ids and links that carry no prefix", () => {
    // Trimmed from GitLab's /markdown output: footnote ids and hrefs unprefixed; the heading anchor's id is prefixed.
    const gitlab = [
      `<h2 dir="auto"><a href="#install" aria-hidden="true" class="anchor" id="user-content-install"></a>Install</h2>`,
      `<p dir="auto">Text<sup class="footnote-ref"><a href="#fn-1-42" id="fnref-1-42" data-footnote-ref="">1</a></sup></p>`,
      `<section data-footnotes="" class="footnotes"><ol>`,
      `<li id="fn-1-42"><p>The note. <a href="#fnref-1-42" data-footnote-backref="" aria-label="Back to reference 1" class="footnote-backref">↩</a></p></li>`,
      `</ol></section>`,
    ].join("");

    const root = inject(prepareProviderHtml(gitlab, "https://gitlab.example.test/team/app", REF, ""));

    expect(inPageReferences(root)).toEqual(["user-content-install", "user-content-fn-1-42", "user-content-fnref-1-42"]);
    expect(deadReferences(root)).toEqual([]);
  });

  it("resolves a percent-encoded fragment to the heading it names", () => {
    const root = inject(prepareProviderHtml(`<a id="user-content-安装" href="#%E5%AE%89%E8%A3%85">安装</a>`, GH, REF, ""));

    expect(inPageReferences(root)).toEqual(["user-content-安装"]);
    expect(deadReferences(root)).toEqual([]);
  });

  it("keeps a raw <a name> target reachable from a link to it", () => {
    const root = inject(prepareProviderHtml(`<p><a name="usage"></a>Usage</p><p><a href="#usage">see usage</a></p>`, GH, REF, ""));

    expect(inPageReferences(root)).toEqual(["user-content-usage"]);
    expect(deadReferences(root)).toEqual([]);
  });
});
