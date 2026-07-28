// Assembles the deployable playground into ./site.
//
// Three inputs come together here:
//   1. web/app.js     — the interface, bundled with Monaco by esbuild
//   2. web/*          — index.html and the page's own stylesheet, copied
//   3. the AppBundle  — the .NET runtime and the interpreter, produced by `dotnet publish`
//
// The publish step is NOT run from here, and that is deliberate: on the machine this was written
// on it cannot be. Publishing a browser-wasm project always relinks the native runtime through
// emscripten, and that relink fails on a space in the path — this repository lives under
// "…\My Stuff\…". So publish from a space-free copy and point this script at the result with
// --bundle=<path>. A CI checkout has no spaces, so there it is an ordinary one-liner.

import * as esbuild from 'esbuild';
import { cp, mkdir, rm, readdir, stat } from 'node:fs/promises';
import { existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const out  = join(here, 'site');

const bundleArg = process.argv.find(a => a.startsWith('--bundle='));
const appBundle = bundleArg
    ? resolve(bundleArg.slice('--bundle='.length))
    : join(here, 'bin', 'Release', 'net10.0-browser', 'browser-wasm', 'AppBundle');

await rm(out, { recursive: true, force: true });
await mkdir(out, { recursive: true });

// --- 1. the interface -------------------------------------------------------------------------

// Monaco reaches for .ttf (its icon font) and .css from inside its own modules, so both loaders
// are required even though none of our code imports either directly.
// Source maps are off by default. Monaco's are ~11 MB — larger than everything else here put
// together — and a browser only fetches them when devtools is open, so they cost nothing to a
// visitor but a great deal to the repository and the deploy. Pass --sourcemap when debugging.
const sourcemap = process.argv.includes('--sourcemap');

const shared = {
    bundle: true,
    format: 'esm',
    target: 'es2022',
    minify: true,
    sourcemap,
    loader: { '.ttf': 'file' },
    logLevel: 'info',
};

// Emitted as playground.js, NOT app.js — bundling JS that imports CSS makes esbuild write a
// stylesheet beside it under the same basename, which would land on top of the page's own
// app.css. Monaco's styles come out as playground.css and index.html links both.
await esbuild.build({
    ...shared,
    entryPoints: [join(here, 'web', 'app.js')],
    outfile: join(out, 'playground.js'),
});

// The editor's own worker. editor.api pulls in no languages, so this is the only worker the page
// needs — app.js points MonacoEnvironment.getWorkerUrl at it.
//
// Built as IIFE, not ESM. Monaco starts it with `new Worker(url)`, which creates a CLASSIC
// worker, and a classic worker cannot parse the `export` statements an ESM bundle ends with —
// it fails at parse time with nothing useful in the console.
await esbuild.build({
    ...shared,
    format: 'iife',
    entryPoints: [join(here, 'node_modules', 'monaco-editor', 'esm', 'vs', 'editor', 'editor.worker.js')],
    outfile: join(out, 'editor.worker.js'),
});

// --- 2. the page -------------------------------------------------------------------------------

for (const file of ['index.html', 'app.css'])
    await cp(join(here, 'web', file), join(out, file));

// GitHub Pages runs everything through Jekyll unless told not to, and Jekyll drops files and
// folders whose names begin with an underscore. The runtime lives in _framework/, so without
// this the deployed site is a page that cannot find .NET.
await cp(join(here, 'web', '.nojekyll'), join(out, '.nojekyll'));

// --- 3. the runtime ----------------------------------------------------------------------------

if (!existsSync(appBundle)) {
    console.error(`\nNo AppBundle at:\n  ${appBundle}\n\n` +
        `Publish it first, then re-run with --bundle=<path>:\n` +
        `  dotnet publish playground/Cufet.Playground.csproj -c Release\n`);
    process.exit(1);
}

// _framework/ is the runtime itself and can only come from the publish.
await cp(join(appBundle, '_framework'), join(out, '_framework'), { recursive: true });

// main.js is taken from SOURCE, not from the bundle, even though the bundle contains a copy of
// it. The SDK's copy is verbatim but incrementally cached, so editing main.js and republishing
// can leave a stale copy in the bundle — which is exactly what happened the first time this ran,
// and it is silent: the page loads and behaves like an older version of itself.
await cp(join(here, 'main.js'), join(out, 'main.js'));

// --- report ------------------------------------------------------------------------------------

async function totalBytes(dir) {
    let sum = 0;
    for (const entry of await readdir(dir, { withFileTypes: true })) {
        const path = join(dir, entry.name);
        sum += entry.isDirectory() ? await totalBytes(path) : (await stat(path)).size;
    }
    return sum;
}

const mb = n => `${(n / 1024 / 1024).toFixed(2)} MB`;
console.log(`\nsite/           ${mb(await totalBytes(out))} on disk`);
console.log(`  _framework/   ${mb(await totalBytes(join(out, '_framework')))}`);
console.log(`\nServe it with:  npm run serve\n`);
