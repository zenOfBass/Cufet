// Lesson 2: numbers. The words are tutorial/lesson-2-draft.md's, and that file is where they are
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

const QUIZ = ['What is 12 * 12?', '144'];

/**
 * Beat 4 is done only when CUFET did the sum: `State 144.` prints the right thing, but the player
 * worked it out. True when `12 * 12` appears outside quote marks, with or without spaces.
 */
const cufetDidTheSum = (code: string) => /12\s*\*\s*12/.test(code.replace(/"[^"\n]*"/g, '""'));

/** Beat 2's reaction to a wrong `+` or `-`: one line for either, said once. */
const plusOrMinus = [grace('Have another look at that one. `+` adds, and `-` takes away.')];

export const lesson2: Lesson = {
    number: 2,
    title: 'Numbers',
    steps: [
        // Straight on from lesson 1, so both rabbits are usually here already; on a page opened
        // at lesson 2 they arrive first.
        { kind: 'enter', who: 'hopper' },
        { kind: 'enter', who: 'grace' },

        // ── Beat 1: a number ────────────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                hopper("You said there's more! What else can State say?"),
                grace('**Numbers**. Watch this, friend.'),
            ],
        },
        { kind: 'write', who: 'grace', code: 'State 2 + 3.' },
        { kind: 'player-run', nudge: [grace('No quote marks this time. Go on, press Run.')] },
        { kind: 'say', lines: [hopper('It did the sum! All by itself!')] },
        { kind: 'write', who: 'hopper', code: 'State "2 + 3".' },
        {
            kind: 'say', lines: [
                hopper("Now it's got quote marks, like our hellos had."),
                grace('So what will it say this time?'),
            ],
        },
        {
            kind: 'predict',
            options: ['`5`', '`2 + 3`', '`"2 + 3"`'],
            correct: 1,
            reactions: [
                [grace('It did the sum last time. But this time the sum is inside quote marks, and that changes everything.')],
                [grace('Spot on. You remembered what quote marks do.')],
                [grace("Nearly. The quote marks only show where the words start and stop. They don't get said themselves.")],
            ],
            after: [grace('Quote marks mean "say exactly this". Anything in quote marks is called **text**. Without them, Cufet works out the answer first, and says that.')],
        },

        // ── Beat 2: the four signs ──────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                grace('Cufet can add, take away, multiply and divide. Two of the signs may be new to you.'),
                hopper('The star means "times"! And the leaning line means "divided by"!'),
            ],
        },
        { kind: 'write', who: 'grace', code: 'State 6 + 2.\nState 6 - 2.\nState 6 * 2.\nState 6 / 2.' },
        { kind: 'say', lines: [grace('Four instructions, so four lines of answers. Type what you think each one will be.')] },
        {
            kind: 'predict-lines',
            slots: 4,
            right: [
                hopper("Four out of four! You're quicker than Grace!"),
                grace("I wouldn't go that far."),
            ],
            wrong: [
                plusOrMinus,
                plusOrMinus,
                [hopper('The star is "times". Six times two is twelve!')],
                [grace('The leaning line is "divided by". Six shared between two is three each.')],
            ],
        },

        // ── Beat 3: will it run? ────────────────────────────────────────────────────────────
        { kind: 'write', who: 'hopper', code: 'State "Here goes!".\nState "2" + 3.' },
        {
            kind: 'say', lines: [
                hopper("Here's a sneaky one. I put quote marks around just the two."),
                grace('Hmm. What do you think, friend? Will Cufet run this one?'),
            ],
        },
        {
            kind: 'predict',
            asks: 'runs',
            options: ['Runs', 'Refused'],
            correct: 1,
            reactions: [
                [grace("It does look like it should. But in quote marks, `\"2\"` is text, not a number, and you can't add text to a number.")],
                [grace("Well spotted. In quote marks, `\"2\"` isn't a number any more. It's text that happens to look like one.")],
            ],
            after: [
                hopper('And look: it didn\'t even say "Here goes!", even though that line was fine.'),
                grace("Cufet reads the whole program before it runs any of it. If one line doesn't make sense, it won't start at all. And look: it tells you which line, and why."),
                hopper('See the bit about "joined to" at the end? That\'s another trick. We\'ll get to it later.'),
            ],
        },

        // ── Beat 4: a blank page ────────────────────────────────────────────────────────────
        // Grace empties the page before she says it is empty (play-tested: it emptied only after).
        { kind: 'write', who: 'grace', code: '' },
        {
            kind: 'say', lines: [
                hopper("Pop quiz, friend! What's twelve times twelve? No fair working it out in your head."),
                grace("The page is empty. Make the output box say what it's showing you: Hopper's question, then Cufet's answer."),
            ],
        },
        {
            kind: 'exercise',
            start: '',
            goal: QUIZ,
            done: a => sameWords(a, QUIZ) && cufetDidTheSum(a.code),
            hints: [
                [grace('Two lines in the output box means two instructions. The question, then the answer.')],
                [hopper("The question is text, so it needs quote marks. The answer is a sum, so it doesn't. And \"times\" is the star!")],
                [grace('Here it is. Type it yourself:', 'State "What is 12 * 12?".\nState 12 * 12.')],
            ],
            reactions: [
                {
                    case: 'the sum in quote marks',
                    when: a => words(lastLine(a)) === '12 12',
                    lines: [hopper("That's just the question again! Take the quote marks off, so Cufet works it out.")],
                },
                {
                    case: 'worked it out by hand',
                    when: a => sameWords(a, QUIZ) && !cufetDidTheSum(a.code),
                    lines: [grace("That's the right number, but you worked it out yourself. Write `12 * 12` and let Cufet do the work.")],
                },
                {
                    case: '"times" as a word',
                    when: a => refusedSaying(a, "found the name 'times'"),
                    lines: [hopper('Cufet doesn\'t know "times" as a word. Use the star: `*`.')],
                },
                {
                    case: 'an x for times',
                    when: a => refusedSaying(a, "found the name 'x'"),
                    lines: [grace('In Cufet, "times" is written with a star, `*`, not an x.')],
                },
                {
                    case: '= 144 on the end',
                    when: a => lastLine(a) === 'true',
                    lines: [grace("You've asked Cufet whether twelve twelves make 144, and it says that's true! Leave off the `= 144` and it will give you the number.")],
                },
                {
                    case: 'both on one line',
                    when: a => refusedSaying(a, "expected '.', but found the number"),
                    lines: [grace('One `State` says one thing. Give the sum a `State` of its own, on the next line.')],
                },
            ],
            success: [
                hopper("Nailed it! And you didn't have to work out a thing yourself."),
                grace('Well done, friend. Text and a number, and you wrote it from nothing.'),
            ],
        },

        // ── Before the lesson ends ──────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                hopper('Look at that! One hundred and forty-four!'),
                grace('Yup. But the moment the code tells you the answer, it goes away. What if we want to use the answers for our math?'),
                hopper("Oh, yeah! We can **name variables**! It's super super useful. We'll show you!"),
            ],
        },

        { kind: 'end', text: 'End of lesson 2.' },
    ],
};
