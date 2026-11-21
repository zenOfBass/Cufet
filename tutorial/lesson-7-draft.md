# Lesson 7, series: a first draft

What's new in this lesson, one idea per beat:

1. A **series** holds a list of things under one name.
2. `the second of`, `the last of` pick one thing out, counting from one.
3. `For each` does the same lines once for every thing in a series.
4. Adding to the series is enough: the loop doesn't change.
5. A blank page: adding up a series with `For each` and `becomes`.

It opens where lesson 6 ended: Hopper has said "Then we need a way to hold a list. And Cufet has
one!"

---

## Beat 1: a series (Predict)

> **Hopper:** It's called a **series**. Here's a list of friends.

*Hopper writes:*

```cufet
Define friends as a series with ("Grace", "Hopper", "friend").
State friends.
```

> **Grace:** That last one is you, friend. What do you think it says?

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `(Grace, Hopper, friend)`
- **B.** Only `Grace`
- **C.** `friends`

*The player picks one and presses Run.*

```output
(Grace, Hopper, friend)
```

**If they chose A:**

> **Hopper:** All three, in the order I wrote them!

**If they chose B:**

> **Grace:** `friends` is the name for the whole series, so Cufet says all of it.

**If they chose C:**

> **Hopper:** No quote marks on `friends`, remember? Cufet says what the name holds.

*Whichever they chose, Grace sums it up:*

> **Grace:** A series keeps a list of things, in order, under one name. The brackets hold the things, with a comma between each.

## Beat 2: one from the series (Predict, twice)

*Hopper changes the second line:*

```cufet
Define friends as a series with ("Grace", "Hopper", "friend").
State "Hello, {the second of friends}!".
```

> **Hopper:** Now I only want one of them. Which one?

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `Hello, Grace!`
- **B.** `Hello, Hopper!`
- **C.** `Hello, friend!`

*The player picks one and presses Run.*

```output
Hello, Hopper!
```

> **Grace:** Cufet counts from one, like you would: Grace is the first, Hopper is the second, you're the third. You can say `the last of friends` too.
>
> **Hopper:** Now a trick question. What about the fourth?

*Hopper changes the second line to `State the fourth of friends.`*

*The output box asks again. Three buttons:*

- **A.** `friend`
- **B.** Nothing at all
- **C.** Cufet stops and explains

*The player picks one and presses Run.*

```output
There's no item 4 — 'friends' has 3 items (you can reach items 1 through 3). This happened on line 2.
```

**If they chose C:**

> **Hopper:** Too smart for me! Only three friends, so there's no fourth.

**If they chose A or B:**

> **Hopper:** Ha! There are only three. Cufet won't make up a fourth. It stops and tells you.

## Beat 3: for each (Predict)

> **Grace:** Now, greeting them all. You could write three `State` lines. Or this.

*Grace writes:*

```cufet
Define friends as a series with ("Grace", "Hopper", "friend").
For each name in friends, repeat:
    State "Hello, {name}!".
Done.
```

> **Hopper:** What will it say, friend?

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `Hello, Grace!`, `Hello, Hopper!`, `Hello, friend!`
- **B.** Only `Hello, Grace!`
- **C.** `Hello, name!` three times

*The player picks one and presses Run.*

```output
Hello, Grace!
Hello, Hopper!
Hello, friend!
```

**If they chose A:**

> **Hopper:** Hello to you too!

**If they chose B:**

> **Grace:** `For each` doesn't stop after the first. It goes round once for every friend in the series.

**If they chose C:**

> **Grace:** `name` is in a hole, the curly brackets, so Cufet fills it in. Each time round, it's the next friend.

*Whichever they chose:*

> **Grace:** `For each` gives every thing in the series a turn. Each time round, `name` stands for the one whose turn it is.
>
> **Hopper:** No counting, no `becomes`. It knows when it runs out!

## Beat 4: one more friend (Modify)

> **Hopper:** Wait, we forgot someone. Lettuce!
>
> **Grace:** Lettuce is lunch, Hopper.
>
> **Hopper:** Lettuce is a friend *and* lunch. Add her to the end of the series, friend. Don't touch the loop.

*The editor holds the program from Beat 3. Done when the output is the three greetings and then
`Hello, Lettuce!`, and the loop is unchanged.*

> **Hopper:** Hello, Lettuce!
>
> **Grace:** The series grew, and the loop went round once more by itself.

### The hint ladder

1. > **Grace:** The friends are all in the first line. Add one more inside the brackets.
2. > **Hopper:** A comma after `"friend"`, then `"Lettuce"` in quote marks!
3. > **Grace:** Here it is. Type it yourself:
   >
   > `Define friends as a series with ("Grace", "Hopper", "friend", "Lettuce").`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| `Lettuce` without quote marks | `Line 1, column 63: identifier 'Lettuce' must start with a lowercase letter.` | **Grace:** Without quote marks, Cufet thinks `Lettuce` is a name. Each friend in the series is text, in quote marks. |
| No comma before `"Lettuce"` | `Line 1, column 62: expected ')', got String "Lettuce".` | **Hopper:** Every friend needs a comma between them and the next! |
| A second `Define friends` line | `That doesn't work: 'friends' is already defined in this scope.` and more | **Grace:** One `Define` per name. Add Lettuce to the line that's already there. |
| A fourth `State "Hello, Lettuce!".` after `Done.` | Prints the right lines, but isn't done | **Hopper:** That works, but the loop should do it! Put Lettuce in the series. |

## Beat 5: a blank page (Make)

*Grace clears the editor and writes one line, `Define hops as a series with (3, 5, 2).`*

> **Hopper:** Your turn, friend! I hopped three times on Monday, five on Tuesday and two on Wednesday. How many is that altogether?
>
> **Grace:** No working it out in your head. I've started the series for you.

*The editor holds one line, `Define hops as a series with (3, 5, 2).` Done when the output is this,
judged on the words and numbers only, and the program uses `For each`:*

```output
I hopped 10 times!
```

> **Hopper:** Ten! My legs knew it was a lot.
>
> **Grace:** A name to keep the total, and `For each` to add every day to it. Well done, friend.

### The hint ladder

1. > **Grace:** Start a total at zero, before the loop.
2. > **Hopper:** Then go through `hops` with `For each`, and each time round, the total becomes the total plus that day's hops. Say it after `Done.`
3. > **Grace:** Here it is. Type it yourself:
   >
   > `Define hops as a series with (3, 5, 2).`
   > `Define total as 0.`
   > `For each count in hops, repeat:`
   > `    The total becomes total + count.`
   > `Done.`
   > `State "I hopped {total} times!".`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| The `State` inside the loop | Runs, prints `I hopped 3 times!`, then 8, then 10 | **Hopper:** Ha, a running score! The last one's right. Move the `State` after `Done.` so it only says the end. |
| No `Define total as 0.` | `That doesn't work: 'total' isn't defined.` and more | **Grace:** The total has to start somewhere. Begin it at zero, before the loop. |
| `Define total as 0.` inside the loop | `That doesn't work: 'total' was defined on line 3, inside a block that has ended.` and more | **Grace:** Cufet explains it: a name made inside the loop is gone after `Done.` Define it before the loop starts. |
| `The total becomes total + hops.` | `That doesn't work: arithmetic requires numbers on both sides.` and more | **Hopper:** `hops` is the whole series! Add the one whose turn it is, the name after `For each`. |
| `For each count of hops, repeat:` | `Line 3, column 16: expected In, got Of "of".` | **Grace:** It's `For each count in hops`, with "in". |
| `State 10.` or `State "I hopped 10 times!".` | Prints the right thing, but isn't done | **Grace:** Right number, but you worked it out yourself. Let `For each` do the adding. |

## Before the lesson ends

> **Hopper:** Ten hops for me! Now do Grace's week.
>
> **Grace:** That's the whole adding-up again, with my numbers. And again for every rabbit we know.
>
> **Hopper:** Unless the adding-up gets a name of its own. Then Cufet can do it for anyone's week.

*End of lesson 7.*

---

## Still open

**Changing a series once it's made** (`Insert`, `Remove`) isn't in this lesson. Beat 4 adds
Lettuce by editing the first line instead. Say if you want `Insert` here.
