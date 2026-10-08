/** The nginx variable nginx.conf's CSP reads. */
export declare const API_ORIGIN_VARIABLE: string;

/**
 * The CSP source for the substituted env script's VITE_API_URL: its origin, its host alone when it is protocol-relative,
 * or "" when it stays on the app's own origin. Throws, naming the setting, on anything else.
 */
export declare function cspApiOrigin(envScript: string): string;

/** The nginx `set` directive docker/init.sh writes for nginx.conf to include. */
export declare function apiOriginDirective(envScript: string): string;
