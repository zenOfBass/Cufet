// Every example, through the playground's own runtime, looking for the one thing a page like this
// must never do: leak a message that was not written for a person.
//
// ★★ WHY THIS EXISTS. A visitor ran a program and the page answered `Process_PlatformNotSupported`
// — a .NET resource key, in a language whose entire claim is that its messages are readable. It was
// found by sweeping the corpus by hand, and nothing would have caught the next one. Now something
// does, before the deploy.
//
// ⚠ The assertions are DELIBERATELY WEAK, and that is not laziness. Most examples cannot produce
// their command-line output in a browser and should not be expected to: `foreign.cufe` says it
// cannot run C here, the subprocess and task examples have no processes to start. Holding those to
// the CLI's output would mean either pinning browser-specific expectations — a second set of files
// to drift — or excluding them, which is the coverage this is trying to add. So the sweep asks only
// what is true of EVERY example: no leaked host vocabulary, and no unexpected death. Exact output is
// still compared where a `.expected` exists, in examples.test.mjs.
//
// ⚠⚠ ONE PROCESS PER EXAMPLE. A program that kills the wasm runtime poisons it for everything
// afterwards. The first hand-run of this sweep reported seven "leaks" that were all echoes of one
// earlier crash; re-running with a fresh process per file gave the true answer, one.

import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { readFileSync, readdirSync, existsSync } from 'node:fs';
import { join, dirname, basename } from 'node:path';
import { pathToFileURL, fileURLToPath } from 'node:url';

const run = promisify(execFile);
const here = dirname(fileURLToPath(import.meta.url));
const playground = join(here, '..');
const repoRoot = join(playground, '..');
const site = join(playground, 'site');

/**
 * Host vocabulary that must never reach a reader.
 *
 * ★ A .NET resource key (`Process_PlatformNotSupported`, `Arg_InvalidOperation`) and a raw exception
 * type name are both shapes no Cufet message has: Cufet writes English sentences. That is what makes
 * this cheap to detect without a list of specific keys to keep up to date.
 */
const HOST_LEAK = /\b(?:[A-Z][A-Za-z]*_[A-Z][A-Za-z]+|System(?:\.[A-Za-z]+)+Exception)\b/;

/**
 * Examples known to stop the runtime, with the reason. An entry here is a claim, and the test below
 * fails if one stops being true — a stale skip asserts something false, and is how a suite quietly
 * stops testing what it names.
 */
const KNOWN_DEATHS = {
    'sudoku.cufe':
        'backtracking recursion exhausts the browser stack. The page reports it and recovers; the '
        + 'program runs fine under `cufet`. See playground/Runtime.cs on maxCallDepth.',
};

function exampleFiles(dir = join(repoRoot, 'examples')) {
    const found = [];
    for (const entry of readdirSync(dir, { withFileTypes: true })) {
        const path = join(dir, entry.name);
        if (entry.isDirectory()) found.push(...exampleFiles(path));
        else if (entry.name.endsWith('.cufe')) found.push(path);
    }
    return found;
}

const examples = exampleFiles();
/** @type {Map<string, { output: string, died: boolean }>} */
const results = new Map();

/** Boots the runtime, seeds what the worker seeds, runs one program, and reports what came back. */
async function sweepOne(file) {
    const script = `
        import { dotnet } from ${JSON.stringify(pathToFileURL(join(site, '_framework', 'dotnet.js')).href)};
        import { readFileSync } from "node:fs";
        const { getAssemblyExports, getConfig } = await dotnet.create();
        const ex = await getAssemblyExports(getConfig().mainAssemblyName);
        const runtime = ex.Cufet.Playground.Runtime;
        for (const path of JSON.parse(readFileSync(${JSON.stringify(join(site, 'seed-manifest.json'))}, "utf8")))
            runtime.PlaceFile(path, readFileSync(${JSON.stringify(site)} + "/" + path, "utf8"));
        // NOTE: no backticks below - this whole script lives in a template literal.
        // Modelled on what worker.js does, because that is what a visitor experiences. An exception
        // ESCAPING Run is not the same event as the runtime EXITING: the first is a host failure
        // whose message the page shows - the Process_PlatformNotSupported class, and the whole
        // point of this sweep - while the second means the runtime is gone. Without the
        // distinction an escaped exception looks like a death, its message is never examined, and
        // this sweep misses the very bug it was written for.
        try {
            const output = runtime.Run(readFileSync(${JSON.stringify(file)}, "utf8"));
            console.log("<<<SWEEP>>>" + JSON.stringify({ output, died: false }));
        } catch (e) {
            const message = String(e?.message ?? e);
            console.log("<<<SWEEP>>>" + JSON.stringify({
                output: message,
                died: typeof e?.status === "number",
            }));
        }`;

    try {
        const { stdout } = await run(process.execPath, ['--input-type=module', '--eval', script],
            { maxBuffer: 32 * 1024 * 1024 });
        return parse(stdout);
    } catch (failure) {
        // ⚠ A non-zero exit is how a killed runtime arrives: the wasm abort takes the host process
        // down. Anything it printed before that is still on stdout and still worth reading.
        const printed = String(failure.stdout ?? '');
        return parse(printed) ?? { output: printed, died: true };
    }
}

function parse(stdout) {
    const marked = stdout.split('\n').find(line => line.startsWith('<<<SWEEP>>>'));
    if (!marked) return null;
    const { output, died } = JSON.parse(marked.slice('<<<SWEEP>>>'.length));
    return { output, died };
}

before(async () => {
    assert.ok(existsSync(join(site, '_framework')),
        'the playground is not built — run `dotnet publish Cufet.Playground.csproj -c Release` then `node build.mjs`');

    // ★ Four at a time. Each example is its own process for isolation, and 38 wasm boots run
    // end-to-end take minutes; a small pool keeps the isolation and most of the wall clock.
    const queue = [...examples];
    const workers = Array.from({ length: 4 }, async () => {
        for (let file = queue.pop(); file; file = queue.pop())
            results.set(basename(file), await sweepOne(file));
    });
    await Promise.all(workers);
}, { timeout: 600_000 });

for (const file of examples) {
    const name = basename(file);

    test(`${name} says nothing a person could not read`, () => {
        const result = results.get(name);
        assert.ok(result, `${name} was never swept`);

        // ⚠ A killed run has no program output to judge — what is captured is the HOST's crash
        // dump, which naturally contains `System.StackOverflowException` and would fail this test
        // for the wrong reason. A visitor never sees that: the worker catches the exit and the page
        // answers in Cufet's own words. The death is covered by the test below.
        if (result.died) return;

        const leak = HOST_LEAK.exec(result.output);
        assert.equal(leak, null,
            `${name} leaked host vocabulary: "${leak?.[0]}". A .NET resource key or exception type `
            + 'reached a reader of a language whose whole claim is readable messages. Catch it where '
            + 'it is thrown and answer in Cufet\'s own words.\n\n' + result.output.slice(0, 600));
    });

    test(`${name} does not stop the runtime unexpectedly`, () => {
        const result = results.get(name);
        assert.ok(result, `${name} was never swept`);
        const reason = Object.hasOwn(KNOWN_DEATHS, name) ? KNOWN_DEATHS[name] : null;

        if (result.died) {
            assert.ok(reason,
                `${name} stopped the wasm runtime, and nothing says it should. Everything printed `
                + 'before it is lost and the page has to replace the worker. Either fix it, or add it '
                + 'to KNOWN_DEATHS with the reason.');
            return;
        }

        // ★ Good news, reported as a failure on purpose. A skip entry that is no longer true makes
        // the suite assert something false, and nobody goes looking for skips that have started
        // passing — so the suite has to be the one that notices.
        assert.equal(reason, null,
            `${name} is listed in KNOWN_DEATHS but completed. That is an improvement — delete the `
            + `entry. Its stated reason was: ${reason}`);
    });
}
