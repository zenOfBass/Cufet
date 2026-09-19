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

**Next:** collections, and what happens when you put the wrong kind of thing in one.
