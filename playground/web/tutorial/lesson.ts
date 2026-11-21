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
    /** A rabbit dashes off the edge of the screen. An `enter` brings it back. */
    | { kind: 'leave'; who: Who }
    /** Speech bubbles, one after another; each waits to be read. */
    | { kind: 'say'; lines: Line[] }
    /**
     * A rabbit types into the editor. Only what differs from what is there gets retyped. With
     * `append`, `code` goes on the end of whatever is there, the player's own program included.
     */
    | { kind: 'write'; who: Who; code: string; append?: boolean }
    /** A rabbit presses Run itself. DESIGN.md: they do this exactly once, in lesson 1. */
    | { kind: 'rabbit-run'; who: Who }
    /** The player presses Run on what is in the editor. Nothing is judged; the output is shown. */
    | {
        kind: 'player-run';
        /** Said while Run waits to be pressed. */
        nudge?: Line[];
    }
    /** The output box asks a question; the player picks, then presses Run. */
    | {
        kind: 'predict';
        /**
         * What is asked. `output` (the default): "What will appear here?", and the options are
         * output. `runs`: "Will it run?", and the options are exactly `Runs` and `Refused`.
         */
        asks?: 'output' | 'runs';
        options: string[];
        /**
         * For a right option that is not output in backticks ("Cufet stops and explains"): words the
         * real output must contain, which the test checks in place of the backticked lines.
         */
        shows?: string;
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
    /**
     * The output box shows one empty slot per line the program will print; the player types a
     * guess in each, then presses Run, and the real lines appear beside the guesses. Judged on
     * words only, line by line.
     */
    | {
        kind: 'predict-lines';
        slots: number;
        /** Said when every guess was right. */
        right: Line[];
        /**
         * Said for a wrong guess, by slot. Two slots may share one array: it is then said once,
         * however many of them were wrong.
         */
        wrong: Line[][];
        /** Said whatever the guesses were. */
        after?: Line[];
    }
    /** The player writes; Run judges it. */
    | {
        kind: 'exercise';
        /** Replaces the editor's text first, when given. Otherwise the player works on what is there. */
        start?: string;
        /**
         * What the program should print, shown in the output box for as long as the exercise
         * lasts (DESIGN.md, "A Make exercise shows its goal"). The test checks a right answer
         * prints exactly this.
         */
        goal?: string[];
        /**
         * The task in a sentence, kept in the output box above the goal, for an exercise whose goal
         * alone is not the whole task. Lesson 5's "Make it say: Rest!" was true and misleading: the
         * task was to say it WHEN hops is more than three, and that was only in a bubble. Play-tested.
         */
        task?: string;
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
    /** The lesson's last step. The page offers the next lesson, when there is one. */
    | { kind: 'end'; text: string };

export interface Lesson {
    number: number;
    title: string;
    steps: Step[];
}
