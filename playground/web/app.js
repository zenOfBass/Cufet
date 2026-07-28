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

// Cufet has no Monaco grammar yet. Registering the language without one is deliberate: the plan
// is to feed Monaco the SAME TextMate grammar the VS Code extension uses, via the oniguruma
// bridge, rather than hand-port it to Monarch — one grammar, so the two cannot drift. Until then
// the editor is unhighlighted, which is honest, where a half-ported Monarch grammar would not be.
monaco.languages.register({ id: LANGUAGE_ID, extensions: ['.cufe'], aliases: ['Cufet'] });

// Mirrors editors/vscode/language-configuration.json. Keep the two in step — they are the same
// language, and every rule below was worked out once already for the extension.
monaco.languages.setLanguageConfiguration(LANGUAGE_ID, {
    // Cufet has no line comment. [[ ]] is all there is, and it nests.
    comments: { blockComment: ['[[', ']]'] },

    brackets: [['[[', ']]'], ['(', ')'], ['{', '}']],

    autoClosingPairs: [
        { open: '[[', close: ' ]]' },
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

const editor = monaco.editor.create(document.getElementById('editor'), {
    value: STARTER,
    language: LANGUAGE_ID,
    theme: 'vs-dark',
    automaticLayout: true,
    minimap: { enabled: false },
    scrollBeyondLastLine: false,
    fontSize: 14,
    fontFamily: "'Cascadia Code', 'Fira Code', Consolas, 'Courier New', monospace",
    tabSize: 4,
    renderLineHighlight: 'none',
    padding: { top: 16, bottom: 16 },
});

const runButton = document.getElementById('run');
const outputPane = document.getElementById('output');
const statusText = document.getElementById('status');

function setOutput(text, kind) {
    outputPane.textContent = text;
    outputPane.dataset.kind = kind;
}

function run() {
    if (!globalThis.cufet) return;

    runButton.disabled = true;
    // The interpreter runs synchronously on this thread, so a long program freezes the page until
    // it finishes. Yield one frame first, otherwise the button never visibly changes state and a
    // slow program looks like a dead page. Moving the runtime to a worker is the real fix, and it
    // is worth doing before anyone can paste an accidental infinite loop in here.
    requestAnimationFrame(() => {
        try {
            const started = performance.now();
            const result = globalThis.cufet.run(editor.getValue());
            const elapsed = Math.round(performance.now() - started);
            setOutput(result.length ? result : '(no output)', result.length ? 'normal' : 'empty');
            statusText.textContent = `ran in ${elapsed} ms`;
        } catch (e) {
            // Run() catches everything Cufet-level and returns it as text, so reaching here means
            // the runtime itself failed — worth showing plainly rather than only in the console.
            setOutput(String(e), 'error');
            statusText.textContent = 'the runtime failed';
        } finally {
            runButton.disabled = false;
        }
    });
}

runButton.addEventListener('click', run);

// Ctrl/Cmd-Enter runs, which is what every playground on the web has trained people to expect.
editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, run);

function onRuntimeReady() {
    runButton.disabled = false;
    statusText.textContent = 'ready';
    // Run the starter program once, so the output pane shows something real on arrival instead of
    // an empty box the visitor has to guess at.
    run();
}

// main.js sets the flag before firing the event. Checking it first covers the case where the
// runtime finished booting before this module ran — the event would already be gone.
if (globalThis.cufetReady) onRuntimeReady();
else document.addEventListener('cufet-ready', onRuntimeReady, { once: true });
