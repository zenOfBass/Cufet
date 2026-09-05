// The squiggles: that `Check` answers, and that the page actually asks.
//
// ★★ WHY BOTH HALVES. `Check` was exported when the playground was built and NOTHING EVER CALLED
// IT — the machinery worked, was tested by nobody, and the page had no squiggles for its entire
// life. A test that only exercised the export would have passed happily throughout. So one test
// asks the runtime, and one asks whether the page is wired to it.
//
// ⚠ Requires a built playground: `dotnet publish` then `node build.mjs`.

import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const playground = join(here, '..');
const site = join(playground, 'site');

let runtime;

before(async () => {
    assert.ok(existsSync(join(site, '_framework')),
        'the playground is not built — run `dotnet publish Cufet.Playground.csproj -c Release` then `node build.mjs`');
    const { dotnet } = await import('../site/_framework/dotnet.js');
    const { getAssemblyExports, getConfig } = await dotnet.create();
    runtime = (await getAssemblyExports(getConfig().mainAssemblyName)).Cufet.Playground.Runtime;
});

/** Check's output is one JSON object per line — the shape `cufet check --json` emits. */
function diagnose(source) {
    return runtime.Check(source)
        .split('\n')
        .map(line => line.trim())
        .filter(line => line.startsWith('{'))
        .map(line => JSON.parse(line));
}

test('a clean program has nothing to say', () => {
    assert.deepEqual(diagnose('State "hi".'), []);
});

test('an error is reported on the line it is on', () => {
    const [first, ...rest] = diagnose('Define n as 1.\nState n + "text".');
    assert.ok(first, 'a program that cannot type-check reported no diagnostic at all');
    assert.equal(first.severity, 'error');
    assert.equal(first.line, 2);
    assert.match(first.message, /\S/);
    // ⚠ The front end throws on the FIRST problem, so there is never a second. Asserting it keeps
    // anyone from building a page that expects a list and quietly shows one of many.
    assert.deepEqual(rest, [], 'Check reported more than one error; the front end stops at the first');
});

test('a style warning is reported, and as a warning', () => {
    // ★★ This is the half that was missing entirely, not merely unused: the old Check returned only
    // the first EXCEPTION, so the linter's advice — a statement should read as a sentence and start
    // with a capital — could not reach the page at all, however the page asked.
    const [warning] = diagnose('Define total as 1.\nstate total.');
    assert.ok(warning, 'the linter had nothing to say about a lowercase statement opener');
    assert.equal(warning.severity, 'warning');
    assert.equal(warning.line, 2);
    assert.match(warning.message, /write 'State'/);
});

test('style is judged only on a program that type-checks', () => {
    // ⚠ The CLI's rule, and the reason for it in its own words: advising someone on how a line reads
    // while it is still wrong would bury the thing they actually need to fix. This program has both
    // faults — a lowercase opener AND a type error — and must report only the error.
    const reported = diagnose('Define n as 1.\nstate n + "text".');
    assert.equal(reported.length, 1, 'a broken program reported style advice alongside the error');
    assert.equal(reported[0].severity, 'error');
});

test('the page asks for diagnostics, on boot and on edit', () => {
    // ★ The original bug in one assertion. `Check` can be perfect and the page still show nothing,
    // which is exactly what shipped for the playground's whole life until now.
    const source = readFileSync(join(playground, 'web', 'app.ts'), 'utf8');

    assert.match(source, /askRuntime\('check'/,
        'app.ts never asks the runtime to check anything — Check is exported and unused, which is '
        + 'the state this feature was built to leave behind');

    assert.match(source, /setModelMarkers/,
        'app.ts gets diagnostics but never turns them into Monaco markers, so nothing is drawn');

    assert.match(source, /onDidChangeModelContent/,
        'nothing re-checks when the program changes, so the marks would be from whatever was in the '
        + 'editor at boot');
});
