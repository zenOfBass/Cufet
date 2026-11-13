# Lesson 2, more lines and numbers: a first draft

**Status: a draft to react to. Nothing here is agreed until you say so.** The agreed design is in
[DESIGN.md](DESIGN.md). It follows [lesson 1](lesson-1-draft.md) and is written the same way:
*italics* are what happens on screen, and each spoken line is one speech bubble. Every program
and every Cufet message below was run through the real build on 12 November 2026.

What's new in this lesson, one idea per beat:

1. A program can hold several instructions, and they run from the top down.
2. Putting lines in order (the first tile exercise).
3. Numbers: quote marks mean "say exactly this", no quote marks mean "work it out".
4. The four signs: `+`, `-`, `*`, `/`.
5. A program can be refused, and then none of it runs.
6. A blank page, using words and numbers together.

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

## Beat 2: Hopper's joke (put the lines in order)

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

*Measured: a missing period is reported on the line AFTER the one that's missing it, because
that's where the computer finds the next `State`. The reaction has to point at the right tile.*

## Beat 3: a number (Run, then Predict)

> **Hopper:** Five lines! My joke had five whole lines. I counted.
>
> **Grace:** Cufet can count too, Hopper. It knows numbers. Watch this, friend.

*Grace clears the editor and writes one line:*

```cufet
State 2 + 3.
```

> **Grace:** No quote marks this time. Go on, press Run.

*The player presses Run.*

```output
5
```

> **Hopper:** It did the sum! All by itself!

*Hopper scurries up and puts quote marks around it:*

```cufet
State "2 + 3".
```

> **Hopper:** Now it's got quote marks, like our hellos had.
>
> **Grace:** So what will it say this time?

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `5`
- **B.** `2 + 3`
- **C.** `"2 + 3"`

*The player picks one and presses Run.*

```output
2 + 3
```

**If they chose A:**

> **Grace:** It did the sum last time. But this time the sum is inside quote marks, and that changes everything.

**If they chose B:**

> **Grace:** Spot on. You remembered what quote marks do.

**If they chose C:**

> **Grace:** Nearly. The quote marks only show where the words start and stop. They don't get said themselves.

*Whichever they chose, Grace sums it up:*

> **Grace:** Quote marks mean "say exactly this". Without them, Cufet works out the answer first, and says that.

## Beat 4: the four signs (Predict, typed)

> **Grace:** Cufet can add, take away, multiply and divide. Two of the signs may be new to you.
>
> **Hopper:** The star means "times"! And the leaning line means "divided by"!

*Grace writes four lines:*

```cufet
State 6 + 2.
State 6 - 2.
State 6 * 2.
State 6 / 2.
```

> **Grace:** Four instructions, so four lines of answers. Type what you think each one will be.

*The output box shows four empty slots. The player types a number in each, then presses Run. The
real answers appear beside their guesses:*

```output
8
4
12
3
```

**If all four are right:**

> **Hopper:** Four out of four! You're quicker than Grace!
>
> **Grace:** I wouldn't go that far.

**If the third is wrong:**

> **Hopper:** The star is "times". Six times two is twelve!

**If the fourth is wrong:**

> **Grace:** The leaning line is "divided by". Six shared between two is three each.

**If the first or second is wrong:**

> **Grace:** Have another look at that one. `+` adds, and `-` takes away.

*A wrong guess here is never a fail. Running it shows the answer.*

## Beat 5: will it run? (Predict)

*Hopper writes two lines, looking pleased with himself:*

```cufet
State "Here goes!".
State "2" + 3.
```

> **Hopper:** I put quote marks on just the two. It's still a two, right?
>
> **Grace:** Hmm. What do you think, friend? Will Cufet run this one?

*The output box asks: **Will it run?** Two buttons: **Runs** and **Refused**. The player picks one
and presses Run. The output box shows:*

```output
That doesn't work: arithmetic requires numbers on both sides.
Here on line 2, you're trying to use + with text and number.

If you meant arithmetic, both sides need to be numbers.
If you meant to join text, use 'joined to': "hello" joined to " world".
```

**If they chose Refused:**

> **Grace:** Well spotted. In quote marks, `"2"` isn't a number any more. It's a word that happens to look like one.

**If they chose Runs:**

> **Grace:** It does look like it should. But in quote marks, `"2"` is a word, not a number, and you can't add a word to a number.

*Whichever they chose:*

> **Hopper:** Hey! It didn't even say "Here goes!" That line was fine!
>
> **Grace:** Cufet reads the whole program before it runs any of it. If one line doesn't make sense, it won't start at all. And look: it tells you which line, and why.
>
> **Hopper:** What's "joined to"?
>
> **Grace:** A story for another day.

## Beat 6: a blank page (Make)

> **Hopper:** All that knocking made me hungry. I found four lettuces!
>
> **Grace:** I found five. Friend, this one is all yours. The page is empty.
>
> **Hopper:** Tell everyone how many we've got! And make Cufet do the adding up.

*The editor is empty. Done when the output is this, judged on the words and numbers only:*

```output
Hopper has 4 lettuces!
Grace has 5 lettuces!
Together that makes:
9
```

> **Hopper:** Nine lettuces! That's a feast!
>
> **Grace:** Well done, friend. Words and numbers, four lines, and you wrote it from nothing.

### The hint ladder

1. > **Grace:** Four lines in the output box means four instructions.
2. > **Hopper:** The first three are words, so they need quote marks. The last one is a sum, so it doesn't.
3. > **Grace:** Here it is. Type it yourself:
   >
   > `State "Hopper has 4 lettuces!".`
   > `State "Grace has 5 lettuces!".`
   > `State "Together that makes:".`
   > `State 4 + 5.`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| Last line `State "4 + 5".` | Runs, prints `4 + 5` | **Grace:** In quote marks, Cufet says the sum without working it out. Take the quote marks off that last line. |
| Last line `State 9.` | Runs, prints `9` | **Done.** **Grace:** Nine is right. Next time, write `4 + 5` and let Cufet do the adding for you. |
| Last line `State 4 plus 5.` | `Line 4, column 9: expected '.', got Identifier "plus".` | **Hopper:** Cufet doesn't know "plus" as a word. Use the sign: `+`. |
| Last line `State 4 + 5 = 9.` | Runs, prints `true` | **Grace:** You've asked Cufet whether four and five make nine, and it says that's true! Leave off the `= 9` and it will give you the number. |
| `State "Together that makes: " 9.` | `Line 3, column 31: expected '.', got Number "9".` | **Grace:** One `State` says one thing. Give the nine a `State` of its own, on the next line. |
| No spaces: `State 4+5.` | Runs, prints `9` | **Done.** Spaces around the sign are only there to make it easy to read. |

### One more, no answer offered

> **Hopper:** Wait, wait! I found more! Now I've got seven, and Grace has six.

*Done when the output is the same four lines with the new numbers, ending in `13`.*

> **Grace:** Thirteen. Did you notice you had to change the seven in two places? Once in the words, and once in the sum.
>
> **Hopper:** Every time I find a lettuce! That's a lot of changing.
>
> **Grace:** There's a better way. You can give a number a name, and then use the name. That's for next time.

*End of lesson 2.*

---

## Things this draft found

1. **A refused program prints nothing at all**, not even the lines before the bad one. Measured
   with the two-line program in Beat 5. It makes "Cufet reads the whole program first" something
   the player can see.
2. **Beat 5 is the first warm Cufet message the player meets**, and it's shown as is. Its last
   line mentions `joined to`, which hasn't been taught, so Hopper asks and Grace defers it.
3. **`State 4 + 5 = 9.` runs and prints `true`.** A child might well type that. The reaction
   explains it without teaching facts yet.
4. **Left out on purpose:** which sign goes first (`2 + 3 * 4` is 14), brackets, and answers with
   a fraction (`7 / 2` is `3.5`). Each needs maths a young child may not have. They could go in a
   later lesson or a side note; say if you want any of them here.
5. **`State 9.` counts as done in Beat 6**, with a nudge from Grace. The game could require the
   sum by checking the code, if you'd rather it did.
