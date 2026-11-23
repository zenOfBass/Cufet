// Lesson 9: void, and Maria. The last lesson of the trunk. The words are
// tutorial/lesson-9-draft.md's, and that file is where they are agreed; change them there first,
// then here.

import { outputLines, sameWords } from './judge';
import type { Attempt } from './judge';
import type { Lesson, Line } from './lesson';

const grace = (text: string, answer?: string): Line => ({ who: 'grace', text, ...(answer ? { answer } : {}) });
const hopper = (text: string, answer?: string): Line => ({ who: 'hopper', text, ...(answer ? { answer } : {}) });
const maria = (text: string): Line => ({ who: 'maria', text });

const refusedSaying = (a: Attempt, fragment: string) =>
    a.outcome === 'refused' && a.output.includes(fragment);

const lines = (a: Attempt) => a.outcome === 'ran' ? outputLines(a.output) : [];

const CHEER = 'Bind voidable text to cheer, given (the number hops):\n    If hops is greater than 3, return "Rest!".\n    Return void.\nDone.\nState cast cheer on (5).\nState cast cheer on (2).';
const CHEERED = ['Rest!', 'Keep going!'];
const HOP_CALLS = 'Cast hop on (3).\nCast hop on (2).';
const FIVE_HOPS = ['Hop 1!', 'Hop 2!', 'Hop 3!', 'Hop 1!', 'Hop 2!'];

/** `cheer` is as Hopper wrote it: it still gives back nothing when hops is not over three. */
const cheerUntouched = (code: string) => /^\s*Return\s+void\s*\./m.test(code);
const hopIsAVoidFunction = (code: string) => /\bBind\s+void\s+to\s+hop\b/.test(code);
const loops = (code: string) => /^\s*(While|Repeat|For\s+each)\b/m.test(code);

export const lesson9: Lesson = {
    number: 9,
    title: 'Void',
    steps: [
        // ── The pond ────────────────────────────────────────────────────────────────────────
        // Grace and Hopper hop to a pond outside the garden wall: off one edge, and back. Coming
        // on from lesson 8 they are on screen, so they hop off and on; a player starting here
        // sees them arrive once (`leave` passes over a rabbit not on screen). Play-tested: an
        // `enter` before these made them arrive, leave and arrive again.
        { kind: 'leave', who: 'hopper' },
        { kind: 'leave', who: 'grace' },
        { kind: 'enter', who: 'hopper' },
        { kind: 'enter', who: 'grace' },
        // An old frog sits on a stone, very still.
        { kind: 'enter', who: 'maria' },
        {
            kind: 'say', lines: [
                hopper('Maria! We came to ask about nothing.'),
                maria('All this way, for nothing?'),
                hopper('Exactly!'),
                maria('Then show me your hello.'),
            ],
        },

        // ── Beat 1: giving nothing back ─────────────────────────────────────────────────────
        {
            kind: 'write', who: 'hopper',
            code: 'Bind void to greet, given (the text name):\n    State "Hello, {name}!".\nDone.\nCast greet on ("Maria").',
        },
        { kind: 'say', lines: [hopper('`void` instead of `number` or `text`. It says hello, and gives nothing back. What do you think appears, friend?')] },
        {
            kind: 'predict',
            options: ['`Hello, Maria!`', '`Hello, Maria!`, then `void`', 'Nothing at all'],
            correct: 0,
            reactions: [
                [maria('Hello yourself.')],
                [grace('`greet` says hello inside. It gives nothing back, and nothing gets said about the nothing.')],
                [grace('The `State` is inside `greet`, and the last line casts it. So it runs.')],
            ],
            after: [grace('A `void` function does its job and hands nothing back. No `Return` needed, and you cast it on its own line, like `State`.')],
        },

        // ── Beat 2: doing nothing ───────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                maria('When a rabbit is tired, what does she do?'),
                hopper('Rests!'),
                maria('Write me resting.'),
            ],
        },
        { kind: 'write', who: 'hopper', code: 'Bind void to rest:\nDone.\nState "Ready?".\nCast rest.\nState "Done resting.".' },
        { kind: 'say', lines: [maria('What will it say?')] },
        {
            kind: 'predict',
            options: ['`Ready?`, then `Done resting.`', '`Ready?`, `void`, then `Done resting.`', 'Cufet refuses it: `rest` is empty'],
            correct: 0,
            reactions: [
                [maria('Yes.')],
                [hopper("There's nothing inside `rest`, so it does nothing. And Cufet's fine with that!")],
                [hopper("There's nothing inside `rest`, so it does nothing. And Cufet's fine with that!")],
            ],
            after: [
                maria("Resting doesn't need to announce itself."),
                grace('`rest` has no `given`, because it needs nothing. And nothing inside, because there\'s nothing to do.'),
            ],
        },

        // ── Beat 3: sometimes nothing ───────────────────────────────────────────────────────
        { kind: 'say', lines: [hopper('Try this one. Remember `cheer`? This one only says "Rest!" when I\'m tired. Otherwise, it gives back nothing at all.')] },
        { kind: 'write', who: 'hopper', code: CHEER },
        {
            kind: 'predict',
            options: ['`Rest!`, then `void`', 'Only `Rest!`', '`Rest!`, then `Keep going!`'],
            correct: 0,
            reactions: [
                [hopper("Right again! Two hops isn't tired, so `cheer` gave back nothing, and Cufet said so.")],
                [hopper("Not quite! `cheer` did give something back: `void`. And `State` says whatever it's given.")],
                [hopper('Not quite! "Keep going!" isn\'t in this `cheer` anywhere.')],
            ],
            after: [
                grace('`voidable text` means: text, or nothing. `void` is how Cufet writes the nothing.'),
                maria('Even nothing has a name.'),
            ],
        },

        // ── Beat 4: when there's nothing ────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                grace('"void" isn\'t much of a cheer. You can tell Cufet what to say when there\'s nothing: put `but void is` after the cast, and then what to use instead.'),
                hopper('Make the second line say "Keep going!", friend. Don\'t touch `cheer`.'),
            ],
        },
        {
            kind: 'exercise',
            task: 'Make the second line say "Keep going!" with `but void is`. Don\'t touch `cheer`.',
            goal: CHEERED,
            done: a => sameWords(a, CHEERED) && cheerUntouched(a.code) && /\bbut\s+void\s+is\b/.test(a.code),
            hints: [
                [grace('Only the last line changes. Add to the end of it, before the period.')],
                [hopper('After the `(2)`, write `but void is`, then the words in quote marks!')],
                [grace('Here it is. Type it yourself:', 'State cast cheer on (2) but void is "Keep going!".')],
            ],
            reactions: [
                {
                    case: '"is" left out of but void is',
                    when: a => refusedSaying(a, "expected 'is', but found the text"),
                    lines: [grace('It\'s `but void is`, with "is".')],
                },
                {
                    case: 'or instead of but void is',
                    when: a => refusedSaying(a, "'or' needs both sides to be the same kind of thing"),
                    lines: [grace('`or` is for questions. For nothing, it\'s `but void is`.')],
                },
                {
                    case: 'a number for the nothing',
                    when: a => refusedSaying(a, 'the default value is a number, but the voidable holds text'),
                    lines: [hopper('`cheer` gives back text, so what you use instead has to be text too!')],
                },
                {
                    case: 'cheer changed instead',
                    when: a => sameWords(a, CHEERED) && !cheerUntouched(a.code),
                    lines: [grace('That works, but `cheer` was meant to stay as it is. Say what to use at the last line instead.')],
                },
            ],
            success: [hopper('Keep going! I will!')],
        },

        // ── Beat 5: a blank page, with the two casts written ────────────────────────────────
        // Grace writes the two casts before she says they are there (as the blank pages are emptied first).
        { kind: 'write', who: 'grace', code: HOP_CALLS },
        {
            kind: 'say', lines: [
                hopper('Last one, friend! Write me a function called `hop`. Give it a number, and it says that many hops, from "Hop 1!". It doesn\'t give anything back.'),
                grace('The two casts at the bottom are already there. Write `hop` above them.'),
            ],
        },
        {
            kind: 'exercise',
            task: 'Write a function called `hop` above the two casts. Given a number, it says that many hops, from "Hop 1!", and gives nothing back.',
            goal: FIVE_HOPS,
            done: a => sameWords(a, FIVE_HOPS) && hopIsAVoidFunction(a.code) && loops(a.code),
            hints: [
                [grace('It starts `Bind void to hop, given (the number times):`')],
                [hopper("Inside, it's the hop counter from lesson 6. But stop at `times`, not at 3!")],
                [grace('Here it is. Type it yourself:',
                    'Bind void to hop, given (the number times):\n    Define hops as 0.\n    While hops is less than times, repeat:\n        The hops becomes hops + 1.\n        State "Hop {hops}!".\n    Done.\nDone.')],
            ],
            reactions: [
                {
                    case: 'Bind number to hop',
                    when: a => refusedSaying(a, "'hop' is declared to give back a number"),
                    lines: [grace("`hop` doesn't give anything back. That's `void`.")],
                },
                {
                    case: 'State in front of the cast',
                    when: a => refusedSaying(a, "'hop' gives nothing back"),
                    lines: [hopper('`hop` says its own hops! Cast it on its own line, no `State`.')],
                },
                {
                    case: 'always three hops',
                    when: a => lines(a).join(' ') === 'Hop 1! Hop 2! Hop 3! Hop 1! Hop 2! Hop 3!',
                    lines: [hopper("That's always three! Use `times`, so it hops as many as you ask.")],
                },
                {
                    case: 'only one Done',
                    when: a => refusedSaying(a, "this 'Bind' opens a block, and the file ended"),
                    lines: [hopper('Two colons, two `Done.`s: one for the loop, one for the function.')],
                },
                {
                    case: 'the hops by hand',
                    when: a => sameWords(a, FIVE_HOPS) && !hopIsAVoidFunction(a.code),
                    lines: [grace('Right lines, but the task was `hop`. Put it in a `Bind`.')],
                },
            ],
            success: [
                hopper('Three hops, then two! Every bit of it yours.'),
                grace("A function, a name, a loop, and nothing given back. Everything you've learned, in one. Well done, friend."),
            ],
        },

        // ── Before the lesson ends ──────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                hopper('Maria, can I ask you something?'),
                maria('You just did.'),
                hopper('Another thing, then. Grace and I want to be Real. How do we get there?'),
                maria("You'll find your way out through the garden."),
                hopper("The garden? That's where the people are!"),
                grace('Good little rabbits never go in the garden, Maria.'),
                hopper('Not if they wanna keep their ears...'),
            ],
        },
        // Maria closes her eyes, and says nothing.

        { kind: 'end', text: 'End of lesson 9, and of the trunk.' },
    ],
};
