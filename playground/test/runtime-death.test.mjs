// What the runtime throws when a program KILLS it, and why the worker special-cases it.
//
// ★★ A visitor ran `examples/algorithms/sudoku.cufe` and the page showed, in red, `[object Object]`.
// Two separate faults behind one symptom:
//
//  1. Emscripten throws an `ExitStatus` OBJECT, not an `Error`. `String(e)` on it is literally
//     "[object Object]"; the text lives on `.message`. The worker was doing `String(e)`.
//  2. The runtime was then GONE — measured: every later run throws too — while the page kept
//     `booted` true and went on asking a corpse. One deep program bricked the playground until a
//     reload.
//
// This pins the two facts the fix rests on: the message is reachable, and the exit is detectable.
// If Emscripten ever throws a real Error instead, the worker's special-casing becomes dead weight
// and this test is where that shows up.
//
// ⚠⚠ IN ITS OWN PROCESS, and that is not tidiness. Killing the runtime poisons it for everything
// afterwards — the whole reason the example sweep runs one process per program. Put this in
// examples.test.mjs and every test after it fails for reasons unrelated to what it asserts.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const playground = join(here, '..');
const site = join(playground, 'site');
const sudoku = join(playground, '..', 'examples', 'algorithms', 'sudoku.cufe');

test('a program that kills the runtime is reportable, and the exit is detectable', () => {
    assert.ok(existsSync(join(site, '_framework')),
        'the playground is not built — run `dotnet publish Cufet.Playground.csproj -c Release` then `node build.mjs`');

    // ⚠ `sudoku.cufe` is the one example known to do this, and it is deliberately not asserted to
    // FAIL — if a future change lets it complete, that is good news and this test says so plainly
    // rather than turning an improvement into a red suite.
    const script = `
        import { dotnet } from ${JSON.stringify(pathToFileURL(join(site, '_framework', 'dotnet.js')).href)};
        import { readFileSync } from "node:fs";
        const { getAssemblyExports, getConfig } = await dotnet.create();
        const ex = await getAssemblyExports(getConfig().mainAssemblyName);
        try {
            ex.Cufet.Playground.Runtime.Run(readFileSync(${JSON.stringify(sudoku)}, "utf8"));
            console.log(JSON.stringify({ survived: true }));
        } catch (e) {
            // Exactly the two expressions worker.js uses, so this tests the contract it relies on.
            console.log(JSON.stringify({
                survived: false,
                viaString: String(e),
                viaMessage: String(e?.message ?? e),
                exited: typeof e?.status === "number",
            }));
        }`;

    // ⚠ A NON-ZERO EXIT IS THE NORMAL CASE HERE. The wasm runtime aborting takes the host node
    // process down with it, so execFileSync throws even though the probe already printed its
    // answer on stdout. Reading stdout off the thrown error is the point, not a fallback.
    let printed;
    try {
        printed = execFileSync(process.execPath, ['--input-type=module', '--eval', script], {
            encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'],
        });
    } catch (failure) {
        printed = String(failure.stdout ?? '');
    }

    const line = printed.trim().split('\n').filter(l => l.startsWith('{')).at(-1);
    assert.ok(line, `the probe printed nothing usable:\n${printed}`);
    const result = JSON.parse(line);

    if (result.survived) {
        console.log('note: sudoku.cufe no longer kills the runtime — the worker\'s exit handling is now unexercised.');
        return;
    }

    // The fault the visitor saw. Kept as an assertion rather than a comment because it is the whole
    // reason `.message` is preferred, and it would come back the moment someone "simplifies" that.
    assert.equal(result.viaString, '[object Object]',
        'String(e) no longer produces [object Object] — Emscripten may throw a real Error now, and '
        + 'worker.js could stop special-casing it.');

    assert.notEqual(result.viaMessage, '[object Object]',
        'the message the worker reports is still [object Object] — the visitor learns nothing');
    assert.match(result.viaMessage, /\S/, 'the reported message is blank');

    assert.equal(result.exited, true,
        'the thrown value carries no numeric .status, so worker.js cannot tell the runtime EXITED — '
        + 'the page would keep asking a dead runtime instead of replacing the worker');
});

// ⚠ The test above pins what EMSCRIPTEN does; this pins what WE do about it. Without this one,
// reverting the worker to `String(e)` brings `[object Object]` straight back and every test still
// passes — the contract would be verified and the use of it unguarded.
test('worker.js reports the message and detects the exit', () => {
    const source = readFileSync(join(playground, 'worker.js'), 'utf8');

    assert.doesNotMatch(source, /error:\s*String\(e\)/,
        'worker.js is back to String(e) on the caught host failure, which renders an ExitStatus as '
        + '"[object Object]" — the exact thing a visitor saw in red. Report `.message` instead.');

    assert.match(source, /\.message/,
        'worker.js no longer reads .message off the thrown value, so a runtime death reports nothing useful');

    assert.match(source, /status === "number"/,
        'worker.js no longer detects that the runtime EXITED, so the page cannot know to replace the '
        + 'worker and will keep asking a dead runtime');
});
