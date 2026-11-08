// The browsable example list: that it exists, and that everything on it works.
//
// ★★ What is on the list is a decision, written out in build.mjs. A file does not reach the menu by
// being in examples/, so being left off says nothing about whether a program runs. What can be
// checked is the other half: a list PROMISES its entries work, so each one has to.
//
// ⚠ Requires a built playground: `dotnet publish` then `node build.mjs`.

import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const playground = join(here, '..');
const repoRoot = join(playground, '..');
const site = join(playground, 'site');
const manifestPath = join(site, 'examples-manifest.json');

let runtime;
let listed = [];

before(async () => {
    assert.ok(existsSync(join(site, '_framework')),
        'the playground is not built — run `dotnet publish Cufet.Playground.csproj -c Release` then `node build.mjs`');
    assert.ok(existsSync(manifestPath),
        'site/examples-manifest.json is missing — build.mjs did not write it');
    const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
    listed = manifest.examples;

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

test('every listed example shows something when it runs', () => {
    // ★ A book or a module only declares, so it finishes cleanly and prints nothing. To a visitor
    // who picked it and pressed Run, that is a broken page.
    const silent = listed.filter(e => runtime.Run(readFileSync(join(site, e.path), 'utf8')).trim() === '');
    assert.deepEqual(silent.map(e => e.path), [], 'on the menu, and Run shows nothing');
});

test('what was ruled off the menu stays off it', () => {
    // ★ Both Games of Life, by name: the first exclusion was by FILENAME, and the second program
    // walked straight past it. And no piece of a project, which cannot be judged on its own.
    const ruledOff = new Set(['gameoflife', 'life']);
    const projects = new Set(['circuits']);
    const offending = listed.filter(e => ruledOff.has(e.name) || projects.has(e.group)).map(e => e.path);
    assert.deepEqual(offending, [], 'on the menu, and ruled off it');
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
