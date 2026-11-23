// Lesson 6: repeating. The words are tutorial/lesson-6-draft.md's, and that file is where they are
// agreed; change them there first, then here.

import { outputLines, sameWords } from './judge';
import type { Attempt } from './judge';
import type { Lesson, Line } from './lesson';

const grace = (text: string, answer?: string): Line => ({ who: 'grace', text, ...(answer ? { answer } : {}) });
const hopper = (text: string, answer?: string): Line => ({ who: 'hopper', text, ...(answer ? { answer } : {}) });

const refusedSaying = (a: Attempt, fragment: string) =>
    a.outcome === 'refused' && a.output.includes(fragment);

const lines = (a: Attempt) => a.outcome === 'ran' ? outputLines(a.output) : [];

/** The program repeats with a loop rather than by copies. */
const loops = (code: string) => /^\s*(While|Repeat)\b/m.test(code);

const hops = (count: number) => Array.from({ length: count }, (_, i) => `Hop ${i + 1}!`);
const TEN = hops(10);
const SIX_WITH_HALFWAY = [...hops(3), 'Halfway!', 'Hop 4!', 'Hop 5!', 'Hop 6!'];

const loop = (limit: number, body: string) =>
    `Define hops as 0.\nWhile hops is less than ${limit}, repeat:\n${body}\nDone.`;
const COUNT_THEN_SAY = '    The hops becomes hops + 1.\n    State "Hop {hops}!".';
const SAY_THEN_COUNT = '    State "Hop {hops}!".\n    The hops becomes hops + 1.';

export const lesson6: Lesson = {
    number: 6,
    title: 'Repeating',
    steps: [
        { kind: 'enter', who: 'hopper' },
        { kind: 'enter', who: 'grace' },

        // ── Beat 1: while ───────────────────────────────────────────────────────────────────
        { kind: 'say', lines: [hopper("Remember counting my hops? The same two lines, written out three times. Here's the same thing with `While`.")] },
        { kind: 'write', who: 'hopper', code: loop(3, COUNT_THEN_SAY) },
        { kind: 'say', lines: [hopper('Go on, friend. What does it say?')] },
        {
            kind: 'predict',
            options: ['`Hop 1!`, `Hop 2!`, `Hop 3!`', 'Only `Hop 1!`', 'Only `Hop 3!`'],
            correct: 0,
            reactions: [
                [hopper('Three hops, and I only wrote them once!')],
                [grace("Cufet didn't stop after one time round. It went back and asked again.")],
                [grace("Cufet didn't stop after one time round. It went back and asked again.")],
            ],
            after: [grace('`While` asks its question. If the answer is true, Cufet does everything down to `Done.`, then goes back and asks again. When the answer is false, it moves on.')],
        },

        // ── Beat 2: ten hops ────────────────────────────────────────────────────────────────
        { kind: 'say', lines: [hopper('Now the fun part. Make it ten hops, friend. You only need to change one thing.')] },
        {
            kind: 'exercise',
            task: 'Make it ten hops. You only need to change one thing.',
            goal: TEN,
            done: a => sameWords(a, TEN) && loops(a.code),
            hints: [
                [grace('Which number decides when it stops?')],
                [hopper('Change the 3 in the `While` line!')],
                [grace('Here it is. Type it yourself:', 'While hops is less than 10, repeat:')],
            ],
            reactions: [
                {
                    case: 'the start changed instead',
                    when: a => a.outcome === 'ran' && /less\s+than\s+3\b/.test(a.code) && !/Define\s+hops\s+as\s+0\s*\./.test(a.code),
                    lines: [hopper("That's where I start counting from! It's the 3 in the `While` line that says when to stop.")],
                },
                {
                    case: 'ten copies, no While',
                    when: a => sameWords(a, TEN) && !loops(a.code),
                    lines: [grace("That's the long way. Let `While` do the repeating, and change one number.")],
                },
            ],
            success: [
                hopper('Ten hops! Three copies would have been six lines. Ten copies would have been twenty.'),
                grace("And with `While`, it's still five."),
                hopper('Ten hops would take me right up to the garden wall.'),
                grace('Not that close, Hopper. Not with the people in there.'),
            ],
        },

        // ── Beat 3: a sneaky swap ───────────────────────────────────────────────────────────
        { kind: 'say', lines: [hopper("Here's a sneaky one. I've swapped the two lines inside.")] },
        { kind: 'write', who: 'hopper', code: loop(3, SAY_THEN_COUNT) },
        {
            kind: 'predict',
            options: [
                '`Hop 1!`, `Hop 2!`, `Hop 3!`',
                '`Hop 0!`, `Hop 1!`, `Hop 2!`',
                '`Hop 0!`, `Hop 1!`, `Hop 2!`, `Hop 3!`',
            ],
            correct: 1,
            reactions: [
                [hopper('Got you! Now it says the hop before counting it, so it starts at zero. And when hops reaches 3, the question is false, so there\'s no "Hop 3!"')],
                [hopper("You didn't fall for it!")],
                [hopper('Got you! Now it says the hop before counting it, so it starts at zero. And when hops reaches 3, the question is false, so there\'s no "Hop 3!"')],
            ],
            after: [grace('The lines inside still run top to bottom, every time round. Their order matters just as much as before.')],
        },

        // ── Beat 4: repeat until ────────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                grace('There\'s a second way to repeat. `Repeat` does the lines first, and asks its question at the end.'),
                hopper('Remember the countdown tiles? Seven of them, for three-two-one. Watch.'),
            ],
        },
        {
            kind: 'write', who: 'hopper',
            code: 'Define count as 3.\nRepeat:\n    State count.\n    The count becomes count - 1.\nuntil count is 0.\nState "Go!".',
        },
        {
            kind: 'predict',
            options: ['`3`, `2`, `1`, `Go!`', '`3`, `2`, `1`, `0`, `Go!`', 'Only `3`, then `Go!`'],
            correct: 0,
            reactions: [
                [hopper('Lift-off!')],
                [grace('When the count reached 0, the question at the bottom was true, so Cufet stopped before saying it.')],
                [grace('After the first time round, the count was 2. "Is it 0?" No, so round again.')],
            ],
            after: [
                grace('Careful with the two: `While` keeps going *while* the answer is true. `until` stops as soon as it\'s true.'),
                hopper('And this one could count down from a hundred without getting any longer.'),
            ],
        },

        // ── Beat 5: a blank page ────────────────────────────────────────────────────────────
        { kind: 'write', who: 'grace', code: '' },
        {
            kind: 'say', lines: [
                hopper('Your turn, friend! Six hops. And when I\'m halfway, after hop three, say "Halfway!"'),
                grace("The page is empty. You'll want a loop, with an `If` inside it."),
            ],
        },
        {
            kind: 'exercise',
            start: '',
            task: 'Six hops, and "Halfway!" after hop three. Use a loop, with an `If` inside it.',
            goal: SIX_WITH_HALFWAY,
            done: a => sameWords(a, SIX_WITH_HALFWAY) && loops(a.code),
            hints: [
                [grace('Start like the first hop counter, but going to 6 instead of 3.')],
                [hopper('Inside the loop, after saying the hop, ask: is hops 3? If so, say "Halfway!"')],
                [grace('Here it is. Type it yourself:', loop(6, `${COUNT_THEN_SAY}\n    If hops is 3, state "Halfway!".`))],
            ],
            reactions: [
                {
                    case: 'the < sign',
                    when: a => sameWords(a, SIX_WITH_HALFWAY) && /While\s+hops\s*<\s*6\b/.test(a.code),
                    lines: [hopper('`<` means "is less than". Cufet knows both!')],
                    done: true,
                },
                {
                    case: 'the If after Done',
                    when: a => sameWords(a, hops(6)),
                    lines: [hopper("By the time the loop's done, I've hopped six times, so I'm never at three! The `If` goes inside the loop.")],
                },
                {
                    case: 'said before counted',
                    when: a => lines(a)[0] === 'Hop 0!',
                    lines: [hopper('Hop zero again! Count the hop before you say it.')],
                },
                {
                    case: 'no repeat',
                    when: a => refusedSaying(a, "expected ',', got Colon"),
                    lines: [grace('A `While` line ends with `, repeat:`.')],
                },
                {
                    case: 'no comma before repeat',
                    when: a => refusedSaying(a, "Cufet needs a comma before 'repeat'"),
                    lines: [grace('A comma goes before `repeat`.')],
                },
                {
                    case: '"is" left out',
                    when: a => refusedSaying(a, "expected ',', got Less"),
                    lines: [grace('It\'s `is less than`, with "is" in front.')],
                },
                {
                    case: 'no Done at the end',
                    when: a => refusedSaying(a, "the file ended before its 'Done.'"),
                    lines: [hopper('Every colon needs a `Done.` to close it!')],
                },
                {
                    case: 'six hops by hand',
                    when: a => sameWords(a, SIX_WITH_HALFWAY) && !loops(a.code),
                    lines: [grace("Right lines, but that's the long way. Let a loop do the repeating.")],
                },
            ],
            success: [
                hopper('Halfway, and then all the way! Phew.'),
                grace('A loop to repeat, and an `If` to decide, each time round. Well done, friend.'),
            ],
        },

        // ── Before the lesson ends ──────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                hopper('I can hop as many times as I like now!'),
                grace('Counting is one kind of repeating. But say you wanted to greet a whole list of friends, one after another.'),
                hopper('Then we need a way to hold a list. And Cufet has one!'),
            ],
        },

        { kind: 'end', text: 'End of lesson 6.' },
    ],
};
