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
        stdin: { contents: "export { lesson1 } from './lesson-1'; export { lesson2 } from './lesson-2'; export { lesson3 } from './lesson-3'; export { lesson4 } from './lesson-4'; export { lesson5 } from './lesson-5'; export { lesson6 } from './lesson-6'; export { lesson7 } from './lesson-7'; export { lesson8 } from './lesson-8'; export { lesson9 } from './lesson-9'; export { respond, guessIsRight } from './judge';", resolveDir: join(playground, 'web', 'tutorial'), loader: 'ts' },
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
    'the hole filled in by hand': 'Define answer as 12 * 12.\nState "The answer is {169}.".',
    'thirteen in words': 'Define answer as thirteen * thirteen.\nState "The answer is {answer}.".',
    'no name at all': 'State "The answer is 49.".',
    'another name': 'Define result as 7 * 7.\nState "The answer is {result}.".',
    'the sum in the quote marks': 'State "The answer is 7 * 7.".',
    'only the number': 'Define answer as 7 * 7.\nState answer.',
    'the name outside the sentence': 'Define answer as 7 * 7.\nState "The answer is " answer.',
    'no "as"': 'Define answer 7 * 7.\nState "The answer is {answer}.".',
    '= instead of as': 'Define answer = 7 * 7.\nState "The answer is {answer}.".',
    'a capital letter': 'Define Answer as 7 * 7.\nState "The answer is {answer}.".',
    'a space in the name': 'Define my answer as 7 * 7.\nState "The answer is {answer}.".',
    'the Define line last': 'State "The answer is {answer}.".\nDefine answer as 7 * 7.',
    'a typo in the hole': 'Define answer as 7 * 7.\nState "The answer is {answr}.".',
    'the Define not first': 'State count.\nDefine count as 3.\nThe count becomes count - 1.\nState count.\nThe count becomes count - 1.\nState count.\nState "Go!".',
    'the same number twice': 'Define count as 3.\nState count.\nState count.\nThe count becomes count - 1.\nThe count becomes count - 1.\nState count.\nState "Go!".',
    '"Go!" too early': 'Define count as 3.\nState "Go!".\nState count.\nThe count becomes count - 1.\nState count.\nThe count becomes count - 1.\nState count.',
    'counted by hand': 'State "Hop 1!".\nState "Hop 2!".\nState "Hop 3!".',
    'said before made bigger':'Define hops as 0.\nState "Hop {hops}!".\nThe hops becomes hops + 1.\nState "Hop {hops}!".\nThe hops becomes hops + 1.\nState "Hop {hops}!".\nThe hops becomes hops + 1.',
    'started at one': 'Define hops as 1.\nThe hops becomes hops + 1.\nState "Hop {hops}!".\nThe hops becomes hops + 1.\nState "Hop {hops}!".\nThe hops becomes hops + 1.\nState "Hop {hops}!".',
    'the name without a hole': 'Define hops as 0.\nThe hops becomes hops + 1.\nState "Hop hops!".',
    '"is" instead of becomes': 'Define hops as 0.\nThe hops is hops + 1.\nState "Hop {hops}!".',
    '= instead of becomes': 'Define hops as 0.\nThe hops = hops + 1.\nState "Hop {hops}!".',
    'Define a second time': 'Define hops as 0.\nDefine hops as hops + 1.\nState "Hop {hops}!".',
    'a typo in the name': 'Define hops as 0.\nThe hosp becomes hops + 1.\nState "Hop {hops}!".',
    'no comma after Otherwise': 'Define hops as 2.\nState "Hop {hops}!".\nIf hops is 3, state "Rest!".\nOtherwise state "Keep going!".',
    'Else for Otherwise': 'Define hops as 2.\nState "Hop {hops}!".\nIf hops is 3, state "Rest!".\nElse, state "Keep going!".',
    'Otherwise above the If': 'Define hops as 2.\nState "Hop {hops}!".\nOtherwise, state "Keep going!".\nIf hops is 3, state "Rest!".',
    'a second If': 'Define hops as 2.\nState "Hop {hops}!".\nIf hops is 3, state "Rest!".\nIf hops is 2, state "Keep going!".',
    'the > sign': 'Define hops as 5.\nIf hops > 3, state "Rest!".\nOtherwise, state "Keep going!".',
    'Rest! with no If': 'State "Rest!".',
    'the start changed instead': 'Define hops as 5.\nWhile hops is less than 3, repeat:\n    The hops becomes hops + 1.\n    State "Hop {hops}!".\nDone.',
    'ten copies, no While': Array.from({ length: 10 }, (_, i) => `State "Hop ${i + 1}!".`).join('\n'),
    'the < sign': 'Define hops as 0.\nWhile hops < 6, repeat:\n    The hops becomes hops + 1.\n    State "Hop {hops}!".\n    If hops is 3, state "Halfway!".\nDone.',
    'the If after Done': 'Define hops as 0.\nWhile hops is less than 6, repeat:\n    The hops becomes hops + 1.\n    State "Hop {hops}!".\nDone.\nIf hops is 3, state "Halfway!".',
    'said before counted': 'Define hops as 0.\nWhile hops is less than 6, repeat:\n    State "Hop {hops}!".\n    The hops becomes hops + 1.\n    If hops is 3, state "Halfway!".\nDone.',
    'no repeat': 'Define hops as 0.\nWhile hops is less than 6:\n    The hops becomes hops + 1.\n    State "Hop {hops}!".\n    If hops is 3, state "Halfway!".\nDone.',
    'no comma before repeat': 'Define hops as 0.\nWhile hops is less than 6 repeat:\n    The hops becomes hops + 1.\n    State "Hop {hops}!".\n    If hops is 3, state "Halfway!".\nDone.',
    '"is" left out': 'Define hops as 0.\nWhile hops less than 6, repeat:\n    The hops becomes hops + 1.\n    State "Hop {hops}!".\n    If hops is 3, state "Halfway!".\nDone.',
    'no Done at the end': 'Define hops as 0.\nWhile hops is less than 6, repeat:\n    The hops becomes hops + 1.\n    State "Hop {hops}!".\n    If hops is 3, state "Halfway!".',
    'Lettuce without quote marks': 'Define friends as a series with ("Grace", "Hopper", "friend", Lettuce).\nFor each name in friends, repeat:\n    State "Hello, {name}!".\nDone.',
    'no comma before Lettuce': 'Define friends as a series with ("Grace", "Hopper", "friend" "Lettuce").\nFor each name in friends, repeat:\n    State "Hello, {name}!".\nDone.',
    'a second Define friends': 'Define friends as a series with ("Grace", "Hopper", "friend").\nDefine friends as a series with ("Lettuce").\nFor each name in friends, repeat:\n    State "Hello, {name}!".\nDone.',
    'Lettuce greeted by hand': 'Define friends as a series with ("Grace", "Hopper", "friend").\nFor each name in friends, repeat:\n    State "Hello, {name}!".\nDone.\nState "Hello, Lettuce!".',
    'the State inside the loop': 'Define hops as a series with (3, 5, 2).\nDefine total as 0.\nFor each count in hops, repeat:\n    The total becomes total + count.\n    State "I hopped {total} times!".\nDone.',
    'the total never started': 'Define hops as a series with (3, 5, 2).\nFor each count in hops, repeat:\n    The total becomes total + count.\nDone.\nState "I hopped {total} times!".',
    'the total started inside the loop': 'Define hops as a series with (3, 5, 2).\nFor each count in hops, repeat:\n    Define total as 0.\n    The total becomes total + count.\nDone.\nState "I hopped {total} times!".',
    'the whole series added': 'Define hops as a series with (3, 5, 2).\nDefine total as 0.\nFor each count in hops, repeat:\n    The total becomes total + hops.\nDone.\nState "I hopped {total} times!".',
    '"of" instead of "in"': 'Define hops as a series with (3, 5, 2).\nDefine total as 0.\nFor each count of hops, repeat:\n    The total becomes total + count.\nDone.\nState "I hopped {total} times!".',
    'ten worked out by hand': 'Define hops as a series with (3, 5, 2).\nState "I hopped 10 times!".',
    'the zeros without a series': 'Bind number to add-up, given (the series of numbers days):\n    Define total as 0.\n    For each count in days, repeat:\n        The total becomes total + count.\n    Done.\n    Return total.\nDone.\nState cast add-up on (a series with (3, 5, 2)).\nState cast add-up on (a series with (4, 1, 6)).\nState cast add-up on (0, 0, 0).',
    'no State in front': 'Bind number to add-up, given (the series of numbers days):\n    Define total as 0.\n    For each count in days, repeat:\n        The total becomes total + count.\n    Done.\n    Return total.\nDone.\nState cast add-up on (a series with (3, 5, 2)).\nState cast add-up on (a series with (4, 1, 6)).\nCast add-up on (a series with (0, 0, 0)).',
    'zero worked out by hand': 'Bind number to add-up, given (the series of numbers days):\n    Define total as 0.\n    For each count in days, repeat:\n        The total becomes total + count.\n    Done.\n    Return total.\nDone.\nState cast add-up on (a series with (3, 5, 2)).\nState cast add-up on (a series with (4, 1, 6)).\nState 0.',
    'state instead of return': 'Bind text to cheer, given (the number hops):\n    If hops is greater than 3, state "Rest!".\n    Otherwise, state "Keep going!".\nDone.\nState cast cheer on (5).\nState cast cheer on (2).',
    'no Otherwise line': 'Bind text to cheer, given (the number hops):\n    If hops is greater than 3, return "Rest!".\nDone.\nState cast cheer on (5).\nState cast cheer on (2).',
    'Bind number': 'Bind number to cheer, given (the number hops):\n    If hops is greater than 3, return "Rest!".\n    Otherwise, return "Keep going!".\nDone.\nState cast cheer on (5).\nState cast cheer on (2).',
    'no return before the words': 'Bind text to cheer, given (the number hops):\n    If hops is greater than 3, "Rest!".\n    Otherwise, return "Keep going!".\nDone.\nState cast cheer on (5).\nState cast cheer on (2).',
    'no Done for the Bind': 'Bind text to cheer, given (the number hops):\n    If hops is greater than 3, return "Rest!".\n    Otherwise, return "Keep going!".\nState cast cheer on (5).\nState cast cheer on (2).',
    'no function at all': 'Define hops as 5.\nIf hops is greater than 3, state "Rest!".\nOtherwise, state "Keep going!".\nThe hops becomes 2.\nIf hops is greater than 3, state "Rest!".\nOtherwise, state "Keep going!".',
    '"is" left out of but void is': 'Bind voidable text to cheer, given (the number hops):\n    If hops is greater than 3, return "Rest!".\n    Return void.\nDone.\nState cast cheer on (5).\nState cast cheer on (2) but void "Keep going!".',
    'or instead of but void is': 'Bind voidable text to cheer, given (the number hops):\n    If hops is greater than 3, return "Rest!".\n    Return void.\nDone.\nState cast cheer on (5).\nState cast cheer on (2) or "Keep going!".',
    'a number for the nothing': 'Bind voidable text to cheer, given (the number hops):\n    If hops is greater than 3, return "Rest!".\n    Return void.\nDone.\nState cast cheer on (5).\nState cast cheer on (2) but void is 0.',
    'cheer changed instead': 'Bind voidable text to cheer, given (the number hops):\n    If hops is greater than 3, return "Rest!".\n    Return "Keep going!".\nDone.\nState cast cheer on (5).\nState cast cheer on (2).',
    'Bind number to hop': 'Bind number to hop, given (the number times):\n    Define hops as 0.\n    While hops is less than times, repeat:\n        The hops becomes hops + 1.\n        State "Hop {hops}!".\n    Done.\nDone.\nCast hop on (3).\nCast hop on (2).',
    'State in front of the cast': 'Bind void to hop, given (the number times):\n    Define hops as 0.\n    While hops is less than times, repeat:\n        The hops becomes hops + 1.\n        State "Hop {hops}!".\n    Done.\nDone.\nState cast hop on (3).\nCast hop on (2).',
    'always three hops': 'Bind void to hop, given (the number times):\n    Define hops as 0.\n    While hops is less than 3, repeat:\n        The hops becomes hops + 1.\n        State "Hop {hops}!".\n    Done.\nDone.\nCast hop on (3).\nCast hop on (2).',
    'only one Done': 'Bind void to hop, given (the number times):\n    Define hops as 0.\n    While hops is less than times, repeat:\n        The hops becomes hops + 1.\n        State "Hop {hops}!".\n    Done.\nCast hop on (3).\nCast hop on (2).',
    'the hops by hand': 'State "Hop 1!".\nState "Hop 2!".\nState "Hop 3!".\nState "Hop 1!".\nState "Hop 2!".',
    'six hops by hand':'State "Hop 1!".\nState "Hop 2!".\nState "Hop 3!".\nState "Halfway!".\nState "Hop 4!".\nState "Hop 5!".\nState "Hop 6!".',
    '"than" left out':'Define hops as 5.\nIf hops is greater 3, state "Rest!".\nOtherwise, state "Keep going!".',
    'five in quote marks': 'Define hops as 5.\nIf hops is "5", state "Rest!".\nOtherwise, state "Keep going!".',
    'no comma after the question': 'Define hops as 5.\nIf hops is greater than 3 state "Rest!".\nOtherwise, state "Keep going!".',
    'asks whether hops is five': 'Define hops as 5.\nIf hops is 5, state "Rest!".\nOtherwise, state "Keep going!".',
    'no period on the end': 'Define hops as 5.\nIf hops is greater than 3, state "Rest!".\nOtherwise, state "Keep going!"',
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
    lesson3: [
        ['Define answer as 13 * 13.\nState "The answer is {answer}.".'],
        [
            'Define answer as 7 * 7.\nState "The answer is {answer}.".',
            'Define answer as 7 * 7.\nState "The answer is { answer }.".',
            'Define answer as 7*7.\nState "the answer is {answer}".',
        ],
    ],
    lesson4: [
        ['Define count as 3.\nState count.\nThe count becomes count - 1.\nState count.\nThe count becomes count - 1.\nState count.\nState "Go!".'],
        ['Define hops as 0.\nThe hops becomes hops + 1.\nState "Hop {hops}!".\nThe hops becomes hops + 1.\nState "Hop {hops}!".\nThe hops becomes hops + 1.\nState "Hop {hops}!".'],
    ],
    lesson5: [
        ['Define hops as 2.\nState "Hop {hops}!".\nIf hops is 3, state "Rest!".\nOtherwise, state "Keep going!".'],
        [
            'Define hops as 5.\nIf hops is greater than 3, state "Rest!".\nOtherwise, state "Keep going!".',
            'Define hops as 5.\nIf hops is greater than 3:\n    State "Rest!".\nDone.\nOtherwise:\n    State "Keep going!".\nDone.',
        ],
        ['Define hops as 2.\nIf hops is greater than 3, state "Rest!".\nOtherwise, state "Keep going!".'],
    ],
    lesson6: [
        ['Define hops as 0.\nWhile hops is less than 10, repeat:\n    The hops becomes hops + 1.\n    State "Hop {hops}!".\nDone.'],
        [
            'Define hops as 0.\nWhile hops is less than 6, repeat:\n    The hops becomes hops + 1.\n    State "Hop {hops}!".\n    If hops is 3, state "Halfway!".\nDone.',
            'Define hops as 0.\nRepeat:\n    The hops becomes hops + 1.\n    State "Hop {hops}!".\n    If hops is 3, state "Halfway!".\nuntil hops is 6.',
        ],
    ],
    lesson7: [
        ['Define friends as a series with ("Grace", "Hopper", "friend", "Lettuce").\nFor each name in friends, repeat:\n    State "Hello, {name}!".\nDone.'],
        [
            'Define hops as a series with (3, 5, 2).\nDefine total as 0.\nFor each count in hops, repeat:\n    The total becomes total + count.\nDone.\nState "I hopped {total} times!".',
            'Define hops as a series with (3, 5, 2).\nDefine total as 0.\nFor each day in hops, repeat:\n    The total becomes total + day.\nDone.\nState "I hopped {total} times!".',
        ],
    ],
    lesson8: [
        ['Bind number to add-up, given (the series of numbers days):\n    Define total as 0.\n    For each count in days, repeat:\n        The total becomes total + count.\n    Done.\n    Return total.\nDone.\nState cast add-up on (a series with (3, 5, 2)).\nState cast add-up on (a series with (4, 1, 6)).\nState cast add-up on (a series with (0, 0, 0)).'],
        [
            'Bind text to cheer, given (the number hops):\n    If hops is greater than 3, return "Rest!".\n    Otherwise, return "Keep going!".\nDone.\nState cast cheer on (5).\nState cast cheer on (2).',
            'Bind text to cheer, given (the number hops):\n    If hops > 3:\n        Return "Rest!".\n    Done.\n    Return "Keep going!".\nDone.\nState cast cheer on (5).\nState cast cheer on (2).',
        ],
    ],
    lesson9: [
        ['Bind voidable text to cheer, given (the number hops):\n    If hops is greater than 3, return "Rest!".\n    Return void.\nDone.\nState cast cheer on (5).\nState cast cheer on (2) but void is "Keep going!".'],
        [
            'Bind void to hop, given (the number times):\n    Define hops as 0.\n    While hops is less than times, repeat:\n        The hops becomes hops + 1.\n        State "Hop {hops}!".\n    Done.\nDone.\nCast hop on (3).\nCast hop on (2).',
            'Bind void to hop, given (the number times):\n    Define hops as 0.\n    Repeat:\n        The hops becomes hops + 1.\n        State "Hop {hops}!".\n    until hops is times.\nDone.\nCast hop on (3).\nCast hop on (2).',
        ],
    ],
};

const backticked = text => [...text.matchAll(/`([^`]+)`/g)].map(m => m[1]);

test('a typed guess: the whole line, or its one number alone', () => {
    const { guessIsRight } = lessons;
    for (const guess of ['50', 'The answer is 50.', 'the answer is 50'])
        assert.ok(guessIsRight(guess, 'The answer is 50.'), `${guess} should be right`);
    for (const guess of ['49', 'The answer is 49.', '', '5'])
        assert.ok(!guessIsRight(guess, 'The answer is 50.'), `${guess} should be wrong`);
    assert.ok(guessIsRight('156', '156'));
    // Two numbers in the line: neither alone is the answer.
    assert.ok(!guessIsRight('12', 'What is 12 * 12?'));
});

for (const name of ['lesson1', 'lesson2', 'lesson3', 'lesson4', 'lesson5', 'lesson6', 'lesson7', 'lesson8', 'lesson9']) {
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
        // ★ Every program that can be in the editor at this point. After an exercise that is ANY
        // of its right answers, and a rabbit's `append` goes on the end of each: lesson 3's
        // "Will it run?" is asked of the PLAYER's program with Hopper's lines added, so it must
        // hold for every program the player could have finished with.
        let codes = [''];
        let exercise = 0;
        let checked = 0;
        for (const step of lessons[name].steps) {
            if (step.kind === 'write')
                codes = step.append ? codes.map(code => `${code.trimEnd()}\n${step.code}`) : [step.code];
            if (step.kind === 'exercise' || step.kind === 'parsons') codes = DONE[name][exercise++];
            if (step.kind !== 'predict' && step.kind !== 'predict-lines') continue;
            checked++;
            for (const code of codes) {
                const tried = attempt(code);
                const printed = tried.output.split('\n').filter(line => line.trim() !== '');
                if (step.kind === 'predict' && step.asks === 'runs') {
                    assert.deepEqual(step.options, ['Runs', 'Refused']);
                    assert.equal(step.options[step.correct], tried.outcome === 'refused' ? 'Refused' : 'Runs',
                        `"Will it run?" has the wrong answer for:\n${code}\n  it ${tried.outcome}`);
                } else if (step.kind === 'predict' && step.shows !== undefined) {
                    assert.ok(tried.output.includes(step.shows),
                        `the program before this prediction does not show "${step.shows}":\n${code}\n  it said: ${tried.output.trim()}`);
                } else if (step.kind === 'predict') {
                    assert.deepEqual(printed, backticked(step.options[step.correct]),
                        `the program before this prediction prints something else:\n${code}`);
                } else {
                    assert.equal(tried.outcome, 'ran', `the program to guess at does not run:\n${code}`);
                    assert.equal(printed.length, step.slots,
                        `${step.slots} slots, but the program prints ${printed.length} lines:\n${code}`);
                    assert.equal(step.wrong.length, step.slots, 'a reaction to a wrong guess is needed for every slot');
                    // A known slip must be a WRONG guess, or its remark would scold a right one.
                    for (const slip of step.slips ?? []) {
                        assert.ok(slip.slot < step.slots, `a slip for slot ${slip.slot}, of ${step.slots}`);
                        assert.ok(!lessons.guessIsRight(slip.guess, printed[slip.slot]),
                            `the slip "${slip.guess}" is the right answer: the program prints "${printed[slip.slot]}"`);
                    }
                }
            }
        }
        // A test that can vanish is not a test: every lesson so far has questions to check.
        assert.ok(checked > 0, 'no predictions were found to check');
    });
}
