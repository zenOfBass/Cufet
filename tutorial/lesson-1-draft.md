# Lesson 1, the opening: a first draft

How to read it: lines in *italics* are what happens on screen. Each spoken line is one speech
bubble, kept short enough to fit one. The voices are a first guess: **Hopper** is quick and
delighted by everything; **Grace** is careful and gets things exactly right.

---

## Beat 1: someone's here

*The screen is the editor, empty. The output box below it is empty too. Nothing moves for a
moment. Then an ear appears at the left edge, and Hopper leans in.*

> **Hopper:** Oh! Grace. *Grace.* Somebody's here.

*Grace comes in from the right edge, slower.*

> **Grace:** So there is. Why, hello. I'm Grace.
> 
> **Hopper:** And I'm Hopper!
> 
> **Grace:** What brings you here, friend?
> 
> **Hopper:** Ooo, maybe they heard about Cufet!
>
> **Grace:** Ahh, of course. That must be it. So, you want to learn how to write some programs?
>
> **Hopper:** Oh, you're gonna love it! Cufet is the best. There aren't any other languages like it out there.
> 
> **Grace:** Indeed, Cufet isn't like all those other old stuffy languages that are hard to read and understand. *Cufet is for everyone*.
>
> **Hopper:** We'll show you! Watch.

## Beat 2: the rabbits show how

*Grace hops up to the editor. Code appears as she moves across it:*

```cufet
State "Hello, world!".
```

> **Grace:** That's one instruction. `State` means "say this".
>
> **Hopper:** But it hasn't said anything yet! Look at the box at the bottom. Empty.
>
> **Grace:** After you write something, you still have to run the code to make everything happen. Here, we have a **run button** to press.
>
> **Hopper:** Me first! I wanna press it!

*Hopper bounces on the Run button. The output box shows:*

```output
Hello, world!
```

> **Hopper:** There! The box at the bottom is the **output box**. That's where a program talks
> back.
>
> **Grace:** Up top, we write. Down there, it answers. That simple.

## Beat 3: the first question (Predict)

*Hopper scurries up to the editor and changes one word:*

```cufet
State "Hello, rabbits!".
```

> **Hopper:** I changed one word. I haven't pressed Run yet!
>
> **Grace:** So, before we do: what do you think it'll say?

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `Hello, rabbits!`
- **B.** `Hello, world!`
- **C.** `State "Hello, rabbits!".`

*The player picks one. The Run button lights up.*

> **Hopper:** Now you press it. Go on, it's the best part.

*The player presses Run. The output box shows their guess, and under it:*

```output
Hello, rabbits!
```

**If they chose A:**

> **Hopper:** It said hello to *us!*
>
> **Grace:** It said what the program told it to say. You saw that coming. Well done.

**If they chose B:**

> **Grace:** It said `Hello, world!` last time. But Hopper changed the code, and the computer only follows the code that's there *now*.

**If they chose C:**

> **Grace:** Close. That's the *code*. But `State` only says what's inside the quote marks.

*Whichever they chose, Hopper sums it up:*

> **Hopper:** See? When you change what's in the quotes, you change what `State` says in the **output box**.

## Beat 4: your turn (Modify)

> **Hopper:** Your turn! You write one.
>
> **Grace:** Make it say hello to me: `Hello, Grace!`
>
> **Hopper:** Change the words between the quote marks, then press Run.

*The editor holds `State "Hello, rabbits!".` and the player can type in it now. Done when the
output says hello to Grace:*

```output
Hello, Grace!
```

*Only the words are checked. Punctuation and capital letters are ignored, so `Hello, Grace.` and
`hello grace` both count. A rabbit may remark on a small difference, but it never fails the
player.*

> **Grace:** Hello to you too. That's your first program. You wrote it, and it did what you said.

### The hint ladder

Each hint needs an attempt since the last one.

1. > **Hopper:** Only the part between the quote marks gets said. Change that part.
2. > **Grace:** Leave `State` and both quote marks alone. Swap `rabbits` for my name.
3. > **Grace:** Here it is. Type it yourself, letter by letter:
   >
   > `State "Hello, Grace!".`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| `State "hello, grace!".` | Runs, prints `hello, grace!` | **Done.** **Grace:** That's me! Though I do spell it with a big G. The computer copies you exactly, capital letters and all. |
| `State "Hello, rabbits!".` (unchanged) | Runs, prints `Hello, rabbits!` | **Hopper:** That's still hello to both of us! Change `rabbits` to Grace's name. |
| `State "Hello, Hopper!".` | Runs, prints `Hello, Hopper!` | **Hopper:** Hello! ...Oh, wait. Grace asked first. Do her, then me! |
| `State "Hello, Grace!"` | `Line 1, column 22: expected '.', got Eof "".` | **Grace:** Good little rabbits always end with a period. Every instruction does, like a sentence. Add one at the very end. |
| `State "Hello, Grace!.` | `Line 1, column 22: unterminated string literal.` | **Hopper:** A quote mark went missing! They come in pairs: one before the words, one after. |
| `State Hello, Grace!.` | `Line 1, column 7: identifier 'Hello' must start with a lowercase letter.` | **Hopper:** Both quote marks are gone! Without them the computer doesn't know those are words to say. |
| `Stat "Hello, Grace!".` | `Line 1, column 1: identifier 'Stat' must start with a lowercase letter.` | **Grace:** The first word has to be exactly `State`. One letter off and the computer doesn't know it. |
| *(everything deleted)* | A four-line message about libraries | **Hopper:** It's all gone! Nothing to run. Want me to put the last one back? |

### One more, no answer offered

> **Hopper:** Now me! Now me! Make it say hello to *me!*

*Done when the output says hello to Hopper, judged the same way. There is no answer rung here.*

> **Hopper:** That's me! That's me! Hello, friend!
>
> **Grace:** Very good job. Saying one thing is a fine start, but sometimes you need to stack more than one `State` in a row. Come on, we'll show you.

## Beat 5: more than one (Predict)

*Grace hops up to the editor. Hopper's hello is still there. She adds a line above it:*

```cufet
State "Hello, Grace!".
State "Hello, Hopper!".
```

> **Grace:** There. Two instructions now, one on each line.
>
> **Hopper:** Ooo. Two! What's it gonna do with two?
>
> **Grace:** Good question. What do you think, friend?

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `Hello, Grace!` and then `Hello, Hopper!` under it
- **B.** `Hello, Hopper!` and then `Hello, Grace!` under it
- **C.** Only `Hello, Hopper!`

*The player picks one. The Run button lights up, and the player presses it.*

```output
Hello, Grace!
Hello, Hopper!
```

**If they chose A:**

> **Grace:** Exactly right. Both of them, and in the same order I wrote them.

**If they chose B:**

> **Grace:** Not quite. I'm on the top line, so I get said first.

**If they chose C:**

> **Hopper:** Don't forget Grace! Every `State` gets a turn.

*Whichever they chose, Grace sums it up:*

> **Grace:** The computer starts at the top and works its way down, one instruction at a time. Each `State` gets a line of its own in the **output box**.

## Beat 6: Hopper's joke (put the lines in order)

> **Hopper:** Ooo, I know a joke! I'll write it as a program.
>
> **Grace:** Oh dear.

*Hopper bounces across the editor. Lines land in a jumbled heap.*

> **Hopper:** Aw, shucks, I tripped and dropped them. Friend, can you put my joke back in order?

*The editor shows five tiles, shuffled. The player drags them into order:*

```cufet
State "Lettuce who?".
State "Knock, knock!".
State "Lettuce in, it's cold out here!".
State "Lettuce!".
State "Who's there?".
```

*Done when the output is the joke, in order:*

```output
Knock, knock!
Who's there?
Lettuce!
Lettuce who?
Lettuce in, it's cold out here!
```

> **Hopper:** Lettuce in! Get it? *Let us* in!
>
> **Grace:** ...We get it, Hopper.
>
> **Hopper:** You know you wanna laugh.
>
> **Grace:** Cufet makes it easy to say whatever you need to. But there's a lot more you can use `State` for than just saying words. Come on, we'll show you.

### The hint ladder

1. > **Hopper:** Every knock-knock joke starts the same way. Which line goes on top?
2. > **Grace:** A question comes before its answer. `Who's there?` has to come before `Lettuce!`
3. > **Grace:** Here's the order. Build it yourself:
   >
   > Knock, knock! · Who's there? · Lettuce! · Lettuce who? · Lettuce in, it's cold out here!

### Reactions

| They build | What Cufet does | The rabbits say |
| --- | --- | --- |
| The joke in the wrong order | Runs, prints it out of order | **Hopper:** That's not how it goes! Read it out loud. Does it sound like a joke? |

*End of lesson 1.*

---

## Still open

**Four of Cufet's messages here would baffle a child**, so the rabbits talk over them:
`expected '.', got Eof ""`, `unterminated string literal`, and the two
`must start with a lowercase letter` ones. The emptied-editor message talks about libraries and
declarations. Should the output box show the real message at all in lesson 1, or only the
rabbits' version?
