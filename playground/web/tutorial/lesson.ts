// The shape of a lesson: a script of steps the page plays in order.
//
// A lesson is DATA. The page (tutorial.ts) knows how to play each kind of step; a lesson file only
// says which steps, in which order, with which words. The words come from tutorial/lesson-N-draft.md
// and should be kept in step with it by hand: the draft is where they are agreed.

import type { Attempt } from './judge';

export type Who = 'grace' | 'hopper';

/**
 * One speech bubble. `text` takes a little markdown: `code`, **bold** and *italics*.
 *
 * `answer` is code shown in the bubble that the player must TYPE, never copy (DESIGN.md: "The answer
 * must be typed in by hand; no pasting"), so the page draws it as text that cannot be selected.
 */
export interface Line {
    who: Who;
    text: string;
    answer?: string;
}

/** A prepared reaction to an attempt. The first whose `when` holds is the one the rabbits say. */
export interface Reaction {
    /** What the reaction is for, in the draft's words. Shown in test failures, never on screen. */
    case: string;
    when: (attempt: Attempt) => boolean;
    lines: Line[];
    /** The attempt counts as done, after the lines are said. */
    done?: boolean;
    /** Offer to put back the last program that had anything in it. */
    offerRestore?: boolean;
}

export type Step =
    /** A rabbit arrives at the edge of the screen. */
    | { kind: 'enter'; who: Who }
    /** Speech bubbles, one after another; each waits to be read. */
    | { kind: 'say'; lines: Line[] }
    /** A rabbit types into the editor. Only what differs from what is there gets retyped. */
    | { kind: 'write'; who: Who; code: string }
    /** A rabbit presses Run itself. DESIGN.md: they do this exactly once, in lesson 1. */
    | { kind: 'rabbit-run'; who: Who }
    /** The output box asks what will appear; the player picks, then presses Run. */
    | {
        kind: 'predict';
        options: string[];
        /** Which option is what the program really prints. The page does not need it; the test
         *  checks it against the real build, so a draft and its program cannot drift apart. */
        correct: number;
        /** Said once a choice is made, before the player presses Run. */
        nudge?: Line[];
        /** What is said for each option, by index. */
        reactions: Line[][];
        /** Said whichever option was chosen. */
        after: Line[];
    }
    /** The player writes; Run judges it. */
    | {
        kind: 'exercise';
        /** Replaces the editor's text first, when given. Otherwise the player works on what is there. */
        start?: string;
        /** Done when the program ran and this holds, and no reaction said otherwise. */
        done: (attempt: Attempt) => boolean;
        /** The hint ladder. Empty for a variation with no answer offered. */
        hints: Line[][];
        reactions: Reaction[];
        /** Said when it is done. */
        success: Line[];
    }
    /** Lines as tiles in the editor, to put in order. */
    | {
        kind: 'parsons';
        /** In the order they are first shown. */
        tiles: string[];
        /** Said once the tiles are on screen. */
        intro: Line[];
        done: (attempt: Attempt) => boolean;
        hints: Line[][];
        reactions: Reaction[];
        success: Line[];
    }
    | { kind: 'end'; text: string };

export interface Lesson {
    number: number;
    title: string;
    steps: Step[];
}
