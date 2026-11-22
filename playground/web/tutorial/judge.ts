// How an attempt is judged. No browser and no Monaco in here, on purpose: test/tutorial.test.mjs
// bundles this file with the lessons and runs every scripted reaction against the REAL wasm build,
// so a reaction whose program no longer produces the output it was written for fails a test rather
// than silently never firing.

import type { Reaction } from './lesson';

/** What happened when the player (or a test) pressed Run. */
export type Outcome =
    | 'ran'        // the program ran; `output` is everything it printed, a runtime error included
    | 'refused'    // the checker refused it; `output` is Cufet's message
    | 'empty'      // there was nothing to run, so nothing was run
    | 'timeout'    // it ran past the time limit and was stopped
    | 'died';      // the runtime itself ended

export interface Attempt {
    code: string;
    outcome: Outcome;
    output: string;
}

/**
 * The words of `text`, for the words-only judging the first lessons use (tutorial/DESIGN.md: "The
 * first writing exercises judge the words only. Punctuation and capital letters are ignored").
 *
 * ★ An apostrophe joins rather than splits, so "Who's" is one word and stays "whos", not "who s".
 */
export function words(text: string): string {
    return text
        .toLowerCase()
        .replace(/['’]/g, '')
        .replace(/[^\p{L}\p{N}]+/gu, ' ')
        .trim();
}

/** The output's non-blank lines. */
export function outputLines(output: string): string[] {
    return output.split('\n').map(line => line.trimEnd()).filter(line => line.trim() !== '');
}

/**
 * Whether a typed guess at one line of output is right. Words only, like everything else here, and
 * one allowance: when the line holds exactly one number, that number alone is a right guess.
 *
 * ★ Play-tested: lesson 4 asks what `State "The answer is {answer}."` will say, a player typed
 * `50`, and it was marked wrong against `The answer is 50.`. The number IS the answer to "what
 * will it say?"; the words around it are what the player wrote nothing about.
 */
export function guessIsRight(guess: string, line: string): boolean {
    const typed = words(guess), said = words(line);
    if (typed === said) return true;
    const numbers = said.split(' ').filter(word => /^\d+$/.test(word));
    return numbers.length === 1 && typed === numbers[0];
}

/** True when the program ran and printed `goal`, line for line, judged on words only. */
export function sameWords(attempt: Attempt, goal: readonly string[]): boolean {
    if (attempt.outcome !== 'ran') return false;
    const got = outputLines(attempt.output);
    return got.length === goal.length && got.every((line, i) => words(line) === words(goal[i] ?? ''));
}

/** True when the program ran and printed `goal` exactly, line for line. */
export function sameText(attempt: Attempt, goal: readonly string[]): boolean {
    if (attempt.outcome !== 'ran') return false;
    const got = outputLines(attempt.output);
    return got.length === goal.length && got.every((line, i) => line === goal[i]);
}

/**
 * What an attempt means for a step: the reaction to say, if any, and whether the step is done.
 *
 * ★★ THE ORDER IS THE RULE, and it was wrong once. A reaction that FINISHES the step (a remark on a
 * right answer, like "though I do spell it with a big G") is looked for first. Then the step's own
 * test. Only then the reactions to a miss. Checking every reaction before the step's test let a
 * miss-reaction written loosely ("it ran") swallow the right answer too, and the joke in lesson 1
 * could never be finished. Shared by the page and the test, so the two cannot judge differently.
 */
export function respond(
    step: { done: (attempt: Attempt) => boolean; reactions: Reaction[] },
    attempt: Attempt,
): { reaction: Reaction | undefined; done: boolean } {
    const finishing = step.reactions.find(r => r.done && r.when(attempt));
    if (finishing) return { reaction: finishing, done: true };
    if (step.done(attempt)) return { reaction: undefined, done: true };
    const reaction = step.reactions.find(r => !r.done && r.when(attempt))
        ?? COMMON_SLIPS.find(r => r.when(attempt));
    return { reaction, done: false };
}

// ── The common slips ─────────────────────────────────────────────────────────────────────────
//
// Reactions for the slips any exercise can meet, said when the exercise has none of its own for
// the attempt (tutorial/DESIGN.md, "Common slips"). Play-tested: a missing period came up five
// times in two runs, and each time the page could only say "look at what it said".
//
// Matched on Cufet's own messages, measured 2026-11-22:
//   Define hops as 2 / State hops.   Line 2, column 1: expected '.', got State "State".
//   State "Hi"                       Line 1, column 11: expected '.', got Eof "".
//   State 2 3.                       Line 1, column 9: expected '.', got Number "3".
//   State Keep going!.               Line 1, column 7: identifier 'Keep' must start with a lowercase letter.
//   Defne x as 1.                    Line 1, column 1: identifier 'Defne' must start with a lowercase letter.
//   If 2 is 2: / State "a".          … this 'If' opens a block, and the file ended before its 'Done.'. …

/** Where Cufet's message points, when it names a place. */
function placeOf(attempt: Attempt): { line: string; column: number } | null {
    const found = /Line (\d+), column (\d+):/.exec(attempt.output);
    if (!found) return null;
    const line = attempt.code.split('\n')[Number(found[1]) - 1];
    return line === undefined ? null : { line, column: Number(found[2]) };
}

/** Cufet's place is the first word of its line: what it noticed was the line's start. */
function atLineStart(attempt: Attempt): boolean {
    const place = placeOf(attempt);
    return place !== null && place.column === place.line.length - place.line.trimStart().length + 1;
}

const refused = (a: Attempt, fragment: string) => a.outcome === 'refused' && a.output.includes(fragment);
const missingPeriod = (a: Attempt) => refused(a, "expected '.'");
const capitalName = (a: Attempt) => refused(a, 'must start with a lowercase letter');

export const COMMON_SLIPS: Reaction[] = [
    {
        case: 'no period on the last line',
        when: a => missingPeriod(a) && a.output.includes('got Eof'),
        lines: [{ who: 'grace', text: 'Every line ends with a period, the last one too.' }],
    },
    {
        // ★ Cufet notices a missing period only when the NEXT line starts, so the line it names
        // is one below the slip (play-tested twice: "the error points at the wrong line").
        case: 'no period, noticed on the next line',
        when: a => missingPeriod(a) && atLineStart(a),
        lines: [{ who: 'grace', text: 'Every line ends with a period. Cufet only noticed when the next line started, so look at the end of the line before the one it named.' }],
    },
    {
        case: 'more on a line than Cufet expected',
        when: missingPeriod,
        lines: [{ who: 'grace', text: 'Cufet expected that line to end where it says, with a period. Is a word too many, or a mark missing?' }],
    },
    {
        case: 'a misspelled first word',
        when: a => capitalName(a) && atLineStart(a),
        lines: [{ who: 'grace', text: "Cufet doesn't know that first word. Check its spelling, capital letter and all." }],
    },
    {
        case: 'words without quote marks',
        when: capitalName,
        lines: [{ who: 'hopper', text: "Words to say go in quote marks! Without them, Cufet thinks they're a name." }],
    },
    {
        case: 'a block without its Done',
        when: a => refused(a, "and the file ended before its 'Done.'"),
        lines: [{ who: 'hopper', text: 'Every colon opens a block, and every block needs its `Done.`' }],
    },
];
