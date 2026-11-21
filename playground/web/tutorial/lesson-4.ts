// Lesson 4: becomes. The words are tutorial/lesson-4-draft.md's, and that file is where they are
// agreed; change them there first, then here.

import { outputLines, sameWords, words } from './judge';
import type { Attempt } from './judge';
import type { Lesson, Line } from './lesson';

const grace = (text: string, answer?: string): Line => ({ who: 'grace', text, ...(answer ? { answer } : {}) });
const hopper = (text: string, answer?: string): Line => ({ who: 'hopper', text, ...(answer ? { answer } : {}) });

const refusedSaying = (a: Attempt, fragment: string) =>
    a.outcome === 'refused' && a.output.includes(fragment);

const lines = (a: Attempt) => a.outcome === 'ran' ? outputLines(a.output) : [];

const COUNTDOWN = ['3', '2', '1', 'Go!'];
const HOPS = ['Hop 1!', 'Hop 2!', 'Hop 3!'];

export const lesson4: Lesson = {
    number: 4,
    title: 'Becomes',
    steps: [
        { kind: 'enter', who: 'hopper' },
        { kind: 'enter', who: 'grace' },

        // ── Beat 1: becoming something else ─────────────────────────────────────────────────
        { kind: 'say', lines: [hopper("Then let's do some becoming! Remember the program Cufet refused?")] },
        {
            kind: 'write', who: 'hopper',
            code: 'Define answer as 7 * 7.\nState "The answer is {answer}.".\nThe answer becomes 8 * 8.\nState "The answer is {answer}.".',
        },
        {
            kind: 'say', lines: [
                hopper('`Define` gives a name for the first time. `becomes` gives a name that already has one a new value.'),
                grace('And we write `The` in front, so the line starts with a capital, like a sentence.'),
                hopper("Now, bet you can't guess what it says, friend!"),
            ],
        },
        {
            kind: 'predict',
            options: [
                '`The answer is 49.`, then `The answer is 64.`',
                '`The answer is 64.` twice',
                '`The answer is 49.` twice',
            ],
            correct: 0,
            reactions: [
                [hopper('Got it in one! The first sentence came before the change, and the second came after.')],
                [grace("Cufet works from the top down. When it said the first sentence, the answer hadn't changed yet.")],
                [hopper('Nope, it changed! The second one says 64.')],
            ],
            after: [grace('`becomes` changes what a name stands for, from that line down. Everything above it saw the old value.')],
        },

        // ── Beat 2: one more than itself ────────────────────────────────────────────────────
        { kind: 'write', who: 'grace', code: 'Define answer as 7 * 7.\nThe answer becomes answer + 1.\nState "The answer is {answer}.".' },
        {
            kind: 'say', lines: [
                hopper(`Ooo, this one's my favorite. "The answer becomes answer plus one." Looks impossible, right? Nothing can be one more than itself!`),
                grace('Type what you think it will say, friend.'),
            ],
        },
        {
            kind: 'predict-lines',
            slots: 1,
            // The draft has one remark whatever the guess: a wrong guess is never a fail here.
            right: [],
            wrong: [[]],
            after: [
                hopper("Here's the trick. `becomes` doesn't mean \"is the same as\". Cufet works out the right side first, using the answer as it is right now. Forty-nine, plus one."),
                grace('And then the answer becomes that. It was forty-nine, and then it *became* fifty.'),
            ],
        },

        // ── Beat 3: a countdown ─────────────────────────────────────────────────────────────
        { kind: 'say', lines: [hopper('I know what that\'s good for. A countdown! Three, two, one, go!')] },
        {
            kind: 'parsons',
            tiles: [
                'State count.',
                'The count becomes count - 1.',
                'State "Go!".',
                'State count.',
                'Define count as 3.',
                'The count becomes count - 1.',
                'State count.',
            ],
            intro: [hopper('Your turn, friend. Put them in order! Three, two, one, and then go!')],
            done: a => sameWords(a, COUNTDOWN),
            hints: [
                [grace('A name has to be given before anything else can use it. Which tile gives it?')],
                [hopper('Say the number, then take one away, then say it again!')],
                [grace("Here's the order. Build it yourself:",
                    'Define count as 3. · State count. · The count becomes count - 1. · State count. · The count becomes count - 1. · State count. · State "Go!".')],
            ],
            reactions: [
                {
                    case: 'the Define not first',
                    when: a => refusedSaying(a, "'count' isn't defined"),
                    lines: [grace('The name has to be given before anything uses it. Put the `Define` on top.')],
                },
                {
                    case: 'the same number twice',
                    when: a => lines(a).some((line, i, all) => i > 0 && line !== 'Go!' && line === all[i - 1]),
                    lines: [hopper('It said the same number twice! Something has to change it in between.')],
                },
                {
                    case: '"Go!" too early',
                    when: a => lines(a).includes('Go!') && lines(a).at(-1) !== 'Go!',
                    lines: [hopper('Hey, I went too early! "Go!" comes after one.')],
                },
            ],
            success: [hopper('Go! Go go go!')],
        },
        // And off he goes, so Grace's line is true on screen.
        { kind: 'leave', who: 'hopper' },
        { kind: 'say', lines: [grace("He'll be back. Well done, friend.")] },
        { kind: 'enter', who: 'hopper' },

        // ── Beat 4: a blank page ────────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                hopper("I'm back! Now count my hops! Hop one, hop two, hop three!"),
                grace('The page is empty, friend. Start a name at zero, and make it one bigger before each hop.'),
            ],
        },
        {
            kind: 'exercise',
            start: '',
            goal: HOPS,
            done: a => sameWords(a, HOPS) && /\bbecomes\b/.test(a.code),
            hints: [
                [grace('Start with `Define hops as 0.` Then, for each hop, make it one bigger and say it.')],
                [hopper('Making it bigger is `The hops becomes hops + 1.` Saying it needs a hole: `{hops}`!')],
                [grace('Here it is. Type it yourself:',
                    'Define hops as 0.\nThe hops becomes hops + 1.\nState "Hop {hops}!".\nThe hops becomes hops + 1.\nState "Hop {hops}!".\nThe hops becomes hops + 1.\nState "Hop {hops}!".')],
            ],
            reactions: [
                {
                    case: 'counted by hand',
                    when: a => sameWords(a, HOPS) && !/\bbecomes\b/.test(a.code),
                    lines: [grace('Those are the right hops, but you counted them yourself. Make a name, and let `becomes` do the counting.')],
                },
                {
                    case: 'said before made bigger',
                    when: a => words(lines(a)[0] ?? '') === 'hop 0',
                    lines: [hopper("Hop zero? I haven't hopped yet! Make it one bigger *before* you say it.")],
                },
                {
                    case: 'started at one',
                    when: a => words(lines(a)[0] ?? '') === 'hop 2',
                    lines: [grace("It's made one bigger before the first hop, so start one lower, at zero.")],
                },
                {
                    case: 'the name without a hole',
                    when: a => lines(a).some(line => words(line) === 'hop hops'),
                    lines: [hopper('"Hop hops"! The name needs a hole, in curly brackets.')],
                },
                {
                    case: '"is" instead of becomes',
                    when: a => refusedSaying(a, 'expected Becomes, got Is'),
                    lines: [grace('Changing a name uses `becomes`, not `is`.')],
                },
                {
                    case: '= instead of becomes',
                    when: a => refusedSaying(a, "'=' is comparison, not assignment"),
                    lines: [grace('Cufet says it well itself: changing a name uses `becomes`.')],
                },
                {
                    case: 'Define a second time',
                    when: a => refusedSaying(a, 'is already defined in this scope'),
                    lines: [hopper("That's the one Cufet refused before! `Define` is only for the first time. After that, it's `becomes`.")],
                },
                {
                    case: 'a typo in the name',
                    when: a => refusedSaying(a, 'Did you mean'),
                    lines: [hopper('Cufet even guessed what you meant!')],
                },
            ],
            success: [
                hopper("Three hops! I counted every one. Well, Cufet did."),
                grace('You started at nothing, and became something new three times over. Well done, friend.'),
            ],
        },

        // ── Before the lesson ends ──────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                grace('So far, Cufet does every line, every time, top to bottom.'),
                hopper('But here\'s a secret, friend: it can decide things too. Like saying "Rest!" only when I\'m tired.'),
                grace('And every decision starts with a question.'),
            ],
        },

        { kind: 'end', text: 'End of lesson 4.' },
    ],
};
