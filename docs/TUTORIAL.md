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

**Next:** choosing and repeating — `If`, and doing something different when there is nothing there.
