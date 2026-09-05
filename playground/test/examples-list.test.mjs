// The browsable example list: that it exists, that everything on it works, and — the part that
// cannot be eyeballed — that the reason things are left OFF it is still true.
//
// ★★ The list is derived by build.mjs scanning source for constructs a browser cannot do (a C
// axiom, launching a program). A scan is a guess about behaviour, and this holds it to the real
// thing: every example on the list must actually run, and every one left off must actually be
// unable to. Either half drifting silently is how a list rots.
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

before(async () => {
    assert.ok(existsSync(join(site, '_framework')),
        'the playground is not built — run `dotnet publish Cufet.Playground.csproj -c Release` then `node build.mjs`');
    assert.ok(existsSync(manifestPath),
        'site/examples-manifest.json is missing — build.mjs did not write it');
    listed = JSON.parse(readFileSync(manifestPath, 'utf8'));

    const { dotnet } = await import('../site/_framework/dotnet.js');
    const { getAssemblyExports, getConfig } = await dotnet.create();
    runtime = (await getAssemblyExports(getConfig().mainAssemblyName)).Cufet.Playground.Runtime;

    // The same files the worker places at boot, so a listed example meets the world it will meet.
    for (const path of JSON.parse(readFileSync(join(site, 'seed-manifest.json'), 'utf8')))
        runtime.PlaceFile(path, readFileSync(join(site, path), 'utf8'));
});

/** The runtime's own verdict: could this program do what it set out to do here? */
const needsATerminal = output => /cannot run here|no processes|not supported/i.test(output);

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
        if (needsATerminal(output)) broken.push(`${example.path}: ${output.trim().split('\n').at(-1)}`);
    }

    assert.deepEqual(broken, [],
        'an example is offered in the picker that cannot do what it demonstrates here. Either the '
        + 'scan in build.mjs stopped recognising the construct, or the runtime gained a new way to '
        + 'refuse:\n  ' + broken.join('\n  '));
});

test('every example left off the list genuinely needs a terminal', () => {
    // ⚠ The half nobody would check by hand. An example dropped by a scan that has quietly started
    // over-matching is invisible: the picker just has one fewer row, and it still looks fine.
    const shown = new Set(listed.map(e => basename(e.path)));
    const wronglyDropped = [];

    for (const file of exampleFiles()) {
        const name = basename(file);
        if (shown.has(name)) continue;

        const output = runtime.Run(readFileSync(file, 'utf8'));
        if (!needsATerminal(output)) wronglyDropped.push(name);
    }

    assert.deepEqual(wronglyDropped, [],
        'these examples run perfectly well here but are missing from the picker — build.mjs\'s scan '
        + 'is over-matching. It matches a CONSTRUCT at the start of a line, not the word anywhere, '
        + 'because axioms.cufe discusses C axioms without using one:\n  ' + wronglyDropped.join('\n  '));
});

test('the page is wired to the list', () => {
    // ★ The lesson from `Check`, which was exported and correct and never called for the whole life
    // of the playground. A perfect manifest shows nobody anything on its own.
    const source = readFileSync(join(playground, 'web', 'app.ts'), 'utf8');

    assert.match(source, /examples-manifest\.json/,
        'app.ts never fetches the example list, so the picker would stay empty and disabled');
    assert.match(source, /optgroup/,
        'the list is grouped in the manifest but the page does not build groups, so the headings are lost');

    const markup = readFileSync(join(playground, 'web', 'index.html'), 'utf8');
    assert.match(markup, /id="examples"/,
        'index.html has no picker for app.ts to fill');
});
