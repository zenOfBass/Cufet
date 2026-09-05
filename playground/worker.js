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

// ⚠⚠ INTEGRITY CHECKING IS OFF, and it is not a shortcut — it is the fix for a ten-minute
// outage after every single deploy. Measured 2026-09-05, from the console of the deployed page:
//
//     Failed to find a valid digest in the 'integrity' attribute for resource
//     '.../Cufet.Interpreter.wasm' ... The resource has been blocked.
//
// GitHub Pages serves everything with `Cache-Control: max-age=600`. After a deploy the browser
// holds the PREVIOUS `dotnet.boot.js` for up to those 600 seconds while fetching the NEW `.wasm`
// files, so the old manifest's SHA-256 values are checked against new bytes and every one of our
// assemblies is blocked. 600 seconds is exactly the ten to eleven minutes the page was dead for.
//
// ★ Only OUR assemblies ever failed, which is what identified it: the framework ones are
// byte-identical build to build, so a stale manifest still describes them correctly.
//
// ⚠ What this gives up: SRI defends against the HOST serving tampered assemblies. Transport is
// still authenticated by HTTPS, this page carries no credentials and no user data, and the
// alternative is a public playground that is reliably broken for ten minutes after every push.
// Blazor ships the same trade as `BlazorCacheBootResources=false` for the same reason.
//
// ★ One line to revert. The other route — keeping SRI and making the manifest uncacheable with a
// per-build query on `withConfigSrc` — fixes the direction observed here but not the mirror case
// (a fresh manifest against stale assemblies), and needs a build stamp threaded into this file.
const { getAssemblyExports, getConfig } = await dotnet
    .withConfig({ disableIntegrityCheck: true })
    .create();
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
        //
        // ⚠⚠ Emscripten throws an `ExitStatus` OBJECT, not an Error, and `String(e)` on it is
        // literally "[object Object]" — which is what a visitor saw, in red, on running
        // sudoku.cufe. The text is on `.message` ("Program terminated with exit(1)").
        //
        // ⚠ `.status` being a number is the runtime saying it has EXITED. Measured: every later
        // run then throws too, so the page must replace this worker rather than keep asking a
        // corpse. Reported rather than decided here — the worker cannot restart itself.
        const thrown = /** @type {{ message?: unknown, status?: unknown }} */ (e);
        self.postMessage({
            id,
            ok: false,
            error: String(thrown?.message ?? e),
            fatal: typeof thrown?.status === "number",
        });
    }
};

// ── The files the examples read ──────────────────────────────────────────────────────────────
//
// ★★ The filesystem is ALREADY here. Emscripten gives the runtime an in-memory one and .NET sits
// on it — measured: a Cufet program can write a file and read it back under wasm, and listing,
// appending and existence checks all work. It just starts empty, so an example reading
// `examples/assets/config.txt` met a truthful `not found`. This puts those files in.
//
// ⚠⚠ BEFORE `ready`, and that ordering is the whole point. The page auto-runs the starter program
// the instant it is told the runtime is up, and a visitor can pick an example immediately after.
// Seeding afterwards would leave a window where a program looks for a file that is on its way —
// a race that would show up as an example failing sometimes.
//
// ★ Failure is swallowed on purpose. Running Cufet is the point and these two files are a nicety;
// if the manifest or a file cannot be fetched, the page still works and those examples fail
// exactly as they did before. The console says what happened.
// ⚠⚠ A DEADLINE, because this sits on the critical path to `ready` and a nicety must never be
// able to hold the interpreter hostage. Seeding blocks `ready` on purpose — the page auto-runs
// the instant it hears it, so seeding afterwards is a race — but 'on purpose' is not 'at any
// cost'. Past this, the worker reports ready anyway and says in the console what it could not
// place; the two examples that read files then fail exactly as they did before seeding existed.
//
// ★ Generous on purpose. Measured against GitHub Pages these are four small files at ~0.1s each,
// so five seconds is ~12x the healthy case: long enough that a slow connection still gets its
// files, short enough that nobody sits looking at a dead Run button wondering.
const SEED_DEADLINE_MS = 5000;

async function placeSeedFiles() {
    /** @type {string[]} */
    let paths = [];
    try {
        const response = await fetch("./seed-manifest.json", { signal: AbortSignal.timeout(SEED_DEADLINE_MS) });
        if (!response.ok) throw new Error(`seed-manifest.json: ${response.status}`);
        paths = await response.json();
    } catch (e) {
        console.warn("no seed manifest — examples that read a file will not find one:", e);
        return;
    }

    await Promise.all(paths.map(async path => {
        try {
            const file = await fetch(`./${path}`, { signal: AbortSignal.timeout(SEED_DEADLINE_MS) });
            if (!file.ok) throw new Error(`${path}: ${file.status}`);
            const failure = runtime.PlaceFile(path, await file.text());
            if (failure) console.warn(`could not place ${path}:`, failure);
        } catch (e) {
            console.warn(`could not fetch ${path}:`, e);
        }
    }));
}

// ⚠ The per-fetch aborts above cap each request; this caps the WHOLE step. Without it, four
// requests each finishing just inside their own deadline could still add up to a long wait.
let seeded = false;
await Promise.race([
    placeSeedFiles().then(() => { seeded = true; }),
    new Promise(resolve => setTimeout(resolve, SEED_DEADLINE_MS)),
]);
if (!seeded)
    console.warn(
        `seeding did not finish within ${SEED_DEADLINE_MS} ms — starting anyway. Examples that `
        + `read a file may report it as not found.`);

for (const request of pending) handle(request);
pending.length = 0;

self.postMessage({ ready: true });
