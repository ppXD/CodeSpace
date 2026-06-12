import js from '@eslint/js'
import globals from 'globals'
import reactHooks from 'eslint-plugin-react-hooks'
import reactRefresh from 'eslint-plugin-react-refresh'
import tseslint from 'typescript-eslint'
import { defineConfig, globalIgnores } from 'eslint/config'

export default defineConfig([
  globalIgnores(['dist', 'src/_imported/**']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      js.configs.recommended,
      tseslint.configs.recommended,
      reactHooks.configs.flat.recommended,
      reactRefresh.configs.vite,
    ],
    languageOptions: {
      globals: globals.browser,
    },
    rules: {
      // Exporting cva variant constants (badgeVariants, buttonVariants, …) next to a component is the
      // shadcn convention and safe for fast refresh; only mixing in a hook/function is a real concern.
      'react-refresh/only-export-components': ['error', { allowConstantExport: true }],
      // Honour the leading-underscore convention for intentionally-unused bindings — e.g. dropping a
      // legacy key while spreading the rest: `const { legacy: _legacy, ...rest } = obj`.
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_', varsIgnorePattern: '^_', caughtErrorsIgnorePattern: '^_' }],
    },
  },
  {
    // Two patterns legitimately co-export a non-component next to a component, which the react-refresh
    // "only export components" heuristic flags but where fast refresh doesn't meaningfully apply:
    //   - src/routes/**     TanStack Router route modules MUST export `Route = createFileRoute(...)`
    //                       (a route change triggers a full reload anyway).
    //   - src/components/ui shadcn primitives co-export their cva variant fns (badgeVariants, …), which
    //                       allowConstantExport can't recognise (a cva() call isn't a literal constant).
    files: ['src/routes/**/*.{ts,tsx}', 'src/components/ui/**/*.{ts,tsx}'],
    rules: {
      'react-refresh/only-export-components': 'off',
    },
  },
])
