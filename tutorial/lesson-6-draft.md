# Lesson 6, repeating: a first draft

**Status: a draft to react to. Nothing here is agreed until you say so.** The agreed design is in
[DESIGN.md](DESIGN.md). It follows [lesson 5](lesson-5-draft.md) and is written the same way:
*italics* are what happens on screen, and each spoken line is one speech bubble. Every program
and every Cufet message below was run through the real build on 15 November 2026.

What's new in this lesson, one idea per beat:

1. `While` repeats lines for as long as its question is true.
2. Repeating ten times instead of three is one change.
3. The lines inside still run top to bottom, every time round.
4. `Repeat … until` does the lines first and asks at the end.
5. A blank page: a loop with an `If` inside it.

Series and `For each` come next, in lesson 7: a series is a new idea of its own.

It opens where lesson 5 ended: Hopper has said "Not if we let Cufet do the repeating. Here's how."

---

## Beat 1: while (Predict)

> **Hopper:** Remember counting my hops? The same two lines, written out three times. Here's the same thing with `While`.

*Hopper writes:*

```cufet
Define hops as 0.
While hops is less than 3, repeat:
    The hops becomes hops + 1.
    State "Hop {hops}!".
Done.
```

> **Hopper:** Go on, friend. What does it say?

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `Hop 1!`, `Hop 2!`, `Hop 3!`
- **B.** Only `Hop 1!`
- **C.** Only `Hop 3!`

*The player picks one and presses Run.*

```output
Hop 1!
Hop 2!
Hop 3!
```

**If they chose A:**

> **Hopper:** Three hops, and I only wrote them once!

**If they chose B or C:**

> **Grace:** Cufet didn't stop after one time round. It went back and asked again.

*Whichever they chose, Grace sums it up:*

> **Grace:** `While` asks its question. If the answer is true, Cufet does everything down to `Done.`, then goes back and asks again. When the answer is false, it moves on.

## Beat 2: ten hops (Modify)

> **Hopper:** Now the fun part. Make it ten hops, friend. You only need to change one thing.

*The editor holds the program from Beat 1. Done when the output is `Hop 1!` through `Hop 10!`, and
the program still uses `While`.*

> **Hopper:** Ten hops! Three copies would have been six lines. Ten copies would have been twenty.
>
> **Grace:** And with `While`, it's still five.

### The hint ladder

1. > **Grace:** Which number decides when it stops?
2. > **Hopper:** Change the 3 in the `While` line!
3. > **Grace:** Here it is. Type it yourself:
   >
   > `While hops is less than 10, repeat:`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| Changes `Define hops as 0.` instead | Runs, prints fewer hops, or none | **Hopper:** That's where I start counting from! It's the 3 in the `While` line that says when to stop. |
| Copies the lines out ten times, without `While` | Prints the right hops, but isn't done | **Grace:** That's the long way. Let `While` do the repeating, and change one number. |

## Beat 3: a sneaky swap (Predict)

> **Hopper:** Here's a sneaky one. I've swapped the two lines inside.

*Hopper changes the program back to 3, with the lines swapped:*

```cufet
Define hops as 0.
While hops is less than 3, repeat:
    State "Hop {hops}!".
    The hops becomes hops + 1.
Done.
```

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `Hop 1!`, `Hop 2!`, `Hop 3!`
- **B.** `Hop 0!`, `Hop 1!`, `Hop 2!`
- **C.** `Hop 0!`, `Hop 1!`, `Hop 2!`, `Hop 3!`

*The player picks one and presses Run.*

```output
Hop 0!
Hop 1!
Hop 2!
```

**If they chose B:**

> **Hopper:** You didn't fall for it!

**If they chose A or C:**

> **Hopper:** Got you! Now it says the hop before counting it, so it starts at zero. And when hops reaches 3, the question is false, so there's no "Hop 3!"

*Whichever they chose:*

> **Grace:** The lines inside still run top to bottom, every time round. Their order matters just as much as before.

## Beat 4: repeat until (Predict)

> **Grace:** There's a second way to repeat. `Repeat` does the lines first, and asks its question at the end.
>
> **Hopper:** Remember the countdown tiles? Seven of them, for three-two-one. Watch.

*Hopper writes:*

```cufet
Define count as 3.
Repeat:
    State count.
    The count becomes count - 1.
until count is 0.
State "Go!".
```

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `3`, `2`, `1`, `Go!`
- **B.** `3`, `2`, `1`, `0`, `Go!`
- **C.** Only `3`, then `Go!`

*The player picks one and presses Run.*

```output
3
2
1
Go!
```

**If they chose A:**

> **Hopper:** Lift-off!

**If they chose B:**

> **Grace:** When the count reached 0, the question at the bottom was true, so Cufet stopped before saying it.

**If they chose C:**

> **Grace:** After the first time round, the count was 2. "Is it 0?" No, so round again.

*Whichever they chose:*

> **Grace:** Careful with the two: `While` keeps going *while* the answer is true. `until` stops as soon as it's true.
>
> **Hopper:** And this one could count down from a hundred without getting any longer.

## Beat 5: a blank page (Make)

> **Hopper:** Your turn, friend! Six hops. And when I'm halfway, after hop three, say "Halfway!"
>
> **Grace:** The page is empty. You'll want a loop, with an `If` inside it.

*The editor is empty. Done when the output is this, judged on the words and numbers only, and the
program uses a loop:*

```output
Hop 1!
Hop 2!
Hop 3!
Halfway!
Hop 4!
Hop 5!
Hop 6!
```

> **Hopper:** Halfway, and then all the way! Phew.
>
> **Grace:** A loop to repeat, and an `If` to decide, each time round. Well done, friend.

### The hint ladder

1. > **Grace:** Start like Beat 1, but going to 6 instead of 3.
2. > **Hopper:** Inside the loop, after saying the hop, ask: is hops 3? If so, say "Halfway!"
3. > **Grace:** Here it is. Type it yourself:
   >
   > `Define hops as 0.`
   > `While hops is less than 6, repeat:`
   > `    The hops becomes hops + 1.`
   > `    State "Hop {hops}!".`
   > `    If hops is 3, state "Halfway!".`
   > `Done.`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| The `If` after `Done.` | Runs, prints six hops and no "Halfway!" | **Hopper:** By the time the loop's done, I've hopped six times, so I'm never at three! The `If` goes inside the loop. |
| `State` before `becomes` | Runs, prints `Hop 0!` first, and "Halfway!" after hop 2 | **Hopper:** Hop zero again! Count the hop before you say it. |
| No `repeat`: `While hops is less than 6:` | `Line 2, column 26: expected ',', got Colon ":".` | **Grace:** A `While` line ends with `, repeat:`. |
| No comma: `While hops is less than 6 repeat:` | `Line 2, column 27: expected ',', got Repeat "repeat".` | **Grace:** A comma goes before `repeat`. |
| `While hops less than 6, repeat:` | `Line 2, column 12: expected ',', got Less "less".` | **Grace:** It's `is less than`, with "is" in front. |
| No `Done.` at the end | `this 'repeat' opens a block, and the file ended before its 'Done.'.` (and more) | **Hopper:** Every colon needs a `Done.` to close it! |
| `While hops < 6, repeat:` | Runs, prints the right thing | **Done.** **Hopper:** `<` means "is less than". Cufet knows both! |
| Six hops written out by hand, without a loop | Prints the right lines, but isn't done | **Grace:** Right lines, but that's the long way. Let a loop do the repeating. |

## Before the lesson ends

> **Hopper:** I can hop as many times as I like now!
>
> **Grace:** Counting is one kind of repeating. But say you wanted to greet a whole list of friends, one after another.
>
> **Hopper:** Then Cufet needs a way to hold a list. And it has one!

*End of lesson 6.*

---

## Still open

**A loop that never ends.** The interpreter has no time limit, so a player's `While` whose question
never turns false would freeze the game. The game needs its own limit, and a rabbit reaction for
it. This draft never shows such a loop.
