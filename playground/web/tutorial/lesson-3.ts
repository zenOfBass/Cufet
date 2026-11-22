// Lesson 3: names. The words are tutorial/lesson-3-draft.md's, and that file is where they are
// agreed; change them there first, then here.

import { outputLines, sameWords, words } from './judge';
import type { Attempt } from './judge';
import type { Lesson, Line } from './lesson';

const grace = (text: string, answer?: string): Line => ({ who: 'grace', text, ...(answer ? { answer } : {}) });
const hopper = (text: string, answer?: string): Line => ({ who: 'hopper', text, ...(answer ? { answer } : {}) });

const refusedSaying = (a: Attempt, fragment: string) =>
    a.outcome === 'refused' && a.output.includes(fragment);

/** The last line the program printed, when it ran. */
const lastLine = (a: Attempt) => a.outcome === 'ran' ? outputLines(a.output).at(-1) ?? '' : '';

/**
 * The sentence says the name `answer` through a hole, spaces inside the brackets allowed.
 *
 * ★ Beat 6 depends on it. Hopper's added `Define answer as 8 * 8.` is refused only because
 * `answer` is already taken, and a program that runs with this hole in it has defined `answer`.
 * (Measured: a separate check that the program defines `answer` was redundant with this one, as a
 * sabotage of it stayed green.)
 */
const holeForAnswer = (code: string) => /\{\s*answer\s*\}/.test(code);

/** The answer is kept under the name `answer`, for the reaction to another name. */
const keepsAnswer = (code: string) => /^\s*Define\s+answer\s+as\b/m.test(code);

const SENTENCE = 'State "The answer is {answer}.".';
const FOLLOWED = ['The answer is 169.'];
const FORTY_NINE = ['The answer is 49.'];

export const lesson3: Lesson = {
    number: 3,
    title: 'Names',
    steps: [
        { kind: 'enter', who: 'hopper' },
        { kind: 'enter', who: 'grace' },

        // ── Beat 1: keeping the answer ──────────────────────────────────────────────────────
        { kind: 'say', lines: [grace("Let's keep that answer this time, friend. Watch.")] },
        { kind: 'write', who: 'grace', code: 'Define answer as 12 * 12.\nState answer.' },
        {
            kind: 'say', lines: [
                grace('The first line works out `12 * 12`, and keeps the answer under a name: `answer`. The second line uses it.'),
                hopper('Notice there are no quote marks on `answer`. So what do you think it says, friend?'),
            ],
        },
        {
            kind: 'predict',
            options: ['`144`', '`answer`', '`144`, and then `144` again under it'],
            correct: 0,
            reactions: [
                [grace('Exactly. Cufet worked out the sum, kept it, and said it when we asked.')],
                [grace("It would have, with quote marks. In quote marks, `\"answer\"` is text. Without them, it's a name, and Cufet says what the name stands for.")],
                [hopper("Only one! `Define` doesn't say anything. It just keeps the answer. `State` does the saying.")],
            ],
            after: [grace('`Define` keeps a value under a **name**. A name kept like this is called a **variable**. After that, wherever you write the name, Cufet uses the value.')],
        },

        // ── Beat 2: using it again ──────────────────────────────────────────────────────────
        { kind: 'write', who: 'grace', code: 'Define answer as 12 * 12.\nState answer + 12.' },
        { kind: 'say', lines: [grace("Now let's use the answer for more math. Type what you think it will say.")] },
        {
            kind: 'predict-lines',
            slots: 1,
            // A wrong guess is never a fail here. Only the likeliest slip gets a remark of its own.
            right: [],
            wrong: [[]],
            slips: [{ slot: 0, guess: '144', lines: [hopper("That's `answer` on its own! The line says `answer + 12`, so it's twelve more.")] }],
            after: [
                hopper("We didn't have to work out a hundred and forty-four all over again!"),
                grace('Cufet kept it for us. A hundred and forty-four, plus twelve.'),
            ],
        },

        // ── Beat 3: saying it in a sentence ─────────────────────────────────────────────────
        { kind: 'say', lines: [hopper('Now, here\'s a trap almost everybody falls into. Say you want a real sentence, like "The answer is 144."')] },
        { kind: 'write', who: 'hopper', code: 'Define answer as 12 * 12.\nState "The answer is answer.".' },
        { kind: 'say', lines: [hopper('What do you think this says, friend?')] },
        {
            kind: 'predict',
            options: ['`The answer is 144.`', '`The answer is answer.`', 'Nothing, because Cufet refuses it'],
            correct: 1,
            reactions: [
                [hopper("That's the trap! Inside quote marks, Cufet says exactly what's written.")],
                [hopper("You didn't fall for it! Inside the quote marks, `answer` is just a word.")],
                [grace("It runs. Cufet is happy to say \"answer\". It just doesn't know you meant the name.")],
            ],
            after: [
                hopper('"The answer is answer." Not very helpful, is it?'),
                grace("Inside quote marks, everything is text, and Cufet says text exactly as it's written. It doesn't look for names in there."),
            ],
        },

        // ── Beat 4: a hole ──────────────────────────────────────────────────────────────────
        { kind: 'say', lines: [grace('Unless you leave a hole for it.')] },
        { kind: 'write', who: 'grace', code: `Define answer as 12 * 12.\n${SENTENCE}` },
        { kind: 'say', lines: [hopper('See the curly brackets? They make a **hole** in the text. Cufet fills the hole with whatever the name stands for.')] },
        { kind: 'player-run', nudge: [grace('Press Run, friend.')] },
        {
            kind: 'say', lines: [
                hopper('A real sentence!'),
                grace('Now change the first line to `13 * 13`, and run it again. Just the first line.'),
            ],
        },
        {
            kind: 'exercise',
            task: 'Change just the first line to `13 * 13`, and run it again.',
            goal: FOLLOWED,
            done: a => sameWords(a, FOLLOWED) && holeForAnswer(a.code),
            hints: [],
            reactions: [
                {
                    case: 'the hole filled in by hand',
                    when: a => sameWords(a, FOLLOWED) && !holeForAnswer(a.code),
                    lines: [grace("That's the right sentence, but now the hole doesn't follow the name. Put `{answer}` back and change only the first line.")],
                },
                {
                    case: 'thirteen in words',
                    when: a => refusedSaying(a, "'thirteen' isn't defined"),
                    lines: [hopper('Numbers are written with digits! Thirteen is `13`.')],
                },
            ],
            success: [grace('The sentence followed along. The hole always says whatever the name is keeping.')],
        },

        // ── Beat 5: a blank page ────────────────────────────────────────────────────────────
        { kind: 'write', who: 'grace', code: '' },
        { kind: 'say', lines: [grace('Your turn, friend. The page is empty. Have Cufet work out seven times seven, keep it as `answer`, and say it in a sentence.')] },
        {
            kind: 'exercise',
            start: '',
            goal: FORTY_NINE,
            done: a => sameWords(a, FORTY_NINE) && holeForAnswer(a.code),
            hints: [
                [grace('Two lines. The first keeps the answer, with `Define`. The second says the sentence.')],
                [hopper('The sentence needs a hole with the name in curly brackets, so Cufet fills in the number!')],
                [grace('Here it is. Type it yourself:', `Define answer as 7 * 7.\n${SENTENCE}`)],
            ],
            reactions: [
                {
                    case: 'no name at all',
                    when: a => sameWords(a, FORTY_NINE) && !/\bDefine\b/.test(a.code),
                    lines: [grace('Right sentence, but you worked it out yourself. Keep `7 * 7` as `answer`, and let Cufet do it.')],
                },
                {
                    case: 'another name',
                    when: a => sameWords(a, FORTY_NINE) && /\bDefine\b/.test(a.code) && !keepsAnswer(a.code),
                    lines: [grace('That works! But I asked you to keep it as `answer`. Use that name, so Hopper can find it.')],
                },
                {
                    case: 'the sum in the quote marks',
                    when: a => words(lastLine(a)) === 'the answer is 7 7',
                    lines: [hopper('The sum is inside the quote marks, so Cufet just says it! Keep the sum with `Define`, and put a hole in the sentence.')],
                },
                {
                    case: 'only the number',
                    when: a => lastLine(a) === '49',
                    lines: [grace('The right number, but Hopper wanted a sentence. Try `"The answer is {answer}."`')],
                },
                {
                    case: 'the name outside the sentence',
                    when: a => refusedSaying(a, 'got Identifier "answer"'),
                    lines: [grace('The name has to go inside the sentence, in a hole: `{answer}`.')],
                },
                {
                    case: 'no "as"',
                    when: a => refusedSaying(a, 'expected As'),
                    lines: [grace('A name and its value are joined by `as`: `Define answer as 7 * 7.`')],
                },
                {
                    case: '= instead of as',
                    when: a => refusedSaying(a, "'=' is comparison, not assignment"),
                    lines: [grace('Cufet says it well itself: a `Define` uses `as`, not `=`.')],
                },
                {
                    case: 'a capital letter',
                    when: a => refusedSaying(a, 'must start with a lowercase letter') && /^\s*Define\s+[A-Z]/m.test(a.code),
                    lines: [hopper('Names start with a small letter! `answer`, not `Answer`.')],
                },
                {
                    case: 'a space in the name',
                    when: a => refusedSaying(a, 'is not a defined type'),
                    lines: [grace("A name can't have a space in it. Use one word, like `answer`.")],
                },
                {
                    case: 'the Define line last',
                    when: a => refusedSaying(a, "'answer' isn't defined"),
                    lines: [grace('The computer reads from the top down, remember? Keep the answer before you use it.')],
                },
                {
                    case: 'a typo in the hole',
                    when: a => refusedSaying(a, "isn't defined"),
                    lines: [hopper("Cufet doesn't know that name! Check the spelling in the curly brackets.")],
                },
            ],
            success: [
                hopper('Forty-nine! In a sentence!'),
                grace('Worked out, kept, and said. Well done, friend.'),
            ],
        },

        // ── Beat 6: one more, and a surprise ────────────────────────────────────────────────
        { kind: 'say', lines: [hopper('One more thing to try, friend. What if we want eight times eight as well? Say we add it to the bottom, like this.')] },
        { kind: 'write', who: 'hopper', append: true, code: `Define answer as 8 * 8.\n${SENTENCE}` },
        { kind: 'say', lines: [grace('Hmm. Friend, will this one run?')] },
        {
            kind: 'predict',
            asks: 'runs',
            options: ['Runs', 'Refused'],
            correct: 1,
            reactions: [
                [grace("It does look like it should. But `Define` makes a *new* name, and there's already one called `answer`.")],
                [grace('Well spotted. A name can only be given once.')],
            ],
            after: [
                hopper('Oh, but look what the message says at the end: "Use \'becomes\'"!'),
                grace('Right, a name *can* change partway through a program.'),
                hopper('I love becoming! It means we have a chance to grow and be something new!'),
            ],
        },

        { kind: 'end', text: 'End of lesson 3.' },
    ],
};
