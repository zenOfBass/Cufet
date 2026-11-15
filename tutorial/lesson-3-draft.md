# Lesson 3, names: a first draft

**Status: a draft to react to. Nothing here is agreed until you say so.** The agreed design is in
[DESIGN.md](DESIGN.md). It follows [lesson 2](lesson-2-draft.md) and is written the same way:
*italics* are what happens on screen, and each spoken line is one speech bubble. Every program
and every Cufet message below was run through the real build on 15 November 2026.

What's new in this lesson, one idea per beat:

1. `Define` keeps a value under a name, and using the name gives the value back.
2. A kept answer can be used in more math.
3. A name doesn't reach inside quote marks.
4. A hole, `{pick}`, puts a name's value into text.
5. A blank page: the quiz from lesson 2, where the number changes in one place.

It opens where lesson 2 ended: Grace has asked "What if we want to use the answers for our math?",
and Hopper has said "We can name variables! It's super super useful. We'll show you!"

---

## Beat 1: keeping the answer (Predict)

> **Grace:** Let's keep that answer this time, friend. Watch.

*Grace clears the editor and writes two lines:*

```cufet
Define answer as 12 * 12.
State answer.
```

> **Grace:** The first line works out `12 * 12`, and keeps the answer under a name: `answer`. The second line uses it.
>
> **Hopper:** No quote marks on `answer`! What's it gonna say?

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `144`
- **B.** `answer`
- **C.** `144`, and then `144` again under it

*The player picks one and presses Run.*

```output
144
```

**If they chose A:**

> **Grace:** Exactly. Cufet worked out the sum, kept it, and said it when we asked.

**If they chose B:**

> **Grace:** It would have, with quote marks. In quote marks, `"answer"` is text. Without them, it's a name, and Cufet says what the name stands for.

**If they chose C:**

> **Hopper:** Only one! `Define` doesn't say anything. It just keeps the answer. `State` does the saying.

*Whichever they chose, Grace sums it up:*

> **Grace:** `Define` keeps a value under a **name**. A name kept like this is called a **variable**. After that, wherever you write the name, Cufet uses the value.

## Beat 2: using it again (Predict, typed)

*Grace changes the second line:*

```cufet
Define answer as 12 * 12.
State answer + 12.
```

> **Grace:** Now let's use the answer for more math. Type what you think it will say.

*The player types a guess, then presses Run.*

```output
156
```

> **Hopper:** We didn't have to work out a hundred and forty-four all over again!
>
> **Grace:** Cufet kept it for us. A hundred and forty-four, plus twelve.

*A wrong guess is never a fail. Running it shows the answer.*

## Beat 3: only halfway (Predict)

> **Hopper:** Ooo! Can we keep the number in the question too? Then I can pick a new one whenever I want!
>
> **Grace:** We can. Let's call it `pick`, for the number you picked.

*Grace puts the question back on top, the way it was in lesson 2, with its number kept under a name:*

```cufet
Define pick as 12.
State "What is 12 times 12?".
State pick * pick.
```

> **Hopper:** I pick thirteen! And I only have to change *one* place!

*Hopper changes the first line to `Define pick as 13.` and steps back, very pleased.*

> **Grace:** Hmm. Before you run it, friend: what will it say?

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `What is 13 times 13?`, then `169`
- **B.** `What is 12 times 12?`, then `169`
- **C.** `What is 12 times 12?`, then `144`

*The player picks one and presses Run.*

```output
What is 12 times 12?
169
```

**If they chose B:**

> **Grace:** Well spotted. The sum changed, but the question didn't.

**If they chose A or C:**

> **Grace:** Look closely. The sum uses the name, so it changed. The question doesn't, so it didn't.

*Whichever they chose:*

> **Hopper:** Aw. The question still says twelve! That's wrong now!
>
> **Grace:** Because the twelves in the question are inside quote marks. That's text, and Cufet says text exactly as it's written. It doesn't look for names in there.

## Beat 4: a hole (Run, then Investigate)

> **Grace:** Unless you leave a hole for it.

*Grace changes the question line:*

```cufet
Define pick as 13.
State "What is {pick} times {pick}?".
State pick * pick.
```

> **Hopper:** The curly brackets! What are those for?
>
> **Grace:** They make a **hole** in the text. Cufet fills the hole with whatever the name stands for. Press Run, friend.

*The player presses Run.*

```output
What is 13 times 13?
169
```

> **Hopper:** It's thirteen everywhere!
>
> **Grace:** Now change the first line to `Define pick as 20.`, and run it again. Just the first line.

*The player changes the 13 to 20 and presses Run. Done when the output says `What is 20 times 20?` and `400`.*

> **Grace:** One change, and everything that uses the name follows along.

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| Changes a hole to `{20}` instead | Runs | **Grace:** That works, but now that hole doesn't follow the name. Put `{pick}` back and change only the first line. |
| `Define pick as twenty.` | `That doesn't work: 'twenty' isn't defined.` and three more lines | **Hopper:** Numbers are written with digits! Twenty is `20`. |

## Beat 5: a blank page (Make)

> **Hopper:** My turn to quiz Grace again! Friend, write it so I can change the number in just one place.
>
> **Grace:** The page is empty. Ask what seven times seven is, and let Cufet work out the answer. Use a name.

*The editor is empty. Done when the output is this, judged on the words and numbers only:*

```output
What is 7 times 7?
49
```

> **Hopper:** Forty-nine!
>
> **Grace:** I'd have got there. Well done, friend.

### The hint ladder

1. > **Grace:** Start by giving seven a name, with `Define`.
2. > **Hopper:** Then two `State`s, like before. The question needs holes for the name, and the sum uses the name!
3. > **Grace:** Here it is. Type it yourself:
   >
   > `Define pick as 7.`
   > `State "What is {pick} times {pick}?".`
   > `State pick * pick.`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| No name: `State "What is 7 times 7?".` then `State 7 * 7.` | Runs, prints the right answer | **Done.** **Hopper:** It's right! But if I want eight, you'd have to change four places again. Try it with a name? |
| `Define pick 7.` | `Line 1, column 13: expected As, got Number "7".` | **Grace:** A name and its value are joined by `as`: `Define pick as 7.` |
| `Define pick = 7.` | `'=' is comparison, not assignment. A 'Define' introduces a name with 'as': 'Define pick as ...'.` (and more) | **Grace:** Cufet says it well itself: a `Define` uses `as`, not `=`. |
| `Define Pick as 7.` | `Line 1, column 8: identifier 'Pick' must start with a lowercase letter.` | **Hopper:** Names start with a small letter! `pick`, not `Pick`. |
| `Define my pick as 7.` | `That doesn't work: 'my' is not a defined type.` | **Grace:** A name can't have a space in it. Join the words with a dash, like `my-pick`, or use one word. |
| `Define my number as 7.` | `Line 1, column 11: expected As, got NumberKw "number".` | **Grace:** Two things there: a name can't have a space, and `number` is one of Cufet's own words. Try `pick`. |
| `Define number as 7.` | `'number' is a word Cufet has taken, so it cannot be a name here.` (and more) | **Grace:** `number` is one of Cufet's own words, so it can't be a name. Choose another, like `pick`. |
| The `Define` line last | `That doesn't work: 'pick' isn't defined.` and three more lines | **Grace:** The computer reads from the top down, remember? Give the name before you use it. |
| A typo in a hole: `{pik}` | `That doesn't work: 'pik' isn't defined.` and three more lines | **Hopper:** Cufet doesn't know `pik`! Check the spelling in the curly brackets. |
| `State "pick * pick".` for the answer | Runs, prints `pick * pick` | **Hopper:** That's the sum as text! Take off the quote marks so Cufet works it out. |
| Spaces in a hole: `{ pick }` | Runs, prints the right answer | **Done.** Spaces inside the curly brackets don't matter. |

## Beat 6: one more, and a surprise (Predict)

> **Hopper:** Again! Seven, then eight! Both in one program!

*Hopper adds two lines to the bottom of the player's program:*

```cufet
Define pick as 7.
State "What is {pick} times {pick}?".
State pick * pick.
Define pick as 8.
State pick * pick.
```

> **Grace:** Hmm. Friend, will this one run?

*The output box asks: **Will it run?** Two buttons: **Runs** and **Refused**. The player picks one
and presses Run. The output box shows:*

```output
That doesn't work: 'pick' is already defined in this scope.
Here on line 4, you're trying to define 'pick' again in the same block.

Each name can only be defined once per block. Use 'becomes' to reassign it, or choose a different name.
```

**If they chose Refused:**

> **Grace:** Well spotted. A name can only be given once.

**If they chose Runs:**

> **Grace:** It does look like it should. But `Define` makes a *new* name, and there's already one called `pick`.

*Whichever they chose:*

> **Hopper:** Aw! But I want it to change!
>
> **Grace:** Look what Cufet says at the end: "Use 'becomes'." A name *can* change partway through a program. That's for next time.

*End of lesson 3.*

---

## Things this draft found

1. **A name with a space gets a misleading message.** `Define my pick as 7.` says
   `'my' is not a defined type`, which is about something else entirely. The rabbits cover it, but
   it belongs with the other messages noted in the roadmap. So does `Define my number as 7.`, which
   says `expected As, got NumberKw "number"`.
2. **`Define pick = 7.` already has a warm message**, unlike most beginner mistakes so far. The
   rabbits just point at it.
3. **The "isn't defined" message says "Nothing later in this block can give it a value"**, and the
   "already defined" one mentions "scope" and "block". Neither word has been taught. The rabbits
   say the same thing in plain words.
4. **Beat 6 ends on a real refusal** whose own last line names the next lesson's word,
   `becomes`.
5. **`Define answer as 12 * 12.` on its own prints nothing.** Beat 1's choice C tests exactly that.
6. **Writing the answer without a name still counts as done in Beat 5**, with Hopper's nudge,
   since the output can't tell the two apart. The game could check the code for a `Define` if
   you'd rather it did.
