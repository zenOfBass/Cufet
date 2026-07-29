// The playground's user interface.
//
// The Cufet side of this page is deliberately thin: main.js boots the .NET runtime, hangs two
// functions off globalThis.cufet, and fires "cufet-ready". Everything below is the editor and
// the wiring between it and those two functions.
//
// Monaco is imported through editor.api rather than editor.main, which is the entry that brings
// NO built-in languages with it. That is exactly what this page wants — Cufet is the only
// language it will ever show, and editor.main would ship ninety grammars to every visitor.

// The specifier looks short because monaco-editor's exports map rewrites "./*" to "./esm/vs/*.js"
// — spelling out the esm/vs prefix here resolves to esm/vs/esm/vs/... and fails.
import * as monaco from 'monaco-editor/editor/editor.api';
import { loadWASM, OnigScanner, OnigString } from 'vscode-oniguruma';
import { Registry, INITIAL, parseRawGrammar } from 'vscode-textmate';

const LANGUAGE_ID = 'cufet';

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
async function startHighlighting() {
    const [wasm, grammarSource, theme] = await Promise.all([
        fetch('./onig.wasm').then(r => r.arrayBuffer()),
        fetch('./cufet.tmLanguage.json').then(r => r.text()),
        fetch('./cufet-theme.json').then(r => r.json()),
    ]);

    await loadWASM(wasm);

    const registry = new Registry({
        onigLib: Promise.resolve({
            createOnigScanner: sources => new OnigScanner(sources),
            createOnigString:  s => new OnigString(s),
        }),
        loadGrammar: scopeName => Promise.resolve(
            scopeName === 'source.cufet'
                ? parseRawGrammar(grammarSource, 'cufet.tmLanguage.json')
                : null),
    });

    const grammar = await registry.loadGrammar('source.cufet');
    if (!grammar) throw new Error('the Cufet grammar did not load');

    // The page's half of this theme is already applied — build.mjs emits it as theme-chrome.css,
    // linked in the page head — so only the editor's half is set here.
    monaco.editor.defineTheme('arctic-candy-darker', theme);
    monaco.editor.setTheme('arctic-candy-darker');

    monaco.languages.setTokensProvider(LANGUAGE_ID, {
        getInitialState: () => INITIAL,
        tokenize(line, state) {
            const result = grammar.tokenizeLine(line, state);
            return {
                // TextMate gives each token the whole stack of scopes that applies to it; Monaco
                // matches a single string. The INNERMOST scope is the most specific one, and
                // Monaco's own theme matching is prefix-based on the dots, so a rule for
                // "comment" still catches "comment.line.double-slash.cufet".
                tokens: result.tokens.map(t => ({
                    startIndex: t.startIndex,
                    scopes: t.scopes[t.scopes.length - 1],
                })),
                endState: result.ruleStack,
            };
        },
    });
}

const editor = monaco.editor.create(document.getElementById('editor'), {
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
});

// Monaco measures character width once, when it is created. With font-display: swap the editor
// is very likely to be built while a fallback face is still showing, and every column position —
// the cursor, selections, the current-line highlight — stays measured against that fallback until
// it is told otherwise. The misalignment is subtle enough to look like a Monaco bug rather than a
// font-loading one, which is exactly why it is worth an explicit line.
document.fonts.ready.then(() => monaco.editor.remeasureFonts());

const runButton = document.getElementById('run');
const outputPane = document.getElementById('output');
const statusText = document.getElementById('status');

function setOutput(text, kind) {
    outputPane.textContent = text;
    outputPane.dataset.kind = kind;
}

// ── The runtime, which lives in a worker ─────────────────────────────────────────────────────
//
// Cufet runs synchronously, so it cannot share a thread with the interface: a slow program would
// freeze the page and a non-terminating one would end it. Everything below is the small amount of
// bookkeeping that buys — a Stop button that always works, because stopping is killing the worker.

let worker = null;
let booted = false;
let running = null;      // the id of the run in flight, or null
let nextRunId = 1;

function spawnWorker() {
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

function onWorkerMessage({ data }) {
    if (data.ready) {
        booted = true;
        setBusy(false);
        statusText.textContent = 'ready';
        if (pendingAutoRun) { pendingAutoRun = false; run(); }
        return;
    }

    // A reply from a run that was already abandoned — the worker it came from has been replaced.
    // Ignoring it keeps a stale result from overwriting whatever the page shows now.
    if (data.id !== running) return;
    running = null;
    setBusy(false);

    if (data.ok) {
        const text = data.result;
        setOutput(text.length ? text : '(no output)', text.length ? 'normal' : 'empty');
        statusText.textContent = `ran in ${Math.round(data.elapsed)} ms`;
    } else {
        setOutput(data.error, 'error');
        statusText.textContent = 'the runtime failed';
    }
}

// The Run button becomes Stop while a program is in flight. One control, and it is always the
// thing you want: a runaway program is precisely when a visitor is looking for a way out.
function setBusy(busy) {
    runButton.textContent = busy ? 'Stop' : 'Run';
    runButton.classList.toggle('is-stop', busy);
    runButton.disabled = !busy && !booted;
}

function run() {
    if (!booted || running !== null) return;
    running = nextRunId++;
    setBusy(true);
    statusText.textContent = 'running…';
    worker.postMessage({ id: running, kind: 'run', source: editor.getValue() });
}

function stop() {
    if (running === null) return;
    // terminate() is the only thing that can interrupt a synchronous WebAssembly loop from
    // outside. The worker is then unusable, so a fresh one is started immediately and the next
    // Run waits for it — see the comment at the top of worker.js for why it has to be this way.
    worker.terminate();
    running = null;
    setOutput('Stopped.', 'empty');
    statusText.textContent = 'restarting the runtime…';
    // Respawn BEFORE repainting the button: spawnWorker clears `booted`, and setBusy reads it to
    // decide whether Run is clickable. The other order leaves the button briefly live over a
    // runtime that does not exist yet.
    spawnWorker();
    setBusy(false);
}

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
