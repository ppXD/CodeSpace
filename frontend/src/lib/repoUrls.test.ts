import { describe, expect, it } from "vitest";

import { blobUrl, rawUrl, resolveReadmeUrl } from "./repoUrls";

const GH = "https://github.com/SolarifyDev/Squid";
const GL = "https://gitlab.sjfood.us/solar/RuHappy-Web";

describe("blobUrl", () => {
  it("builds a GitHub /blob/ link", () => {
    expect(blobUrl(GH, "main", "src/app.ts")).toBe("https://github.com/SolarifyDev/Squid/blob/main/src/app.ts");
  });
  it("builds a GitLab /-/blob/ link", () => {
    expect(blobUrl(GL, "main", "web/index.ts")).toBe("https://gitlab.sjfood.us/solar/RuHappy-Web/-/blob/main/web/index.ts");
  });
  it("normalizes leading/trailing slashes", () => {
    expect(blobUrl(GH + "/", "main", "/src/x")).toBe("https://github.com/SolarifyDev/Squid/blob/main/src/x");
  });
});

describe("rawUrl", () => {
  it("uses raw.githubusercontent.com for GitHub", () => {
    expect(rawUrl(GH, "main", "branding/logo.svg")).toBe("https://raw.githubusercontent.com/SolarifyDev/Squid/main/branding/logo.svg");
  });
  it("uses /-/raw/ for GitLab", () => {
    expect(rawUrl(GL, "v1.2", "docs/a.png")).toBe("https://gitlab.sjfood.us/solar/RuHappy-Web/-/raw/v1.2/docs/a.png");
  });
});

describe("resolveReadmeUrl", () => {
  it("passes absolute / data / anchor URLs through untouched", () => {
    expect(resolveReadmeUrl("https://img.shields.io/x.svg", GH, "main", "", true)).toBe("https://img.shields.io/x.svg");
    expect(resolveReadmeUrl("#section", GH, "main", "", false)).toBe("#section");
    expect(resolveReadmeUrl("data:image/png;base64,AAAA", GH, "main", "", true)).toBe("data:image/png;base64,AAAA");
  });

  it("resolves a relative image to the raw URL, relative to the README's dir", () => {
    expect(resolveReadmeUrl("branding/logo.svg", GH, "main", "", true))
      .toBe("https://raw.githubusercontent.com/SolarifyDev/Squid/main/branding/logo.svg");
    expect(resolveReadmeUrl("./img/a.png", GH, "main", "docs", true))
      .toBe("https://raw.githubusercontent.com/SolarifyDev/Squid/main/docs/img/a.png");
  });

  it("resolves a relative link to the blob view", () => {
    expect(resolveReadmeUrl("CONTRIBUTING.md", GH, "main", "", false))
      .toBe("https://github.com/SolarifyDev/Squid/blob/main/CONTRIBUTING.md");
  });
});
