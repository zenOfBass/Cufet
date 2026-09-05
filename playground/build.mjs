// Assembles the deployable playground into ./site.
//
// Three inputs come together here:
//   1. web/app.ts     — the interface, bundled with Monaco by esbuild
//   2. web/*          — index.html and the page's own stylesheet, copied
//   3. the AppBundle  — the .NET runtime and the interpreter, produced by `dotnet publish`
//
// The publish step is NOT run from here. Publishing a browser-wasm project relinks the native
// runtime through emscripten, and that relink fails on a space anywhere in the path — so the
// AppBundle has to be produced separately, and --bundle=<path> points this script at one built
// somewhere else if the checkout cannot host it.
//
//   dotnet publish playground/Cufet.Playground.csproj -c Release
//
// ★ Which means a stale AppBundle is possible, and it is a nasty one: the page loads, runs, and
// quietly answers with an OLD interpreter — so a fix to the front end appears not to have worked
// when in fact it was never shipped. The staleness check below is there because that cost a round.

import * as esbuild from 'esbuild';
import { spawn } from 'node:child_process';
import { convertTheme, chromeStylesheet } from './build-theme.mjs';
import { cp, mkdir, readFile, rm, readdir, stat, writeFile } from 'node:fs/promises';
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

// ── Type checking ───────────────────────────────────────────────────────────────────────────
//
// ★★ esbuild STRIPS types; it does not check them. It will happily compile a file that says a
// number is a string. Without this step TypeScript would be documentation with syntax, and the
// one thing it was adopted for — the postMessage boundary, where a misspelled request kind used
// to RUN THE PROGRAM instead of failing — would go back to being unenforced.
//
// ⚠ It gates the build rather than warning. A type error that only prints is a type error that
// ships, and the whole point of the strict setting was to be told before the page is built.
await new Promise((resolveCheck, rejectCheck) => {
    // ⚠ Node itself, running tsc's entry module — NOT `npx`. On Windows, spawning a `.cmd`
    // shim without a shell fails with EINVAL (Node blocks it), and going through a shell to
    // work around that means quoting rules differ per platform for no gain. The binary is a
    // plain node script, so node can just run it.
    const tsc = spawn(
        process.execPath,
        [join(here, "node_modules", "typescript", "bin", "tsc"), "--noEmit", "--pretty", "false"],
        { cwd: here, stdio: "inherit" });
    tsc.on("error", rejectCheck);
    tsc.on("close", code => code === 0
        ? resolveCheck()
        : rejectCheck(new Error(
            "\n  TypeScript found problems (above). The bundle was NOT built." +
            "\n  Check without building:  npm run check\n")));
});

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
    entryPoints: [join(here, 'web', 'app.ts')],
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

// --- 2. highlighting: the grammar, the theme, and the regex engine ------------------------------

// The grammar is taken from the VS Code extension, not copied into the playground. One grammar,
// one source of truth — the editor and this page cannot drift apart, which is the whole reason
// for going through TextMate instead of hand-porting to Monaco's own Monarch format.
await cp(join(here, '..', 'editors', 'vscode', 'syntaxes', 'cufet.tmLanguage.json'),
         join(out, 'cufet.tmLanguage.json'));

// Oniguruma is the regex engine TextMate grammars are written against; JavaScript's own RegExp
// cannot run them (no \h, no (?i:...) inline groups, different backreference rules). It ships as
// WebAssembly and is fetched at runtime, so it is copied rather than bundled.
await cp(join(here, 'node_modules', 'vscode-oniguruma', 'release', 'onig.wasm'),
         join(out, 'onig.wasm'));

// Arctic Candy Darker by Kenan Salar, MIT, vendored under vendor/ with its licence.
const theme = await convertTheme(
    join(here, 'vendor', 'arctic-candy-dark', 'Arctic Candy Darker-color-theme.json'));

// The editor's half is fetched at runtime; the page's half is a stylesheet, so the chrome is
// correct at first paint instead of flashing a placeholder palette while JSON loads.
await writeFile(join(out, 'cufet-theme.json'), JSON.stringify(theme.monaco));
await writeFile(join(out, 'theme-chrome.css'), chromeStylesheet(theme.chrome));

console.log(`\ntheme: ${theme.monaco.rules.length} token rules, ` +
            `${Object.keys(theme.monaco.colors).length} editor colours, ` +
            `${Object.values(theme.chrome).filter(Boolean).length} chrome variables`);

// --- 3. the typeface ---------------------------------------------------------------------------

// JetBrains Mono, SIL Open Font Licence 1.1, vendored rather than fetched from a font CDN for the
// same reason Monaco is: a language's front door should not stop looking right because someone
// else's server is having a bad day.
//
// Only the LATIN subset, and only woff2 — every browser that can run WebAssembly can read woff2,
// so the .woff fallbacks would be dead weight. Four faces are needed rather than one because the
// theme genuinely uses them: comments are italic and constants are bold.
const FONT_DIR = join(here, 'node_modules', '@fontsource', 'jetbrains-mono', 'files');
const FONT_FILES = [
    'jetbrains-mono-latin-400-normal.woff2',
    'jetbrains-mono-latin-400-italic.woff2',
    'jetbrains-mono-latin-700-normal.woff2',
    'jetbrains-mono-latin-700-italic.woff2',
];

await mkdir(join(out, 'fonts'), { recursive: true });
for (const file of FONT_FILES) {
    // Named explicitly, and missing is an ERROR rather than a silent skip. A dropped face would
    // otherwise fall back to a system monospace mid-page, which looks like a rendering bug and is
    // exactly the kind of thing nobody notices until it ships.
    if (!existsSync(join(FONT_DIR, file)))
        throw new Error(`font file missing: ${file}\nRun npm install, or update FONT_FILES if @fontsource renamed it.`);
    await cp(join(FONT_DIR, file), join(out, 'fonts', file));
}

// --- 4. the page -------------------------------------------------------------------------------

for (const file of ['index.html', 'app.css'])
    await cp(join(here, 'web', file), join(out, file));

// The rabbit, as artwork rather than as the character U+1F407. A character is drawn by whatever
// emoji font the visitor's OS ships, so the one mark on the page was a different animal on Windows,
// Android and iOS. Shipping Noto's own SVG pins it. Copied from vendor/ rather than kept in web/ so
// the asset has one home, next to the licence it arrived with.
await cp(join(here, 'vendor', 'noto-emoji', 'emoji_u1f407.svg'), join(out, 'rabbit.svg'));

// GitHub Pages runs everything through Jekyll unless told not to, and Jekyll drops files and
// folders whose names begin with an underscore. The runtime lives in _framework/, so without
// this the deployed site is a page that cannot find .NET.
await cp(join(here, 'web', '.nojekyll'), join(out, '.nojekyll'));

// --- 5. the runtime ----------------------------------------------------------------------------

if (!existsSync(appBundle)) {
    console.error(`\nNo AppBundle at:\n  ${appBundle}\n\n` +
        `Publish it first, then re-run with --bundle=<path>:\n` +
        `  dotnet publish playground/Cufet.Playground.csproj -c Release\n`);
    process.exit(1);
}

// The same silent-staleness trap as worker.js below, one level up: the bundle carries a COMPILED
// interpreter, so a source change that has not been republished ships as if it had been. Comparing
// the built assembly against the newest source file catches it. A warning rather than a failure —
// a bundle built elsewhere legitimately has whatever timestamps the copy gave it.
const builtAt = (await stat(join(appBundle, '_framework', 'Cufet.Interpreter.wasm'))).mtimeMs;
const newestSource = await newestFileTime(join(here, '..', 'src'));
if (newestSource > builtAt)
    console.warn(
        '\n  ⚠ the AppBundle is OLDER than src/ — the page will run a stale interpreter.\n' +
        '    dotnet publish playground/Cufet.Playground.csproj -c Release\n');

// _framework/ is the runtime itself and can only come from the publish.
await cp(join(appBundle, '_framework'), join(out, '_framework'), { recursive: true });

// worker.js is taken from SOURCE, not from the bundle, even though the bundle contains a copy of
// it. The SDK's copy is verbatim but incrementally cached, so editing it and republishing can
// leave a stale copy in the bundle — which is exactly what happened the first time this ran, and
// it is silent: the page loads and behaves like an older version of itself.
await cp(join(here, 'worker.js'), join(out, 'worker.js'));

// --- the files the examples read ----------------------------------------------------------------
//
// ★★ There IS a filesystem in the browser — Emscripten gives the runtime an in-memory one and
// .NET's File APIs sit on it. Measured, before any of this was written: a Cufet program can write
// a file and read it back under wasm, and listing, appending and existence checks all work.
//
// It just starts EMPTY. `examples/parsing/config.cufe` and `examples/algorithms/wordfreq.cufe` read
// files that exist in the repository and not in a browser, so both met a truthful `not found` and
// could not demonstrate themselves on the page they exist to demonstrate on.
//
// ★ Copied as ORDINARY STATIC FILES, not embedded in the wasm. Embedding would put them in the
// download every visitor pays for, whether or not they run those two examples, and would mean a
// `dotnet publish` to change a text file. Served like this they are cached separately and the
// payload does not move.
//
// ⚠ The path under site/ MATCHES the path a program asks for, deliberately. An example says
// `read all from the file "examples/assets/config.txt"` because that is where the file is in a
// checkout; serving it at the same relative path means the manifest is a plain list and there is
// no mapping table to get wrong.
const assetsFrom = join(here, "..", "examples", "assets");
const assetPaths = [];
if (existsSync(assetsFrom)) {
    const assetsTo = join(out, "examples", "assets");
    await mkdir(assetsTo, { recursive: true });
    for (const entry of await readdir(assetsFrom, { withFileTypes: true })) {
        if (!entry.isFile()) continue;
        await cp(join(assetsFrom, entry.name), join(assetsTo, entry.name));
        assetPaths.push(`examples/assets/${entry.name}`);
    }
}

// The worker reads this at boot and seeds each path before it reports ready. A list rather than a
// hard-coded set in the worker: build.mjs is what knows which files it copied, and a file added to
// examples/assets/ should reach the page without anyone editing JavaScript to allow it.
// ★★ And the BOOKS an example pulls, which is a different placement for a different reason.
//
// `Pull a bookkeeping.` reaches `bookkeeping.cufe` in the SOURCE DIRECTORY — beside the program
// that pulls it. A pasted program has no directory of its own, so the runtime's working directory
// plays that part and a book belongs at its root, NOT at the path it happens to occupy in a
// checkout. That is why these are seeded by basename while the assets above keep their folders:
// an asset is addressed by the path a program writes, a book by the name a program pulls.
//
// ⚠ DERIVED from how a module DECLARES itself, anchored at column 0. Matching `and module`
// anywhere would also catch ledger.cufe, which only mentions the phrase in its opening comment —
// and seeding the program that does the pulling would be silently pointless.
const DECLARES_A_MODULE = /^Define object [\w-]+ .*\band (module|book)\b/m;

async function cufeFilesUnder(dir) {
    const found = [];
    for (const entry of await readdir(dir, { withFileTypes: true })) {
        const path = join(dir, entry.name);
        if (entry.isDirectory()) found.push(...await cufeFilesUnder(path));
        else if (entry.name.endsWith(".cufe")) found.push(path);
    }
    return found;
}

const examplesDir = join(here, "..", "examples");
if (existsSync(examplesDir)) {
    for (const path of await cufeFilesUnder(examplesDir)) {
        if (!DECLARES_A_MODULE.test(await readFile(path, "utf8"))) continue;
        const name = path.split(/[\\/]/).pop();
        await cp(path, join(out, name));
        assetPaths.push(name);
    }
}

await writeFile(join(out, "seed-manifest.json"), JSON.stringify(assetPaths, null, 2) + "\n");
console.log(`seeded:         ${assetPaths.length} file(s) the examples read`);

// --- report ------------------------------------------------------------------------------------

async function totalBytes(dir) {
    let sum = 0;
    for (const entry of await readdir(dir, { withFileTypes: true })) {
        const path = join(dir, entry.name);
        sum += entry.isDirectory() ? await totalBytes(path) : (await stat(path)).size;
    }
    return sum;
}

// bin/ and obj/ are skipped: they hold build OUTPUT, which is newer than the bundle by definition
// and would report every build as stale.
async function newestFileTime(dir) {
    let newest = 0;
    for (const entry of await readdir(dir, { withFileTypes: true })) {
        if (entry.name === 'bin' || entry.name === 'obj') continue;
        const path = join(dir, entry.name);
        const when = entry.isDirectory() ? await newestFileTime(path) : (await stat(path)).mtimeMs;
        if (when > newest) newest = when;
    }
    return newest;
}

const mb = n => `${(n / 1024 / 1024).toFixed(2)} MB`;
console.log(`\nsite/           ${mb(await totalBytes(out))} on disk`);
console.log(`  _framework/   ${mb(await totalBytes(join(out, '_framework')))}`);
console.log(`\nServe it with:  npm run serve\n`);
