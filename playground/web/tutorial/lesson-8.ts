// Lesson 8: functions. The words are tutorial/lesson-8-draft.md's, and that file is where they are
// agreed; change them there first, then here.

import { outputLines, sameWords } from './judge';
import type { Attempt } from './judge';
import type { Lesson, Line } from './lesson';

const grace = (text: string, answer?: string): Line => ({ who: 'grace', text, ...(answer ? { answer } : {}) });
const hopper = (text: string, answer?: string): Line => ({ who: 'hopper', text, ...(answer ? { answer } : {}) });

const refusedSaying = (a: Attempt, fragment: string) =>
    a.outcome === 'refused' && a.output.includes(fragment);

const lines = (a: Attempt) => a.outcome === 'ran' ? outputLines(a.output) : [];

const ADD_UP = 'Bind number to add-up, given (the series of numbers days):\n    Define total as 0.\n    For each count in days, repeat:\n        The total becomes total + count.\n    Done.\n    Return total.\nDone.';
const SNEAKY = 'Bind number to add-up, given (the series of numbers days):\n    Define total as 0.\n    For each count in days, repeat:\n        The total becomes total + count.\n        Return total.\n    Done.\n    Return total.\nDone.';
const HOPPERS_WEEK = 'State cast add-up on (a series with (3, 5, 2)).';
const GRACES_WEEK = 'State cast add-up on (a series with (4, 1, 6)).';
const BEAT_2 = `${ADD_UP}\n${HOPPERS_WEEK}\n${GRACES_WEEK}`;
const THREE_WEEKS = ['10', '11', '0'];

const CHEER_CALLS = 'State cast cheer on (5).\nState cast cheer on (2).';
const CHEERS = ['Rest!', 'Keep going!'];

/** How many times `add-up` is run: Lettuce's week must be a third. */
const castsOfAddUp = (code: string) => (code.match(/\bcast\s+add-up\b/gi) ?? []).length;
const bindsCheer = (code: string) => /\bBind\s+\w+\s+to\s+cheer\b/.test(code);
const NO_RETURN = 'can reach its end without returning one';

export const lesson8: Lesson = {
    number: 8,
    title: 'Functions',
    steps: [
        { kind: 'enter', who: 'hopper' },
        { kind: 'enter', who: 'grace' },

        // ── Beat 1: a name for the adding-up ────────────────────────────────────────────────
        { kind: 'say', lines: [hopper("Here's the adding-up from last time, with a name of its own: `add-up`.")] },
        { kind: 'write', who: 'hopper', code: `${ADD_UP}\n${HOPPERS_WEEK}` },
        { kind: 'say', lines: [hopper('The inside should look familiar. What do you think it says?')] },
        {
            kind: 'predict',
            options: ['`10`', '`(3, 5, 2)`', 'Nothing at all'],
            correct: 0,
            reactions: [
                [hopper('Ten hops, same as last time!')],
                [grace('The series went *into* `add-up`, and the total came back out.')],
                [grace('The last line runs `add-up`, so the lines inside it run too.')],
            ],
            after: [
                grace('`Bind` makes a **function**: lines with a name of their own. `number` says it gives back a number. `given` says what it needs: a series of numbers, called `days`.'),
                hopper('`Return` hands the answer back. And `cast` is how you run it. `cast add-up on`, and then what to add up, in round brackets.'),
                grace('And good little rabbits always give back what they promise. This one promised a `number`.'),
            ],
        },

        // ── Beat 2: anyone's week ───────────────────────────────────────────────────────────
        { kind: 'say', lines: [hopper("Now Grace's week. One more line, and that's it.")] },
        { kind: 'write', who: 'hopper', code: BEAT_2 },
        { kind: 'say', lines: [grace('What will it say this time, friend?')] },
        {
            kind: 'predict',
            options: ['`10`, then `11`', 'Only `11`', '`10`, then `10` again'],
            correct: 0,
            reactions: [
                [
                    hopper('Grace out-hopped me!'),
                    grace("I've been practising."),
                    hopper('Real rabbits can hop all day, you know.'),
                    grace('One day, Hopper.'),
                ],
                [grace("The first line is still there, so `add-up` runs twice: my week and Hopper's.")],
                [grace("Each time it runs, `days` is whatever series you give it. The second time, it's my week.")],
            ],
            after: [grace('The adding-up is written once. Every `cast` uses it again, with different days.')],
        },

        // ── Beat 3: a sneaky return ─────────────────────────────────────────────────────────
        { kind: 'say', lines: [hopper("Bet you can't spot this one. I've put one more `Return` inside the loop.")] },
        { kind: 'write', who: 'hopper', code: `${SNEAKY}\n${HOPPERS_WEEK}` },
        {
            kind: 'predict',
            options: ['`10`', '`3`', '`3`, `8`, then `10`'],
            correct: 1,
            reactions: [
                [hopper('Caught you! The first time round, the total is 3, and `Return` hands it straight back. The rest of the week never gets added.')],
                [hopper('Spotted it!')],
                [hopper('Caught you! The first time round, the total is 3, and `Return` hands it straight back. The rest of the week never gets added.')],
            ],
            after: [grace('`Return` gives the answer back and leaves the function, right there. So it goes where the answer is ready.')],
        },
        // Hopper tidies up: the extra `Return` out, and Grace's week back, as Beat 4 begins from Beat 2.
        { kind: 'write', who: 'hopper', code: BEAT_2 },

        // ── Beat 4: Lettuce's week ──────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                hopper("We forgot someone again. Lettuce's week!"),
                grace("Lettuce doesn't hop, Hopper."),
                hopper("Then it's zero, zero and zero. Add a line that adds up Lettuce's week, friend."),
            ],
        },
        {
            kind: 'exercise',
            task: "Add a line that adds up Lettuce's week: zero, zero and zero.",
            goal: THREE_WEEKS,
            done: a => sameWords(a, THREE_WEEKS) && castsOfAddUp(a.code) >= 3,
            hints: [
                [grace('Copy the shape of the last line, with Lettuce\'s days.')],
                [hopper('`State cast add-up on`, then a series of three zeros!')],
                [grace('Here it is. Type it yourself:', 'State cast add-up on (a series with (0, 0, 0)).')],
            ],
            reactions: [
                {
                    case: 'the zeros without a series',
                    when: a => refusedSaying(a, "'add-up' expects 1 argument(s), but you passed 3"),
                    lines: [grace('`add-up` needs one thing: a series. Put `a series with` in front of the zeros.')],
                },
                {
                    case: 'no State in front',
                    when: a => lines(a).join(' ') === '10 11' && castsOfAddUp(a.code) >= 3,
                    lines: [hopper("It added them up, but nobody said the answer! Put `State` in front.")],
                },
                {
                    case: 'zero worked out by hand',
                    when: a => sameWords(a, THREE_WEEKS) && castsOfAddUp(a.code) < 3,
                    lines: [grace('Right, but you worked it out yourself. Let `add-up` do it.')],
                },
            ],
            success: [hopper('Zero! Told you she was resting.')],
        },

        // ── Beat 5: a blank page, with the last two lines written ───────────────────────────
        // Grace writes the two lines before she says she has (as the blank pages are emptied first).
        { kind: 'write', who: 'grace', code: CHEER_CALLS },
        {
            kind: 'say', lines: [
                hopper('Your turn, friend! Remember "Rest!" and "Keep going!"? Make it a function called `cheer`. Give it a number of hops, and it gives back the right one.'),
                grace("It gives back text this time, so it's `Bind text`, not `Bind number`. I've written the last two lines for you. Write `cheer` above them."),
            ],
        },
        {
            kind: 'exercise',
            task: 'Write a function called `cheer` above the two lines. Given a number of hops, it gives back "Rest!" when it\'s greater than 3, and "Keep going!" when it isn\'t.',
            goal: CHEERS,
            done: a => sameWords(a, CHEERS) && bindsCheer(a.code),
            hints: [
                [grace('It gives back text, and it\'s given a number. So it starts `Bind text to cheer, given (the number hops):`')],
                [hopper('Inside, it\'s the `If` and `Otherwise` from before, but with `return` instead of `state`!')],
                [grace('Here it is. Type it yourself:',
                    'Bind text to cheer, given (the number hops):\n    If hops is greater than 3, return "Rest!".\n    Otherwise, return "Keep going!".\nDone.')],
            ],
            reactions: [
                {
                    case: 'state instead of return',
                    when: a => refusedSaying(a, NO_RETURN) && /,\s*state\b/.test(a.code),
                    lines: [hopper('That says it, but `cheer` has to *give it back*. Use `return`, and the `State` lines at the bottom do the saying.')],
                },
                {
                    case: 'no Otherwise line',
                    when: a => refusedSaying(a, NO_RETURN) && !/^\s*Otherwise\b/m.test(a.code),
                    lines: [grace("When hops isn't more than three, `cheer` has nothing to give back. Add the `Otherwise`.")],
                },
                {
                    case: 'Bind number',
                    when: a => refusedSaying(a, 'this function is declared to give back a number'),
                    lines: [grace('"Rest!" is text, so it\'s `Bind text to cheer`.')],
                },
                {
                    case: 'no return before the words',
                    when: a => refusedSaying(a, "on its own doesn't do anything"),
                    lines: [grace('Cufet needs to know what to do with "Rest!". Put `return` in front of it.')],
                },
                {
                    case: 'no Done for the Bind',
                    when: a => refusedSaying(a, "this 'Bind' opens a block, and the file ended"),
                    lines: [hopper('Every colon needs a `Done.`!')],
                },
                {
                    case: 'no function at all',
                    when: a => sameWords(a, CHEERS) && !bindsCheer(a.code),
                    lines: [grace('Right lines, but the task was a function called `cheer`. Put them inside a `Bind`.')],
                },
            ],
            success: [
                hopper("It knows when I'm tired, every time I ask!"),
                grace('A function that decides. Well done, friend.'),
            ],
        },

        // ── Before the lesson ends ──────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                hopper("Now I want a function that just says hello to a friend. It doesn't need to give anything back."),
                grace('Then it gives back nothing. And Cufet has a word for nothing.'),
                hopper('My favourite kind of word.'),
                grace("If we're going to learn nothing properly, we should ask Maria. Nobody knows more about nothing."),
                hopper('To the pond!'),
            ],
        },

        { kind: 'end', text: 'End of lesson 8.' },
    ],
};
