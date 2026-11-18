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
    return { reaction: step.reactions.find(r => !r.done && r.when(attempt)), done: false };
}
