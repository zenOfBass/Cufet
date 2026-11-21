# Lesson 3, names: a first draft

What's new in this lesson, one idea per beat:

1. `Define` keeps a value under a name, and using the name gives the value back.
2. A kept answer can be used in more math.
3. A name inside quote marks is just text.
4. A hole, `{answer}`, puts a name's value into text.
5. A blank page: keep an answer, and say it in a sentence.
6. A name can only be given once, which leads to the next lesson.

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
> **Hopper:** Notice there are no quote marks on `answer`. So what do you think it says, friend?

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

## Beat 3: saying it in a sentence (Predict)

> **Hopper:** Now, here's a trap almost everybody falls into. Say you want a real sentence, like "The answer is 144."

*Hopper scurries up to the editor and changes the second line:*

```cufet
Define answer as 12 * 12.
State "The answer is answer.".
```

> **Hopper:** What do you think this says, friend?

*The output box asks: **What will appear here?** Three buttons:*

- **A.** `The answer is 144.`
- **B.** `The answer is answer.`
- **C.** Nothing, because Cufet refuses it

*The player picks one and presses Run.*

```output
The answer is answer.
```

**If they chose B:**

> **Hopper:** You didn't fall for it! Inside the quote marks, `answer` is just a word.

**If they chose A:**

> **Hopper:** That's the trap! Inside quote marks, Cufet says exactly what's written.

**If they chose C:**

> **Grace:** It runs. Cufet is happy to say "answer". It just doesn't know you meant the name.

*Whichever they chose:*

> **Hopper:** "The answer is answer." Not very helpful, is it?
>
> **Grace:** Inside quote marks, everything is text, and Cufet says text exactly as it's written. It doesn't look for names in there.

## Beat 4: a hole (Run, then Investigate)

> **Grace:** Unless you leave a hole for it.

*Grace changes the second line:*

```cufet
Define answer as 12 * 12.
State "The answer is {answer}.".
```

> **Hopper:** See the curly brackets? They make a **hole** in the text. Cufet fills the hole with whatever the name stands for.
>
> **Grace:** Press Run, friend.

*The player presses Run.*

```output
The answer is 144.
```

> **Hopper:** A real sentence!
>
> **Grace:** Now change the first line to `13 * 13`, and run it again. Just the first line.

*The output box keeps the task under **Your task:**, "Change just the first line to `13 * 13`, and
run it again." The player changes the first line and presses Run. Done when the output says
`The answer is 169.`*

> **Grace:** The sentence followed along. The hole always says whatever the name is keeping.

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| Changes the hole to `{169}` instead | Runs, prints `The answer is 169.`, but isn't done | **Grace:** That's the right sentence, but now the hole doesn't follow the name. Put `{answer}` back and change only the first line. |
| `Define answer as thirteen * thirteen.` | `That doesn't work: 'thirteen' isn't defined.` and three more lines | **Hopper:** Numbers are written with digits! Thirteen is `13`. |

## Beat 5: a blank page (Make)

*Grace clears the editor.*

> **Grace:** Your turn, friend. The page is empty. Have Cufet work out seven times seven, keep it as `answer`, and say it in a sentence.

*The editor is empty. Done when the output is this, judged on the words and numbers only:*

```output
The answer is 49.
```

> **Hopper:** Forty-nine! In a sentence!
>
> **Grace:** Worked out, kept, and said. Well done, friend.

### The hint ladder

1. > **Grace:** Two lines. The first keeps the answer, with `Define`. The second says the sentence.
2. > **Hopper:** The sentence needs a hole with the name in curly brackets, so Cufet fills in the number!
3. > **Grace:** Here it is. Type it yourself:
   >
   > `Define answer as 7 * 7.`
   > `State "The answer is {answer}.".`

### Reactions to what they might type

| They type | What Cufet does | The rabbits say |
| --- | --- | --- |
| No name: `State "The answer is 49.".` | Runs, prints the right sentence, but isn't done | **Grace:** Right sentence, but you worked it out yourself. Keep `7 * 7` as `answer`, and let Cufet do it. |
| Another name: `Define result as 7 * 7.` | Runs, prints the right sentence, but isn't done (Beat 6 needs the name `answer`) | **Grace:** That works! But I asked you to keep it as `answer`. Use that name, so Hopper can find it. |
| `State "The answer is 7 * 7.".` | Runs, prints `The answer is 7 * 7.` | **Hopper:** The sum is inside the quote marks, so Cufet just says it! Keep the sum with `Define`, and put a hole in the sentence. |
| Only `State answer.` for the second line | Runs, prints `49` | **Grace:** The right number, but Hopper wanted a sentence. Try `"The answer is {answer}."` |
| `State "The answer is " answer.` | `Line 2, column 24: expected '.', got Identifier "answer".` | **Grace:** The name has to go inside the sentence, in a hole: `{answer}`. |
| `Define answer 7 * 7.` | `Line 1, column 15: expected As, got Number "7".` | **Grace:** A name and its value are joined by `as`: `Define answer as 7 * 7.` |
| `Define answer = 7 * 7.` | `'=' is comparison, not assignment. A 'Define' introduces a name with 'as': 'Define answer as ...'.` (and more) | **Grace:** Cufet says it well itself: a `Define` uses `as`, not `=`. |
| `Define Answer as 7 * 7.` | `Line 1, column 8: identifier 'Answer' must start with a lowercase letter.` | **Hopper:** Names start with a small letter! `answer`, not `Answer`. |
| `Define my answer as 7 * 7.` | `That doesn't work: 'my' is not a defined type.` | **Grace:** A name can't have a space in it. Use one word, like `answer`. |
| The `Define` line last | `That doesn't work: 'answer' isn't defined.` and three more lines | **Grace:** The computer reads from the top down, remember? Keep the answer before you use it. |
| A typo in the hole: `{answr}` | `That doesn't work: 'answr' isn't defined.` and three more lines | **Hopper:** Cufet doesn't know `answr`! Check the spelling in the curly brackets. |
| Spaces in the hole: `{ answer }` | Runs, prints the right sentence | **Done.** Spaces inside the curly brackets don't matter. |

## Beat 6: one more, and a surprise (Predict)

> **Hopper:** One more thing to try, friend. What if we want eight times eight as well? Say we add it to the bottom, like this.

*Hopper adds two lines to the player's program:*

```cufet
Define answer as 7 * 7.
State "The answer is {answer}.".
Define answer as 8 * 8.
State "The answer is {answer}.".
```

> **Grace:** Hmm. Friend, will this one run?

*The output box asks: **Will it run?** Two buttons: **Runs** and **Refused**. The player picks one
and presses Run. The output box shows:*

```output
That doesn't work: 'answer' is already defined in this scope.
Here on line 3, you're trying to define 'answer' again in the same block.

Each name can only be defined once per block. Use 'becomes' to reassign it, or choose a different name.
```

**If they chose Refused:**

> **Grace:** Well spotted. A name can only be given once.

**If they chose Runs:**

> **Grace:** It does look like it should. But `Define` makes a *new* name, and there's already one called `answer`.

*Whichever they chose:*

> **Hopper:** Oh, but look what the message says at the end: "Use 'becomes'"!
>
> **Grace:** Right, a name *can* change partway through a program.
>
> **Hopper:** I love becoming! It means we have a chance to grow and be something new!

*End of lesson 3.*
