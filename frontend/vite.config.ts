import path from "node:path";
import tailwindcss from "@tailwindcss/vite";
import { TanStackRouterVite } from "@tanstack/router-plugin/vite";
import react from "@vitejs/plugin-react";
import { defineConfig } from "vite";

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    // TanStack Router plugin must run before @vitejs/plugin-react.
    TanStackRouterVite({ target: "react", autoCodeSplitting: true }),
    react(),
    tailwindcss(),
  ],
  resolve: {
    alias: {
      "@": path.resolve(__dirname, "./src"),
    },
  },
  server: {
    // 5180 — Vite's defaults (5173, 5174) and 6666 are all taken or unusable on this
    // operator's machine. 6666 in particular was blocked by Chrome/Firefox as an IRC port
    // (ERR_UNSAFE_PORT — see Chromium's restricted-ports list). 5180 is off every browser
    // blocklist and uncommon for dev. The backend's OAuth callback URL + CORS auto-allow
    // list (appsettings + Startup.cs) are pinned to this port; keep them in sync.
    //
    // strictPort: fail fast if 5180 is busy rather than silently jumping to 5181 and
    // breaking the OAuth round-trip.
    // host '127.0.0.1': bind IPv4 explicitly. Vite's default ('localhost') resolves to
    // [::1] only on some macOS setups, which can leave IPv4-only browsers (or curl --4)
    // unable to connect.
    port: 5180,
    strictPort: true,
    host: "127.0.0.1",
    proxy: {
      // Forward /api to .NET backend so Cookie auth + relative URLs work in dev.
      "/api": {
        // macOS 12+ binds AirPlay Receiver to 5000, so the API listens on 5099 (the
        // .NET template's own default). Mirror that here for the dev proxy.
        target: "http://localhost:5099",
        changeOrigin: true,
      },
    },
  },
});
