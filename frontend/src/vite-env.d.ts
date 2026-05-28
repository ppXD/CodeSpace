/// <reference types="vite/client" />
/// <reference types="@testing-library/jest-dom" />

// @fontsource-variable/* ships only CSS; the package has no .d.ts, so TS rejects the
// side-effect import in strict mode. Declare the module shape here once.
declare module "@fontsource-variable/geist-mono";
