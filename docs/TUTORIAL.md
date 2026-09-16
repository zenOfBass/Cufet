# Cufet, from the beginning

Two rabbits, learning the language by getting it wrong.

| If you want to know | Read |
| --- | --- |
| What Cufet is, and why you might care | [README.md](../README.md) |
| How to use a feature | [REFERENCE.md](REFERENCE.md) |
| Exactly what the rules are | [GRAMMAR.md](GRAMMAR.md) |

This is a way *in*. It assumes nothing, it goes in order, and it teaches by breaking things —
because the first thing anybody meets in a new language is a refusal, and Cufet's refusals are
written to be read.

> Every program in this file is run by the test suite, and every message beneath one is compared
> to what the language actually says. If a lesson claims Cufet prints something, Cufet prints it.

---

## Lesson 1 — Hopper counts his carrots

### Under the fence

There are two rabbits at the bottom of the garden, and neither of them is real yet.

Grace is the careful one. Hopper is not.

They have heard that things become real in the garden — that if you can get in, and if you can
make something *work*, you stop being stuffed and start being a rabbit. Nobody has told them how.
So they go under the fence to find out.

> **HOPPER:** I've written one. I've written a program.
>
> **GRACE:** Already.
>
> **HOPPER:** I counted my carrots and then I told everyone. That's a program.
>
> **GRACE:** Run it, then.

Run it, and Cufet says no:

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

> **HOPPER:** It hates me.
>
> **GRACE:** It does not hate you. It told you four things. Look.
>
> **GRACE:** *The rule.* You can only join text to text.
>
> **GRACE:** *What you did.* You joined text to a number.
>
> **GRACE:** *What to do.* Use `converted to text`.
>
> **GRACE:** And then it wrote you an example, in case you weren't sure of the shape.
>
> **HOPPER:** …it's being quite nice about it, actually.
>
> **GRACE:** It usually is. That's the whole trick, Hopper. Everything in here tells you what is
> wrong with it. Most of learning this is learning to **read the no**.

### What the no meant

`"Hopper has "` is text. `carrots` is `3`, which is a number. Cufet will not quietly turn one into
the other — quietly turning things into other things is where wrong answers come from.

Add the two words it asked for, and it runs:

```cufet
Define carrots as 3.
State "Hopper has " joined to carrots converted to text.
```
```output
Hopper has 3
```

> **HOPPER:** I'm real!
>
> **GRACE:** You are not remotely real. You printed a three.
>
> **HOPPER:** It's a start.

> **Good little rabbits always remember to say which kind of thing they mean.**

### Grace has a tidier way

> **GRACE:** May I.
>
> **HOPPER:** You've been sitting on it this whole time, haven't you.

```cufet
Define carrots as 3.
State "Hopper has {carrots} carrots.".
```
```output
Hopper has 3 carrots.
```

> **HOPPER:** That's cheating.
>
> **GRACE:** It is not cheating. The rule still holds. The curly braces are a **hole** — a place in
> the sentence where you have said *"a value goes here"*. You asked for the conversion by the shape
> of what you wrote. You just did not have to say it twice.
>
> **HOPPER:** So why did I have to do it the long way?
>
> **GRACE:** So you would know what the hole is doing for you.

### Before you go

Give Hopper `12` carrots and run it again.

Then give Grace some carrots of her own, and get them both into one sentence.

> **HOPPER:** How many do *you* have?
>
> **GRACE:** Four.
>
> **HOPPER:** Four?
>
> **GRACE:** I ate the rest. Come on.

---

**Next:** Grace has a basket, and Hopper has put the wrong thing in it.
