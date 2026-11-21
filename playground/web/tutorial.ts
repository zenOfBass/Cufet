// The tutorial page: plays a lesson (web/tutorial/lesson-N.ts) step by step.
//
// Shares the runtime (worker.js) and the language (cufet-language.ts) with the playground, so the
// tutorial cannot run a different Cufet, or colour it differently, from the page beside it.
//
// The design it follows is tutorial/DESIGN.md. Where that file says nothing, this page does the
// least it can, and says so where it does it: see FALLBACK below.

import * as monaco from 'monaco-editor/editor/editor.api';
import { LANGUAGE_ID, semanticsChanged, startHighlighting, tokenizeEverythingNow } from './cufet-language';
import { RuntimeClient, TIME_LIMIT_MS } from './tutorial/runtime-client';
import { lesson1 } from './tutorial/lesson-1';
import { lesson2 } from './tutorial/lesson-2';
import { lesson3 } from './tutorial/lesson-3';
import { lesson4 } from './tutorial/lesson-4';
import { guessIsRight, outputLines, respond } from './tutorial/judge';
import type { Attempt } from './tutorial/judge';
import type { Lesson, Line, Reaction, Step, Who } from './tutorial/lesson';

function element<T extends HTMLElement>(id: string): T {
    const found = document.getElementById(id);
    if (!found) throw new Error(`the page is missing an element with id "${id}"`);
    return found as T;
}

// ★ FALLBACK. What the rabbits say when an attempt matches none of the step's prepared reactions.
// The drafts do not script these: they are the page's placeholders, kept short and plain until the
// drafts say otherwise.
const FALLBACK: Record<Attempt['outcome'], Line[]> = {
    ran: [{ who: 'hopper', text: 'Not quite! Look at what it said, and try again.' }],
    refused: [{ who: 'grace', text: "Cufet didn't run that one. Its message is in the output box." }],
    empty: [{ who: 'hopper', text: 'There is nothing to run yet!' }],
    timeout: [{ who: 'grace', text: `That was still going after ${TIME_LIMIT_MS / 1000} seconds, so I stopped it.` }],
    died: [{ who: 'grace', text: 'Something went wrong inside the computer itself. Press Run again.' }],
};

// ── The editor ───────────────────────────────────────────────────────────────────────────────

const runtime = new RuntimeClient(ready => { if (ready) semanticsChanged.fire(); });

const editor = monaco.editor.create(element('editor'), {
    value: '',
    language: LANGUAGE_ID,
    theme: 'vs-dark',              // replaced by the real theme once it loads; see app.ts
    automaticLayout: true,
    minimap: { enabled: false },
    scrollBeyondLastLine: false,
    fontSize: 16,
    fontFamily: getComputedStyle(document.documentElement).getPropertyValue('--mono').trim()
        || "'JetBrains Mono', Consolas, monospace",
    tabSize: 4,
    renderLineHighlight: 'none',
    padding: { top: 16, bottom: 16 },
    lineNumbersMinChars: 2,
    glyphMargin: false,
    folding: false,
    readOnly: true,
    // A beginner's editor: no pop-ups offering words they have not met.
    quickSuggestions: false,
    suggestOnTriggerCharacters: false,
    wordBasedSuggestions: 'off',
    parameterHints: { enabled: false },
    hover: { enabled: 'off' },
});
// ⚠⚠ ONE LINE ENDING. A model takes the OS's by default, which on Windows is \r\n: a rabbit's typed
// "\n" then became two characters, every offset after it was one place out, and Grace's second line
// landed in the middle of her first. Measured in Chrome on Windows, on lesson 1's Beat 5.
editor.getModel()?.setEOL(monaco.editor.EndOfLineSequence.LF);

/**
 * Replace the editor's text. ⚠⚠ Always through here, never `editor.setValue`: replacing the text
 * picks the line ending AGAIN, from the new text, and text with no line break in it (the blank
 * page, `''`) gets the OS's, \r\n. Every Enter the player then typed was two characters, and
 * Hopper's lines added in lesson 3's Beat 6 landed on the end of the line above. Play-tested.
 */
function setCode(text: string): void {
    editor.setValue(text);
    editor.getModel()?.setEOL(monaco.editor.EndOfLineSequence.LF);
}

/** The player's program, with \n line endings whatever the model holds. */
function codeNow(): string {
    return editor.getModel()?.getValue(monaco.editor.EndOfLinePreference.LF) ?? '';
}
document.fonts.ready.then(() => monaco.editor.remeasureFonts());
startHighlighting(editor, (kind, source) => runtime.ask(kind, source))
    .catch(e => console.error('syntax highlighting failed to start:', e));

/**
 * True while the player is meant to be typing. Pasting is refused then (DESIGN.md: "The answer
 * must be typed in by hand; no pasting"), EXCEPT text copied or cut from this editor.
 *
 * ★ The rule is about the hint's answer, and refusing every paste went wider than that: a
 * play-tester copied their own `The hops becomes hops + 1.` to write it three times, and could
 * not. Their own lines are theirs to repeat. What comes from anywhere else is still undone.
 */
let typing = false;
let copiedHere = '';

/** What a copy or cut takes from the editor: the selections, or with none, the whole lines. */
function selectedText(): string {
    const model = editor.getModel();
    const selections = editor.getSelections() ?? [];
    if (!model || selections.length === 0) return '';
    if (selections.every(s => s.isEmpty()))     // Monaco copies the whole line then
        return selections.map(s => `${model.getLineContent(s.startLineNumber)}\n`).join('');
    return selections.filter(s => !s.isEmpty())
        .map(s => model.getValueInRange(s, monaco.editor.EndOfLinePreference.LF)).join('\n');
}

for (const event of ['copy', 'cut'] as const)
    element('editor').addEventListener(event, () => { copiedHere = selectedText(); }, true);

editor.onDidPaste(({ range }) => {
    if (!typing) return;
    const pasted = editor.getModel()?.getValueInRange(range, monaco.editor.EndOfLinePreference.LF) ?? '';
    if (copiedHere !== '' && pasted.trimEnd() === copiedHere.trimEnd()) return;
    editor.trigger('tutorial', 'undo', null);
});

function setTyping(on: boolean): void {
    typing = on;
    editor.updateOptions({ readOnly: !on });
    if (on) editor.focus();
}

const runButton = element<HTMLButtonElement>('run');
const hintButton = element<HTMLButtonElement>('hint');
const outputBox = element('output');
const tilesList = element<HTMLOListElement>('tiles');
const editorHost = element('editor');

// ── Little markdown, for the drafts' `code`, **bold** and *italics* ─────────────────────────

function escapeHtml(text: string): string {
    return text.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c] ?? c);
}

function inline(text: string): string {
    return escapeHtml(text)
        .replace(/`([^`]+)`/g, '<code>$1</code>')
        .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
        .replace(/\*([^*]+)\*/g, '<em>$1</em>');
}

// ── Grace and Hopper ─────────────────────────────────────────────────────────────────────────

const rabbits: Record<Who, HTMLElement> = { hopper: element('hopper'), grace: element('grace') };
const bubbleOf = (who: Who) => rabbits[who].querySelector<HTMLElement>('.bubble')!;

function hideBubbles(): void {
    for (const who of ['hopper', 'grace'] as const) bubbleOf(who).hidden = true;
}

/**
 * The chain of bubbles now on screen. A new chain cancels the old one, so pressing Run again in the
 * middle of a reaction moves straight on to the reaction to the NEW attempt.
 */
let chain = 0;

/** Wakes whichever bubble is waiting to be read. */
let advance: (() => void) | null = null;

document.addEventListener('keydown', event => {
    if (!advance) return;
    if (event.key !== 'Enter' && event.key !== ' ') return;
    // Enter in the editor is a new line, not "next".
    if (editor.hasTextFocus()) return;
    if (document.activeElement instanceof HTMLButtonElement) return;
    // Nor in a guess's slot, where it moves to the next slot.
    if (document.activeElement instanceof HTMLInputElement) return;
    event.preventDefault();
    advance();
});

/**
 * What each rabbit said the last time it spoke: all its lines from that one go. Clicking a rabbit
 * says them again, for a player who read too fast (play-tested: the answer Grace gave at the end of
 * lesson 2 was gone once its bubble was clicked away, and clicking Grace was the first thing tried).
 */
const lastSaid: Record<Who, Line[]> = { hopper: [], grace: [] };

for (const who of ['hopper', 'grace'] as const) {
    for (const part of rabbits[who].querySelectorAll('img, figcaption')) {
        part.addEventListener('click', () => {
            // Not while a bubble waits to be read: that bubble is the one to read.
            if (advance || lastSaid[who].length === 0) return;
            void say([...lastSaid[who]]);
        });
    }
}

interface SayOptions {
    /** Shown in the last bubble: a button that does something. */
    offer?: { label: string; act: () => void };
}

/**
 * Say `lines`, one bubble at a time, each waiting to be read. Resolves when the last is read, or
 * at once when a newer chain has taken over.
 */
async function say(lines: Line[], options: SayOptions = {}): Promise<void> {
    const mine = ++chain;
    const spoke = new Set<Who>();
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i]!;
        if (mine !== chain) return;
        if (!spoke.has(line.who)) { spoke.add(line.who); lastSaid[line.who] = []; }
        lastSaid[line.who].push(line);
        hideBubbles();
        const bubble = bubbleOf(line.who);
        rabbits[line.who].hidden = false;
        bubble.innerHTML = inline(line.text)
            + (line.answer ? `<span class="answer">${escapeHtml(line.answer)}</span>` : '')
            + '<span class="next" aria-hidden="true">▸</span>';
        const offer = i === lines.length - 1 ? options.offer : undefined;
        if (offer) {
            const button = document.createElement('button');
            button.type = 'button';
            button.className = 'offer';
            button.textContent = offer.label;
            button.addEventListener('click', event => { event.stopPropagation(); offer.act(); hideBubbles(); });
            bubble.append(button);
        }
        bubble.hidden = false;

        await new Promise<void>(resolve => {
            let finished = false;
            const finish = () => {
                if (finished) return;
                finished = true;
                clearInterval(watch);
                bubble.removeEventListener('click', finish);
                // ⚠ Only our own: a newer chain may already have put its bubble's waker here.
                if (advance === finish) advance = null;
                resolve();
            };
            // A newer chain wakes this one so it can see it has been replaced.
            const watch = setInterval(() => { if (mine !== chain) finish(); }, 100);
            advance = finish;
            bubble.addEventListener('click', finish);
        });
    }
    if (mine === chain) hideBubbles();
}

function busy(who: Who, on: boolean): void {
    rabbits[who].classList.toggle('busy', on);
}

const sleep = (ms: number) => new Promise(resolve => setTimeout(resolve, ms));

/** Colour `element`'s text as Cufet, the way the editor colours it. Plain if that fails. */
function colourAsCode(element: HTMLElement): void {
    monaco.editor.colorize(element.textContent ?? '', LANGUAGE_ID, {})
        .then(html => { element.innerHTML = html; })
        .catch(() => { /* plain is fine */ });
}

// ── The output box ───────────────────────────────────────────────────────────────────────────

/**
 * The exercise's goal, while one is up: what the program should print. Kept at the top of the
 * output box through every attempt, so the task is never lost with the bubble that set it
 * (play-tested: at lesson 2's blank page, nothing on screen said what to make).
 */
let goal: string[] | null = null;

function faint(text: string): HTMLElement {
    const label = document.createElement('p');
    label.className = 'faint';
    label.textContent = text;
    return label;
}

function showGoal(): void {
    if (!goal) return;
    const lines = document.createElement('pre');
    lines.className = 'goal';
    lines.textContent = goal.join('\n');
    outputBox.append(faint('Make it say:'), lines);
}

function showAttempt(attempt: Attempt, guess?: string): void {
    outputBox.replaceChildren();
    if (goal) {
        showGoal();
        outputBox.append(faint('It said:'));
    }
    if (guess !== undefined) {
        const label = document.createElement('p');
        label.className = 'faint';
        label.textContent = 'You guessed:';
        const chosen = document.createElement('div');
        chosen.className = 'guess';
        chosen.innerHTML = inline(guess);
        const said = document.createElement('p');
        said.className = 'faint';
        said.textContent = 'It said:';
        outputBox.append(label, chosen, said);
    }
    const pre = document.createElement('pre');
    switch (attempt.outcome) {
        case 'ran': pre.textContent = attempt.output || '(nothing)'; break;
        case 'refused': pre.textContent = attempt.output; pre.className = 'refused'; break;
        case 'empty': pre.textContent = '(there is no program to run)'; break;
        case 'timeout': pre.textContent = `Stopped: still running after ${TIME_LIMIT_MS / 1000} seconds.`; pre.className = 'refused'; break;
        case 'died': pre.textContent = attempt.output; pre.className = 'refused'; break;
    }
    outputBox.append(pre);
}

/**
 * Run `code`, showing that work is going on if it takes a moment. Play-tested: a run took about two
 * seconds with nothing on screen to say so. A quick run shows nothing, so the box does not flicker.
 */
async function attemptShowingWork(code: string, keepBox = false): Promise<Attempt> {
    const running = document.createElement('p');
    running.className = 'running';
    running.innerHTML = '<span class="spinner" aria-hidden="true"></span>Running…';
    const timer = setTimeout(() => {
        // `keepBox`: the box holds something still needed (the guess slots), so add below it.
        if (!keepBox) {
            outputBox.replaceChildren();
            showGoal();
        }
        outputBox.append(running);
    }, 200);
    try {
        return await runtime.attempt(code);
    } finally {
        clearTimeout(timer);
        running.remove();
    }
}

/** Resolves on the next press of Run. */
function runPressed(): Promise<void> {
    return new Promise(resolve => runButton.addEventListener('click', () => resolve(), { once: true }));
}

function lightRun(on: boolean): void {
    runButton.disabled = !on;
    runButton.classList.toggle('lit', on);
}

// ── The steps ────────────────────────────────────────────────────────────────────────────────

/**
 * Where a rabbit's edit goes: the stretch of `old` to replace, by character offset.
 *
 * ★ Lines first, then characters. Lines kept whole at the top and bottom are left alone, so a line
 * added ABOVE another is typed as a new line above it, the way the drafts describe it ("She adds a
 * line above it"), rather than spliced into the middle of the line below. Only when a single line
 * changed into a single line is the change narrowed to the characters that differ, so changing one
 * word retypes one word.
 */
function changedStretch(old: string, code: string): { from: number; to: number; text: string } {
    const was = old.split('\n'), now = code.split('\n');
    let top = 0;
    while (top < was.length && top < now.length && was[top] === now[top]) top++;
    let bottom = 0;
    while (bottom < Math.min(was.length, now.length) - top
        && was[was.length - 1 - bottom] === now[now.length - 1 - bottom]) bottom++;

    const head = was.slice(0, top).reduce((n, line) => n + line.length + 1, 0);
    const tail = bottom === 0 ? 0 : was.slice(was.length - bottom).join('\n').length;
    let from = Math.min(head, old.length), to = old.length - tail;
    let text = code.slice(from, code.length - tail);
    if (to < from) to = from;

    const gone = old.slice(from, to);
    if (!gone.includes('\n') && !text.includes('\n')) {
        let same = 0;
        while (same < gone.length && same < text.length && gone[same] === text[same]) same++;
        let end = 0;
        while (end < gone.length - same && end < text.length - same
            && gone[gone.length - 1 - end] === text[text.length - 1 - end]) end++;
        from += same;
        to -= end;
        text = text.slice(same, text.length - end);
    }
    return { from, to, text };
}

/** How long a rabbit takes over one key: unhurried, a little uneven, slower after a word. */
function keystroke(typed: string): number {
    const pause = typed === '\n' ? 350 : /[ ,.!?]/.test(typed) ? 120 : 0;
    return 70 + Math.random() * 70 + pause;
}

/** A rabbit types `code` into the editor, retyping only the part that differs. */
async function write(who: Who, code: string): Promise<void> {
    const model = editor.getModel();
    if (!model) return;
    outputBox.replaceChildren();       // what the box said was about the program being replaced
    busy(who, true);
    const old = model.getValue(monaco.editor.EndOfLinePreference.LF);
    const { from, to, text } = changedStretch(old, code);

    if (old.slice(from, to).includes('\n')) {
        // More than a line goes: selected, then deleted at once, the way a person clears a page.
        // Backspacing a whole joke a key at a time took ten seconds of watching.
        const range = monaco.Range.fromPositions(model.getPositionAt(from), model.getPositionAt(to));
        editor.setSelection(range);
        await sleep(500);
        model.applyEdits([{ range, text: '' }]);
        editor.setPosition(model.getPositionAt(from));
        await sleep(250);
    } else {
        for (let at = to; at > from; at--) {        // delete the old stretch, from its end
            const start = model.getPositionAt(at - 1), end = model.getPositionAt(at);
            model.applyEdits([{ range: monaco.Range.fromPositions(start, end), text: '' }]);
            await sleep(55);
        }
    }
    for (let i = 0; i < text.length; i++) {         // and type the new one
        const at = model.getPositionAt(from + i);
        model.applyEdits([{ range: monaco.Range.fromPositions(at, at), text: text[i]! }]);
        await sleep(keystroke(text[i]!));
    }
    busy(who, false);
}

async function rabbitRun(who: Who): Promise<void> {
    busy(who, true);
    await sleep(400);
    runButton.classList.add('pressed');
    await sleep(180);
    runButton.classList.remove('pressed');
    busy(who, false);
    showAttempt(await attemptShowingWork(codeNow()));
}

async function playerRun(step: Extract<Step, { kind: 'player-run' }>): Promise<void> {
    lightRun(true);
    const pressed = runPressed();
    if (step.nudge) void say(step.nudge);
    await pressed;
    lightRun(false);
    chain++;
    hideBubbles();
    showAttempt(await attemptShowingWork(codeNow()));
}

function questionLine(text: string): HTMLElement {
    const question = document.createElement('p');
    question.className = 'question';
    question.textContent = text;
    return question;
}

async function predict(step: Extract<Step, { kind: 'predict' }>): Promise<void> {
    outputBox.replaceChildren();
    const question = questionLine(step.asks === 'runs' ? 'Will it run?' : 'What will appear here?');
    const choices = document.createElement('div');
    choices.className = 'choices';
    const buttons = step.options.map(option => {
        const button = document.createElement('button');
        button.type = 'button';
        // ★ Plain, every one, even an option that is code. The question is what will appear in
        // the OUTPUT BOX, which shows plain text, so every choice is shown the way it would land
        // there. Colouring only the code one would mark it as code, which is the very thing Beat 3's
        // third option asks the player to notice ("Close. That's the code.").
        button.innerHTML = inline(option);
        choices.append(button);
        return button;
    });
    outputBox.append(question, choices);

    // ★ A choice can be changed until Run is pressed. Locked on the first click, a slip of the
    // mouse was a wrong answer (play-tested: "that doesn't feel fair").
    let chosen = 0;
    await new Promise<void>(resolve => buttons.forEach((button, i) => button.addEventListener('click', () => {
        chosen = i;
        buttons.forEach((other, j) => other.classList.toggle('chosen', j === i));
        resolve();
    })));

    // DESIGN.md: once they have answered, the Run button lights up and a rabbit nudges them to it.
    lightRun(true);
    const pressed = runPressed();
    if (step.nudge) void say(step.nudge);
    await pressed;
    buttons.forEach(button => { button.disabled = true; });
    lightRun(false);
    chain++;
    hideBubbles();

    showAttempt(await attemptShowingWork(codeNow()), step.options[chosen]);
    await say([...(step.reactions[chosen] ?? []), ...step.after]);
}

/** One slot per line of output: the player types a guess in each, then Run shows the real ones. */
async function predictLines(step: Extract<Step, { kind: 'predict-lines' }>): Promise<void> {
    outputBox.replaceChildren();
    const grid = document.createElement('div');
    grid.className = 'slots';
    const slots = Array.from({ length: step.slots }, (_, i) => {
        const input = document.createElement('input');
        input.type = 'text';
        input.autocomplete = 'off';
        input.spellcheck = false;
        input.setAttribute('aria-label', `Line ${i + 1}`);
        const real = document.createElement('span');
        real.className = 'real';
        grid.append(input, real);
        return { input, real };
    });
    const question = questionLine('What will appear here?');
    outputBox.append(question, grid);

    const filled = () => slots.every(slot => slot.input.value.trim() !== '');
    slots.forEach(({ input }, i) => {
        input.addEventListener('input', () => lightRun(filled()));
        // Enter moves to the next slot, and on the last one presses Run.
        input.addEventListener('keydown', event => {
            if (event.key !== 'Enter') return;
            event.preventDefault();
            const next = slots[i + 1];
            if (next) next.input.focus();
            else if (filled()) runButton.click();
        });
    });
    lightRun(false);
    slots[0]?.input.focus();
    await runPressed();
    lightRun(false);
    chain++;
    hideBubbles();
    for (const { input } of slots) input.disabled = true;

    const attempt = await attemptShowingWork(codeNow(), true);
    if (attempt.outcome !== 'ran') {         // the lesson's own program; only a broken runtime gets here
        showAttempt(attempt);
        return;
    }
    const printed = outputLines(attempt.output);
    const said: Line[][] = [];
    slots.forEach(({ input, real }, i) => {
        const right = guessIsRight(input.value, printed[i] ?? '');
        real.textContent = right ? '✓' : '✗';
        real.classList.add(right ? 'right' : 'wrong');
        const reaction = step.wrong[i];
        if (!right && reaction && !said.includes(reaction)) said.push(reaction);
    });
    // ★ Then the run itself, as the box shows any run: the question was what would appear HERE,
    // so what appeared is shown here, whole, under the guesses. Laid out as the multiple-choice
    // questions are ("You guessed:" / "It said:"). Play-tested: with each real line tucked beside
    // its guess, the box never showed the program's own output.
    question.className = 'faint';
    question.textContent = 'You guessed:';
    const ran = document.createElement('pre');
    ran.textContent = attempt.output || '(nothing)';
    outputBox.append(faint('It said:'), ran);
    await say([...(said.length === 0 ? step.right : said.flat()), ...(step.after ?? [])]);
}

/**
 * The hint ladder (DESIGN.md): each rung is earned by an attempt since the last one, and the last
 * rung is the answer, to be typed by hand.
 */
class HintLadder {
    private used = 0;
    private attemptsSinceHint = 0;

    constructor(private readonly hints: Line[][]) {
        hintButton.hidden = hints.length === 0;
        hintButton.onclick = () => this.take();
        this.paint();
    }

    attempted(): void { this.attemptsSinceHint++; this.paint(); }

    /** A new rung is ready: one is left, and the player has tried since the last. */
    private get ready(): boolean { return this.used < this.hints.length && this.attemptsSinceHint > 0; }

    private take(): void {
        if (!this.ready) {
            // ★ No new rung yet: the last one again, so a hint read too fast is not lost.
            const last = this.hints[this.used - 1];
            if (last) void say(last);
            return;
        }
        const rung = this.hints[this.used]!;
        this.used++;
        this.attemptsSinceHint = 0;
        this.paint();
        void say(rung);
    }

    private paint(): void {
        hintButton.disabled = !this.ready && this.used === 0;
        hintButton.title = this.ready ? ''
            : this.used === 0 ? 'Press Run first'
            : 'Hear the last hint again';
    }

    close(): void { hintButton.hidden = true; hintButton.onclick = null; }
}

/**
 * Run attempts until one counts as done. `codeNow` reads the player's program; `step` holds
 * everything the step prepared. Shared by exercises and Parsons problems.
 */
async function untilDone(
    codeNow: () => string,
    step: { done: (attempt: Attempt) => boolean; reactions: Reaction[]; hints: Line[][] },
    restore: (code: string) => void,
): Promise<void> {
    const ladder = new HintLadder(step.hints);
    let lastWithCode = codeNow();
    lightRun(true);
    try {
        for (;;) {
            await runPressed();
            chain++;
            hideBubbles();
            runButton.disabled = true;
            const attempt = await attemptShowingWork(codeNow());
            runButton.disabled = false;
            showAttempt(attempt);
            ladder.attempted();
            if (attempt.code.trim() !== '') lastWithCode = attempt.code;

            const { reaction, done } = respond(step, attempt);
            if (done) {
                if (reaction) await say(reaction.lines);
                return;
            }
            const back = lastWithCode;
            void say(reaction?.lines ?? FALLBACK[attempt.outcome], reaction?.offerRestore
                ? { offer: { label: 'Yes, put it back', act: () => restore(back) } }
                : {});
        }
    } finally {
        lightRun(false);
        ladder.close();
    }
}

async function exercise(step: Extract<Step, { kind: 'exercise' }>): Promise<void> {
    if (step.start !== undefined) setCode(step.start);
    goal = step.goal ?? null;
    if (goal) { outputBox.replaceChildren(); showGoal(); }
    setTyping(true);
    try {
        await untilDone(codeNow, step,
            code => { setCode(code); editor.focus(); });
    } finally {
        goal = null;
    }
    setTyping(false);
    await say(step.success);
}

/** Tiles in the editor's place, to drag (or move with the arrows) into order. */
async function parsons(step: Extract<Step, { kind: 'parsons' }>): Promise<void> {
    const order = [...step.tiles];
    let dragging: number | null = null;

    const paint = () => {
        tilesList.replaceChildren(...order.map((text, i) => {
            const tile = document.createElement('li');
            tile.className = 'tile';
            tile.draggable = true;
            const code = document.createElement('code');
            code.textContent = text;
            colourAsCode(code);
            const up = document.createElement('button');
            up.type = 'button'; up.textContent = '▲'; up.disabled = i === 0;
            up.setAttribute('aria-label', 'Move up');
            up.addEventListener('click', () => move(i, i - 1));
            const down = document.createElement('button');
            down.type = 'button'; down.textContent = '▼'; down.disabled = i === order.length - 1;
            down.setAttribute('aria-label', 'Move down');
            down.addEventListener('click', () => move(i, i + 1));
            tile.append(code, up, down);
            tile.addEventListener('dragstart', () => { dragging = i; tile.classList.add('dragging'); });
            tile.addEventListener('dragend', () => { dragging = null; tile.classList.remove('dragging'); });
            tile.addEventListener('dragover', event => event.preventDefault());
            tile.addEventListener('drop', event => {
                event.preventDefault();
                if (dragging !== null) move(dragging, i);
            });
            return tile;
        }));
    };
    const move = (from: number, to: number) => {
        if (to < 0 || to >= order.length) return;
        const [tile] = order.splice(from, 1);
        order.splice(to, 0, tile!);
        paint();
    };

    outputBox.replaceChildren();
    editorHost.hidden = true;
    tilesList.hidden = false;
    document.body.classList.add('sorting');
    paint();
    await say(step.intro);
    await untilDone(() => order.join('\n'), step, () => { });
    tilesList.hidden = true;
    editorHost.hidden = false;
    document.body.classList.remove('sorting');
    // ⚠ Shown FIRST, then filled, then coloured all at once. Filled while hidden, Monaco coloured
    // only the lines it had been shown and left the rest to an idle pass that never came: the
    // joke came back with two lines coloured and three plain. The playground's own trap, met again.
    setCode(order.join('\n'));
    const model = editor.getModel();
    if (model) tokenizeEverythingNow(model);
    await say(step.success);
}

/** A rabbit arrives. One already here stays put: a lesson that follows on finds them waiting. */
async function enter(who: Who): Promise<void> {
    const rabbit = rabbits[who];
    if (!rabbit.hidden) return;
    rabbit.hidden = false;
    rabbit.classList.add('arriving');
    await sleep(600);
    rabbit.classList.remove('arriving');
}

/** A rabbit dashes off the edge of the screen; `enter` brings it back. */
async function leave(who: Who): Promise<void> {
    const rabbit = rabbits[who];
    if (rabbit.hidden) return;
    bubbleOf(who).hidden = true;
    rabbit.classList.add('leaving');
    await sleep(450);
    rabbit.classList.remove('leaving');
    rabbit.hidden = true;
}

async function play(lesson: Lesson): Promise<void> {
    element('lesson-title').textContent = `Lesson ${lesson.number}: ${lesson.title}`;
    for (const step of lesson.steps) {
        switch (step.kind) {
            case 'enter': await enter(step.who); break;
            case 'leave': await leave(step.who); break;
            case 'say': await say(step.lines); break;
            case 'write': await write(step.who, step.append
                ? `${codeNow().trimEnd()}\n${step.code}`
                : step.code); break;
            case 'rabbit-run': await rabbitRun(step.who); break;
            case 'player-run': await playerRun(step); break;
            case 'predict': await predict(step); break;
            case 'predict-lines': await predictLines(step); break;
            case 'exercise': await exercise(step); break;
            case 'parsons': await parsons(step); break;
            case 'end': await ending(step.text, LESSONS[LESSONS.indexOf(lesson) + 1]); break;
        }
    }
}

const LESSONS: Lesson[] = [lesson1, lesson2, lesson3, lesson4];

/**
 * The end of a lesson. With a lesson after it, a button goes on to it, and this resolves when it is
 * pressed; after the last lesson it never does.
 */
function ending(text: string, next: Lesson | undefined): Promise<void> {
    const box = element('ending');
    const line = document.createElement('p');
    line.textContent = text;
    box.replaceChildren(line);
    box.hidden = false;
    if (!next) return new Promise(() => { });
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = `On to lesson ${next.number}`;
    box.append(button);
    button.focus();
    return new Promise(resolve => button.addEventListener('click', () => { box.hidden = true; resolve(); }, { once: true }));
}

/** Plays from the lesson the address names (`?lesson=2`), or the first, to the end. */
async function playFrom(number: number): Promise<void> {
    const first = Math.max(0, LESSONS.findIndex(lesson => lesson.number === number));
    for (const lesson of LESSONS.slice(first)) {
        // So a reload comes back to the lesson the player is on.
        const address = new URL(location.href);
        address.searchParams.set('lesson', String(lesson.number));
        history.replaceState(null, '', address);
        await play(lesson);
    }
}

playFrom(Number(new URLSearchParams(location.search).get('lesson') ?? 1))
    .catch(e => console.error('the lesson stopped:', e));
