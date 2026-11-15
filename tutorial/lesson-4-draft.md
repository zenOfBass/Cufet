# Lesson 4, becomes: a first draft

**Status: a draft to react to. Nothing here is agreed until you say so.** The agreed design is in
[DESIGN.md](DESIGN.md). It follows [lesson 3](lesson-3-draft.md) and is written the same way:
*italics* are what happens on screen, and each spoken line is one speech bubble. Every program
and every Cufet message below was run through the real build on 15 November 2026.

What's new in this lesson, one idea per beat:

1. `becomes` gives a name a new value, from that line down.
2. A name can become something worked out from its own old value.
3. Putting `becomes` lines in order (a countdown, with tiles).
4. A blank page: counting up, in sentences.

It opens where lesson 3 ended: Hopper has just said "I love becoming! It means we have a chance to
grow and be something new!"

---

## Beat 1: becoming something else (Predict)

> **Hopper:** And I'll show you how! Remember the program Cufet refused? Watch this.

*Hopper changes the third line of the program from lesson 3:*

```cufet
Define answer as 7 * 7.
State "The answer is {answer}.".
The answer becomes 8 * 8.
State "The answer is {answer}.".
```

> **Hopper:** `Define` gives a name for the first time. `becomes` gives a name that already has one a new value.
>
> **Grace:** And we write `The` in front, so the line starts with a capital, like a sentence.
>
> **Hopper:** Now, bet you can't guess what it says, friend!

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `The answer is 49.`, then `The answer is 64.`
- **B.** `The answer is 64.` twice
- **C.** `The answer is 49.` twice

*The player picks one and presses Run.*

```output
The answer is 49.
The answer is 64.
```

**If they chose A:**

> **Hopper:** Got it in one! The first sentence came before the change, and the second came after.

**If they chose B:**

> **Grace:** Cufet works from the top down. When it said the first sentence, the answer hadn't changed yet.

**If they chose C:**

> **Hopper:** Nope, it changed! The second one says 64.

*Whichever they chose, Grace sums it up:*

> **Grace:** `becomes` changes what a name stands for, from that line down. Everything above it saw the old value.

## Beat 2: one more than itself (Predict, typed)

*Grace clears the editor and writes three lines:*

```cufet
Define answer as 7 * 7.
The answer becomes answer + 1.
State "The answer is {answer}.".
```

> **Hopper:** Ooo, this one's my favorite. "The answer becomes answer plus one." Looks impossible, right? Nothing can be one more than itself!
>
> **Grace:** Type what you think it will say, friend.

*The player types a guess, then presses Run.*

```output
The answer is 50.
```

> **Hopper:** Here's the trick. `becomes` doesn't mean "is the same as". Cufet works out the right side first, using the answer as it is right now. Forty-nine, plus one.
>
> **Grace:** And then the answer becomes that. It was forty-nine, and then it *became* fifty.

*A wrong guess is never a fail. Running it shows the answer.*

## Beat 3: a countdown (put the lines in order)

> **Hopper:** I know what that's good for. A countdown! Three, two, one, go!

*Hopper tips a pile of tiles into the editor. Some of them are the same.*

> **Hopper:** Your turn, friend. Put them in order! Three, two, one, and then go!

*The editor shows seven tiles, shuffled:*

```cufet
State count.
The count becomes count - 1.
State "Go!".
State count.
Define count as 3.
The count becomes count - 1.
State count.
```

*Done when the output is:*

```output
3
2
1
Go!
```

> **Hopper:** Go! Go go go!
>
> **Grace:** He'll be back. Well done, friend.

### The hint ladder

1. > **Grace:** A name has to be given before anything else can use it. Which tile gives it?
2. > **Hopper:** Say the number, then take one away, then say it again!
3. > **Grace:** Here's the order. Build it yourself:
   >
   > `Define count as 3.` · `State count.` · `The count becomes count - 1.` · `State count.` ·
   > `The count becomes count - 1.` · `State count.` · `State "Go!".`

### Reactions

| They build | What Cufet does | The rabbits say |
| --- | --- | --- |
| Two `State count.` tiles in a row | Runs, prints the same number twice | **Hopper:** It said the same number twice! Something has to change it in between. |
| `Define count as 3.` not first | `That doesn't work: 'count' isn't defined.` and three more lines, or `'count' isn't defined on line 1 — use Define to create it first, then becomes to change it.` | **Grace:** The name has to be given before anything uses it. Put the `Define` on top. |
| `State "Go!".` not last | Runs, prints "Go!" too early | **Hopper:** Hey, I went too early! "Go!" comes after one. |

## Beat 4: a blank page (Make)

> **Hopper:** I'm back! Now count my hops! Hop one, hop two, hop three!
>
> **Grace:** The page is empty, friend. Start a name at zero, and make it one bigger before each hop.

*The editor is empty. Done when the output is this, judged on the words and numbers only:*

```output
Hop 1!
Hop 2!
Hop 3!
```

> **Hopper:** Three hops! I counted every one. Well, Cufet did.
>
> **Grace:** You started at nothing, and became something new three times over. Well done, friend.

### The hint ladder

1. > **Grace:** Start with `Define hops as 0.` Then, for each hop, make it one bigger and say it.
2. > **Hopper:** Making it bigger is `The hops becomes hops + 1.` Saying it needs a hole: `{hops}`!
3. > **Grace:** Here it is. Type it yourself:
   >
   > `Define hops as 0.`
   > `The hops becomes hops + 1.`
   > `State "Hop {hops}!".`
   > `The hops becomes hops + 1.`
   > `State "Hop {hops}!".`
   > `The hops becomes hops + 1.`
   > `State "Hop {hops}!".`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| `State` before `becomes` each time | Runs, prints `Hop 0!` first | **Hopper:** Hop zero? I haven't hopped yet! Make it one bigger *before* you say it. |
| Starts at `Define hops as 1.` | Runs, the first line is `Hop 2!` | **Grace:** It's made one bigger before the first hop, so start one lower, at zero. |
| `State "Hop hops!".` | Runs, prints `Hop hops!` | **Hopper:** "Hop hops"! The name needs a hole, in curly brackets. |
| `The hops is hops + 1.` | `Line 2, column 10: expected Becomes, got Is "is".` | **Grace:** Changing a name uses `becomes`, not `is`. |
| `The hops = hops + 1.` | `'=' is comparison, not assignment. Did you mean 'hops becomes ...' (update) or 'Define hops as ...' (introduce)?` | **Grace:** Cufet says it well itself: changing a name uses `becomes`. |
| `Define hops as hops + 1.` | `That doesn't work: 'hops' is already defined in this scope.` and more | **Hopper:** That's the one Cufet refused before! `Define` is only for the first time. After that, it's `becomes`. |
| A typo: `The hosp becomes hops + 1.` | `'hosp' isn't defined on line 2 — use Define to create it first, then becomes to change it. Did you mean 'hops'?` | **Hopper:** Cufet even guessed what you meant! |

## Before the lesson ends

> **Grace:** So far, Cufet does every line, every time, top to bottom.
>
> **Hopper:** But here's a secret, friend: it can decide things too. Like saying "Rest!" only when I'm tired.
>
> **Grace:** Come on, we'll show you.

*End of lesson 4.*

---

## Things this draft found

1. **`The hops is hops + 1.` gets `expected Becomes, got Is "is"`.** "Is" is exactly the word a
   child reaches for, and the message shows parser vocabulary. Another one for the roadmap list.
2. **Most `becomes` mistakes already get warm messages**, better than `Define`'s: `=` suggests
   `becomes`, a missing `Define` says to use one, and a typo gets "Did you mean 'hops'?"
3. **Changing a number name to text** says "Variables keep their type for life." "Type" hasn't
   been taught. It's not in any exercise here, but a player could hit it.
4. **`answer becomes 8 * 8.` without `The` also runs.** The lesson writes `The` because Cufet's
   style capitalises the first word of a line. Beat 1 says so in one line.
5. **The ending's hook is weak in the same way lesson 2's was at first:** in a fixed program,
   you always know what will happen, so deciding isn't needed yet. It's a curiosity hook, not
   a felt limitation. Lesson 5 may need to bring in something the program can't know ahead of
   time.
