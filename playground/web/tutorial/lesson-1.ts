// Lesson 1: saying words. The words are tutorial/lesson-1-draft.md's, and that file is where they
// are agreed; change them there first, then here.

import { sameText, sameWords } from './judge';
import type { Attempt } from './judge';
import type { Lesson, Line, Reaction } from './lesson';

const grace = (text: string, answer?: string): Line => ({ who: 'grace', text, ...(answer ? { answer } : {}) });
const hopper = (text: string, answer?: string): Line => ({ who: 'hopper', text, ...(answer ? { answer } : {}) });

const refusedSaying = (a: Attempt, fragment: string) =>
    a.outcome === 'refused' && a.output.includes(fragment);

/**
 * The draft's reactions to a broken `State` line. They are about the line, not the greeting, so
 * the "one more" variation after Beat 4 takes them too.
 */
const brokenLine: Reaction[] = [
    {
        case: 'no period at the end',
        when: a => refusedSaying(a, "expected '.', got Eof"),
        lines: [grace('Good little rabbits always end with a period. Every instruction does, like a sentence. Add one at the very end.')],
    },
    {
        case: 'a quote mark missing',
        when: a => refusedSaying(a, 'unterminated string literal'),
        lines: [hopper('A quote mark went missing! They come in pairs: one before the words, one after.')],
    },
    {
        case: 'both quote marks gone',
        when: a => refusedSaying(a, 'must start with a lowercase letter') && /^\s*State\s+[^"\s]/.test(a.code),
        lines: [hopper("Both quote marks are gone! Without them the computer doesn't know those are words to say.")],
    },
    {
        case: 'the first word is not State',
        when: a => refusedSaying(a, 'must start with a lowercase letter') && !/^\s*State\b/.test(a.code),
        lines: [grace("The first word has to be exactly `State`. One letter off and the computer doesn't know it.")],
    },
    {
        case: 'everything deleted',
        when: a => a.outcome === 'empty',
        lines: [hopper("It's all gone! Nothing to run. Want me to put the last one back?")],
        offerRestore: true,
    },
];

export const lesson1: Lesson = {
    number: 1,
    title: 'Saying words',
    steps: [
        // ── Beat 1: someone's here ──────────────────────────────────────────────────────────
        { kind: 'enter', who: 'hopper' },
        { kind: 'say', lines: [hopper("Oh! Grace. *Grace.* Somebody's here.")] },
        { kind: 'enter', who: 'grace' },
        {
            kind: 'say', lines: [
                grace("So there is. Why, hello. I'm Grace."),
                hopper("And I'm Hopper!"),
                grace('What brings you here, friend?'),
                hopper('Ooo, maybe they heard about Cufet!'),
                grace('Ahh, of course. That must be it. So, you want to learn how to write some programs?'),
                hopper("Oh, you're gonna love it! Cufet is the best. There aren't any other languages like it out there."),
                grace("Indeed, Cufet isn't like all those other old stuffy languages that are hard to read and understand. *Cufet is for everyone*."),
                hopper("We'll show you! Watch."),
            ],
        },

        // ── Beat 2: the rabbits show how ────────────────────────────────────────────────────
        { kind: 'write', who: 'grace', code: 'State "Hello, world!".' },
        {
            kind: 'say', lines: [
                grace('That\'s one instruction. `State` means "say this".'),
                hopper("But it hasn't said anything yet! Look at the box under the code. Empty."),
                grace('After you write something, you still have to run the code to make everything happen. Here, we have a **run button** to press.'),
                hopper('Me first! I wanna press it!'),
            ],
        },
        { kind: 'rabbit-run', who: 'hopper' },
        {
            kind: 'say', lines: [
                hopper("There! The box under the code is the **output box**. That's where a program talks back."),
                grace('Up top, we write. Down there, it answers. That simple.'),
            ],
        },

        // ── Beat 3: the first question ──────────────────────────────────────────────────────
        { kind: 'write', who: 'hopper', code: 'State "Hello, rabbits!".' },
        {
            kind: 'say', lines: [
                hopper("I changed one word. I haven't pressed Run yet!"),
                grace("So, before we do: what do you think it'll say?"),
            ],
        },
        {
            kind: 'predict',
            options: ['`Hello, rabbits!`', '`Hello, world!`', '`State "Hello, rabbits!".`'],
            correct: 0,
            nudge: [hopper("Now you press it. Go on, it's the best part.")],
            reactions: [
                [
                    hopper('It said hello to *us!*'),
                    grace('It said what the program told it to say. You saw that coming. Well done.'),
                ],
                [grace("It said `Hello, world!` last time. But Hopper changed the code, and the computer only follows the code that's there *now*.")],
                [grace("Close. That's the *code*. But `State` only says what's inside the quote marks.")],
            ],
            after: [hopper("See? When you change what's in the quotes, you change what `State` says in the **output box**.")],
        },

        // ── Beat 4: your turn ───────────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                hopper('Your turn! You write one.'),
                grace('Make it say hello to me: `Hello, Grace!`'),
                hopper('Change the words between the quote marks, then press Run.'),
            ],
        },
        {
            kind: 'exercise',
            goal: ['Hello, Grace!'],
            done: a => sameWords(a, ['Hello, Grace!']),
            hints: [
                [hopper('Only the part between the quote marks gets said. Change that part.')],
                [grace('Leave `State` and both quote marks alone. Swap `rabbits` for my name.')],
                [grace('Here it is. Type it yourself, letter by letter:', 'State "Hello, Grace!".')],
            ],
            reactions: [
                {
                    case: 'all in small letters',
                    when: a => sameWords(a, ['Hello, Grace!']) && !sameText(a, ['Hello, Grace!']) && !a.output.includes('Grace'),
                    lines: [grace("That's me! Though I do spell it with a big G. The computer copies you exactly, capital letters and all.")],
                    done: true,
                },
                {
                    case: 'unchanged',
                    when: a => sameWords(a, ['Hello, rabbits!']),
                    lines: [hopper("That's still hello to both of us! Change `rabbits` to Grace's name.")],
                },
                {
                    case: 'hello to Hopper first',
                    when: a => sameWords(a, ['Hello, Hopper!']),
                    lines: [hopper('Hello! ...Oh, wait. Grace asked first. Do her, then me!')],
                },
                ...brokenLine,
            ],
            success: [grace("Hello to you too. That's your first program. You wrote it, and it did what you said.")],
        },

        // ── One more, no answer offered ─────────────────────────────────────────────────────
        { kind: 'say', lines: [hopper('Now me! Now me! Make it say hello to *me!*')] },
        {
            kind: 'exercise',
            goal: ['Hello, Hopper!'],
            done: a => sameWords(a, ['Hello, Hopper!']),
            hints: [],
            reactions: brokenLine,
            success: [
                hopper("That's me! That's me! Hello, friend!"),
                grace("Very good job. Saying one thing is a fine start, but sometimes you need to stack more than one `State` in a row. Come on, we'll show you."),
            ],
        },

        // ── Beat 5: more than one ───────────────────────────────────────────────────────────
        { kind: 'write', who: 'grace', code: 'State "Hello, Grace!".\nState "Hello, Hopper!".' },
        {
            kind: 'say', lines: [
                grace('There. Two instructions now, one on each line.'),
                hopper("Ooo. Two! What's it gonna do with two?"),
                grace('Good question. What do you think, friend?'),
            ],
        },
        {
            kind: 'predict',
            options: [
                '`Hello, Grace!` and then `Hello, Hopper!` under it',
                '`Hello, Hopper!` and then `Hello, Grace!` under it',
                'Only `Hello, Hopper!`',
            ],
            correct: 0,
            reactions: [
                [grace('Exactly right. Both of them, and in the same order I wrote them.')],
                [grace("Not quite. I'm on the top line, so I get said first.")],
                [hopper("Don't forget Grace! Every `State` gets a turn.")],
            ],
            after: [grace('The computer starts at the top and works its way down, one instruction at a time. Each `State` gets a line of its own in the **output box**.')],
        },

        // ── Beat 6: Hopper's joke ───────────────────────────────────────────────────────────
        {
            kind: 'say', lines: [
                hopper("Ooo, I know a joke! I'll write it as a program."),
                grace('Oh dear.'),
            ],
        },
        {
            kind: 'parsons',
            tiles: [
                'State "Lettuce who?".',
                'State "Knock, knock!".',
                'State "Lettuce in, it\'s cold out here!".',
                'State "Lettuce!".',
                'State "Who\'s there?".',
            ],
            intro: [hopper('Aw, shucks, I tripped and dropped them. Friend, can you put my joke back in order?')],
            done: a => sameWords(a, ['Knock, knock!', "Who's there?", 'Lettuce!', 'Lettuce who?', "Lettuce in, it's cold out here!"]),
            hints: [
                [hopper('Every knock-knock joke starts the same way. Which line goes on top?')],
                [grace("A question comes before its answer. `Who's there?` has to come before `Lettuce!`")],
                [grace("Here's the order. Build it yourself:", "Knock, knock! · Who's there? · Lettuce! · Lettuce who? · Lettuce in, it's cold out here!")],
            ],
            reactions: [{
                case: 'the joke in the wrong order',
                when: a => a.outcome === 'ran',
                lines: [hopper("That's not how it goes! Read it out loud. Does it sound like a joke?")],
            }],
            success: [
                hopper('Lettuce in! Get it? *Let us* in!'),
                grace('...We get it, Hopper.'),
                hopper('You know you wanna laugh.'),
                grace("Cufet makes it easy to say whatever you need to. But there's a lot more you can use `State` for than just saying words. Come on, we'll show you."),
            ],
        },

        { kind: 'end', text: 'End of lesson 1.' },
    ],
};
