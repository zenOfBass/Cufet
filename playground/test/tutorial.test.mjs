// The tutorial's lessons, played against the REAL wasm build.
//
// ★★ It judges with the page's own `respond`, so reactions are tried in the page's order.
//
// ★★ WHY. Every reaction in a lesson is written for a program the player might type, and keyed on
// what Cufet does with it: a refusal's wording, an output's words. If the language changes a
// message, the reaction keyed on it simply never fires again, and nothing on the page says so —
// the rabbits fall back to a generic line. So every reaction carries a SAMPLE here, and the test
// requires that the sample reaches that reaction and no earlier one. A reaction added without a
// sample fails too, so none can be unwatched.
//
// It also checks each prediction: the option the lesson treats as right must be what the program
// on screen at that moment really prints.
//
// ⚠ Requires a built playground: `dotnet publish` then `node build.mjs`.

import { test, before } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, mkdtempSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import * as esbuild from 'esbuild';

const here = dirname(fileURLToPath(import.meta.url));
const playground = join(here, '..');
const site = join(playground, 'site');

let runtime;
let lessons;

before(async () => {
    assert.ok(existsSync(join(site, '_framework')),
        'the playground is not built — run `dotnet publish Cufet.Playground.csproj -c Release` then `node build.mjs`');
    const { dotnet } = await import('../site/_framework/dotnet.js');
    const { getAssemblyExports, getConfig } = await dotnet.create();
    runtime = (await getAssemblyExports(getConfig().mainAssemblyName)).Cufet.Playground.Runtime;

    // The lessons are TypeScript and import no browser code, so they bundle for node as they are.
    const out = join(mkdtempSync(join(tmpdir(), 'cufet-tutorial-')), 'lessons.mjs');
    await esbuild.build({
        stdin: { contents: "export { lesson1 } from './lesson-1'; export { lesson2 } from './lesson-2'; export { respond } from './judge';", resolveDir: join(playground, 'web', 'tutorial'), loader: 'ts' },
        bundle: true, format: 'esm', platform: 'node', outfile: out, logLevel: 'silent',
    });
    lessons = await import(pathToFileURL(out).href);
});

/** What the page's RuntimeClient.attempt does, in-process: refused is the CHECKER's say-so. */
function attempt(code) {
    if (code.trim() === '') return { code, outcome: 'empty', output: '' };
    const refused = runtime.Check(code).split('\n').some(line => {
        try { return JSON.parse(line).severity === 'error'; } catch { return false; }
    });
    return { code, outcome: refused ? 'refused' : 'ran', output: runtime.Run(code) };
}

// The programs from the drafts' "Reactions" tables, keyed by each reaction's `case`.
const SAMPLES = {
    'all in small letters': 'State "hello, grace!".',
    'unchanged': 'State "Hello, rabbits!".',
    'hello to Hopper first': 'State "Hello, Hopper!".',
    'no period at the end': 'State "Hello, Grace!"',
    'a quote mark missing': 'State "Hello, Grace!.',
    'both quote marks gone': 'State Hello, Grace!.',
    'the first word is not State': 'Stat "Hello, Grace!".',
    'everything deleted': '',
    'the sum in quote marks': 'State "What is 12 * 12?".\nState "12 * 12".',
    'worked it out by hand': 'State "What is 12 * 12?".\nState 144.',
    '"times" as a word': 'State "What is 12 * 12?".\nState 12 times 12.',
    'an x for times': 'State "What is 12 * 12?".\nState 12 x 12.',
    '= 144 on the end': 'State "What is 12 * 12?".\nState 12 * 12 = 144.',
    'both on one line': 'State "What is 12 times 12?" 12 * 12.',
    'the joke in the wrong order':'State "Lettuce!".\nState "Knock, knock!".\nState "Who\'s there?".\nState "Lettuce who?".\nState "Lettuce in, it\'s cold out here!".',
};

// What finishes each exercise and Parsons problem, in the order they come in the lesson.
const DONE = {
    lesson1: [
        ['State "Hello, Grace!".', 'State "Hello, Grace.".'],
        ['State "Hello, Hopper!".'],
        ['State "Knock, knock!".\nState "Who\'s there?".\nState "Lettuce!".\nState "Lettuce who?".\nState "Lettuce in, it\'s cold out here!".'],
    ],
    lesson2: [
        ['State "What is 12 * 12?".\nState 12 * 12.', 'State "What is 12 * 12?".\nState 12*12.'],
    ],
};

const backticked = text => [...text.matchAll(/`([^`]+)`/g)].map(m => m[1]);

for (const name of ['lesson1', 'lesson2']) {
    test(`${name}: every reaction is what its sample gets`, () => {
        for (const step of lessons[name].steps) {
            if (step.kind !== 'exercise' && step.kind !== 'parsons') continue;
            for (const reaction of step.reactions) {
                assert.ok(reaction.case in SAMPLES, `no sample for the reaction "${reaction.case}" — add one to SAMPLES`);
                const tried = attempt(SAMPLES[reaction.case]);
                const got = lessons.respond(step, tried);
                assert.equal(got.reaction?.case, reaction.case,
                    `"${reaction.case}": its sample got ${got.reaction ? `"${got.reaction.case}"` : got.done ? 'done' : 'no reaction'}`
                    + `\n  it ${tried.outcome} with: ${tried.output.trim()}`);
                assert.equal(got.done, reaction.done === true, `"${reaction.case}": done should be ${reaction.done === true}`);
            }
        }
    });

    test(`${name}: the right answers finish, and set off no reaction`, () => {
        const steps = lessons[name].steps.filter(s => s.kind === 'exercise' || s.kind === 'parsons');
        assert.equal(steps.length, DONE[name].length, 'DONE lists a different number of exercises than the lesson has');
        steps.forEach((step, i) => {
            // The goal on screen is what the first right answer prints, exactly.
            if (step.goal) {
                const printed = attempt(DONE[name][i][0]).output.split('\n').filter(line => line.trim() !== '');
                assert.deepEqual(printed, step.goal, 'the goal shown is not what the right answer prints');
            }
            for (const code of DONE[name][i]) {
                const tried = attempt(code);
                const got = lessons.respond(step, tried);
                assert.equal(got.reaction, undefined, `${JSON.stringify(code)} set off "${got.reaction?.case}"`);
                assert.ok(got.done, `${JSON.stringify(code)} did not count as done: ${tried.output.trim()}`);
            }
        });
    });

    test(`${name}: each prediction's right answer is what the program does`, () => {
        let code = '';
        let checked = 0;
        for (const step of lessons[name].steps) {
            if (step.kind === 'write') code = step.code;
            if (step.kind === 'exercise' && step.start !== undefined) code = step.start;
            if (step.kind !== 'predict' && step.kind !== 'predict-lines') continue;
            checked++;
            const tried = attempt(code);
            const printed = tried.output.split('\n').filter(line => line.trim() !== '');
            if (step.kind === 'predict' && step.asks === 'runs') {
                assert.deepEqual(step.options, ['Runs', 'Refused']);
                assert.equal(step.options[step.correct], tried.outcome === 'refused' ? 'Refused' : 'Runs',
                    `"Will it run?" has the wrong answer for:\n${code}\n  it ${tried.outcome}`);
            } else if (step.kind === 'predict') {
                assert.deepEqual(printed, backticked(step.options[step.correct]),
                    `the program before this prediction prints something else:\n${code}`);
            } else {
                assert.equal(tried.outcome, 'ran', `the program to guess at does not run:\n${code}`);
                assert.equal(printed.length, step.slots,
                    `${step.slots} slots, but the program prints ${printed.length} lines:\n${code}`);
                assert.equal(step.wrong.length, step.slots, 'a reaction to a wrong guess is needed for every slot');
            }
        }
        // A test that can vanish is not a test: every lesson so far has questions to check.
        assert.ok(checked > 0, 'no predictions were found to check');
    });
}
