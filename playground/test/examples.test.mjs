// The playground's only automated test, and it guards the one thing about it that is quiet when
// it breaks: whether a program running in a browser can find the files it reads.
//
// ★★ WHY THIS AND NOT MORE. The playground's front end has no test at all — there is no DOM here,
// and asserting on Monaco would mostly assert on Monaco. What IS testable without a browser is the
// runtime surface the page talks to, and the seeding path is the part with a silent failure mode:
// if build.mjs stops copying the assets, or worker.js stops placing them, or PlaceFile stops
// creating directories, nothing throws. The examples simply go back to reporting `not found` — a
// truthful message about a file that should have been there, which reads like the program's fault.
//
// ⚠ The set of examples is DERIVED, never listed. An example counts if its source reads a file, so
// a new one that reads a file is covered the day it is written and nobody has to remember a list.
// A hard-coded list here would rot exactly as quietly as the thing it is guarding.
//
// ⚠ Requires a built playground: `dotnet publish` then `node build.mjs`. The runtime under test is
// the real wasm build, not a stand-in — see the node harness notes in the repo's memory.

import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const playground = join(here, '..');
const repoRoot = join(playground, '..');
const site = join(playground, 'site');

/** Trailing-newline and line-ending differences are not what this test is about. */
const norm = s => s.replace(/\r\n/g, '\n').replace(/\n+$/, '');

/** Every .cufe under examples/, recursively. */
function exampleFiles(dir = join(repoRoot, 'examples')) {
    const found = [];
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const path = join(dir, entry.name);
        if (entry.isDirectory()) found.push(...exampleFiles(path));
        else if (entry.name.endsWith('.cufe')) found.push(path);
    }
    return found;
}

// ★ The property that makes an example relevant here, asked of its SOURCE: does it need a file to
// already exist? Three forms do — the two whole-file reads, and a stream opened FOR READING.
//
// ⚠ `open for writing` and `open for appending` are deliberately excluded: they CREATE the file, so
// an empty filesystem is no obstacle and including them would assert nothing while looking as
// though it did. ⚠ The first draft of this regex covered only the whole-file reads and silently
// missed wordfreq.cufe, which uses the stream form — a derivation can be wrong in exactly the way
// a hard-coded list can, and the only defence is checking what it actually matched.
const READS_A_FILE = /read all (lines )?from the file|open for reading/;

let runtime;
const placed = [];

before(async () => {
    assert.ok(existsSync(join(site, '_framework')),
        'the playground is not built — run `dotnet publish Cufet.Playground.csproj -c Release` then `node build.mjs`');

    const { dotnet } = await import('../site/_framework/dotnet.js');
    const { getAssemblyExports, getConfig } = await dotnet.create();
    const exports = await getAssemblyExports(getConfig().mainAssemblyName);
    runtime = exports.Cufet.Playground.Runtime;

    // Exactly what worker.js does at boot, in the same order. If this diverges from the worker the
    // test stops testing the thing that ships — so it reads the same manifest rather than its own
    // list of files.
    const manifest = JSON.parse(readFileSync(join(site, 'seed-manifest.json'), 'utf8'));
    for (const path of manifest) {
        const failure = runtime.PlaceFile(path, readFileSync(join(site, path), 'utf8'));
        assert.equal(failure, '', `PlaceFile refused ${path}: ${failure}`);
        placed.push(path);
    }
});

test('the build produced a seed manifest naming files that exist', () => {
    const manifestPath = join(site, 'seed-manifest.json');
    assert.ok(existsSync(manifestPath), 'site/seed-manifest.json is missing — build.mjs did not write it');

    const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
    assert.ok(manifest.length > 0, 'the manifest is empty — no assets were copied into site/');

    // ⚠ A manifest naming a file the server does not have is worse than no manifest: the worker
    // fetches, gets a 404, warns to a console nobody reads, and the example fails as if seeding had
    // never been added.
    for (const path of manifest)
        assert.ok(existsSync(join(site, path)), `the manifest names ${path}, which is not in site/`);
});

// ── One test per example that reads a file ───────────────────────────────────────────────────

const readers = exampleFiles().filter(f => READS_A_FILE.test(readFileSync(f, 'utf8')));

test('some example actually reads a file', () => {
    // Guards the derivation itself. If the two forms above are ever renamed, `readers` silently
    // empties and every test below passes by having nothing to check — the exact failure this file
    // exists to prevent, reproduced in the test.
    assert.ok(readers.length > 0,
        'no example matched the file-reading forms — has the syntax changed? This test is now inert.');
});

// ★★ And the examples that pull a BOOK from another file, which is the module system — a shipped
// feature the playground could not demonstrate at all until the runtime was given a source
// directory to look in. `ledger.cufe` failed on the page that exists to show it off.
//
// ⚠ Derived from the SEEDED BOOKS, not from a list: a name counts if a `<name>.cufe` module was
// placed, and an example counts if it pulls that name. Add a multi-file example and it is covered.
// ⚠⚠ Read from the MANIFEST FILE, not from `placed`. `placed` is filled in `before()`, which runs
// AFTER this module body — so deriving from it produced an empty set and every test below simply
// did not exist. It looked green. This is the second time that shape has bitten in this file, and
// the guard below is why it was caught at all.
const seededBooks = JSON.parse(readFileSync(join(site, "seed-manifest.json"), "utf8"))
    .filter(path => path.endsWith(".cufe"))
    .map(path => path.replace(/\.cufe$/, ""));

const pullers = exampleFiles().filter(file => {
    const source = readFileSync(file, "utf8");
    // ⚠ `\\b`, not `\b`. Inside a TEMPLATE LITERAL a single backslash-b is the
    // BACKSPACE character, so the pattern quietly looked for a control code, matched nothing, and
    // the derived set came out empty — green, and testing nothing.
    return seededBooks.some(book => new RegExp(`Pull (a |books? on )?${book}\\b`).test(source));
});

test("a seeded book is actually pulled by some example", () => {
    // The invariant, in both directions: a book is only worth placing if something pulls it, and a
    // book that IS placed must have coverage. Phrased as an implication so removing the last
    // multi-file example is not a spurious failure — it just means nothing is seeded either.
    if (seededBooks.length === 0) return;
    assert.ok(pullers.length > 0,
        `books were seeded (${seededBooks.join(", ")}) but no example pulls them — either the pull `
        + "spelling changed and this derivation is now inert, or build.mjs is seeding files nothing needs.");
});

for (const file of readers) {
    const name = basename(file);

    test(`${name} finds its files in the playground`, () => {
        const output = runtime.Run(readFileSync(file, 'utf8'));

        // The exact regression: seeding stops working and a truthful `not found` reads like the
        // program's fault rather than the page's.
        assert.doesNotMatch(output, /was not found/,
            `${name} could not find a file it reads. Seeded: ${placed.join(', ') || '(nothing)'}`);
        assert.doesNotMatch(output, /could not read/,
            `${name} could not read a file. Seeded: ${placed.join(', ') || '(nothing)'}`);
    });

    // ★ Opt-in, exactly like the oracle's own pins: no `.expected`, no assertion. Where one exists
    // it is the same file the compiler suite compares against, so the playground is held to the
    // output the language already promises rather than to a second expectation that could drift.
    const expected = join(repoRoot, 'examples', 'expected', name.replace(/\.cufe$/, '.expected'));
    if (!existsSync(expected)) continue;

    test(`${name} produces its recorded output in the playground`, () => {
        assert.equal(norm(runtime.Run(readFileSync(file, 'utf8'))), norm(readFileSync(expected, 'utf8')));
    });
}

for (const file of pullers) {
    const name = basename(file);

    test(`${name} can pull the book it needs`, () => {
        const output = runtime.Run(readFileSync(file, "utf8"));
        assert.doesNotMatch(output, /there is nothing named .* to pull/,
            `${name} could not reach a book in another file. Books placed: ${seededBooks.join(", ") || "(none)"}. `
            + "Either the book was not seeded, or Runtime.cs stopped setting SourceDirectory.");
    });

    const expected = join(repoRoot, "examples", "expected", name.replace(/\.cufe$/, ".expected"));
    if (!existsSync(expected)) continue;

    test(`${name} produces its recorded output across two files`, () => {
        assert.equal(norm(runtime.Run(readFileSync(file, "utf8"))), norm(readFileSync(expected, "utf8")));
    });
}

// ⚠ Everything above shares ONE wasm process, because booting costs a couple of seconds and none of
// these programs is known to kill the runtime. If one ever does, every test after it fails for a
// reason that has nothing to do with what it was asserting — so this asks, last, whether the
// runtime is still answering at all, and says so plainly when it is not.
test('the runtime survived every example', () => {
    assert.equal(norm(runtime.Run('State "still here".')), 'still here',
        'an earlier example killed the wasm runtime — results above this line cannot be trusted');
});
