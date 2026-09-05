// The playground's user interface.
//
// The Cufet side of this page is deliberately thin: the worker boots the .NET runtime and answers
// three questions over postMessage. Everything below is the editor and the wiring between it and
// those answers. The wire format itself lives in protocol.ts, shared with the worker.
//
// Monaco is imported through editor.api rather than editor.main, which is the entry that brings
// NO built-in languages with it. That is exactly what this page wants — Cufet is the only
// language it will ever show, and editor.main would ship ninety grammars to every visitor.

// The specifier looks short because monaco-editor's exports map rewrites "./*" to "./esm/vs/*.js"
// — spelling out the esm/vs prefix here resolves to esm/vs/esm/vs/... and fails.
import * as monaco from 'monaco-editor/editor/editor.api';
import { loadWASM, OnigScanner, OnigString } from 'vscode-oniguruma';
import { Registry, INITIAL, parseRawGrammar } from 'vscode-textmate';
import type { StateStack } from 'vscode-textmate';
import { isReady } from './protocol';
import type { RequestKind, RuntimeAnswer } from './protocol';

// ★ Semantic tokens do not work in standalone Monaco without these next three lines, and NOTHING
// reports that they are missing. The two facts behind them, both read out of monaco-editor 0.56.0:
//
//  1. `registerDocumentSemanticTokensProvider` only puts a provider in a registry. The thing that
//     READS that registry is an editor CONTRIBUTION — `registerEditorFeature(...)` at the bottom of
//     contrib/semanticTokens/browser/documentSemanticTokens.js — and editor.api.js imports exactly
//     one contribution (format). So the provider registers, and nothing ever calls it.
//  2. Even loaded, the feature asks `isSemanticColoringEnabled`, which reads the setting
//     `editor.semanticHighlighting.enabled` — default `'configuredByTheme'`, not a boolean, so it
//     defers to `theme.semanticHighlighting`. And StandaloneTheme sets that field to `false` in its
//     constructor and never reads it back off the theme data. Passing `semanticHighlighting: true`
//     to `defineTheme` is therefore INERT — a plausible-looking line that does nothing, which is
//     what sent four attempts at this bug looking in the wrong place. The setting is the only lever.
//
// This is the price of editor.api over editor.main: features come à la carte. Adding just this one
// is still far cheaper than the ninety grammars editor.main brings with it.
import 'monaco-editor/editor/contrib/semanticTokens/browser/documentSemanticTokens';
import { StandaloneServices } from 'monaco-editor/editor/standalone/browser/standaloneServices';
import { IConfigurationService } from 'monaco-editor/platform/configuration/common/configuration';

const configuration = StandaloneServices.get(IConfigurationService);
configuration.updateValue('editor.semanticHighlighting.enabled', true);
if (configuration.getValue<{ enabled?: unknown } | undefined>('editor.semanticHighlighting')?.enabled !== true)
    console.error('semantic highlighting could not be switched on — names will keep the grammar\'s colours');

const LANGUAGE_ID = 'cufet';

/**
 * An element the page cannot work without.
 *
 * ★ Under `strict`, `getElementById` is `HTMLElement | null` and every use needs an answer for the
 * null. Asserting it away with `!` would give a `Cannot read properties of null` at some later line
 * that has nothing to do with the cause; this names the missing id at the moment it is missing.
 */
function element<T extends HTMLElement>(id: string): T {
    const found = document.getElementById(id);
    if (!found) throw new Error(`the page is missing an element with id "${id}"`);
    return found as T;
}

// A first program that shows the language rather than describing it. Taken verbatim from the
// README's "A taste" section rather than written fresh, so the first thing a visitor runs is a
// program that is already known to work and already appears in the docs.
const STARTER = `Define the scores as a series with (92, 85, 71, 88).
Define total as 0.

For each score in the scores, repeat:
    The total becomes the total + the score.
Done.

Define the average as the total / the number of the scores.
State the average.
`;

// Monaco asks for a worker per language service. With editor.api and no languages there is only
// the editor's own worker, and esbuild has already emitted it beside this file.
self.MonacoEnvironment = {
    getWorkerUrl: () => './editor.worker.js',
};

monaco.languages.register({ id: LANGUAGE_ID, extensions: ['.cufe'], aliases: ['Cufet'] });

// Mirrors editors/vscode/language-configuration.json. Keep the two in step — they are the same
// language, and every rule below was worked out once already for the extension.
monaco.languages.setLanguageConfiguration(LANGUAGE_ID, {
    // C-style, with one difference: the block form NESTS, as it does in Rust and Swift.
    comments: { lineComment: '//', blockComment: ['/*', '*/'] },

    brackets: [['(', ')'], ['{', '}']],

    autoClosingPairs: [
        { open: '/*', close: ' */' },
        { open: '(', close: ')' },
        { open: '{', close: '}' },
        // No pair for ' — the possessive 's would auto-close into nonsense on every field access.
        { open: '"', close: '"', notIn: ['string', 'comment'] },
    ],

    surroundingPairs: [
        { open: '(', close: ')' },
        { open: '{', close: '}' },
        { open: '"', close: '"' },
    ],

    // Hyphens are identifier characters in Cufet: add-edge is ONE name. Without this, double-click
    // selects only "add" and word-wise cursor movement stops inside the name.
    wordPattern: /[A-Za-z][A-Za-z0-9]*(?:-[A-Za-z0-9]+)*|\d+(?:\.\d+)?/g,

    // A block opens with a trailing ':' and closes with 'Done.' or 'Until ...'. 'Otherwise' closes
    // the arm above it and opens its own, so it dedents going in and its ':' indents the body out.
    indentationRules: {
        increaseIndentPattern: /:\s*$/,
        decreaseIndentPattern: /^\s*(done|until|otherwise)(?![\w-])/i,
    },

    onEnterRules: [
        { beforeText: /:\s*$/, action: { indentAction: monaco.languages.IndentAction.Indent } },
    ],
});

// ── Highlighting ─────────────────────────────────────────────────────────────────────────────
//
// Monaco's own tokenizer format is Monarch, and porting the grammar to it was rejected: there
// would then be two grammars for one language, and they would drift. Instead the editor is given
// the SAME TextMate grammar file the VS Code extension uses, run through the same engine VS Code
// runs it through — vscode-textmate over an Oniguruma regex engine compiled to WebAssembly.
//
// It also buys the theme. VS Code colour themes are scope→colour rules, so they only mean
// anything if the tokenizer produces real TextMate scopes. Monarch could not have consumed one.
//
// All of it is async (a wasm fetch, two JSON fetches), so it deliberately does NOT block the
// editor from appearing. Monaco renders unhighlighted for a moment, then re-tokenizes.
async function startHighlighting(): Promise<void> {
    const [wasm, grammarSource, theme] = await Promise.all([
        fetch('./onig.wasm').then(r => r.arrayBuffer()),
        fetch('./cufet.tmLanguage.json').then(r => r.text()),
        // ⚠ A fetched JSON body is `any` by construction — no amount of strictness makes a network
        // response knowable. It is asserted, not validated: a malformed theme is a build mistake
        // (build-theme.mjs produces this file), and Monaco rejecting it says so plainly enough.
        fetch('./cufet-theme.json').then(r => r.json() as Promise<monaco.editor.IStandaloneThemeData>),
    ]);

    await loadWASM(wasm);

    const registry = new Registry({
        onigLib: Promise.resolve({
            createOnigScanner: (sources: string[]) => new OnigScanner(sources),
            createOnigString: (s: string) => new OnigString(s),
        }),
        loadGrammar: scopeName => Promise.resolve(
            scopeName === 'source.cufet'
                ? parseRawGrammar(grammarSource, 'cufet.tmLanguage.json')
                : null),
    });

    const grammar = await registry.loadGrammar('source.cufet');
    if (!grammar) throw new Error('the Cufet grammar did not load');

    // The page's half of this theme is already applied — build.mjs emits it as theme-chrome.css,
    // linked in the page head — so only the editor's half is set here. Semantic highlighting is
    // switched on at the top of this file, NOT here: a standalone theme drops the flag on the
    // floor. See the note there.
    monaco.editor.defineTheme('arctic-candy-darker', theme);
    monaco.editor.setTheme('arctic-candy-darker');

    monaco.languages.setTokensProvider(LANGUAGE_ID, {
        // ⚠ Two libraries, two spellings of one idea. vscode-textmate threads a `StateStack`
        // through tokenizeLine; Monaco threads an `IState`. The objects are the same objects and
        // both carry clone/equals, but neither package knows about the other, so the handoff is
        // asserted at exactly these two points and nowhere else.
        getInitialState: () => INITIAL as unknown as monaco.languages.IState,
        tokenize(line: string, state: monaco.languages.IState): monaco.languages.ILineTokens {
            const result = grammar.tokenizeLine(line, state as unknown as StateStack);
            return {
                // TextMate gives each token the whole stack of scopes that applies to it; Monaco
                // matches a single string. The INNERMOST scope is the most specific one, and
                // Monaco's own theme matching is prefix-based on the dots, so a rule for
                // "comment" still catches "comment.line.double-slash.cufet".
                tokens: result.tokens.map(t => ({
                    startIndex: t.startIndex,
                    scopes: t.scopes[t.scopes.length - 1] ?? '',
                })),
                endState: result.ruleStack as unknown as monaco.languages.IState,
            };
        },
    });

    // The layer the grammar cannot reach. A regex cannot tell a variable from a function from a
    // type in an English-like syntax, so this asks the real front end, in the worker, what each
    // name IS. It sits on top of the TextMate pass rather than replacing it: keywords, strings and
    // comments keep the colours they already had.
    //
    // The legend is spelled as SCOPES rather than as the LSP names the producer uses, because a
    // standalone Monaco theme has no semantic-token defaults — it resolves a token by matching the
    // type against its ordinary rules. Naming them this way means the theme already in hand colours
    // every kind, with no second colour table to keep in step with the first.
    // ★ onDidChange is not optional here, whatever the interface says. Monaco asks for semantic
    // tokens ONCE per model revision, and the first ask lands the instant this provider is
    // registered — which is the moment three small fetches finish, seconds before the .NET runtime
    // in the worker has booted. askRuntime answers null to everything until then, so that first ask
    // resolves to nothing and, with no revision to invalidate it, Monaco never asks again. The page
    // then sits on raw TextMate colours forever, and looks for all the world like a provider that
    // was never registered. Firing this when the runtime becomes able to answer is what makes the
    // whole semantic layer arrive.
    monaco.languages.registerDocumentSemanticTokensProvider(LANGUAGE_ID, {
        onDidChange: semanticsChanged.event,
        getLegend: () => SEMANTIC_LEGEND,
        releaseDocumentSemanticTokens() { },
        async provideDocumentSemanticTokens(model) {
            // While a program is running the worker is inside a synchronous call and cannot answer,
            // and after a Stop it no longer exists. Handing back the previous answer keeps the
            // colours steady instead of dropping them for the length of a run.
            const reply = await askRuntime('tokens', model.getValue());
            if (reply === null) return lastSemanticTokens;
            lastSemanticTokens = { data: encodeSemanticTokens(reply) };
            return lastSemanticTokens;
        },
    });
}

// Order IS the wire format, and it mirrors SemanticTokenKind in src/Interpreter/SemanticTokens.cs.
const SEMANTIC_LEGEND: monaco.languages.SemanticTokensLegend = {
    tokenTypes: [
        'entity.name.namespace',    // namespace — a book or its alias
        'entity.name.type',         // type      — an object or interface name
        'variable.parameter',       // parameter
        'variable',                 // variable
        'variable.other.property',  // property  — a field, getter or setter
        'entity.name.function',     // function
        // keyword — `output` and `seed`, which open a statement but lex as identifiers. The
        // grammar cannot tell those from a variable of the same name; the producer can, because
        // it has the parse. Spelled as the scope the theme already colours every other keyword.
        'keyword.control',
    ],
    tokenModifiers: [],
};

const SEMANTIC_KIND_INDEX: Readonly<Record<string, number>> = {
    namespace: 0, type: 1, parameter: 2, variable: 3, property: 4, function: 5, keyword: 6,
};

/** One line of the producer's output. Mirrors the JSON written by src/Interpreter/SemanticTokens.cs. */
interface ProducedToken {
    kind: string;
    line: number;
    column: number;
    length: number;
}

let lastSemanticTokens: monaco.languages.SemanticTokens = { data: new Uint32Array(0) };

// Fired whenever the runtime goes from "cannot answer" to "can" — the worker finishing its boot,
// or a run releasing it. Monaco responds by asking the provider again for every visible model,
// which is the only way an answer that was unavailable the first time ever reaches the screen.
const semanticsChanged = new monaco.Emitter<void>();

// Monaco wants one flat array of 5-tuples, each position stated as a delta from the one before it:
// line relative to the previous token's line, and start column relative to the previous token's
// column when they share a line. The producer already emits in position order, which is what makes
// a single pass enough.
function encodeSemanticTokens(jsonLines: string): Uint32Array {
    const data: number[] = [];
    let prevLine = 0, prevChar = 0;

    for (const raw of jsonLines.split('\n')) {
        const text = raw.trim();
        if (!text.startsWith('{')) continue;

        let token: ProducedToken;
        try { token = JSON.parse(text) as ProducedToken; } catch { continue; }

        const index = SEMANTIC_KIND_INDEX[token.kind];
        if (index === undefined) continue;

        const line = token.line - 1, char = token.column - 1;   // the producer counts from 1
        const deltaLine = line - prevLine;
        data.push(deltaLine, deltaLine === 0 ? char - prevChar : char, token.length, index, 0);
        prevLine = line;
        prevChar = char;
    }

    return new Uint32Array(data);
}

const editor = monaco.editor.create(element('editor'), {
    value: STARTER,
    language: LANGUAGE_ID,
    theme: 'vs-dark',   // replaced by Arctic Candy Darker once the theme has been fetched
    automaticLayout: true,
    minimap: { enabled: false },
    scrollBeyondLastLine: false,
    fontSize: 14,
    // Read from the stylesheet rather than repeated here, so the editor and the output pane
    // cannot end up on different fonts.
    fontFamily: getComputedStyle(document.documentElement).getPropertyValue('--mono').trim()
        || "'JetBrains Mono', Consolas, monospace",
    fontLigatures: true,
    tabSize: 4,
    renderLineHighlight: 'none',
    padding: { top: 16, bottom: 16 },

    // ⚠ Monaco's gutter defaults reserve a lot of width for things this page does not have, and
    // on a phone that width comes straight out of the code. Measured against the defaults:
    //   lineNumbersMinChars  5 -> 3   room for 5 digits, on files of at most a few hundred lines
    //   lineDecorationsWidth 10 -> 6  the strip between numbers and text; nothing draws in it
    //   glyphMargin       true -> off a column for breakpoint dots, which this page cannot set
    // The line numbers stay: they are how a diagnostic's `(line N)` is found by eye.
    lineNumbersMinChars: 3,
    lineDecorationsWidth: 6,
    glyphMargin: false,
});

// ★ Narrower still where every column counts. Folding earns its keep on `huffmancoding.cufe` at
// 200-odd lines and costs a gutter it has not earned on a phone, so it is a width question rather
// than a preference — and `matchMedia` answers it again when the phone is turned sideways.
const narrow = window.matchMedia('(max-width: 820px)');
function fitTheGutter(): void {
    editor.updateOptions({
        folding: !narrow.matches,
        lineNumbersMinChars: narrow.matches ? 2 : 3,
        lineDecorationsWidth: narrow.matches ? 2 : 6,
    });
}
narrow.addEventListener('change', fitTheGutter);
fitTheGutter();

// Monaco measures character width once, when it is created. With font-display: swap the editor
// is very likely to be built while a fallback face is still showing, and every column position —
// the cursor, selections, the current-line highlight — stays measured against that fallback until
// it is told otherwise. The misalignment is subtle enough to look like a Monaco bug rather than a
// font-loading one, which is exactly why it is worth an explicit line.
document.fonts.ready.then(() => monaco.editor.remeasureFonts());

const runButton = element<HTMLButtonElement>('run');
const outputPane = element('output');
const statusText = element('status');

type OutputKind = 'normal' | 'error' | 'empty';

function setOutput(text: string, kind: OutputKind): void {
    outputPane.textContent = text;
    outputPane.dataset['kind'] = kind;
}

// ── The examples ─────────────────────────────────────────────────────────────────────────────
//
// ★★ The corpus is the best documentation of what the language can do, and none of it was
// reachable from the page that exists to show the language off. 35 of the 38 are here; the three
// left out need a terminal (C axioms, launching a program) and `build.mjs` selects them out by
// scanning for the construct, not the word — `axioms.cufe` discusses C axioms at length while
// using none.

/** One row of examples-manifest.json, written by build.mjs. */
interface Example {
    path: string;
    group: string;
    name: string;
}

const menuButton = element<HTMLButtonElement>('examples-button');
const menuPanel = element('examples-panel');

// ⚠ Fetched at boot, but only the LIST — a few hundred bytes of names. An example's text is
// fetched when it is picked. Seeding already sits on the critical path to a usable page, and
// thirty-odd more requests do not belong there.
async function loadExampleList(): Promise<void> {
    let examples: Example[];
    try {
        const response = await fetch('./examples-manifest.json');
        if (!response.ok) throw new Error(`examples-manifest.json: ${response.status}`);
        ({ examples } = await response.json() as { examples: Example[] });
    } catch (e) {
        // A nicety. The editor, the runtime and Run are the page; losing the list costs a way in,
        // not the thing itself, so it stays quiet and disabled rather than shouting.
        console.warn('the example list could not be loaded:', e);
        return;
    }

    let group = '';
    for (const example of examples) {
        if (example.group !== group) {
            group = example.group;
            const heading = document.createElement('h3');
            heading.textContent = group;
            menuPanel.append(heading);
        }
        const item = document.createElement('button');
        item.type = 'button';
        item.textContent = example.name;
        item.dataset['path'] = example.path;
        item.setAttribute('role', 'menuitem');
        item.addEventListener('click', () => { closeMenu(); void openExample(example.path); });
        menuPanel.append(item);
    }

    menuButton.disabled = false;
}

/** Every item, in the order they appear — what the arrow keys walk. */
const menuItems = (): HTMLButtonElement[] =>
    Array.from(menuPanel.querySelectorAll('button'));

function openMenu(): void {
    menuPanel.hidden = false;
    menuButton.setAttribute('aria-expanded', 'true');
    menuItems()[0]?.focus();
}

// ★ Focus goes back to the button. Closing a menu and leaving focus nowhere strands a keyboard
// reader at the top of the document, which is the most common way this control is got wrong.
function closeMenu(): void {
    if (menuPanel.hidden) return;
    menuPanel.hidden = true;
    menuButton.setAttribute('aria-expanded', 'false');
    menuButton.focus();
}

menuButton.addEventListener('click', () => (menuPanel.hidden ? openMenu() : closeMenu()));

// Escape from anywhere inside, and arrows to walk it — what a menu is expected to do.
menuPanel.addEventListener('keydown', event => {
    if (event.key === 'Escape') { closeMenu(); return; }
    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;

    event.preventDefault();
    const items = menuItems();
    const here = items.indexOf(document.activeElement as HTMLButtonElement);
    const step = event.key === 'ArrowDown' ? 1 : -1;
    // Wraps, because a list this long is easier to reach the end of by going up from the top.
    items[(here + step + items.length) % items.length]?.focus();
});

// ⚠ Anywhere outside closes it. Without this the panel survives a click on the editor and sits
// over the code the visitor just went back to reading.
document.addEventListener('pointerdown', event => {
    if (!menuPanel.hidden && !element('examples-menu').contains(event.target as Node)) closeMenu();
});

// ⚠ The path in the manifest is the path in a checkout, and the path it is served from — the same
// arrangement the seeded assets use, so there is no mapping to get wrong.
async function openExample(path: string): Promise<void> {
    const model = editor.getModel();
    if (!model) return;

    try {
        const response = await fetch(`./${path}`);
        if (!response.ok) throw new Error(`${path}: ${response.status}`);
        const text = await response.text();

        // ★ setValue, not a patch: this replaces the program wholesale, and the undo stack of the
        // program before it is not something a reader wants to step back into.
        model.setValue(text);
        editor.setPosition({ lineNumber: 1, column: 1 });
        editor.revealLine(1);
        editor.focus();
    } catch (e) {
        console.warn('could not open the example:', e);
        setOutput(`Could not load ${path}.`, 'error');
    }
}

// ⚠ Opening does NOT run. The visitor chose to read something; running it is a second decision.
// The editor content changing triggers a re-check, so the squiggles follow along on their own.
// ── Diagnostics ──────────────────────────────────────────────────────────────────────────────
//
// ★★ The SAME front end the VS Code extension calls, reached the same way: `Check` answers one
// JSON object per line, exactly as `cufet check --json` does, and this parses it exactly as
// editors/vscode/extension.js does. Two editors, one set of diagnostics, no second opinion to
// drift out of step with the first.
//
// ⚠ The machinery has been exported since the playground was built and nothing ever called it,
// so the page has never shown a squiggle. That is what this is.

/** One line of Check's output. Mirrors what the CLI emits and what the extension parses. */
interface Reported {
    line: number;
    severity: string;
    message: string;
}

/**
 * Where to draw the mark for a diagnostic on `line`.
 *
 * ★ Ported from the extension's `rangeForLine`, and it matters that it agrees: underline the
 * CODE on the line rather than its leading indentation, and never produce a zero-width range —
 * a zero-width squiggle is an invisible squiggle. A blank line has nothing to underline, so it
 * takes in the line break to leave a visible mark.
 */
function markerRange(model: monaco.editor.ITextModel, oneBasedLine: number) {
    const last = model.getLineCount();
    const line = Math.min(Math.max(oneBasedLine || 1, 1), last);
    const text = model.getLineContent(line);

    if (text.trim().length > 0)
        return {
            startLineNumber: line,
            startColumn: model.getLineFirstNonWhitespaceColumn(line),
            endLineNumber: line,
            endColumn: model.getLineMaxColumn(line),
        };

    return line < last
        ? { startLineNumber: line, startColumn: 1, endLineNumber: line + 1, endColumn: 1 }
        : { startLineNumber: line, startColumn: 1, endLineNumber: line, endColumn: model.getLineMaxColumn(line) };
}

async function refreshDiagnostics(): Promise<void> {
    const model = editor.getModel();
    if (!model) return;

    const answer = await askRuntime('check', model.getValue());
    // ⚠ null means the runtime could not answer — booting, or busy inside a run. Leaving the
    // marks alone is right: dropping them for the length of a run would make the editor flicker
    // clean every time someone pressed Run.
    if (answer === null) return;

    const markers: monaco.editor.IMarkerData[] = [];
    for (const raw of answer.split('\n')) {
        const text = raw.trim();
        if (!text.startsWith('{')) continue;

        let reported: Reported;
        try { reported = JSON.parse(text) as Reported; } catch { continue; }

        markers.push({
            ...markerRange(model, reported.line),
            message: reported.message,
            severity: reported.severity === 'warning'
                ? monaco.MarkerSeverity.Warning
                : monaco.MarkerSeverity.Error,
            source: 'cufet',
        });
    }

    monaco.editor.setModelMarkers(model, 'cufet', markers);
}

// ⚠ Debounced, because Check runs the whole front end — lex, parse, type-check, and the loader
// for any book the program pulls. Asking on every keystroke would queue work the visitor has
// already invalidated by typing the next character.
let checkPending: ReturnType<typeof setTimeout> | undefined;
function scheduleDiagnostics(): void {
    clearTimeout(checkPending);
    checkPending = setTimeout(() => { void refreshDiagnostics(); }, 400);
}

// ── The runtime, which lives in a worker ─────────────────────────────────────────────────────
//
// Cufet runs synchronously, so it cannot share a thread with the interface: a slow program would
// freeze the page and a non-terminating one would end it. Everything below is the small amount of
// bookkeeping that buys — a Stop button that always works, because stopping is killing the worker.

let worker: Worker | null = null;
let booted = false;
let running: number | null = null;      // the id of the run in flight, or null
let nextRunId = 1;

function spawnWorker(): void {
    booted = false;
    // A module worker, because the .NET runtime's loader is an ES module. Same-origin, same
    // directory — the runtime resolves _framework/ relative to this script, exactly as it would
    // have on the page.
    worker = new Worker('./worker.js', { type: 'module' });
    worker.onmessage = onWorkerMessage;
    worker.onerror = e => {
        setOutput(`the runtime failed to start: ${e.message}`, 'error');
        setBusy(false);
        statusText.textContent = 'the runtime failed';
    };
}

// Question-and-answer traffic that is not a program run — the semantic-token requests. Kept in its
// own map so it never touches `running`, which the Run/Stop button depends on meaning exactly one
// thing: a program is in flight.
const asked = new Map<number, (answer: string | null) => void>();

// Resolves null when the runtime cannot answer — not booted, busy inside a run, or killed by Stop.
// Null means "no new answer", which the caller reads as "keep what you had".
//
// ★ `worker` is in the guard for the type checker's benefit and states a real invariant: `booted`
// is only ever set by a message FROM a worker, so the two cannot disagree — but nothing in the
// types said so, and now something does.
function askRuntime(kind: RequestKind, source: string): Promise<string | null> {
    if (!booted || running !== null || !worker) return Promise.resolve(null);
    const live = worker;
    return new Promise(resolve => {
        const id = nextRunId++;
        asked.set(id, resolve);
        live.postMessage({ id, kind, source });
    });
}

// A terminated worker will never reply, so every question waiting on it has to be let go or the
// caller waits forever.
function abandonAsked(): void {
    for (const resolve of asked.values()) resolve(null);
    asked.clear();
}

function onWorkerMessage({ data }: MessageEvent<RuntimeAnswer>): void {
    // ★ The boot notice is separated FIRST, where it used to fall through the id lookup. It carries
    // no id, so `asked.get(undefined)` missed and it reached the right branch anyway — equivalent,
    // but only by accident of a lookup that could not match. Now the shape says which is which.
    if (isReady(data)) {
        booted = true;
        setBusy(false);
        statusText.textContent = 'ready';
        semanticsChanged.fire();
        // ★ The first check of the session. Everything before this answered null, so the starter
        // program has never been looked at — and a page that greets you with no marks on a
        // program it has not read is indistinguishable from one that found nothing wrong.
        scheduleDiagnostics();
        if (pendingAutoRun) { pendingAutoRun = false; run(); }
        return;
    }

    const answer = asked.get(data.id);
    if (answer) {
        asked.delete(data.id);
        answer(data.ok ? data.result : null);
        return;
    }

    // A reply from a run that was already abandoned — the worker it came from has been replaced.
    // Ignoring it keeps a stale result from overwriting whatever the page shows now.
    if (data.id !== running) return;
    running = null;
    setBusy(false);
    // The worker is answerable again. It refused every token request for the length of the run —
    // and the auto-run on first boot means that refusal covers the page's whole arrival.
    semanticsChanged.fire();
    scheduleDiagnostics();

    if (data.ok) {
        const text = data.result;
        setOutput(text.length ? text : '(no output)', text.length ? 'normal' : 'empty');
        statusText.textContent = `ran in ${Math.round(data.elapsed)} ms`;
    } else if (data.fatal) {
        // ⚠⚠ The runtime EXITED — it will answer nothing further, and every later run throws too.
        // Measured with sudoku.cufe: before this, the page reported the failure, left `booted`
        // true, and kept talking to a corpse, so one deep program bricked the playground until a
        // reload. Replacing the worker is the same recovery Stop already performs.
        //
        // ★ It states the fact and does not diagnose, for the same reason the depth message does:
        // an exit says the runtime is gone, never why. Deep recursion is ONE known cause and is
        // named as such — the page allows far less stack than the command line does — without
        // claiming it is the cause this time.
        setOutput(
            `The runtime stopped while running this program, and the page has started a fresh one. `
            + `Anything the program had printed is gone.\n\n`
            + `This is not a Cufet error — the runtime ended, which it cannot report on from the `
            + `inside. One known cause is going deeper than this page's stack allows, which is much `
            + `smaller than the cufet command line's, so a program that runs there can still stop `
            + `the runtime here.\n\n(${data.error})`,
            'error');
        statusText.textContent = 'the runtime stopped — restarting';
        abandonAsked();
        spawnWorker();
        setBusy(false);
    } else {
        setOutput(data.error, 'error');
        statusText.textContent = 'the runtime failed';
    }
}

// The Run button becomes Stop while a program is in flight. One control, and it is always the
// thing you want: a runaway program is precisely when a visitor is looking for a way out.
function setBusy(busy: boolean): void {
    runButton.textContent = busy ? 'Stop' : 'Run';
    runButton.classList.toggle('is-stop', busy);
    runButton.disabled = !busy && !booted;
}

function run(): void {
    if (!booted || running !== null || !worker) return;
    running = nextRunId++;
    setBusy(true);
    statusText.textContent = 'running…';
    worker.postMessage({ id: running, kind: 'run', source: editor.getValue() });
}

function stop(): void {
    if (running === null || !worker) return;
    // terminate() is the only thing that can interrupt a synchronous WebAssembly loop from
    // outside. The worker is then unusable, so a fresh one is started immediately and the next
    // Run waits for it — see the comment at the top of worker.js for why it has to be this way.
    worker.terminate();
    abandonAsked();
    running = null;
    setOutput('Stopped.', 'empty');
    statusText.textContent = 'restarting the runtime…';
    // Respawn BEFORE repainting the button: spawnWorker clears `booted`, and setBusy reads it to
    // decide whether Run is clickable. The other order leaves the button briefly live over a
    // runtime that does not exist yet.
    spawnWorker();
    setBusy(false);
}

// ⚠ On every edit, debounced. `onDidChangeModelContent` fires per keystroke; scheduleDiagnostics
// collapses a burst of them into one ask once the typing stops.
editor.onDidChangeModelContent(() => scheduleDiagnostics());

runButton.addEventListener('click', () => (running === null ? run() : stop()));

// Ctrl/Cmd-Enter runs, which is what every playground on the web has trained people to expect.
editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, () => run());

// Run the starter program once the runtime is up, so the output pane shows something real on
// arrival instead of an empty box the visitor has to guess at. Only on the FIRST boot — a restart
// after Stop must not immediately re-run the program the visitor just stopped.
let pendingAutoRun = true;

setBusy(false);
spawnWorker();

// Highlighting is a nicety; running Cufet is the point. If the grammar, the theme or the regex
// engine fails to load, say so in the console and leave a working uncoloured editor rather than
// taking the page down with it.
startHighlighting().catch(e => console.error('syntax highlighting failed to start:', e));

// Neither of these blocks the runtime, and neither can take the page down: the editor and Run
// are what the page is for, and both work with no examples and no colour.
loadExampleList().catch(e => console.error('the example list failed to load:', e));
