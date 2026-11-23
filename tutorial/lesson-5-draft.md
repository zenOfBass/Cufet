# Lesson 5, choosing: a first draft

What's new in this lesson, one idea per beat:

1. A question like `hops is 3` has an answer: `true` or `false`, called a **fact**.
2. `If` does something only when the fact is true. The same program can do different things.
3. `Otherwise` says what to do when it isn't.
4. A colon and `Done.` let each side hold more than one line.
5. A blank page that has to work both ways.

It opens where lesson 4 ended: Hopper has said Cufet can decide things, "like saying 'Rest!' only
when I'm tired", and Grace has said "And every decision starts with a question."

---

## Beat 1: a question with an answer (Predict)

> **Hopper:** First, you need to know Cufet can answer questions. Watch this.

*Hopper writes three lines:*

```cufet
Define hops as 3.
State hops is 3.
State hops is greater than 5.
```

> **Hopper:** What do you think it says, friend?

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `true`, then `false`
- **B.** `3`, then `5`
- **C.** `hops is 3`, then `hops is greater than 5`

*The player picks one and presses Run.*

```output
true
false
```

**If they chose A:**

> **Hopper:** Spot on! Hops *is* three, and it *isn't* greater than five.

**If they chose B:**

> **Grace:** Cufet didn't just say the numbers. It answered the questions: is hops three? Yes. Is it greater than five? No.

**If they chose C:**

> **Grace:** There are no quote marks, so Cufet doesn't say the words. It works out the answers.

*Whichever they chose, Grace sums it up:*

> **Grace:** A question like `hops is 3` has an answer: `true` or `false`. An answer like that is called a **fact**.

## Beat 2: deciding (Predict, twice)

*Hopper changes the program:*

```cufet
Define hops as 3.
State "Hop {hops}!".
If hops is 3, state "Rest!".
```

> **Hopper:** Here's the deciding part. Bet you can work out what this says.

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `Hop 3!`, then `Rest!`
- **B.** Only `Hop 3!`
- **C.** Only `Rest!`

*The player picks one and presses Run.*

```output
Hop 3!
Rest!
```

> **Grace:** `If` asks a question. When the answer is true, Cufet does the rest of the line. When it's false, Cufet skips it.
>
> **Hopper:** Now here's the fun part. Same program, but I've only hopped twice.

*Hopper changes the first line to `Define hops as 2.`*

> **Hopper:** What now, friend?

*The output box asks again. Three buttons:*

- **A.** `Hop 2!`, then `Rest!`
- **B.** Only `Hop 2!`
- **C.** Nothing at all

*The player picks one and presses Run.*

```output
Hop 2!
```

**If they chose B:**

> **Hopper:** No rest for me! Hops isn't three, so Cufet skipped it.

**If they chose A or C:**

> **Grace:** The first line always runs. The `If` asked "is hops three?", the answer was false, so Cufet skipped "Rest!"

*Whichever they chose:*

> **Grace:** The same program said different things, because of what `hops` was. The program decided.

## Beat 3: the other way (Modify)

> **Hopper:** But when I'm not tired, I want it to say "Keep going!" For that, there's `Otherwise`.
>
> **Grace:** Add a line after the `If`: `Otherwise`, a comma, and what to do. Try it, friend.

*The editor holds the program from Beat 2, with `hops` at 2. Done when the output is, on the words
only:*

```output
Hop 2!
Keep going!
```

> **Hopper:** Keep going! I will!

### The hint ladder

1. > **Hopper:** Start a new line under the `If`, with the word `Otherwise`.
2. > **Grace:** After `Otherwise`, a comma, then `state` and the words, just like the `If` line.
3. > **Grace:** Here it is. Type it yourself:
   >
   > `Otherwise, state "Keep going!".`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| `Otherwise state "Keep going!".` | `Line 4, column 11: Cufet needs a comma or a colon before 'state': …` | **Grace:** `Otherwise` needs a comma after it, the same as the `If` line. |
| `Else, state "Keep going!".` | `Line 4, column 1: 'Else' isn't a word Cufet knows to start a line with. Cufet says 'Otherwise'.` | **Hopper:** Some languages say "else". Cufet says `Otherwise`. |
| The `Otherwise` line above the `If` | `Line 3, column 1: expected statement keyword, got Otherwise "Otherwise".` | **Grace:** `Otherwise` only makes sense after an `If`. Move it below. |
| A second `If`: `If hops is 2, state "Keep going!".` | Runs, prints the right lines, but isn't done | **Grace:** That works for two. But `Otherwise` catches every other number too, not just two. Try it with `Otherwise`. |

## Beat 4: more than one line (Predict)

> **Hopper:** One more trick. What if I want to say two things when I'm tired? Swap the comma for a colon, put the lines underneath, and close it with `Done.`

*Hopper changes the program:*

```cufet
Define hops as 2.
If hops is 3:
    State "Rest!".
    State "Phew.".
Done.
Otherwise:
    State "Keep going!".
Done.
```

> **Grace:** Each side gets its own `Done.` Good little rabbits always close what they open.
>
> **Hopper:** What do you think it says, friend?

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `Keep going!`
- **B.** `Rest!`, `Phew.`, then `Keep going!`
- **C.** `Rest!`, then `Phew.`

*The player picks one and presses Run.*

```output
Keep going!
```

**If they chose A:**

> **Hopper:** Right! Hops is two, so the whole tired part gets skipped.

**If they chose B or C:**

> **Grace:** Hops is two, not three. So Cufet skips everything between the colon and the first `Done.`, and does the `Otherwise` side.

*Whichever they chose:*

> **Hopper:** A comma for one thing. A colon and `Done.` for as many as you like.

### If the player experiments

| They write | What Cufet does | The rabbits say |
| --- | --- | --- |
| No `Done.` before `Otherwise:` | `this 'If' opens a block, and 'Otherwise' arrived on line 4 before its 'Done.'.` (and more) | **Grace:** Cufet explains it well: each side closes with its own `Done.` before the next one starts. |
| No `Done.` at the very end | `this 'If' opens a block, and the file ended before its 'Done.'.` (and more) | **Hopper:** Every colon needs a `Done.` to close it! |

## Beat 5: a blank page (Make)

*Grace clears the editor.*

> **Hopper:** Your turn, friend! I've hopped five times. Write me a program that says "Rest!" when I've hopped more than three times, and "Keep going!" when I haven't.
>
> **Grace:** The page is empty. Start with `Define hops as 5.`

*The editor is empty. The output box keeps the task under **Your task:**, "Say "Rest!" when hops is
greater than 3, and "Keep going!" when it isn't. Start with `Define hops as 5.`", and the goal under
**Make it say:**. Done when the output is, on the words only:*

```output
Rest!
```

> **Hopper:** Rest! Now the real test. Say I've only hopped twice. Change just the first line to 2, and run it again.

*The task becomes "Change just the first line to `Define hops as 2.`, and run it again." Done when
the output is:*

```output
Keep going!
```

> **Hopper:** It knew when I was tired! Both times!
>
> **Grace:** You didn't touch the `If`, and it still got it right. That's the program deciding. Well done, friend.
>
> **Hopper:** Do you think Real rabbits get tired?
>
> **Grace:** We get tired.
>
> **Hopper:** ...Huh.

### The hint ladder

1. > **Grace:** Three lines: give `hops` a value, then `If`, then `Otherwise`.
2. > **Hopper:** The question is `hops is greater than 3`!
3. > **Grace:** Here it is. Type it yourself:
   >
   > `Define hops as 5.`
   > `If hops is greater than 3, state "Rest!".`
   > `Otherwise, state "Keep going!".`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| No `If`: just `State "Rest!".` | Runs, prints `Rest!`, but isn't done | **Hopper:** That says "Rest!", but it always will! Ask Cufet whether I've hopped more than three times, with `If`, and let it decide. |
| `If hops is greater 3, ...` | `Line 2, column 20: expected Than, got Number "3".` | **Grace:** It's `is greater than`, with "than" on the end. |
| `If hops > 3, ...` | Runs, prints the right thing | **Done.** **Hopper:** `>` means "is greater than". Cufet knows both! |
| `If hops is "5", ...` | `That doesn't work: equality comparison requires matching types.` and more | **Grace:** `"5"` in quote marks is text, and a number can never be text. Take off the quote marks. |
| `If hops is greater than 3 state "Rest!".` | `Line 2, column 27: Cufet needs a comma or a colon before 'state': a comma for one instruction on this line, or a colon to start a block that ends with 'Done.'.` | **Grace:** A comma goes after the question, before `state`. |
| `If hops is 5, ...` | Prints the right thing for 5 and 2, but isn't done | **Hopper:** It works for five and two. But if I hop six times, it says "Keep going!" when I'm tired! Ask whether it's *greater than* three. |
| No period on the end | `this line needs a period at the end. The file ended before one came.` | **Grace:** Every instruction ends with a period, even an `If`. |

## Before the lesson ends

> **Hopper:** You know what still bugs me? To count three hops, we wrote the same two lines three times.
>
> **Grace:** And a hundred hops would be a hundred copies.
>
> **Hopper:** Not if we let Cufet do the repeating. Here's how.

*End of lesson 5.*
