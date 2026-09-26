# Cufet, from the beginning

A way *in* to the language. It assumes nothing and goes in order.

| If you want to know | Read |
| --- | --- |
| What Cufet is, and why you might care | [README.md](../README.md) |
| How to use a feature | [REFERENCE.md](REFERENCE.md) |
| Exactly what the rules are | [GRAMMAR.md](GRAMMAR.md) |

It teaches by breaking things first. The first thing anybody meets in a new language is a refusal,
and Cufet's refusals are written to be read — so learning to read them is the shortest way in.

> Every program in this file is run by the test suite, and every message beneath one is compared to
> what the language actually says. If a lesson claims Cufet prints something, Cufet prints it.

---

## Lesson 1 — Saying something, and the first refusal

Here is a program that counts something and says so. It does not work.

```cufet-refused
Define carrots as 3.
State "Hopper has " joined to carrots.
```
```output
That doesn't work: you can only join text to text.
Here on line 2, you're trying to join text to a number.

Convert the number first: use 'converted to text'.
For example: "score: " joined to n converted to text.
```

### Read the refusal

Every refusal is built the same way. Three parts are always there, and two more turn up when they
have something to add:

| Part | What it says here | Always? |
| --- | --- | --- |
| **The rule** | You can only join text to text. | always |
| **Why that is the rule** | — | sometimes |
| **What you did** | You joined text to a number. | always |
| **What to do** | Use `converted to text`. | always |
| **An example** | The shape it should be, written out. | sometimes |

The message above has four of the five. It does not explain *why* text and numbers refuse to join,
because the rule already says it — and a sentence repeating the rule in other words would be one
more thing to read and nothing more to learn. Most refusals are like that.

The two that come and go are worth knowing about, so that a shorter message does not read as a
worse one. **Why that is the rule** appears where the rule would otherwise look arbitrary, and
**an example** where the fix is a shape rather than a word. A refusal with neither has not given
up on you; it had nothing to add.

Most of learning Cufet is learning to read those parts. They are not an apology for a failure
— they are the language telling you the rule you have just met.

### Why it refused

`"Hopper has "` is text. `carrots` is `3`, which is a number. Cufet will not quietly turn one into
the other: quietly turning things into other things is where wrong answers come from.

Add the two words it asked for, and it runs:

```cufet
Define carrots as 3.
State "Hopper has " joined to carrots converted to text.
```
```output
Hopper has 3
```

`Define` introduces a name. `State` says something. `joined to` puts two pieces of text together,
and `converted to text` is how a number becomes text you can join.

### A shorter way to say the same thing

Writing `joined to … converted to text` for every value gets long. A **hole** in a piece of text —
curly braces around a name — does the same job:

```cufet
Define carrots as 3.
State "Hopper has {carrots} carrots.".
```
```output
Hopper has 3 carrots.
```

The rule has not changed. A hole is a place where you have said *"a value goes here"*, so you asked
for the conversion by the shape of what you wrote rather than by naming it. The long form is worth
knowing first, because it is what the hole is doing for you.

### Try it

- Change `3` to `12` and run it again.
- Add a second name with a number of its own, and get both into one sentence.

---

## Lesson 2 — Keeping several things together

One carrot count is a number. The counts in three baskets are a **series**: several values, kept in
order under one name. Here is one. It does not work.

```cufet-refused
Define baskets as a series with (3, 5, "four").
```
```output
That doesn't work: every item in a series must be the same type.
The first item is a number, so all items must be numbers.
Here on line 1, you're trying to make item 3 a text.

Remove the mismatched item, or define two separate series — one for numbers and one for text.
```

A series holds one kind of thing. That is lesson 1's rule again — text and numbers do not mix — and
it is what lets Cufet know, whatever you take out of `baskets`, that it is a number.

### Series

Write the third basket as a number, and it runs:

```cufet
Define baskets as a series with (3, 5, 4).
State baskets.
State item 2 of baskets.
State the first of baskets.
State the number of baskets.
```
```output
(3, 5, 4)
5
3
3
```

Items are counted from 1, the way people count them. `the number of` a series is how many things it
holds.

### Maps

A **map** keeps each value under a key, so you can look it up by name instead of by position:

```cufet
Define pantry as a map with ("carrots" : 12, "kale" : 3).
State pantry.
State the entry for "carrots" in pantry.
State the size of pantry.
```
```output
map {carrots: 12, kale: 3}
12
2
```

Every key is the same kind of thing, and so is every value — here the keys are text and the values
are numbers. A map's count is `the size of`, not `the number of`; ask for the wrong one and Cufet
says which to use.

### Records

A **record** keeps a few named things about one subject together, and they need not be the same kind:

```cufet
Define hopper as a record with (the name "Hopper", the carrots 12).
State hopper.
State "{the name of hopper} has {the carrots of hopper} carrots.".
```
```output
record(carrots: 12, name: Hopper)
Hopper has 12 carrots.
```

A series is many of one thing, a map is things found by key, and a record is one thing described by
several names.

### Try it

- Add a fourth basket, and see what `the number of baskets` says.
- Ask for `item 5 of baskets`, and read what Cufet says.
- Add `"lettuce"` to the pantry with a count of its own.

---

## Lesson 3 — Something that might not be there

Here is the pantry from lesson 2, and a program that adds one carrot to what is in it. It does not
work.

```cufet-refused
Define pantry as a map with ("carrots" : 12, "kale" : 3).
Define carrots as the entry for "carrots" in pantry.
State carrots + 1.
```
```output
That doesn't work: 'carrots' might be void, and + needs a number that is there.
Here on line 3, you're trying to use + with a voidable number.

Say what to use when it is void: '(carrots but void is 0)' in its place.
Keep the brackets: without them, the default runs on to the end of the line.
Or check first, with 'If carrots is not void:'.
```

### What void is

The pantry has carrots, so this looks safe. But ask it for something it does not have:

```cufet
Define pantry as a map with ("carrots" : 12, "kale" : 3).
State the entry for "carrots" in pantry.
State the entry for "lettuce" in pantry.
```
```output
12
void
```

**`void` means nothing is there.** A map cannot promise to have every key you might ask for, so what
`the entry for` gives back is a *voidable number*: a number, or void. That is the word the refusal
used — and Cufet checks it for every key, not just the ones that happen to be missing, because it
cannot know in advance which those are.

In many languages `carrots + 1` would run anyway, and the program would find out halfway through
that there was nothing to add to. Cufet asks before it runs: if `carrots` turns out to be void,
what should happen? You are the only one who knows.

### Saying what to use

`but void is` gives the answer — use this instead when there is nothing there:

```cufet
Define pantry as a map with ("carrots" : 12, "kale" : 3).
Define carrots as the entry for "carrots" in pantry.
State (carrots but void is 0) + 1.
```
```output
13
```

Ask for `"lettuce"` instead and it prints `1`: none in the pantry, plus one.

The brackets matter, and the refusal said so. Without them, `carrots but void is 0 + 1` reads the
default as `0 + 1` — the whole rest of the line — so it would print `12`, which is not what anybody
meant.

### Saying it once

If the answer is the same everywhere, say it where the name is defined, and the name is a plain
number from then on:

```cufet
Define pantry as a map with ("carrots" : 12, "kale" : 3).
Define lettuce as the entry for "lettuce" in pantry but void is 0.
State lettuce + 1.
State "Hopper has {lettuce} lettuces.".
```
```output
1
Hopper has 0 lettuces.
```

For a pantry, `0` is the right answer: a key that is not there means none. It is not always right —
a missing *price* is not a free one — and then the answer is to check first and do something
different when there is nothing there. That needs `If`, which is the next lesson.

### Try it

- Ask the pantry for `"kale"`, then for something it does not have.
- Put the default straight into the hole: `{the entry for "lettuce" in pantry but void is 0}`.

---

## Lesson 4 — Choosing, and doing it again

Lesson 3 ended on the other way to handle absence: check first, and do something different when
there is nothing there. Here is that program. It does not work.

```cufet-refused
Define pantry as a map with ("carrots" : 12, "kale" : 3).
Define lettuce as the entry for "lettuce" in pantry.
If lettuce is not void:
    State "Hopper has {lettuce} lettuces.".
Otherwise:
    State "There is no lettuce.".
```
```output
Line 3, column 1: this 'If' opens a block, and 'Otherwise' arrived on line 5 before its 'Done.'. In the block form every arm closes before the next one begins: 'If x is 1: ... Done. Otherwise: ... Done.'. The INLINE form is the one that needs no 'Done.': 'If x is 1, state "one". Otherwise, state "other".'
```

This refusal is about shape rather than type, so it reads differently from the others — but it
still names the rule, where you broke it, and both ways to write it.

### Two ways to write `If`

A colon opens a **block**: as many lines as you like, closed by `Done.`. Each arm closes before the
next begins:

```cufet
Define pantry as a map with ("carrots" : 12, "kale" : 3).
Define lettuce as the entry for "lettuce" in pantry.
If lettuce is not void:
    State "Hopper has {lettuce} lettuces.".
Done.
Otherwise:
    State "There is no lettuce.".
Done.
```
```output
There is no lettuce.
```

A comma instead of a colon is the **inline** form — one statement, and no `Done.`:

```cufet
Define pantry as a map with ("carrots" : 12, "kale" : 3).
Define lettuce as the entry for "lettuce" in pantry.
If lettuce is not void, state "Hopper has {lettuce} lettuces.".
Otherwise, state "There is no lettuce.".
```
```output
There is no lettuce.
```

Look at the first arm. `lettuce` went into a hole without `but void is` — something lesson 3 said
could not be done. Inside `If lettuce is not void`, Cufet knows it is not void, so there it is a
plain number. The check is what earned it.

### Facts

What an `If` tests is a **fact** — `true` or `false`. A comparison is one:

```cufet
Define carrots as 12.
If carrots is greater than 10, state "plenty".
Otherwise, state "running low".
State carrots is greater than 10.
```
```output
plenty
true
```

`fact` is the third kind of value, after numbers and text. Comparing things of different kinds is
refused, the same way joining them was in lesson 1: `If carrots is "12"` asks whether a number is
the same as some text, and it never can be.

### Doing it again

`For each` runs its block once for every item of a series, naming the item as it goes:

```cufet
Define baskets as a series with (3, 5, 4).
Define total as 0.
For each basket in baskets, repeat:
    Increment total by basket.
Done.
State "{total} carrots in all.".
```
```output
12 carrots in all.
```

`Increment total by basket.` adds to `total` where it stands. An `If` inside a loop runs for each
item in turn:

```cufet
Define baskets as a series with (3, 5, 4).
For each basket in baskets, repeat:
    If basket is greater than 4, state "{basket} is a big basket".
    Otherwise, state "{basket} is a small basket".
Done.
```
```output
3 is a small basket
5 is a big basket
4 is a small basket
```

### Choosing among many

Several `If`s in a row can pick one of many — or, if none of them matches, quietly do nothing.
`Judge` picks one of many and will not let you forget the rest:

```cufet
Define wanted as "kale".
Judge wanted, where it is:
    It is "carrots", state "orange and crunchy".
    It is "kale" or "lettuce", state "something green".
    Otherwise, state "never heard of {it}".
Done.
```
```output
something green
```

Leave out the `Otherwise` and Cufet refuses: no list of words can cover every piece of text, so it
asks what should happen to the rest. That is the same question lesson 3 asked about void, in a
different place.

### Try it

- Set `wanted` to `"parsnip"`, and see what `it` becomes.
- Count only the big baskets: add to `total` inside an `If`.
- Add `"lettuce" : 2` to the pantry in the first program, and watch the other arm run.

---

## Lesson 5 — Borrowing from a book

Hopper's garden is square, and covers 16 square metres. How long is each side? That is a square
root, and Cufet has one. This does not work yet.

```cufet-refused
Define garden as 16.
State math's square-root of (garden).
```
```output
That doesn't work: 'math' is a book, and it is not pulled here.
A book's members can be used only inside 'Pull a book on math. … Done.', and the pull lasts until its 'Done.'.
Here on line 2, you're trying to use 'math' outside a pull on it.

Put this line inside 'Pull a book on math.', before its 'Done.'.
```

### What a book is

Most programs never need a square root. So instead of being part of every program, it lives in a
**book** — something that comes with Cufet, which a program brings in when it wants it. You *pull*
a book for a block, and inside that block its members are reached with `'s`, the way a record's
fields are:

```cufet
Define garden as 16.
Pull a book on math.
    State math's square-root of (garden).
    State math's pi.
Done.
```
```output
4
3.1415926535897932384626433833
```

A pull is a block like any other, so it ends at its `Done.` — use `math` after that line and you
get the same refusal. Nothing is borrowed for longer than you asked.

### A book you have met before, in a way

`collections` is the book of things to do with a series:

```cufet
Define baskets as a series with (3, 5, 4).
Pull a book on collections.
    State collections's maximum of (baskets).
    State collections's average of (baskets).
    State collections's maximum of (a series of number).
Done.
```
```output
5
4
void
```

An empty series has no biggest item, so `maximum` gives back a voidable number — and you already
know what to do with one of those:

```cufet
Define baskets as a series with (3, 5, 4).
Pull a book on collections.
    Define biggest as collections's maximum of (baskets) but void is 0.
    State "The biggest basket holds {biggest}.".
Done.
```
```output
The biggest basket holds 5.
```

A book's members follow the same rules as everything else. Nothing about void changed because the
number came out of a book.

### Several at once

`Pull books on` takes a list, and one `Done.` closes them all:

```cufet
Define garden as 16.
Define baskets as a series with (3, 5, 4).
Pull books on math and collections.
    State math's square-root of (garden).
    State collections's average of (baskets).
Done.
```
```output
4
4
```

### Try it

- Move `State math's pi.` below the `Done.`, and read what Cufet says.
- Find the smallest basket: `collections` has a `minimum`.
- `math` has `power` too: `math's power of (2, 10)`.

---

**Next:** your own words — `Bind`, and teaching Cufet something new.
