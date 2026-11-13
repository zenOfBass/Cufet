# Lesson 2, more than one line: a first draft

**Status: a draft to react to. Nothing here is agreed until you say so.** The agreed design is in
[DESIGN.md](DESIGN.md). It follows [lesson 1](lesson-1-draft.md) and is written the same way:
*italics* are what happens on screen, and each spoken line is one speech bubble. Every program
and every Cufet message below was run through the real build on 12 November 2026.

The one new idea: a program can hold several instructions, and they run from the top down.

It opens where lesson 1 ended, with `State "Hello, Hopper!".` still in the editor.

---

## Beat 1: more than one (Predict)

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

## Beat 2: Hopper wants to go first (Modify)

> **Hopper:** Hold on a tick. How come Grace gets to go first?
>
> **Grace:** Because I put my line on top, Hopper.
>
> **Hopper:** Well, I wanna go first this time! Hey, friend, can you fix it so it says hello to me, and *then* Grace?

*The editor holds the two lines from Beat 1. Done when the output says hello to Hopper and then
to Grace, judged on the words only:*

```output
Hello, Hopper!
Hello, Grace!
```

> **Hopper:** First! I'm first!
>
> **Grace:** So you are. The order you write them in is the order they happen in. Always.

### The hint ladder

1. > **Grace:** The top line is said first. So whose line needs to be on top?
2. > **Hopper:** Swap the names! Put mine in the first line and Grace's in the second.
3. > **Grace:** Here it is. Type it yourself:
   >
   > `State "Hello, Hopper!".`
   > `State "Hello, Grace!".`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| *(nothing changed)* | Prints Grace, then Hopper | **Hopper:** Grace is still first! The top line goes first, so I need to be up there. |
| Both lines say `Hello, Hopper!` | Prints Hopper twice | **Grace:** Two hellos for Hopper and none for me? The second line should be mine. |
| Only `State "Hello, Hopper!".` | Prints Hopper only | **Grace:** You've left me out! It needs two lines: Hopper's, then mine. |
| First line has no period at the end | `Line 2, column 1: expected '.', got State "State".` | **Grace:** The first instruction never ended, so the computer ran straight into the second one. Put a period at the end of line 1. |
| Both instructions on one line | Runs, prints both lines correctly | **Done.** **Grace:** That works too. The period is what ends an instruction. But one to a line is much easier to read. |

*Measured: a missing period is reported on the line AFTER the one that's missing it, because
that's where the computer finds the next `State`. The reaction has to say "line 1".*

## Beat 3: Hopper's joke (put the lines in order)

> **Hopper:** Ooo, I know a joke! I'll write it as a program.
>
> **Grace:** Oh dear.

*Hopper bounces across the editor. Lines land in a jumbled heap.*

> **Hopper:** Aw, shucks, I tripped and dropped them. Friend, can you put my joke back in order?
>
> **Grace:** Careful. I count one line too many in that heap.

*The editor shows six tiles, shuffled. The player drags them into order. One doesn't belong:*

```cufet
State "Lettuce who?".
State "Knock, knock!".
State "Lettuce in, it's cold out here!".
State "Who's there?"
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

### The hint ladder

1. > **Hopper:** Every knock-knock joke starts the same way. Which line goes on top?
2. > **Grace:** Two of the tiles look almost the same. Look closely at how each one ends.
3. > **Grace:** Here's the order. Build it yourself:
   >
   > Knock, knock! · Who's there? · Lettuce! · Lettuce who? · Lettuce in, it's cold out here!

### Reactions

| They build | What Cufet does | The rabbits say |
| --- | --- | --- |
| The joke in the wrong order | Runs, prints it out of order | **Hopper:** That's not how it goes! Read it out loud. Does it sound like a joke? |
| The joke using the tile with no period | `Line 3, column 1: expected '.', got State "State".` | **Grace:** One of those `Who's there?` tiles never ends its instruction. Use the other one. |
| All six tiles | Refused, because the tile with no period is in there | **Grace:** One tile too many. One of them doesn't belong in the joke at all. |

## Beat 4: a blank page (Make)

> **Hopper:** I wanna race! Grace, race me!
>
> **Grace:** Then someone has to start us off. Friend, this one is all yours. The page is empty.
>
> **Hopper:** Make it say "Ready!", then "Set!", then "Go!" Each one on a line of its own!

*The editor is empty. Done when the output is, on the words only:*

```output
Ready!
Set!
Go!
```

*On Go, both rabbits tear off around the edge of the screen. Hopper wins by an ear.*

> **Hopper:** I won! I won!
>
> **Grace:** You started three hops early. But well done, friend. You wrote that one from nothing.

### The hint ladder

1. > **Grace:** Three lines in the output box means three instructions.
2. > **Hopper:** Each one starts with `State`, then the word in quote marks, then a period.
3. > **Grace:** Here it is. Type it yourself:
   >
   > `State "Ready!".`
   > `State "Set!".`
   > `State "Go!".`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| `State "Ready! Set! Go!".` | Runs, prints it all on one line | **Grace:** All the right words, but on a single line. One `State` makes one line. You need three. |
| `State "Ready!" "Set!" "Go!".` | `Line 1, column 16: expected '.', got String "Set!".` | **Grace:** One `State` says one thing. Give each word a `State` of its own. |
| The lines in the wrong order | Runs, prints them out of order | **Hopper:** I can't go before I'm ready! Top to bottom, remember? |
| A blank line between instructions | Runs, prints the three lines | **Done.** Blank lines in the code change nothing. |

### One more, no answer offered

> **Hopper:** Again! Again! But this time do a countdown. "Three!", "Two!", "One!", and *then* "Go!"

*Done when the output is those four words, in that order.*

> **Grace:** Nicely done. Though you wrote those numbers as words, did you notice? Cufet knows real numbers too. That's for next time.
>
> **Hopper:** Next time! Don't go far, friend!

*End of lesson 2.*
