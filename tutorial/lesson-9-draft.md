# Lesson 9, void: a first draft

**Status: a draft to react to. Nothing here is agreed until you say so.** The agreed design is in
[DESIGN.md](DESIGN.md). It follows [lesson 8](lesson-8-draft.md) and is written the same way:
*italics* are what happens on screen, and each spoken line is one speech bubble. Every program
and every Cufet message below was run through the real build on 15 November 2026.

What's new in this lesson, one idea per beat:

1. `Bind void to …` makes a function that gives nothing back.
2. A function can do nothing at all, and that's fine.
3. `void` is Cufet's word for nothing, and a `voidable` answer may be nothing.
4. `but void is` says what to use when there's nothing.
5. A blank page: a function with a loop inside that gives nothing back. The whole trunk in one.

It opens where lesson 8 ended: Hopper wants a function that says hello and gives nothing back,
Grace has said "Then it gives back nothing. And Cufet has a word for nothing", and Hopper has said
"My favourite kind of word."

This is the last lesson of the trunk, and the first with Maria.

---

## The pond

*Grace and Hopper hop to a pond outside the garden wall. An old frog sits on a stone, very still.*

> **Grace:** Maria knows more about nothing than anyone.
>
> **Hopper:** Maria! We came to ask about nothing.
>
> **Maria:** All this way, for nothing?
>
> **Hopper:** Exactly!
>
> **Maria:** Then show me your hello.

## Beat 1: giving nothing back (Predict)

*Hopper writes:*

```cufet
Bind void to greet, given (the text name):
    State "Hello, {name}!".
Done.
Cast greet on ("Maria").
```

> **Hopper:** `void` instead of `number` or `text`. It says hello, and gives nothing back. What do you think appears, friend?

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `Hello, Maria!`
- **B.** `Hello, Maria!`, then `void`
- **C.** Nothing at all

*The player picks one and presses Run.*

```output
Hello, Maria!
```

**If they chose A:**

> **Maria:** Hello yourself.

**If they chose B:**

> **Grace:** `greet` says hello inside. It gives nothing back, and nothing gets said about the nothing.

**If they chose C:**

> **Grace:** The `State` is inside `greet`, and the last line casts it. So it runs.

*Whichever they chose:*

> **Grace:** A `void` function does its job and hands nothing back. No `Return` needed, and you cast it on its own line, like `State`.

## Beat 2: doing nothing (Predict)

> **Maria:** When a rabbit is tired, what does she do?
>
> **Hopper:** Rests!
>
> **Maria:** Write me resting.

*Hopper thinks, then writes:*

```cufet
Bind void to rest:
Done.
State "Ready?".
Cast rest.
State "Done resting.".
```

> **Maria:** What will it say?

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `Ready?`, then `Done resting.`
- **B.** `Ready?`, `void`, then `Done resting.`
- **C.** Cufet refuses it: `rest` is empty

*The player picks one and presses Run.*

```output
Ready?
Done resting.
```

**If they chose A:**

> **Maria:** Yes.

**If they chose B or C:**

> **Hopper:** There's nothing inside `rest`, so it does nothing. And Cufet's fine with that!

*Whichever they chose:*

> **Maria:** Resting doesn't need to announce itself.
>
> **Grace:** `rest` has no `given`, because it needs nothing. And nothing inside, because there's nothing to do.

## Beat 3: sometimes nothing (Predict)

> **Hopper:** Try this one. Remember `cheer`? This one only says "Rest!" when I'm tired. Otherwise, it gives back nothing at all.

*Hopper writes:*

```cufet
Bind voidable text to cheer, given (the number hops):
    If hops is greater than 3, return "Rest!".
    Return void.
Done.
State cast cheer on (5).
State cast cheer on (2).
```

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `Rest!`, then `void`
- **B.** Only `Rest!`
- **C.** `Rest!`, then `Keep going!`

*The player picks one and presses Run.*

```output
Rest!
void
```

**If they chose A:**

> **Hopper:** Right again! Two hops isn't tired, so `cheer` gave back nothing, and Cufet said so.

**If they chose B:**

> **Hopper:** Not quite! `cheer` did give something back: `void`. And `State` says whatever it's given.

**If they chose C:**

> **Hopper:** Not quite! "Keep going!" isn't in this `cheer` anywhere.

*Whichever they chose:*

> **Grace:** `voidable text` means: text, or nothing. `void` is how Cufet writes the nothing.
>
> **Maria:** Even nothing has a name.

## Beat 4: when there's nothing (Modify)

> **Grace:** "void" isn't much of a cheer. You can tell Cufet what to say when there's nothing: put `but void is` after the cast, and then what to use instead.
>
> **Hopper:** Make the second line say "Keep going!", friend. Don't touch `cheer`.

*The editor holds the program from Beat 3. Done when the output is, on the words only, `Rest!` then
`Keep going!`, `cheer` is unchanged, and the line uses `but void is`.*

> **Hopper:** Keep going! I will!

### The hint ladder

1. > **Grace:** Only the last line changes. Add to the end of it, before the period.
2. > **Hopper:** After the `(2)`, write `but void is`, then the words in quote marks!
3. > **Grace:** Here it is. Type it yourself:
   >
   > `State cast cheer on (2) but void is "Keep going!".`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| `but void "Keep going!"`, no `is` | `Line 5, column 34: expected Is, got String "Keep going!".` | **Grace:** It's `but void is`, with "is". |
| `or "Keep going!"` | `That doesn't work: 'or' needs both sides to be the same kind of thing.` and more | **Grace:** `or` is for questions. For nothing, it's `but void is`. |
| `but void is 0` | `That doesn't work: the default value is a number, but the voidable holds text.` and more | **Hopper:** `cheer` gives back text, so what you use instead has to be text too! |
| Changes `Return void.` to `Return "Keep going!".` | Prints the right lines, but isn't done | **Grace:** That works, but `cheer` was meant to stay as it is. Say what to use at the last line instead. |

## Beat 5: a blank page (Make)

> **Hopper:** Last one, friend! Write me a function called `hop`. Give it a number, and it says that many hops, from "Hop 1!". It doesn't give anything back.
>
> **Grace:** The two casts at the bottom are already there. Write `hop` above them.

*The editor holds two lines:*

```cufet
Cast hop on (3).
Cast hop on (2).
```

*Done when the output is this, judged on the words and numbers only, and the program has a
`Bind void to hop` with a loop inside:*

```output
Hop 1!
Hop 2!
Hop 3!
Hop 1!
Hop 2!
```

> **Hopper:** Three hops, then two! Every bit of it yours.
>
> **Grace:** A function, a name, a loop, and nothing given back. Everything you've learned, in one. Well done, friend.

### The hint ladder

1. > **Grace:** It starts `Bind void to hop, given (the number times):`
2. > **Hopper:** Inside, it's the hop counter from lesson 6. But stop at `times`, not at 3!
3. > **Grace:** Here it is. Type it yourself:
   >
   > `Bind void to hop, given (the number times):`
   > `    Define hops as 0.`
   > `    While hops is less than times, repeat:`
   > `        The hops becomes hops + 1.`
   > `        State "Hop {hops}!".`
   > `    Done.`
   > `Done.`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| `Bind number to hop, …` | `That doesn't work: 'hop' is declared to give back a number, but it can reach its end without returning one.` and more | **Grace:** `hop` doesn't give anything back. That's `void`. |
| `State cast hop on (3).` | `That doesn't work: 'hop' gives nothing back — it can't be used as a value.` and more | **Hopper:** `hop` says its own hops! Cast it on its own line, no `State`. |
| `While hops is less than 3, …` | Prints three hops twice | **Hopper:** That's always three! Use `times`, so it hops as many as you ask. |
| Only one `Done.` | `this 'Bind' opens a block, and the file ended before its 'Done.'.` and more | **Hopper:** Two colons, two `Done.`s: one for the loop, one for the function. |
| No function: the hops written out by hand | Prints the right lines, but isn't done | **Grace:** Right lines, but the task was `hop`. Put it in a `Bind`. |

## Before the lesson ends

> **Hopper:** Maria, can I ask you something?
>
> **Maria:** You just did.
>
> **Hopper:** Another thing, then. Grace and I want to be Real. How do we get there?
>
> **Maria:** You'll find your way out through the garden.
>
> **Hopper:** The garden? That's where the people are!
>
> **Grace:** Good little rabbits never go in the garden, Maria.

*Maria closes her eyes, and says nothing.*

*End of lesson 9, and of the trunk.*

---

## Still open

**How they get to the pond.** Lesson 8 still ends with Hopper's "My favourite kind of word", and
this lesson opens with them arriving. Grace's "Maria knows more about nothing than anyone" is a
placeholder for whoever suggests going; that's yours.
