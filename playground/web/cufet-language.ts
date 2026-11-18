// The Cufet language, as Monaco sees it: registration, editing rules, and highlighting.
//
// Shared by the playground (app.ts) and the tutorial (tutorial.ts), so the two pages cannot drift
// apart in how they colour or indent the same language. Moved here verbatim from app.ts; the only
// change is that startHighlighting is TOLD which editor and how to ask the runtime, where it used
// to reach for app.ts's own.

// The specifier looks short because monaco-editor's exports map rewrites "./*" to "./esm/vs/*.js"
// — spelling out the esm/vs prefix here resolves to esm/vs/esm/vs/... and fails.
import * as monaco from 'monaco-editor/editor/editor.api';
import { loadWASM, OnigScanner, OnigString } from 'vscode-oniguruma';
import { Registry, INITIAL, parseRawGrammar } from 'vscode-textmate';
import type { StateStack } from 'vscode-textmate';
import type { RequestKind } from './protocol';

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

export const LANGUAGE_ID = 'cufet';

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
// editor from appearing. Monaco renders unhighlighted for a moment, then re-tokenizes — but only
// if something asks it to, which is what tokenizeEverythingNow is for.

/**
 * Tokenize the whole program NOW, rather than whenever the browser next feels idle.
 *
 * ⚠⚠ **Registering a tokenizer against a model that already exists does NOT reliably repaint it.**
 * Monaco repaints synchronously only the line ranges an attached view has already reported, and
 * hands everything else to a background pass that runs inside `requestIdleCallback`. Measured on
 * Opera for Android: on a cold load that idle slot never came, and the program sat in the default
 * foreground — keywords, numbers and marks alike — with only the semantic layer's names coloured
 * on top of nothing. Waiting did not help. Scrolling did, because scrolling reports a range.
 *
 * ★ **Building the editor AFTER the grammar instead does not fix this**, and that was measured too
 * rather than reasoned: the model's token store is built before any view is attached to it, so
 * there is still nothing to paint synchronously and the work still falls to the same idle pass.
 * The tokenizer's PRESENCE was never the missing piece — something asking for the lines was.
 *
 * ⚠ `forceTokenization` is load-bearing inside Monaco and is NOT in `monaco.d.ts`, so reaching it
 * takes a cast. That is a bet on an internal, and it is why `monaco-editor` is pinned to an exact
 * version in package.json rather than a range — the bet is only ever re-taken deliberately. If a
 * future version moves it, this says so in the console instead of quietly going back to waiting.
 */
function tokenizeEverythingNow(model: monaco.editor.ITextModel): void {
    const reachable = model as unknown as
        { tokenization?: { forceTokenization?(lineNumber: number): void } };

    if (typeof reachable.tokenization?.forceTokenization !== 'function') {
        console.error(
            'monaco no longer exposes forceTokenization — the first paint is back to whenever the '
            + 'browser next goes idle, which some browsers never do while the runtime is booting');
        return;
    }

    reachable.tokenization.forceTokenization(model.getLineCount());
}

/**
 * Colour `editor`, and keep colouring it. `ask` is how this reaches the runtime for the semantic
 * layer; it answers null whenever the runtime cannot answer, which means "keep what you had".
 */
export async function startHighlighting(
    editor: monaco.editor.IStandaloneCodeEditor,
    ask: (kind: RequestKind, source: string) => Promise<string | null>,
): Promise<void> {
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

    // ⚠⚠ And then paint what is already on screen, because registering a tokenizer against a model
    // that ALREADY EXISTS does not reliably do it. See tokenizeEverythingNow.
    const model = editor.getModel();
    if (model) tokenizeEverythingNow(model);

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
            const reply = await ask('tokens', model.getValue());
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
export const semanticsChanged = new monaco.Emitter<void>();

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
