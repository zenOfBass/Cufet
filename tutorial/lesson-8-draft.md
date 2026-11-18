# Lesson 8, functions: a first draft

What's new in this lesson, one idea per beat:

1. `Bind` gives some lines a name of their own: a **function**. `cast` runs it.
2. One function, used as many times as you like, with different things each time.
3. `Return` gives the answer back and leaves the function straight away.
4. Writing a `cast` yourself.
5. A blank page: a function that decides, using `If` and `Otherwise`.

It opens where lesson 7 ended: Hopper has said "Unless the adding-up gets a name of its own. Then
Cufet can do it for anyone's week."

---

## Beat 1: a name for the adding-up (Predict)

> **Hopper:** Here's the adding-up from last time, with a name of its own: `add-up`.

*Hopper writes:*

```cufet
Bind number to add-up, given (the series of numbers days):
    Define total as 0.
    For each count in days, repeat:
        The total becomes total + count.
    Done.
    Return total.
Done.
State cast add-up on (a series with (3, 5, 2)).
```

> **Hopper:** The inside should look familiar. What do you think it says?

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `10`
- **B.** `(3, 5, 2)`
- **C.** Nothing at all

*The player picks one and presses Run.*

```output
10
```

**If they chose A:**

> **Hopper:** Ten hops, same as last time!

**If they chose B:**

> **Grace:** The series went *into* `add-up`, and the total came back out.

**If they chose C:**

> **Grace:** The last line runs `add-up`, so the lines inside it run too.

*Whichever they chose, Grace takes it a line at a time:*

> **Grace:** `Bind` makes a **function**: lines with a name of their own. `number` says it gives back a number. `given` says what it needs: a series of numbers, called `days`.
>
> **Hopper:** `Return` hands the answer back. And `cast` is how you run it. `cast add-up on`, and then what to add up, in brackets.
>
> **Grace:** And good little rabbits always give back what they promise. This one promised a `number`.

## Beat 2: anyone's week (Predict)

> **Hopper:** Now Grace's week. One more line, and that's it.

*Hopper adds a line to the bottom:*

```cufet
State cast add-up on (a series with (4, 1, 6)).
```

> **Grace:** What will it say this time, friend?

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `10`, then `11`
- **B.** Only `11`
- **C.** `10`, then `10` again

*The player picks one and presses Run.*

```output
10
11
```

**If they chose A:**

> **Hopper:** Grace out-hopped me!
>
> **Grace:** I've been practising.
>
> **Hopper:** Real rabbits can hop all day, you know.
>
> **Grace:** One day, Hopper.

**If they chose B:**

> **Grace:** The first line is still there, so `add-up` runs twice: my week and Hopper's.

**If they chose C:**

> **Grace:** Each time it runs, `days` is whatever series you give it. The second time, it's my week.

*Whichever they chose:*

> **Grace:** The adding-up is written once. Every `cast` uses it again, with different days.

## Beat 3: a sneaky return (Predict)

> **Hopper:** Bet you can't spot this one. I've put one more `Return` inside the loop.

*Hopper changes the function, and takes out Grace's line:*

```cufet
Bind number to add-up, given (the series of numbers days):
    Define total as 0.
    For each count in days, repeat:
        The total becomes total + count.
        Return total.
    Done.
    Return total.
Done.
State cast add-up on (a series with (3, 5, 2)).
```

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `10`
- **B.** `3`
- **C.** `3`, `8`, then `10`

*The player picks one and presses Run.*

```output
3
```

**If they chose B:**

> **Hopper:** Spotted it!

**If they chose A or C:**

> **Hopper:** Caught you! The first time round, the total is 3, and `Return` hands it straight back. The rest of the week never gets added.

*Whichever they chose:*

> **Grace:** `Return` gives the answer back and leaves the function, right there. So it goes where the answer is ready.

*Hopper takes the extra `Return` out again.*

## Beat 4: Lettuce's week (Modify)

> **Hopper:** We forgot someone again. Lettuce's week!
>
> **Grace:** Lettuce doesn't hop, Hopper.
>
> **Hopper:** Then it's zero, zero and zero. Add a line that adds up Lettuce's week, friend.

*The editor holds the program from Beat 2. Done when the output is `10`, `11`, then `0`, and the
new line uses `cast add-up`.*

> **Hopper:** Zero! Told you she was resting.

### The hint ladder

1. > **Grace:** Copy the shape of the last line, with Lettuce's days.
2. > **Hopper:** `State cast add-up on`, then a series of three zeros!
3. > **Grace:** Here it is. Type it yourself:
   >
   > `State cast add-up on (a series with (0, 0, 0)).`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| `State cast add-up on (0, 0, 0).` | `That doesn't work: 'add-up' expects 1 argument(s), but you passed 3.` and more | **Grace:** `add-up` needs one thing: a series. Put `a series with` in front of the zeros. |
| `Cast add-up on (a series with (0, 0, 0)).`, no `State` | Runs, but prints only `10` and `11` | **Hopper:** It added them up, but nobody said the answer! Put `State` in front. |
| `State 0.` | Prints the right lines, but isn't done | **Grace:** Right, but you worked it out yourself. Let `add-up` do it. |

## Beat 5: a blank page (Make)

> **Hopper:** Your turn, friend! Remember "Rest!" and "Keep going!"? Make it a function called `cheer`. Give it a number of hops, and it gives back the right one.
>
> **Grace:** I've written the last two lines for you. Write `cheer` above them.

*The editor holds two lines:*

```cufet
State cast cheer on (5).
State cast cheer on (2).
```

*Done when the output is this, judged on the words only, and the program has a `Bind` for `cheer`:*

```output
Rest!
Keep going!
```

> **Hopper:** It knows when I'm tired, every time I ask!
>
> **Grace:** A function that decides. Well done, friend.

### The hint ladder

1. > **Grace:** It gives back text, and it's given a number. So it starts `Bind text to cheer, given (the number hops):`
2. > **Hopper:** Inside, it's the `If` and `Otherwise` from before, but with `return` instead of `state`!
3. > **Grace:** Here it is. Type it yourself:
   >
   > `Bind text to cheer, given (the number hops):`
   > `    If hops is greater than 3, return "Rest!".`
   > `    Otherwise, return "Keep going!".`
   > `Done.`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| `state` instead of `return` inside | `That doesn't work: 'cheer' is declared to give back a text, but it can reach its end without returning one.` and more | **Hopper:** That says it, but `cheer` has to *give it back*. Use `return`, and the `State` lines at the bottom do the saying. |
| No `Otherwise` line | The same message | **Grace:** When hops isn't more than three, `cheer` has nothing to give back. Add the `Otherwise`. |
| `Bind number to cheer, …` | `That doesn't work: this function is declared to give back a number.` and more | **Grace:** "Rest!" is text, so it's `Bind text to cheer`. |
| `If hops is greater than 3, "Rest!".`, with no `return` | `Line 2, column 32: expected statement keyword, got String "Rest!".` | **Grace:** Cufet needs to know what to do with "Rest!". Put `return` in front of it. |
| No `Done.` | `this 'Bind' opens a block, and the file ended before its 'Done.'.` and more | **Hopper:** Every colon needs a `Done.`! |
| `If` and `Otherwise` with no function, and the two `State cast` lines deleted | Prints the right lines, but isn't done | **Grace:** Right lines, but the task was a function called `cheer`. Put them inside a `Bind`. |

## Before the lesson ends

> **Hopper:** Now I want a function that just says hello to a friend. It doesn't need to give anything back.
>
> **Grace:** Then it gives back nothing. And Cufet has a word for nothing.
>
> **Hopper:** My favourite kind of word.
>
> **Grace:** If we're going to learn nothing properly, we should ask Maria. Nobody knows more about nothing.
>
> **Hopper:** To the pond!

*End of lesson 8.*

