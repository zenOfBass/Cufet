// ⚠⚠ THIS DIRECTORY HOLDS NO CODE. It exists so that one import can be type-checked, and nothing
// here is ever copied, bundled or served.
//
// `worker.js` imports `./_framework/dotnet.js` — the .NET runtime's ES-module loader. That file is
// emitted by the wasm SDK into the published AppBundle, and `build.mjs` copies the whole
// `_framework/` directory from there into `site/`. So the specifier resolves at RUNTIME, beside the
// built worker, and can never resolve in the source tree.
//
// TypeScript resolves an import by path, so the declaration has to sit where the import points.
// Hence a `_framework/` next to worker.js containing exactly one `.d.ts` and no `.js`.
//
// ★ The SDK does emit its own `dotnet.d.ts` — 785 lines of it — beside the real loader. It is NOT
// used here: it lands in `bin/`, which is build output, and source reaching into build output is a
// dependency that breaks the moment someone cleans. This declares the three members the worker
// actually calls, and nothing else. If the loader's shape ever changes under us, a narrow
// declaration fails loudly where a broad one would quietly keep agreeing.

/** What `dotnet.create()` resolves to, narrowed to what the worker uses. */
interface DotnetRuntime {
    /** ⚠ `CufetAssemblyExports` is declared in web/ambient.d.ts and mirrors Runtime.cs. */
    getAssemblyExports(assemblyName: string): Promise<CufetAssemblyExports>;
    getConfig(): { mainAssemblyName: string };
}

export declare const dotnet: {
    create(): Promise<DotnetRuntime>;
};
