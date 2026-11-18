// The tutorial page: plays a lesson (web/tutorial/lesson-N.ts) step by step.
//
// Shares the runtime (worker.js) and the language (cufet-language.ts) with the playground, so the
// tutorial cannot run a different Cufet, or colour it differently, from the page beside it.
//
// The design it follows is tutorial/DESIGN.md. Where that file says nothing, this page does the
// least it can, and says so where it does it: see FALLBACK below.

import * as monaco from 'monaco-editor/editor/editor.api';
import { LANGUAGE_ID, semanticsChanged, startHighlighting } from './cufet-language';
import { RuntimeClient, TIME_LIMIT_MS } from './tutorial/runtime-client';
import { lesson1 } from './tutorial/lesson-1';
import { respond } from './tutorial/judge';
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
document.fonts.ready.then(() => monaco.editor.remeasureFonts());
startHighlighting(editor, (kind, source) => runtime.ask(kind, source))
    .catch(e => console.error('syntax highlighting failed to start:', e));

/** True while the player is meant to be typing. Pasting is refused then: DESIGN.md, "no pasting". */
let typing = false;
editor.onDidPaste(() => { if (typing) editor.trigger('tutorial', 'undo', null); });

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
    event.preventDefault();
    advance();
});

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
    for (let i = 0; i < lines.length; i++) {
        const line = lines[i]!;
        if (mine !== chain) return;
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

// ── The output box ───────────────────────────────────────────────────────────────────────────

function showAttempt(attempt: Attempt, guess?: string): void {
    outputBox.replaceChildren();
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

/** Resolves on the next press of Run. */
function runPressed(): Promise<void> {
    return new Promise(resolve => runButton.addEventListener('click', () => resolve(), { once: true }));
}

function lightRun(on: boolean): void {
    runButton.disabled = !on;
    runButton.classList.toggle('lit', on);
}

// ── The steps ────────────────────────────────────────────────────────────────────────────────

/** A rabbit types `code` into the editor, retyping only the part that differs. */
async function write(who: Who, code: string): Promise<void> {
    const model = editor.getModel();
    if (!model) return;
    busy(who, true);
    const old = model.getValue();
    let prefix = 0;
    while (prefix < old.length && prefix < code.length && old[prefix] === code[prefix]) prefix++;
    let suffix = 0;
    while (suffix < old.length - prefix && suffix < code.length - prefix
        && old[old.length - 1 - suffix] === code[code.length - 1 - suffix]) suffix++;

    const end = old.length - suffix;
    for (let at = end; at > prefix; at--) {          // delete the old middle, from its end
        const from = model.getPositionAt(at - 1), to = model.getPositionAt(at);
        model.applyEdits([{ range: monaco.Range.fromPositions(from, to), text: '' }]);
        await sleep(25);
    }
    const middle = code.slice(prefix, code.length - suffix);
    for (let i = 0; i < middle.length; i++) {        // and type the new one
        const at = model.getPositionAt(prefix + i);
        model.applyEdits([{ range: monaco.Range.fromPositions(at, at), text: middle[i]! }]);
        await sleep(middle[i] === '\n' ? 120 : 45);
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
    showAttempt(await runtime.attempt(editor.getValue()));
}

async function predict(step: Extract<Step, { kind: 'predict' }>): Promise<void> {
    outputBox.replaceChildren();
    const question = document.createElement('p');
    question.className = 'question';
    question.textContent = 'What will appear here?';
    const choices = document.createElement('div');
    choices.className = 'choices';
    const buttons = step.options.map(option => {
        const button = document.createElement('button');
        button.type = 'button';
        button.innerHTML = inline(option);
        choices.append(button);
        return button;
    });
    outputBox.append(question, choices);

    const chosen = await new Promise<number>(resolve =>
        buttons.forEach((button, i) => button.addEventListener('click', () => resolve(i), { once: true })));
    buttons.forEach((button, i) => { button.disabled = true; button.classList.toggle('chosen', i === chosen); });

    // DESIGN.md: once they have answered, the Run button lights up and a rabbit nudges them to it.
    lightRun(true);
    const pressed = runPressed();
    if (step.nudge) void say(step.nudge);
    await pressed;
    lightRun(false);
    chain++;
    hideBubbles();

    showAttempt(await runtime.attempt(editor.getValue()), step.options[chosen]);
    await say([...(step.reactions[chosen] ?? []), ...step.after]);
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

    private take(): void {
        const rung = this.hints[this.used];
        if (!rung || this.attemptsSinceHint === 0) return;
        this.used++;
        this.attemptsSinceHint = 0;
        this.paint();
        void say(rung);
    }

    private paint(): void {
        hintButton.disabled = this.attemptsSinceHint === 0 || this.used >= this.hints.length;
        hintButton.title = this.used >= this.hints.length ? 'No more hints'
            : this.attemptsSinceHint === 0 ? 'Press Run first' : '';
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
            const attempt = await runtime.attempt(codeNow());
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
    if (step.start !== undefined) editor.setValue(step.start);
    setTyping(true);
    await untilDone(() => editor.getValue(), step,
        code => { editor.setValue(code); editor.focus(); });
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

    editorHost.hidden = true;
    tilesList.hidden = false;
    paint();
    await say(step.intro);
    await untilDone(() => order.join('\n'), step, () => { });
    editor.setValue(order.join('\n'));
    tilesList.hidden = true;
    editorHost.hidden = false;
    await say(step.success);
}

async function enter(who: Who): Promise<void> {
    const rabbit = rabbits[who];
    rabbit.hidden = false;
    rabbit.classList.add('arriving');
    await sleep(600);
    rabbit.classList.remove('arriving');
}

async function play(lesson: Lesson): Promise<void> {
    element('lesson-title').textContent = `Lesson ${lesson.number}: ${lesson.title}`;
    for (const step of lesson.steps) {
        switch (step.kind) {
            case 'enter': await enter(step.who); break;
            case 'say': await say(step.lines); break;
            case 'write': await write(step.who, step.code); break;
            case 'rabbit-run': await rabbitRun(step.who); break;
            case 'predict': await predict(step); break;
            case 'exercise': await exercise(step); break;
            case 'parsons': await parsons(step); break;
            case 'end': {
                const ending = element('ending');
                ending.textContent = step.text;
                ending.hidden = false;
                break;
            }
        }
    }
}

play(lesson1).catch(e => console.error('the lesson stopped:', e));
