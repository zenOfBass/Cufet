// Lesson 7: series. The words are tutorial/lesson-7-draft.md's, and that file is where they are
// agreed; change them there first, then here.

import { outputLines, sameWords, words } from './judge';
import type { Attempt } from './judge';
import type { Lesson, Line } from './lesson';

const grace = (text: string, answer?: string): Line => ({ who: 'grace', text, ...(answer ? { answer } : {}) });
const hopper = (text: string, answer?: string): Line => ({ who: 'hopper', text, ...(answer ? { answer } : {}) });

const refusedSaying = (a: Attempt, fragment: string) =>
    a.outcome === 'refused' && a.output.includes(fragment);

const lines = (a: Attempt) => a.outcome === 'ran' ? outputLines(a.output) : [];

const FRIENDS = 'Define friends as a series with ("Grace", "Hopper", "friend").';
const GREET_ALL = 'For each name in friends, repeat:\n    State "Hello, {name}!".\nDone.';
const FOUR_HELLOS = ['Hello, Grace!', 'Hello, Hopper!', 'Hello, friend!', 'Hello, Lettuce!'];
const HOPS = 'Define hops as a series with (3, 5, 2).';
const TEN = ['I hopped 10 times!'];

/** Lettuce is in the series itself, not greeted by a line of her own. */
const lettuceInTheSeries = (code: string) => /series\s+with\s*\([^)]*"Lettuce"[^)]*\)/.test(code);
const usesForEach = (code: string) => /^\s*For\s+each\b/m.test(code);

export const lesson7: Lesson = {
    number: 7,
    title: 'Series',
    steps: [
        { kind: 'enter', who: 'hopper' },
        { kind: 'enter', who: 'grace' },

        // ── Beat 1: a series ────────────────────────────────────────────────────────────────
        { kind: 'say', lines: [hopper("It's called a **series**. Here's a list of friends.")] },
        { kind: 'write', who: 'hopper', code: `${FRIENDS}\nState friends.` },
        { kind: 'say', lines: [grace('That last one is you, friend. What do you think it says?')] },
        {
            kind: 'predict',
            options: ['`(Grace, Hopper, friend)`', 'Only `Grace`', '`friends`'],
            correct: 0,
            reactions: [
                [hopper('All three, in the order I wrote them!')],
                [grace('`friends` is the name for the whole series, so Cufet says all of it.')],
                [hopper('No quote marks on `friends`, remember? Cufet says what the name holds.')],
            ],
            after: [grace('A series keeps a list of things, in order, under one name. The round brackets hold the things, with a comma between each.')],
        },

        // ── Beat 2: one from the series ─────────────────────────────────────────────────────
        { kind: 'write', who: 'hopper', code: `${FRIENDS}\nState "Hello, {the second of friends}!".` },
        { kind: 'say', lines: [hopper('Now I only want one of them. Which one?')] },
        {
            kind: 'predict',
            options: ['`Hello, Grace!`', '`Hello, Hopper!`', '`Hello, friend!`'],
            correct: 1,
            // The draft has one remark here whichever was chosen.
            reactions: [[], [], []],
            after: [
                grace("Cufet counts from one, like you would: Grace is the first, Hopper is the second, you're the third. You can say `the last of friends` too."),
                hopper('Now a trick question. What about the fourth?'),
            ],
        },
        { kind: 'write', who: 'hopper', code: `${FRIENDS}\nState the fourth of friends.` },
        {
            kind: 'predict',
            options: ['`friend`', 'Nothing at all', 'Cufet stops and explains'],
            correct: 2,
            shows: "There's no item 4",
            reactions: [
                [hopper("Ha! There are only three. Cufet won't make up a fourth. It stops and tells you.")],
                [hopper("Ha! There are only three. Cufet won't make up a fourth. It stops and tells you.")],
                [hopper("Too smart for me! Only three friends, so there's no fourth.")],
            ],
            after: [],
        },

        // ── Beat 3: for each ────────────────────────────────────────────────────────────────
        { kind: 'say', lines: [grace('Now, greeting them all. You could write three `State` lines. Or this.')] },
        { kind: 'write', who: 'grace', code: `${FRIENDS}\n${GREET_ALL}` },
        { kind: 'say', lines: [hopper('What will it say, friend?')] },
        {
            kind: 'predict',
            options: [
                '`Hello, Grace!`, `Hello, Hopper!`, `Hello, friend!`',
                'Only `Hello, Grace!`',
                '`Hello, name!` three times',
            ],
            correct: 0,
            reactions: [
                [hopper('Hello to you too!')],
                [grace("`For each` doesn't stop after the first. It goes round once for every friend in the series.")],
                [grace("`name` is in a hole, the curly brackets, so Cufet fills it in. Each time round, it's the next friend.")],
            ],
            after: [
                grace('`For each` gives every thing in the series a turn. Each time round, `name` stands for the one whose turn it is.'),
                hopper('No counting, no `becomes`. It knows when it runs out!'),
            ],
        },

        // ── Beat 4: one more friend ─────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                hopper('Wait, we forgot someone. Lettuce!'),
                grace('Lettuce is lunch, Hopper.'),
                hopper("Lettuce is a friend *and* lunch. Add her to the end of the series, friend. Don't touch the loop."),
            ],
        },
        {
            kind: 'exercise',
            task: 'Add `"Lettuce"` to the end of the series. Don\'t touch the loop.',
            goal: FOUR_HELLOS,
            done: a => sameWords(a, FOUR_HELLOS) && lettuceInTheSeries(a.code) && usesForEach(a.code),
            hints: [
                [grace('The friends are all in the first line. Add one more inside the round brackets.')],
                [hopper('A comma after `"friend"`, then `"Lettuce"` in quote marks!')],
                [grace('Here it is. Type it yourself:', 'Define friends as a series with ("Grace", "Hopper", "friend", "Lettuce").')],
            ],
            reactions: [
                {
                    case: 'Lettuce without quote marks',
                    when: a => refusedSaying(a, "identifier 'Lettuce' must start with a lowercase letter"),
                    lines: [grace('Without quote marks, Cufet thinks `Lettuce` is a name. Each friend in the series is text, in quote marks.')],
                },
                {
                    case: 'no comma before Lettuce',
                    when: a => refusedSaying(a, "expected ')', got String"),
                    lines: [hopper('Every friend needs a comma between them and the next!')],
                },
                {
                    case: 'a second Define friends',
                    when: a => refusedSaying(a, "'friends' is already defined"),
                    lines: [grace("One `Define` per name. Add Lettuce to the line that's already there.")],
                },
                {
                    // Judging is on words, so "Hello, lettuce!" matches; the series check is not.
                    case: 'lettuce without a capital',
                    when: a => sameWords(a, FOUR_HELLOS) && /series\s+with\s*\([^)]*"lettuce"/.test(a.code),
                    lines: [hopper("Lettuce has a big L! Cufet says exactly what's in the quote marks.")],
                },
                {
                    case: 'Lettuce greeted by hand',
                    when: a => sameWords(a, FOUR_HELLOS) && !lettuceInTheSeries(a.code),
                    lines: [hopper('That works, but the loop should do it! Put Lettuce in the series.')],
                },
            ],
            success: [
                hopper('Hello, Lettuce!'),
                grace('The series grew, and the loop went round once more by itself.'),
            ],
        },

        // ── Beat 5: adding up ───────────────────────────────────────────────────────────────
        { kind: 'say', lines: [hopper("Lunch count! I've got a puzzle for you, friend.")] },
        {
            kind: 'write', who: 'hopper',
            code: 'Define carrots as a series with (2, 4, 1).\nDefine total as 0.\nFor each count in carrots, repeat:\n    The total becomes total + count.\nDone.\nState "We ate {total} carrots!".',
        },
        {
            kind: 'predict',
            options: ['`We ate 7 carrots!`', '`We ate 1 carrots!`', '`We ate 2 carrots!`, `We ate 6 carrots!`, then `We ate 7 carrots!`'],
            correct: 0,
            reactions: [
                [hopper('Seven! Two, then four more, then one more.')],
                [grace("`becomes` doesn't throw the old total away. It's `total + count`, so each count is *added* to what's there.")],
                [grace('The `State` is after `Done.`, outside the loop. So it only speaks once, at the end.')],
            ],
            after: [grace("Start a total at zero, then add each one to it. That's how you add up a whole series.")],
        },

        // ── Beat 6: a blank page, with the series started ───────────────────────────────────
        // Grace writes the first line before she says she has (as the blank pages are emptied first).
        { kind: 'write', who: 'grace', code: HOPS },
        {
            kind: 'say', lines: [
                hopper('Your turn, friend! I hopped three times on Monday, five on Tuesday and two on Wednesday. How many is that altogether?'),
                grace("No working it out in your head. I've started the series for you."),
            ],
        },
        {
            kind: 'exercise',
            task: 'Add up the hops with `For each`, and say the total in a sentence.',
            goal: TEN,
            done: a => sameWords(a, TEN) && usesForEach(a.code),
            hints: [
                [grace('Start a total at zero, before the loop.')],
                [hopper('Then go through `hops` with `For each`, and each time round, the total becomes the total plus that day\'s hops. Say it after `Done.`')],
                [grace('Here it is. Type it yourself:',
                    `${HOPS}\nDefine total as 0.\nFor each count in hops, repeat:\n    The total becomes total + count.\nDone.\nState "I hopped {total} times!".`)],
            ],
            reactions: [
                {
                    case: 'the State inside the loop',
                    when: a => lines(a).length > 1 && words(lines(a).at(-1) ?? '') === 'i hopped 10 times',
                    lines: [hopper("Ha, a running score! The last one's right. Move the `State` after `Done.` so it only says the end.")],
                },
                {
                    case: 'the total never started',
                    when: a => refusedSaying(a, "'total' isn't defined"),
                    lines: [grace('The total has to start somewhere. Begin it at zero, before the loop.')],
                },
                {
                    case: 'the total started inside the loop',
                    when: a => refusedSaying(a, 'inside a block that has ended'),
                    lines: [grace('Cufet explains it: a name made inside the loop is gone after `Done.` Define it before the loop starts.')],
                },
                {
                    case: 'the whole series added',
                    when: a => refusedSaying(a, 'arithmetic requires numbers on both sides'),
                    lines: [hopper('`hops` is the whole series! Add the one whose turn it is, the name after `For each`.')],
                },
                {
                    case: '"of" instead of "in"',
                    when: a => refusedSaying(a, 'expected In, got Of'),
                    lines: [grace('It\'s `For each count in hops`, with "in".')],
                },
                {
                    case: 'ten worked out by hand',
                    when: a => sameWords(a, TEN) && !usesForEach(a.code),
                    lines: [grace('Right number, but you worked it out yourself. Let `For each` do the adding.')],
                },
            ],
            success: [
                hopper('Ten! My legs knew it was a lot.'),
                grace('A name to keep the total, and `For each` to add every day to it. Well done, friend.'),
            ],
        },

        // ── Before the lesson ends ──────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                hopper("Ten hops for me! Now do Grace's week."),
                grace("That's the whole adding-up again, with my numbers. And again for every rabbit we know."),
                hopper('Unless the adding-up gets a name of its own. Then Cufet can do it for anyone\'s week.'),
            ],
        },

        { kind: 'end', text: 'End of lesson 7.' },
    ],
};
