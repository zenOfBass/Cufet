# Cufet Playground

The Cufet interpreter compiled to WebAssembly, with an editor around it. Everything runs in the
visitor's browser — no server executes Cufet, which is the point: Cufet can spawn subprocesses and
touch files, so running strangers' programs server-side would be a genuine security hole.

The compiler backend is deliberately not included. It shells out to gcc, which means nothing in a
browser.

## Layout

```
Cufet.Playground.csproj   the browser-wasm build (NOT Blazor — [JSExport] is the whole need)
Runtime.cs                the browser-facing surface: Run, Check and Tokens, each source text in
worker.js                 the .NET entry point; boots WASM in a Web Worker and answers requests
web/index.html            the page
web/app.ts                the editor, and the wiring between it and the worker
web/protocol.ts           the page-to-worker wire format, shared by both ends
web/ambient.d.ts          the edges no package describes (Monaco internals, MonacoEnvironment)
web/app.css               the page's own styling (Monaco brings its own)
_framework/dotnet.d.ts    a declaration ONLY — see the file; no code lives in that directory
tsconfig.json             strict type checking; the build runs it and fails on an error
build.mjs                 type-checks, then assembles everything into site/
serve.mjs                 a local static server for looking at site/
```

`web/app.ts` is bundled with Monaco by esbuild; `worker.js` is not, because the .NET SDK treats it
as the app's entry point (`WasmMainJSPath`) and copies it verbatim.

⚠ **`worker.js` is the one browser file that is still JavaScript, and deliberately.** The csproj
names it in `WasmMainJSPath`, so `dotnet publish` copies it by that name — before `build.mjs` runs
and knowing nothing about it. Making it TypeScript would make the dotnet publish depend on the node
build. It carries `// @ts-check` and JSDoc instead, so the same tsconfig checks it against the same
`protocol.ts` the page uses.

## Building

```
npm install
dotnet publish Cufet.Playground.csproj -c Release
npm run build
npm run serve            # → http://localhost:8080
```

Pass `--sourcemap` to `node build.mjs` when debugging. It is off by default because Monaco's maps
are ~11 MB — more than everything else combined — and a browser only fetches them with devtools
open.

### ★ Publishing does not work from a path containing a space

`dotnet publish` for browser-wasm **always** relinks the native runtime through emscripten, and
that relink fails on a space in the path. This repository normally lives under `…\My Stuff\…`,
where it fails with a `wasm-ld` "cannot open output file" naming a directory that demonstrably
exists. Bisected: the same project publishes fine from a space-free path.

So publish from a space-free copy and point the assembler at the result:

```powershell
robocopy .\src        C:\cufetwasm\Cufet\src        /MIR /XD bin obj
robocopy .\playground C:\cufetwasm\Cufet\playground /MIR /XD bin obj node_modules site
dotnet publish C:\cufetwasm\Cufet\playground\Cufet.Playground.csproj -c Release
node build.mjs --bundle="C:\cufetwasm\Cufet\playground\bin\Release\net10.0-browser\browser-wasm\AppBundle"
```

CI is unaffected — a GitHub Actions checkout path has no spaces — so a workflow can publish and
build in one step, and can also re-enable `InvariantGlobalization` (see the csproj comment).

## Notes worth not rediscovering

- **`worker.js` is copied from source, not from the AppBundle.** The SDK's copy is verbatim but
  incrementally cached, so editing `worker.js` and republishing can leave a stale copy in the
  bundle. It fails silently: the page loads and behaves like an older version of itself.
- **The editor worker is bundled as IIFE, not ESM.** Monaco starts it with `new Worker(url)` — a
  *classic* worker — which cannot parse the `export` an ESM bundle ends with.
- **esbuild strips types; it does not check them.** It will compile a file that says a number is a
  string. `build.mjs` runs `tsc --noEmit` first and refuses to build the bundle on an error —
  without that step the types would be documentation with syntax. `npm run check` alone is fine
  while working.
- **The type check spawns `node`, not `npx`.** On Windows, spawning the `.cmd` shim without a shell
  fails with `EINVAL`; `tsc`'s bin is a plain node script, so node runs it directly and the same
  line works on every platform.
- **`.nojekyll` is required.** GitHub Pages runs a site through Jekyll by default, and Jekyll
  omits anything whose name starts with an underscore. The runtime is served from `_framework/`.
- **Monaco's package exports rewrite `./*` to `./esm/vs/*.js`**, so the import specifier is
  `monaco-editor/editor/editor.api` — spelling out `esm/vs` resolves to `esm/vs/esm/vs/…`.
- **`editor.api`, not `editor.main`.** `editor.main` brings ~90 bundled language grammars; Cufet
  is the only language this page will ever show.

## Not done yet

- **Syntax highlighting.** The plan is to feed Monaco the *same* TextMate grammar the VS Code
  extension uses, through `monaco-editor-textmate` + `vscode-oniguruma`, rather than hand-port it
  to Monarch — one grammar, so the two cannot drift. That bridge is also what lets a VS Code colour
  theme work, since themes are scope→colour rules Monarch cannot consume.
- **Diagnostics.** `Check(source)` already returns JSON and is unused by the page.
- **Running off the main thread.** The interpreter runs synchronously, so an accidental infinite
  loop freezes the tab. Worth fixing before the page is public.
- **An in-memory filesystem.** File I/O currently degrades to an ordinary Cufet failure, which a
  program can catch. emscripten supplies a virtual FS, so this may be close to free.
