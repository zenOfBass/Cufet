// The edges of this page that no package describes.
//
// ⚠ This file is deliberately a SCRIPT, not a module — it has no top-level import or export. That
// is what makes the declarations below ambient (global) rather than augmentations of some other
// module, and `declare module 'x'` for a package that ships no types only works this way.

/**
 * Monaco's dependency-injection key. The real thing is a decorator function carrying its service
 * type; the only part this page needs is the link from the key to what `get` hands back.
 */
interface MonacoServiceId<T> {
    readonly __serviceType?: T;
}

/**
 * ★★ Monaco INTERNALS, which ship no types — the deep-import cost of `editor.api`.
 *
 * `monaco-editor` types its public API thoroughly and stops at the boundary, and these two
 * specifiers reach past it. They are imported for one reason, explained at length in app.ts: a
 * standalone theme drops `semanticHighlighting` on the floor, so the SETTING is the only lever that
 * turns semantic tokens on, and the setting is only reachable through the configuration service.
 *
 * ⚠ Only the two members actually called are declared. A fuller transcription would be a second
 * copy of someone else's API, drifting silently against the real one; a narrow declaration fails
 * loudly if Monaco moves these, which is the behaviour worth having.
 */
declare module 'monaco-editor/platform/configuration/common/configuration' {
    export interface IConfigurationService {
        getValue<T>(section: string): T;
        updateValue(key: string, value: unknown): Promise<void>;
    }

    /** The service identifier. Merges with the interface above: same name, value and type. */
    export const IConfigurationService: MonacoServiceId<IConfigurationService>;
}

declare module 'monaco-editor/editor/standalone/browser/standaloneServices' {
    export const StandaloneServices: {
        get<T>(id: MonacoServiceId<T>): T;
    };
}

/**
 * Imported for its side effect only — it registers the editor contribution that READS the semantic
 * token registry. Without the import nothing calls the provider; see app.ts. It exports nothing, so
 * there is nothing to describe beyond its existence.
 */
declare module 'monaco-editor/editor/contrib/semanticTokens/browser/documentSemanticTokens';

/**
 * What `[JSExport]` on `Cufet.Playground.Runtime` produces, reached through `getAssemblyExports`.
 * ⚠ Mirrors playground/Runtime.cs — three entry points, each taking source text and returning text.
 */
interface CufetAssemblyExports {
    Cufet: {
        Playground: {
            Runtime: {
                Run(source: string): string;
                Check(source: string): string;
                Tokens(source: string): string;
                /** Puts a file where a program can read it. Returns "" on success, else why not. */
                PlaceFile(path: string, content: string): string;
            };
        };
    };
}

/**
 * Monaco reads this global to find its own web workers. It is not part of the `editor.api` type
 * surface because consumers set it and Monaco reads it, never the other way round.
 */
declare var MonacoEnvironment: {
    getWorkerUrl: (moduleId: string, label: string) => string;
} | undefined;
