// @ts-check
// The .NET runtime's entry point — and it runs in a Web Worker, not on the page.
//
// ★ WHY A WORKER: the Cufet interpreter executes synchronously. On the UI thread a long program
// freezes the tab, and a NON-TERMINATING one kills it outright — no rendering, no clicks, no way
// back. A playground is exactly where someone writes an accidental infinite loop, so that is not
// an edge case, it is Tuesday.
//
// ★ WHY TERMINATE RATHER THAN INTERRUPT: the interpreter does have a cooperative interrupt (the
// same one Ctrl-C uses natively), but a worker blocked inside a synchronous Execute never reaches
// its message queue, so it can never be told to stop. Setting a flag from outside would need
// SharedArrayBuffer + Atomics, and those require COOP/COEP headers that GitHub Pages cannot send.
// So the page kills the worker and starts a new one. Losing the runtime is acceptable: every run
// builds a fresh Interpreter anyway, so there is no state worth preserving between runs.
//
// ★★ THIS FILE STAYS JAVASCRIPT, deliberately. Cufet.Playground.csproj names it in WasmMainJSPath,
// so the .NET SDK copies it into the AppBundle by that name during `dotnet publish` — which runs
// BEFORE build.mjs and knows nothing about it. Making it TypeScript would mean the dotnet publish
// depends on the node build. `// @ts-check` plus the JSDoc below gets the same checking from the
// same tsconfig, against the same protocol.ts the page uses, and costs no build order at all.

/** @typedef {import('./web/protocol').RuntimeRequest} RuntimeRequest */

import { dotnet } from './_framework/dotnet.js';

// Requests that arrive before the runtime has booted. The handler is attached synchronously, on
// the first line that runs, because the page may post work the instant the worker is constructed
// — and a message delivered with no handler attached is simply dropped.
/** @type {RuntimeRequest[]} */
const pending = [];

/** @type {(request: RuntimeRequest) => void} */
let handle = request => { pending.push(request); };

self.onmessage = event => handle(event.data);

const { getAssemblyExports, getConfig } = await dotnet.create();
const exports = await getAssemblyExports(getConfig().mainAssemblyName);
const runtime = exports.Cufet.Playground.Runtime;

handle = ({ id, kind, source }) => {
    try {
        const started = performance.now();
        // ⚠ The fallthrough is `Run`, which is why `kind` is a union in protocol.ts rather than a
        // string. A misspelled kind did not fail here — it ran the visitor's program.
        const result = kind === 'check'  ? runtime.Check(source)
                     : kind === 'tokens' ? runtime.Tokens(source)
                     :                     runtime.Run(source);
        self.postMessage({ id, ok: true, result, elapsed: performance.now() - started });
    } catch (e) {
        // Run and Check both turn Cufet-level errors into ordinary return values, so anything
        // caught here is the runtime itself failing — worth reporting rather than swallowing.
        self.postMessage({ id, ok: false, error: String(e) });
    }
};

for (const request of pending) handle(request);
pending.length = 0;

self.postMessage({ ready: true });
