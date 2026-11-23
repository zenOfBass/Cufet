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
- **A Make exercise shows its goal.** The rabbits say what to make, and the output box keeps what
  the program should print, under "Make it say:", until it's done. What fades is help with the
  code, never the task: a bubble read too fast is gone, and a blank page with no goal on it left a
  play-tester with nothing to do. When the output alone isn't the whole task (say it *when* something
  is true), the box also keeps the task in a sentence, under "Your task:".
- **Parsons problems:** put given lines in order.
- **How an exercise is judged follows its step:** the guess, the output, or whether the checker
  refuses it.
- **The hint ladder:**
  - Each hint is earned by an attempt since the last one.
  - Hint before any attempt is answered, not dark: Hopper says "Try it first! Press Run, and if
    it goes wrong, I'll help." (A dark button looked broken in play-testing.)
  - The last rung is the answer.
  - The answer must be typed in by hand; no pasting. A player's own lines, copied or cut from the
    editor, may be pasted back: the rule is about the answer, not about repeating yourself.
  - A close variation may follow, with no answer offered, but only where it adds something.
- **Common slips:** one shared set of reactions to the slips any exercise can meet, said when the
  exercise has no reaction of its own for the attempt. Each is matched on Cufet's own message
  (`playground/web/tutorial/judge.ts`, `COMMON_SLIPS`). The words are first drafts:
  - No period on the last line. **Grace:** "Every line ends with a period, the last one too."
  - No period at the end of another line (Cufet names that line). **Grace:** "Every line ends
    with a period."
  - More on a line than Cufet expected. **Grace:** "Cufet expected that line to end where it says,
    with a period. Is a word too many, or a mark missing?"
  - A misspelled first word. **Grace:** "Cufet doesn't know that first word. Check its spelling,
    capital letter and all."
  - Words without quote marks. **Hopper:** "Words to say go in quote marks! Without them, Cufet
    thinks they're a name."
  - A block without its `Done.` **Hopper:** "Every colon opens a block, and every block needs its
    `Done.`"

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
- **The master is Maria, a frog,** named after Maria Mitchell, who computed the planets for the
  Nautical Almanac in her head. "Velveteen Rabbit" and "Skin Horse" are both a material and an
  animal, so the horse is named on the rabbits' terms; ours have names, so she has one too. A frog
  hops like the rabbits but is almost alien beside them, and still common.
- **She lives at a pond outside the garden.** She tells them "You'll find your way out through the
  garden", and they work out how to get in by themselves.
- **They already know the garden, and they're afraid of it,** because of the people there, as in
  the Peter Rabbit stories.
- **Two refrains.** Grace quotes "Good little rabbits always…", rules they were raised on, with
  affection, never as a scold. Most are real habits worth having; "Good little rabbits never go in
  the garden" is the one the story breaks. Hopper wonders what "Real rabbits…" do; the line keeps
  moving, as "when is Cufet a real language?" did. A few of each, a bubble or two at a time.
- **Maria only speaks.** The editor stays the rabbits'; she did it all in her head.
- **She first appears in the Void lesson,** and is in all of it.

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
- **Exercises check that the player did what was asked, not just the output.** When the task is
  to let Cufet do the sum, use a name, or use `Otherwise`, the game checks the code for it, since
  typing the answer in by hand can produce the same output. A shortcut gets a rabbit's nudge
  toward what was asked, not "done".
- **No hard breaks between lessons, for now.** The format isn't decided, so the dialogue never
  says "next time" or "another day"; one lesson leads straight into the next.
- The working scripts are [lesson-1-draft.md](lesson-1-draft.md) (saying words: the opening, several lines, the joke),
  [lesson-2-draft.md](lesson-2-draft.md) (numbers),
  [lesson-3-draft.md](lesson-3-draft.md) (names),
  [lesson-4-draft.md](lesson-4-draft.md) (becomes),
  [lesson-5-draft.md](lesson-5-draft.md) (choosing),
  [lesson-6-draft.md](lesson-6-draft.md) (repeating),
  [lesson-7-draft.md](lesson-7-draft.md) (series),
  [lesson-8-draft.md](lesson-8-draft.md) (functions), and
  [lesson-9-draft.md](lesson-9-draft.md) (void).

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
- One parse error would mislead a newcomer: `print("hi")` mentions `Becomes`. The tutorial
  can supply its own reactions; whether to improve the messages is a question for the language.
