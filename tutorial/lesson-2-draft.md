# Lesson 2, numbers: a first draft

**Status: a draft to react to. Nothing here is agreed until you say so.** The agreed design is in
[DESIGN.md](DESIGN.md). It follows [lesson 1](lesson-1-draft.md) and is written the same way:
*italics* are what happens on screen, and each spoken line is one speech bubble. Every program
and every Cufet message below was run through the real build on 12 November 2026.

What's new in this lesson, one idea per beat:

1. Numbers: quote marks mean "say exactly this", no quote marks mean "work it out".
2. The four signs: `+`, `-`, `*`, `/`.
3. A program can be refused, and then none of it runs.
4. A blank page, using text and numbers together.

It opens where lesson 1 ended, just after Hopper's joke.

---

## Beat 1: a number (Run, then Predict)

> **Hopper:** You said there's more! What else can State say?
>
> **Grace:** **Numbers**. Watch this, friend.

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

> **Grace:** Quote marks mean "say exactly this". Anything in quote marks is called **text**. Without them, Cufet works out the answer first, and says that.

## Beat 2: the four signs (Predict, typed)

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

## Beat 3: will it run? (Predict)

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

> **Grace:** Well spotted. In quote marks, `"2"` isn't a number any more. It's text that happens to look like one.

**If they chose Runs:**

> **Grace:** It does look like it should. But in quote marks, `"2"` is text, not a number, and you can't add text to a number.

*Whichever they chose:*

> **Hopper:** Hey! It didn't even say "Here goes!" That line was fine!
>
> **Grace:** Cufet reads the whole program before it runs any of it. If one line doesn't make sense, it won't start at all. And look: it tells you which line, and why.
>
> **Hopper:** Hmmmm, but what's "joined to"?
>
> **Grace:** That's a story for another day.

## Beat 4: a blank page (Make)

> **Grace:** Hopper, quick: what's twelve times twelve?
>
> **Hopper:** Uhhh. Um. Twelve... and then... more twelves?
>
> **Grace:** Friend, help him out. The page is empty. Write a program that asks the question, then lets Cufet work out the answer.

*The editor is empty. Done when the output is this, judged on the words and numbers only:*

```output
What is 12 times 12?
144
```

> **Hopper:** A hundred and forty-four! ...I knew that.
>
> **Grace:** Of course you did. Well done, friend. Text and a number, and you wrote it from nothing.

### The hint ladder

1. > **Grace:** Two lines in the output box means two instructions. The question, then the answer.
2. > **Hopper:** The question is text, so it needs quote marks. The answer is a sum, so it doesn't. And "times" is the star!
3. > **Grace:** Here it is. Type it yourself:
   >
   > `State "What is 12 times 12?".`
   > `State 12 * 12.`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| Last line `State "12 * 12".` | Runs, prints `12 * 12` | **Hopper:** That's just the question again! Take the quote marks off, so Cufet works it out. |
| Last line `State 144.` | Runs, prints `144` | **Done.** **Grace:** That's right, but you did the sum yourself. Next time, write `12 * 12` and let Cufet do the work. |
| Last line `State 12 times 12.` | `Line 2, column 10: expected '.', got Identifier "times".` | **Hopper:** Cufet doesn't know "times" as a word. Use the star: `*`. |
| Last line `State 12 x 12.` | `Line 2, column 10: expected '.', got Identifier "x".` | **Grace:** In Cufet, "times" is written with a star, `*`, not an x. |
| Last line `State 12 * 12 = 144.` | Runs, prints `true` | **Grace:** You've asked Cufet whether twelve twelves make 144, and it says that's true! Leave off the `= 144` and it will give you the number. |
| `State "What is 12 times 12?" 12 * 12.` | `Line 1, column 30: expected '.', got Number "12".` | **Grace:** One `State` says one thing. Give the sum a `State` of its own, on the next line. |
| No spaces: `State 12*12.` | Runs, prints `144` | **Done.** Spaces around the sign are only there to make it easy to read. |

### One more, no answer offered

> **Hopper:** My turn! Grace, what's thirteen times thirteen? Ha! Friend, don't tell her. Write it!

*Done when the output is the question with 13, then `169`.*

> **Grace:** One hundred and sixty-nine. Well played, both of you. But did you notice, friend? You had to change twelve into thirteen in four places. Twice in the text, and twice in the sum.
>
> **Hopper:** Four! That's a lot of changing.
>
> **Grace:** There's a better way. You can give a number a name, and then use the name. That's for next time.

*End of lesson 2.*

---

## Things this draft found

1. **A refused program prints nothing at all**, not even the lines before the bad one. Measured
   with the two-line program in Beat 3. It makes "Cufet reads the whole program first" something
   the player can see.
2. **Beat 3 is the first warm Cufet message the player meets**, and it's shown as is. Its last
   line mentions `joined to`, which hasn't been taught, so Hopper asks and Grace defers it.
3. **`State 12 * 12 = 144.` runs and prints `true`.** A child might well type that. The reaction
   explains it without teaching facts yet.
4. **Left out on purpose:** which sign goes first (`2 + 3 * 4` is 14), brackets, and answers with
   a fraction (`7 / 2` is `3.5`). Each needs maths a young child may not have. They could go in a
   later lesson or a side note; say if you want any of them here.
5. **`State 144.` counts as done in Beat 4**, with a nudge from Grace. The game could require the
   sum by checking the code, if you'd rather it did.
