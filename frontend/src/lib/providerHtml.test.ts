import { describe, expect, it } from "vitest";

import { prepareProviderHtml } from "./providerHtml";

const GH = "https://github.com/SolarifyDev/Squid";
const REF = "main";

describe("prepareProviderHtml", () => {
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

  it("leaves in-page anchors as-is without forcing a new tab", () => {
    const html = prepareProviderHtml(`<a href="#install">Install</a>`, GH, REF, "");
    expect(html).toContain('href="#install"');
    expect(html).not.toContain("target");
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

  it("resolves every candidate in a srcset", () => {
    const html = prepareProviderHtml(`<img srcset="a.png 1x, b.png 2x">`, GH, REF, "docs");
    expect(html).toContain("https://raw.githubusercontent.com/SolarifyDev/Squid/main/docs/a.png 1x");
    expect(html).toContain("https://raw.githubusercontent.com/SolarifyDev/Squid/main/docs/b.png 2x");
  });
});
