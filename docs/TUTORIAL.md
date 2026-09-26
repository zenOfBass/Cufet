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

## Lesson 6 — Words of your own

Lesson 5 borrowed a square root from a book. Now Hopper wants a word Cufet does not have: the area
of a garden bed, from its width and depth. `Bind` makes one. Calling it the way lesson 5 called
`square-root` does not work:

```cufet-refused
Bind number to area, given (the number width, the number depth):
    Return width * depth.
Done.
State area of (4, 5).
```
```output
Line 4, column 12: 'area of (…)' is how a BOOK's member is called — 'math's square-root of (16)'. A function of your own is called with 'cast': 'cast area on (…)'.
```

### Binding a word

Read the first line as a sentence: *bind a number to `area`, given a number called `width` and a
number called `depth`*. It says what the word gives back, what it is called, and what it needs.
`Return` hands the answer back. A word of your own is used with `cast`:

```cufet
Bind number to area, given (the number width, the number depth):
    Return width * depth.
Done.
State cast area on (4, 5).
Define garden as cast area on (4, 4).
State "The garden covers {garden} square metres.".
```
```output
20
The garden covers 16 square metres.
```

`cast area on (4, 5)` is a number like any other, so it can be stated, defined, or put in a hole.
Pass it the wrong kind of thing — `cast area on (4, "five")` — and Cufet says which argument, and
what it should have been.

### Every way out gives an answer

A word that says it gives back text has to, whichever way it goes. With an `If` from lesson 4 that
is easy to forget: if `how-many` only returned inside the `If`, Cufet would refuse it — *"it can
reach its end without returning one"* — because a small count would fall out of the bottom with
nothing. Say what happens the rest of the time:

```cufet
Bind text to how-many, given (the number carrots):
    If carrots is greater than 10, return "plenty".
    Return "running low".
Done.
State cast how-many on (12).
State cast how-many on (3).
```
```output
plenty
running low
```

### A word that only does something

Not every word gives an answer. One that only *does* something gives back `void` — nothing — and
is used as a statement, with a capital `Cast`:

```cufet
Bind void to greet, given (the text name):
    State "Hello, {name}.".
Done.
Cast greet on ("Hopper").
Cast greet on ("Grace").
```
```output
Hello, Hopper.
Hello, Grace.
```

### Handing a word to something else

A word you bound is a value too, and some things take one. Text sorts alphabetically; to sort by
length, hand `sorted by` a word that says how long each one is:

```cufet
Bind number to size-of, given (the text word):
    Return the length of word.
Done.
Define words as a series of text with ("pear", "apple", "fig").
State words sorted.
State words sorted by size-of.
```
```output
(apple, fig, pear)
(fig, pear, apple)
```

`sorted by` calls `size-of` once for each word and sorts by what comes back.

### Try it

- Write `perimeter`, which gives back twice the width plus twice the depth.
- Delete `Return "running low".` from `how-many`, and read what Cufet says.
- Try `State area(4, 5).` — the way other languages call a function — and see what Cufet tells you.

---

## Lesson 7 — When it can fail

A garden bed cannot be minus four metres wide. `area` from lesson 6 should refuse to answer rather
than give back a nonsense number — it should **fail**, and say why. Adding that to it does not work:

```cufet-refused
Bind number to area, given (the number width, the number depth):
    If width is less than 0, return a failure "a width cannot be negative".
    Return width * depth.
Done.
```
```output
That doesn't work: this function is declared to give back a number, and a failure is not one.
You declared the return type as number on line 1.
Here on line 2, you're trying to return a failure from it.

If it is meant to be able to fail, say so where it is declared: 'Bind number or failure to …'.
```

### Saying a word can fail

The first line of a `Bind` is a promise about what comes back. `area` promised a number, and a
failure is not one — so the promise has to change: *a number, or a failure*.

That fixes `area`, and moves the question to everyone who uses it:

```cufet-refused
Bind number or failure to area, given (the number width, the number depth):
    If width is less than 0, return a failure "a width cannot be negative".
    Return width * depth.
Done.
State cast area on (4, 5).
```
```output
That doesn't work: 'area' can fail — you must handle the failure.
Here on line 5, you're trying to use a fallible function's result without handling the failure.

Wrap the call in a 'Try to: / In case of failure:' block, use 'but on failure <default>', or use 'or pass the failure off'.
```

`4` and `5` are fine, so this would work today — but Cufet does not check the numbers, it checks
the promise. `area` says it *can* fail, so every use of it has to say what happens if it does. The
refusal lists the three ways.

### Trying, and catching

`Try to:` runs a block. If anything in it fails, the rest of the block is skipped and
`In case of failure:` runs instead, with the failure's message to hand:

```cufet
Bind number or failure to area, given (the number width, the number depth):
    If width is less than 0, return a failure "a width cannot be negative".
    Return width * depth.
Done.
Try to:
    State cast area on (4, 5).
    State cast area on (0 - 4, 5).
    State "not reached".
Done.
In case of failure:
    State "That did not work: {the message of the failure}.".
Done.
```
```output
20
That did not work: a width cannot be negative.
```

### A default instead

`but on failure` is lesson 3's `but void is` for failures — use this instead if it fails:

```cufet
Bind number or failure to area, given (the number width, the number depth):
    If width is less than 0, return a failure "a width cannot be negative".
    Return width * depth.
Done.
Define garden as cast area on (0 - 4, 5) but on failure 0.
State "The garden covers {garden} square metres.".
```
```output
The garden covers 0 square metres.
```

### Passing it on

Sometimes the word you are writing cannot fix the failure either, and the right thing is to fail
too. `or pass the failure off` does that — if `area` fails, `two-beds` fails with the same message:

```cufet
Bind number or failure to area, given (the number width, the number depth):
    If width is less than 0, return a failure "a width cannot be negative".
    Return width * depth.
Done.
Bind number or failure to two-beds, given (the number width, the number depth):
    Define one-bed as cast area on (width, depth) or pass the failure off.
    Return one-bed * 2.
Done.
State cast two-beds on (4, 5) but on failure 0.
Try to:
    State cast two-beds on (0 - 4, 5).
Done.
In case of failure:
    State "No beds: {the message of the failure}.".
Done.
```
```output
40
No beds: a width cannot be negative.
```

`two-beds` had to say `or failure` as well. A failure can only travel through words that admit they
can fail, so reading the first line of any `Bind` tells you whether you will have to handle one.

### Void, or a failure?

They can look alike, and they mean different things. **Void** is an ordinary answer: the pantry has
no lettuce, and nothing went wrong. A **failure** is something going wrong, with a message saying
what. A map lookup gives void; a garden with a negative width is a failure.

### Try it

- Make `area` fail for a negative depth too, with its own message.
- Use `but on failure 0` on a call that works, and check the default is not used.
- Take `or failure` off `two-beds`'s first line, and read what Cufet says.

---

## Lesson 8 — Things of your own

A record from lesson 2 describes one thing. An **object** is a *kind* of thing, with a name of its
own, and it can do things as well as hold them. Here is a garden bed that knows its own area. It
does not work.

```cufet-refused
Define object bed with (the number width, the number depth):
    Bind number to area:
        Return width * depth.
    Done.
Done.
```
```output
That doesn't work: 'width' is a field of 'bed', and inside its methods a field is reached through 'one'.
Here on line 3, you're trying to use 'width' on its own.

Write 'one's width' — 'one' is the bed the method was called on.
```

### An object, and its methods

`Define object bed with (…)` names a new kind of thing and says what every one of them holds. A
`Bind` inside it is a **method** — a word that belongs to beds. Inside one, `one` is the particular
bed it was called on, so its width is `one's width`:

```cufet
Define object bed with (the number width, the number depth):
    Bind number to area:
        Return one's width * one's depth.
    Done.
Done.
Define front as a new bed { the width 4, the depth 5 }.
State cast front's area.
State front's width.
State front.
```
```output
20
4
bed(depth: 5, width: 4)
```

`a new bed { … }` makes one, with a value for each field. A method is called with `cast`, the same
way lesson 6 called a word of your own — `front's area` on its own is refused, because a method is
there to be called. A field is just read. Change one with `becomes`, as you would a name:
`The front's width becomes 6.`

### Fields with a default

Leave a field out when making one, and Cufet says which is missing — and suggests a default. A
default is written where the kind is defined, and every bed made without that field gets it:

```cufet
Define object bed with (the number width, the number depth, the text crop with default "nothing yet").
Define front as a new bed { the width 4, the depth 5 }.
Define back as a new bed { the width 2, the depth 3, the crop "carrots" }.
State "The front bed grows {front's crop}; the back grows {back's crop}.".
```
```output
The front bed grows nothing yet; the back grows carrots.
```

### When one goes away

Some things need tidying up after. An **unmaker** is a word Cufet runs for you when a thing's name
goes out of use — at the `Done.` of the block it was defined in:

```cufet
Define object watering-can with (the text label).
Bind unmaking a watering-can to put-away:
    State "putting the {one's label} watering can away".
Done.
If 1 is 1:
    Define can as a new watering-can { the label "green" }.
    State "watering with the {can's label} can".
Done.
State "back in the house".
```
```output
watering with the green can
putting the green watering can away
back in the house
```

A loop's block ends once per turn, so a can defined inside a loop is put away every time round:

```cufet
Define object watering-can with (the text label).
Bind unmaking a watering-can to put-away:
    State "putting the {one's label} watering can away".
Done.
For each colour in a series of text with ("green", "red"), repeat:
    Define can as a new watering-can { the label colour }.
    State "watering with the {colour} can".
Done.
```
```output
watering with the green can
putting the green watering can away
watering with the red can
putting the red watering can away
```

Two things to know before relying on one.

**It needs a block.** A thing defined at the very top of a program, or directly in a word's body,
is never unmade — neither of those is a block that ends. Put it inside an `If`, a loop, or the
lifetime of lesson 9.

**It belongs to the name, not the thing.** Objects are copied, so `Define spare as can.` makes a
second watering can — and both are put away:

```cufet
Define object watering-can with (the text label).
Bind unmaking a watering-can to put-away:
    State "putting the {one's label} watering can away".
Done.
If 1 is 1:
    Define can as a new watering-can { the label "green" }.
    Define spare as can.
    State "watering".
Done.
```
```output
watering
putting the green watering can away
putting the green watering can away
```

So write an unmaker that is safe to run twice: saying something, or setting a flag, is fine. For
tidying that must happen exactly once, give the object a method and call it yourself.

### Try it

- Give `bed` a second method, `perimeter`.
- Define a watering can at the top of the program, outside any block, and see whether it is put away.
- Write `State front's area.` without `cast`, and read what Cufet says.

---

**Next:** things with a lifetime — `Pull a rabbit.`, and what its `Done.` lets go of.
