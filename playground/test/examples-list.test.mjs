// The browsable example list: that it exists, that everything on it works, and — the part that
// cannot be eyeballed — that the reason things are left OFF it is still true.
//
// ★★ Two things keep an example off the list, and only one of them is visible in source.
// build.mjs scans for constructs a browser cannot do (a C axiom, launching a program), and
// carries a short list of programs that start fine and cannot FINISH — sudoku needs more stack
// than a browser has. Both are claims about behaviour, and this holds them to it: every example
// on the list must actually run, and every one left off must actually be unable to. Either half
// drifting silently is how a list rots.
//
// ⚠ Requires a built playground: `dotnet publish` then `node build.mjs`.

import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join, dirname, basename } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const playground = join(here, '..');
const repoRoot = join(playground, '..');
const site = join(playground, 'site');
const manifestPath = join(site, 'examples-manifest.json');

/** Every .cufe in the corpus, so "what was left off" can be worked out rather than assumed. */
function exampleFiles(dir = join(repoRoot, 'examples')) {
    const found = [];
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const path = join(dir, entry.name);
        if (entry.isDirectory()) found.push(...exampleFiles(path));
        else if (entry.name.endsWith('.cufe')) found.push(path);
    }
    return found;
}

let runtime;
let listed = [];
/** filename -> { verifiable, why }, exactly the claims build.mjs acted on. */
let excluded = {};

before(async () => {
    assert.ok(existsSync(join(site, '_framework')),
        'the playground is not built — run `dotnet publish Cufet.Playground.csproj -c Release` then `node build.mjs`');
    assert.ok(existsSync(manifestPath),
        'site/examples-manifest.json is missing — build.mjs did not write it');
    const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
    listed = manifest.examples;
    excluded = manifest.excluded;

    const { dotnet } = await import('../site/_framework/dotnet.js');
    const { getAssemblyExports, getConfig } = await dotnet.create();
    runtime = (await getAssemblyExports(getConfig().mainAssemblyName)).Cufet.Playground.Runtime;

    // The same files the worker places at boot, so a listed example meets the world it will meet.
    for (const path of JSON.parse(readFileSync(join(site, 'seed-manifest.json'), 'utf8')))
        runtime.PlaceFile(path, readFileSync(join(site, path), 'utf8'));
});

/**
 * The runtime's own verdict: could this program do what it set out to do here?
 *
 * ⚠ Covers BOTH reasons an example is left off. `cannot run here` is a terminal-only construct —
 * a C axiom, launching a program. `ran out of stack` is a program that starts fine and cannot
 * finish, which no source scan can see; sudoku prints a partial board and stops.
 */
const cannotDeliverHere = output =>
    /cannot run here|no processes|not supported|ran out of stack/i.test(output);

test('the list is not empty and every entry is actually served', () => {
    assert.ok(listed.length > 0, 'the example list is empty — nothing would appear in the picker');

    for (const example of listed) {
        assert.ok(existsSync(join(site, example.path)),
            `the list names ${example.path}, which was not copied into site/ — the picker would 404`);
        assert.ok(example.name && example.group,
            `${example.path} has no name or group, so it cannot be shown under a heading`);
        assert.doesNotMatch(example.name, /\.cufe$/,
            `${example.name} keeps its extension; every row would carry the same noise`);
    }
});

test('every listed example runs here', () => {
    const broken = [];
    for (const example of listed) {
        const output = runtime.Run(readFileSync(join(site, example.path), 'utf8'));
        if (cannotDeliverHere(output)) broken.push(`${example.path}: ${output.trim().split('\n').at(-1)}`);
    }

    assert.deepEqual(broken, [],
        'an example is offered in the picker that cannot do what it demonstrates here. Either a scan in '
        + 'build.mjs stopped recognising the construct, an example started needing more stack than a '
        + 'browser has, or the runtime gained a new way to refuse:\n  ' + broken.join('\n  '));
});

test('every example left off the list genuinely cannot deliver here', () => {
    // ⚠ The half nobody would check by hand. An example dropped by a scan that has quietly started
    // over-matching is invisible: the picker just has one fewer row, and it still looks fine.
    const shown = new Set(listed.map(e => basename(e.path)));
    const wronglyDropped = [];

    for (const file of exampleFiles()) {
        const name = basename(file);
        if (shown.has(name)) continue;

        // ⚠⚠ Some exclusions are JUDGEMENTS the runtime cannot check. `gameoflife.cufe` runs
        // perfectly and finishes; it is left out because its output is one board per generation
        // and the point is watching them arrive, which a buffered pane cannot show. Asserting
        // that the runtime refuses it would be asserting something false. They are marked
        // `verifiable: false` at the source rather than quietly skipped here.
        if (excluded[name]?.verifiable === false) continue;

        const output = runtime.Run(readFileSync(file, 'utf8'));
        if (!cannotDeliverHere(output)) wronglyDropped.push(name);
    }

    assert.deepEqual(wronglyDropped, [],
        'these examples run perfectly well here but are missing from the picker. Either the scan '
        + 'in build.mjs is over-matching — it matches a CONSTRUCT at line start, not the word '
        + 'anywhere, because axioms.cufe discusses C axioms without using one — or an entry in '
        + 'CANNOT_FINISH has come good and should be deleted:\n  ' + wronglyDropped.join('\n  '));
});

test('the page is wired to the list', () => {
    // ★ The lesson from `Check`, which was exported and correct and never called for the whole life
    // of the playground. A perfect manifest shows nobody anything on its own.
    const source = readFileSync(join(playground, 'web', 'app.ts'), 'utf8');

    assert.match(source, /examples-manifest\.json/,
        'app.ts never fetches the example list, so the menu would stay empty and disabled');
    assert.match(source, /example\.group/,
        'the list is grouped in the manifest but the page never reads the group, so 33 examples '
        + 'arrive as one undifferentiated column');
    assert.match(source, /aria-expanded/,
        'the menu button does not report whether it is open, so a screen reader cannot tell');

    const markup = readFileSync(join(playground, 'web', 'index.html'), 'utf8');
    assert.match(markup, /id="examples-button"/,
        'index.html has no menu button for app.ts to enable');
    assert.match(markup, /id="examples-panel"/,
        'index.html has no panel for app.ts to fill');
});

test('an exclusion the runtime cannot check still carries a reason, and still runs', () => {
    // ★ A judgement cannot be verified, but it can be kept honest in two smaller ways: it must
    // say WHY in words, and the program must still work — so if `gameoflife.cufe` ever breaks
    // outright, that is not hidden behind an exclusion made for a completely different reason.
    for (const [name, claim] of Object.entries(excluded)) {
        assert.ok(claim.why && claim.why.length > 40,
            `${name} is excluded with no real reason recorded; a bare exclusion rots into folklore`);
        if (claim.verifiable !== false) continue;

        const file = exampleFiles().find(f => basename(f) === name);
        assert.ok(file, `${name} is excluded but is not in the corpus at all — delete the entry`);
        const output = runtime.Run(readFileSync(file, 'utf8'));
        assert.doesNotMatch(output, /cannot run here|ran out of stack|That doesn't work/,
            `${name} is excluded as a presentation judgement, but it is actually FAILING now:`
            + `\n${output.trim().split('\n').slice(-2).join('\n')}`);
    }
});
