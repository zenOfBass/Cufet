# The tutorial: design notes

Only what has been agreed goes here. Anything still being worked out is under **Open**.

## What it is

A game for learning Cufet, written in TypeScript, for someone who knows nothing about programming,
possibly a child.

- **The screen** is a traditional editor: code on top, the **output box** below. (Not "terminal":
  `with the terminal` already means something in Cufet.)
- **Grace and Hopper** are desktop-pet sprites around the edges, with speech bubbles.
- **The feel** is mostly heads-down exercises. The story moves in small moments between them.
- **No language model.** Help is hints written ahead of time, plus prepared reactions to the
  checker's refusals and to the program's output.

## How it teaches

- **PRIMM:** Predict, Run, Investigate, Modify, Make.
- **Worked examples fade to a blank page.**
- **Parsons problems:** put given lines in order.
- **How an exercise is judged follows its step:** the guess, the output, or whether the checker
  refuses it.
- **The hint ladder:**
  - Each hint is earned by an attempt since the last one.
  - The last rung is the answer.
  - The answer must be typed in by hand; no pasting.
  - A close variation follows, with no answer offered.

## The story

From *The Velveteen Rabbit*, with a Zen turn: Grace and Hopper were always Real, and they only have
to learn it.

- A master, like the Skin Horse, sends them off: "you'll find your way out through the garden."
  They assume there's a hole to escape through. There isn't one, and the master didn't promise one.
- There are no gates, stages or "now you're Real" reward.
- It ends with the two of them looking out at a sunset together.
- **Frame models:** the Tibetan *Prince and the Zombie*, where the learning happens on the way and
  the frame takes its time before the quest begins; and Marpa and Milarepa's towers.
- **The story starts small.** The master's send-off is not at the start; it comes once there's
  something to be sent toward.

## What is taught, and in what order

Everyone does the same material. Testing out might come later, not now.

The order is a net, not a line. The **trunk** comes first, then **main topics** and **side quests**
open as their neighbours are done. A main topic never requires a side quest. The net itself,
with the evidence for every edge, is in [`tools/curriculum-net/`](../tools/curriculum-net/).

**The trunk**, in order:

1. **Step zero:** what a program is, the editor, the Run button, the output box.
2. **Saying something:** `State`, text and numbers, arithmetic.
3. **Names:** `Define`, and giving a name a new value.
4. **Choosing:** `If`, `Otherwise`.
5. **Repeating:** `While`, `Repeat … until`, `For each` over a series.
6. **Functions.**
7. **Void.**

## The first lesson

- **The first exercise is a demonstration.** Grace and Hopper write `State "Hello, world!".`,
  press Run, and show what appears in the output box.
- **Then the first question.** They change it to `State "Hello, rabbits!".` and ask what the
  player thinks the output will be, before running it.
- **The rabbits press Run only once, in that demonstration.** From then on the player presses it:
  once they've answered, the Run button lights up and a rabbit nudges them toward it.
- **The first texts end in `!`, not a period,** so the only period on the line is the one that
  ends the instruction.
- **The first writing exercises judge the words only.** Punctuation and capital letters are
  ignored, so nobody fails over a comma. A rabbit may remark on a small difference without
  failing the player. The wrong words, or a refused program, still don't count as done.
- The working scripts are [lesson-1-draft.md](lesson-1-draft.md) (the opening) and
  [lesson-2-draft.md](lesson-2-draft.md) (more lines, and numbers).

## How questions are asked

- **The question appears where the answer will land.** Before Run, the output box asks what will
  appear, and the choices are buttons inside it. After Run, the real output appears under the
  player's guess, in the same box.
- **A typed prediction** is typed into the output box.
- **"Will it run?"** is two buttons in the output box: *Runs* and *Refused*.
- **A Parsons problem** puts the lines in the editor as tiles to drag into order.
- The editor is for code, the output box is for what you expect and what you get, and the
  rabbits' bubbles carry the talk around them.

## Open

- Whether Grace and Hopper speak the hints and reactions, or only the story moments.
- How Reading input works in a browser: input written before Run, or typing while the program runs.
  The playground today gives every program empty input.
- Parse errors are terse (`expected '.', got Eof ""`), and two would mislead a newcomer:
  `print("hi")` mentions `Becomes`, and `Say "hi".` talks about lowercase letters. The tutorial
  can supply its own reactions; whether to improve the messages is a question for the language.
