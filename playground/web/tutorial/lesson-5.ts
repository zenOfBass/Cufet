// Lesson 5: choosing. The words are tutorial/lesson-5-draft.md's, and that file is where they are
// agreed; change them there first, then here.

import { sameWords } from './judge';
import type { Attempt } from './judge';
import type { Lesson, Line } from './lesson';

const grace = (text: string, answer?: string): Line => ({ who: 'grace', text, ...(answer ? { answer } : {}) });
const hopper = (text: string, answer?: string): Line => ({ who: 'hopper', text, ...(answer ? { answer } : {}) });

const refusedSaying = (a: Attempt, fragment: string) =>
    a.outcome === 'refused' && a.output.includes(fragment);

/** Both sides of a decision are written: an `If`, and an `Otherwise` after it. */
const decides = (code: string) => /^\s*If\b/m.test(code) && /^\s*Otherwise\b/m.test(code);

/** The question asks whether hops IS five, which is right for 5 and 2 and wrong for 6. */
const asksForFive = (code: string) => /If\s+hops\s+is\s+5\b/.test(code);

const OTHER_WAY = ['Hop 2!', 'Keep going!'];
const TIRED = ['Rest!'];
const NOT_TIRED = ['Keep going!'];

const BEAT_2 = 'Define hops as 3.\nState "Hop {hops}!".\nIf hops is 3, state "Rest!".';

export const lesson5: Lesson = {
    number: 5,
    title: 'Choosing',
    steps: [
        { kind: 'enter', who: 'hopper' },
        { kind: 'enter', who: 'grace' },

        // ── Beat 1: a question with an answer ───────────────────────────────────────────────
        { kind: 'say', lines: [hopper('First, you need to know Cufet can answer questions. Watch this.')] },
        { kind: 'write', who: 'hopper', code: 'Define hops as 3.\nState hops is 3.\nState hops is greater than 5.' },
        { kind: 'say', lines: [hopper('What do you think it says, friend?')] },
        {
            kind: 'predict',
            options: ['`true`, then `false`', '`3`, then `5`', '`hops is 3`, then `hops is greater than 5`'],
            correct: 0,
            reactions: [
                [hopper('Spot on! Hops *is* three, and it *isn\'t* greater than five.')],
                [grace("Cufet didn't just say the numbers. It answered the questions: is hops three? Yes. Is it greater than five? No.")],
                [grace("There are no quote marks, so Cufet doesn't say the words. It works out the answers.")],
            ],
            after: [grace('A question like `hops is 3` has an answer: `true` or `false`. An answer like that is called a **fact**.')],
        },

        // ── Beat 2: deciding ────────────────────────────────────────────────────────────────
        { kind: 'write', who: 'hopper', code: BEAT_2 },
        { kind: 'say', lines: [hopper("Here's the deciding part. Bet you can work out what this says.")] },
        {
            kind: 'predict',
            options: ['`Hop 3!`, then `Rest!`', 'Only `Hop 3!`', 'Only `Rest!`'],
            correct: 0,
            // The draft has one remark here whichever was chosen.
            reactions: [[], [], []],
            after: [
                grace("`If` asks a question. When the answer is true, Cufet does the rest of the line. When it's false, Cufet skips it."),
                hopper("Now here's the fun part. Same program, but I've only hopped twice."),
            ],
        },
        { kind: 'write', who: 'hopper', code: BEAT_2.replace('as 3.', 'as 2.') },
        { kind: 'say', lines: [hopper('What now, friend?')] },
        {
            kind: 'predict',
            options: ['`Hop 2!`, then `Rest!`', 'Only `Hop 2!`', 'Nothing at all'],
            correct: 1,
            reactions: [
                [grace('The first line always runs. The `If` asked "is hops three?", the answer was false, so Cufet skipped "Rest!"')],
                [hopper("No rest for me! Hops isn't three, so Cufet skipped it.")],
                [grace('The first line always runs. The `If` asked "is hops three?", the answer was false, so Cufet skipped "Rest!"')],
            ],
            after: [grace('The same program said different things, because of what `hops` was. The program decided.')],
        },

        // ── Beat 3: the other way ───────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                hopper('But when I\'m not tired, I want it to say "Keep going!" For that, there\'s `Otherwise`.'),
                grace('Add a line after the `If`: `Otherwise`, a comma, and what to do. Try it, friend.'),
            ],
        },
        {
            kind: 'exercise',
            goal: OTHER_WAY,
            done: a => sameWords(a, OTHER_WAY) && /^\s*Otherwise\b/m.test(a.code),
            hints: [
                [hopper('Start a new line under the `If`, with the word `Otherwise`.')],
                [grace('After `Otherwise`, a comma, then `state` and the words, just like the `If` line.')],
                [grace('Here it is. Type it yourself:', 'Otherwise, state "Keep going!".')],
            ],
            reactions: [
                {
                    case: 'no comma after Otherwise',
                    when: a => refusedSaying(a, "expected ':', got State") && /^\s*Otherwise\s+state/m.test(a.code),
                    lines: [grace('`Otherwise` needs a comma after it, the same as the `If` line.')],
                },
                {
                    case: 'Else for Otherwise',
                    when: a => refusedSaying(a, "identifier 'Else' must start with a lowercase letter"),
                    lines: [hopper('Some languages say "else". Cufet says `Otherwise`.')],
                },
                {
                    case: 'Otherwise above the If',
                    when: a => refusedSaying(a, 'got Otherwise "Otherwise"'),
                    lines: [grace('`Otherwise` only makes sense after an `If`. Move it below.')],
                },
                {
                    case: 'a second If',
                    when: a => sameWords(a, OTHER_WAY) && !/^\s*Otherwise\b/m.test(a.code),
                    lines: [grace('That works for two. But `Otherwise` catches every other number too, not just two. Try it with `Otherwise`.')],
                },
            ],
            success: [hopper('Keep going! I will!')],
        },

        // ── Beat 4: more than one line ──────────────────────────────────────────────────────
        { kind: 'say', lines: [hopper('One more trick. What if I want to say two things when I\'m tired? Swap the comma for a colon, put the lines underneath, and close it with `Done.`')] },
        {
            kind: 'write', who: 'hopper',
            code: 'Define hops as 2.\nIf hops is 3:\n    State "Rest!".\n    State "Phew.".\nDone.\nOtherwise:\n    State "Keep going!".\nDone.',
        },
        {
            kind: 'say', lines: [
                grace('Each side gets its own `Done.` Good little rabbits always close what they open.'),
                hopper('What do you think it says, friend?'),
            ],
        },
        {
            kind: 'predict',
            options: ['`Keep going!`', '`Rest!`, `Phew.`, then `Keep going!`', '`Rest!`, then `Phew.`'],
            correct: 0,
            reactions: [
                [hopper('Right! Hops is two, so the whole tired part gets skipped.')],
                [grace('Hops is two, not three. So Cufet skips everything between the colon and the first `Done.`, and does the `Otherwise` side.')],
                [grace('Hops is two, not three. So Cufet skips everything between the colon and the first `Done.`, and does the `Otherwise` side.')],
            ],
            after: [hopper('A comma for one thing. A colon and `Done.` for as many as you like.')],
        },

        // ── Beat 5: a blank page, run both ways ─────────────────────────────────────────────
        {
            kind: 'say', lines: [
                hopper('Your turn, friend! I\'ve hopped five times. Write me a program that says "Rest!" when I\'ve hopped more than three times, and "Keep going!" when I haven\'t.'),
                grace('The page is empty. Start with `Define hops as 5.`'),
            ],
        },
        {
            kind: 'exercise',
            start: '',
            goal: TIRED,
            done: a => sameWords(a, TIRED) && decides(a.code) && !asksForFive(a.code),
            hints: [
                [grace('Three lines: give `hops` a value, then `If`, then `Otherwise`.')],
                [hopper('The question is `hops is greater than 3`!')],
                [grace('Here it is. Type it yourself:',
                    'Define hops as 5.\nIf hops is greater than 3, state "Rest!".\nOtherwise, state "Keep going!".')],
            ],
            reactions: [
                {
                    case: 'the > sign',
                    when: a => sameWords(a, TIRED) && decides(a.code) && /If\s+hops\s*>\s*3\b/.test(a.code),
                    lines: [hopper('`>` means "is greater than". Cufet knows both!')],
                    done: true,
                },
                {
                    case: '"than" left out',
                    when: a => refusedSaying(a, 'expected Than'),
                    lines: [grace('It\'s `is greater than`, with "than" on the end.')],
                },
                {
                    case: 'five in quote marks',
                    when: a => refusedSaying(a, 'equality comparison requires matching types'),
                    lines: [grace('`"5"` in quote marks is text, and a number can never be text. Take off the quote marks.')],
                },
                {
                    case: 'no comma after the question',
                    when: a => refusedSaying(a, "expected ':', got State"),
                    lines: [grace('A comma goes after the question, before `state`.')],
                },
                {
                    case: 'asks whether hops is five',
                    when: a => sameWords(a, TIRED) && asksForFive(a.code),
                    lines: [hopper('It works for five and two. But if I hop six times, it says "Keep going!" when I\'m tired! Ask whether it\'s *greater than* three.')],
                },
                {
                    case: 'no period on the end',
                    when: a => refusedSaying(a, "expected '.', got Eof"),
                    lines: [grace('Every instruction ends with a period, even an `If`.')],
                },
            ],
            success: [hopper('Rest! Now the real test. Say I\'ve only hopped twice. Change just the first line to 2, and run it again.')],
        },
        {
            kind: 'exercise',
            goal: NOT_TIRED,
            done: a => sameWords(a, NOT_TIRED) && decides(a.code) && /^\s*Define\s+hops\s+as\s+2\s*\./m.test(a.code),
            hints: [],
            reactions: [],
            success: [
                hopper('It knew when I was tired! Both times!'),
                grace("You didn't touch the `If`, and it still got it right. That's the program deciding. Well done, friend."),
                hopper('Do you think Real rabbits get tired?'),
                grace('We get tired.'),
                hopper('...Huh.'),
            ],
        },

        // ── Before the lesson ends ──────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                hopper('You know what still bugs me? To count three hops, we wrote the same two lines three times.'),
                grace('And a hundred hops would be a hundred copies.'),
                hopper('Not if we let Cufet do the repeating. Here\'s how.'),
            ],
        },

        { kind: 'end', text: 'End of lesson 5.' },
    ],
};
