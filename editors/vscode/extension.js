'use strict';

// Error squiggles for Cufet.
//
// This is deliberately not a language server. Cufet's front end already produces exactly one
// diagnostic per run, with a line number and a long, carefully-written explanation — so there
// is nothing here for LSP's incremental machinery to earn back. Running `cufet check --json`
// and turning its output into a vscode.Diagnostic is the whole job, and it stays honest by
// construction: the squiggles are the compiler's own errors, never a second opinion from a
// re-implementation of the rules that could drift away from it.

const vscode = require('vscode');
const cp     = require('child_process');
const fs     = require('fs');
const os     = require('os');
const path   = require('path');

const LANGUAGE           = 'cufet';
const TYPE_DEBOUNCE_MS   = 400;
const CHECK_TIMEOUT_MS   = 20000;

let diagnostics;
let output;
let debounce;
let warnedNoExecutable = false;

// ── Finding something to run ──────────────────────────────────────────────

function readSubdirectories(dir) {
    try {
        return fs.readdirSync(dir, { withFileTypes: true })
                 .filter(entry => entry.isDirectory())
                 .map(entry => entry.name);
    } catch {
        return [];
    }
}

// A workspace can hold several builds at once — Debug and Release, one directory per target
// framework — so take the most recently built one. That is the one that matches the source
// you are currently looking at.
function discoverExecutable() {
    const exeName = process.platform === 'win32' ? 'Cufet.App.exe' : 'Cufet.App';
    const found   = [];

    for (const folder of vscode.workspace.workspaceFolders || []) {
        const bin = path.join(folder.uri.fsPath, 'src', 'App', 'bin');
        for (const config of readSubdirectories(bin))
            for (const framework of readSubdirectories(path.join(bin, config))) {
                const candidate = path.join(bin, config, framework, exeName);
                try { found.push({ candidate, mtime: fs.statSync(candidate).mtimeMs }); } catch { }
            }
    }

    found.sort((a, b) => b.mtime - a.mtime);
    return found.length > 0 ? found[0].candidate : null;
}

function resolveExecutable() {
    const configured = vscode.workspace.getConfiguration('cufet').get('executable', '').trim();
    return configured || discoverExecutable() || 'cufet';
}

function reportMissingExecutable(attempted) {
    if (warnedNoExecutable) return;
    warnedNoExecutable = true;

    vscode.window.showWarningMessage(
        `Cufet: couldn't run '${attempted}'. Error checking stays off until there is a build to run.`,
        'Build it', 'Open settings'
    ).then(choice => {
        if (choice === 'Build it') {
            const terminal = vscode.window.createTerminal('Cufet build');
            terminal.show(true);
            terminal.sendText('dotnet build');
            warnedNoExecutable = false;   // let it try again once that build lands
        } else if (choice === 'Open settings') {
            vscode.commands.executeCommand('workbench.action.openSettings', 'cufet.executable');
        }
    });
}

// ── Checking ──────────────────────────────────────────────────────────────

// Unsaved edits have to reach the front end somehow, and it only reads files. Working from a
// copy is safe precisely because neither `check` nor `tokens` runs the program: nothing in either
// resolves a path relative to the source file, so the copy behaves identically to the original.
// The prefix keeps two kinds of request from writing the same scratch file at the same moment.
function materialize(document, prefix) {
    if (!document.isDirty && !document.isUntitled)
        return { target: document.uri.fsPath, cleanup: () => { } };

    const scratch = path.join(os.tmpdir(),
        `cufet-${prefix}-${process.pid}-${fingerprint(document.uri.toString())}.cufe`);
    try {
        fs.writeFileSync(scratch, document.getText(), 'utf8');
        return { target: scratch, cleanup: () => { try { fs.unlinkSync(scratch); } catch { } } };
    } catch {
        return { target: document.uri.fsPath, cleanup: () => { } };   // fall back to what is on disk
    }
}

function checkDocument(document) {
    if (document.languageId !== LANGUAGE) return;

    const settings = vscode.workspace.getConfiguration('cufet');
    const command  = resolveExecutable();
    const args     = ['check', '--json'];
    if (settings.get('checkNativeCompatibility', true)) args.push('--native');

    const { target, cleanup } = materialize(document, 'check');
    args.push(target);

    cp.execFile(command, args, { timeout: CHECK_TIMEOUT_MS, maxBuffer: 4 * 1024 * 1024 },
        (error, stdout, stderr) => {
            cleanup();

            if (error && error.code === 'ENOENT') { reportMissingExecutable(command); return; }

            // `check` exits 1 whenever it finds a problem, so a non-zero exit is the ordinary
            // path, not a failure. Only an exit that produced no diagnostics at all is worth
            // reporting — and to the output channel, never as a popup, because a checker that
            // interrupts you on every keystroke is worse than no checker.
            if (error && !stdout.trim() && error.code !== 1) {
                output.appendLine(`cufet check failed (${error.message.trim()})`);
                if (stderr.trim()) output.appendLine(stderr.trim());
                return;
            }

            diagnostics.set(document.uri, parseDiagnostics(stdout, document));
        });
}

function parseDiagnostics(stdout, document) {
    const results = [];

    for (const rawLine of stdout.split(/\r?\n/)) {
        const line = rawLine.trim();
        if (!line.startsWith('{')) continue;

        let reported;
        try { reported = JSON.parse(line); } catch { continue; }

        const diagnostic = new vscode.Diagnostic(
            rangeForLine(document, reported.line),
            reported.message,
            reported.severity === 'warning'
                ? vscode.DiagnosticSeverity.Warning
                : vscode.DiagnosticSeverity.Error);
        diagnostic.source = 'cufet';
        results.push(diagnostic);
    }

    return results;
}

// Underline the code on the line rather than its leading indentation, and never produce a
// zero-width range — a zero-width squiggle is an invisible squiggle.
function rangeForLine(document, oneBasedLine) {
    const lastLine = Math.max(document.lineCount - 1, 0);
    const index    = Math.min(Math.max((oneBasedLine || 1) - 1, 0), lastLine);
    const line     = document.lineAt(index);

    if (!line.isEmptyOrWhitespace)
        return new vscode.Range(
            index, line.firstNonWhitespaceCharacterIndex,
            index, line.range.end.character);

    // A blank line has nothing to underline, so take in the line break to leave a visible mark.
    return index < lastLine
        ? new vscode.Range(index, 0, index + 1, 0)
        : new vscode.Range(index, 0, index, line.text.length);
}

function fingerprint(text) {
    let h = 0;
    for (let i = 0; i < text.length; i++) h = (Math.imul(31, h) + text.charCodeAt(i)) | 0;
    return Math.abs(h).toString(36);
}

// ── Semantic tokens ───────────────────────────────────────────────────────
//
// What a TextMate grammar cannot know. A regex over one line has no way to tell whether a bare
// word is a variable, a function, a type, a parameter or a field — and in a language whose surface
// is English, most of a line IS bare words. `cufet tokens --json` answers that from the real front
// end, so the colours agree with the type checker by construction rather than by resemblance.
//
// The names are the LSP standard ones, in the order src/Interpreter/SemanticTokens.cs declares.
// That is what lets any colour theme handle them with no per-theme configuration here.
const SEMANTIC_LEGEND = new vscode.SemanticTokensLegend(
    ['namespace', 'type', 'parameter', 'variable', 'property', 'function'],
    ['declaration']);

const semanticTokensProvider = {
    provideDocumentSemanticTokens(document) {
        const command = resolveExecutable();
        const { target, cleanup } = materialize(document, 'tokens');

        return new Promise(resolve => {
            cp.execFile(command, ['tokens', '--json', target],
                { timeout: CHECK_TIMEOUT_MS, maxBuffer: 4 * 1024 * 1024 },
                (error, stdout) => {
                    cleanup();

                    // A file that does not type-check has no reliable kinds to report — `tokens`
                    // exits non-zero and says nothing. The grammar keeps colouring it, and the
                    // squiggles from `check` are what explain the problem.
                    if (error && !stdout.trim()) { resolve(null); return; }

                    const builder = new vscode.SemanticTokensBuilder(SEMANTIC_LEGEND);
                    for (const rawLine of stdout.split(/\r?\n/)) {
                        const text = rawLine.trim();
                        if (!text.startsWith('{')) continue;

                        let token;
                        try { token = JSON.parse(text); } catch { continue; }
                        if (!SEMANTIC_LEGEND.tokenTypes.includes(token.kind)) continue;

                        // The producer counts lines and columns from 1; VS Code counts from 0.
                        builder.push(token.line - 1, token.column - 1, token.length,
                                     SEMANTIC_LEGEND.tokenTypes.indexOf(token.kind),
                                     encodeModifiers(token.modifiers));
                    }
                    resolve(builder.build());
                });
        });
    },
};

function encodeModifiers(names) {
    let bits = 0;
    for (const name of names || []) {
        const index = SEMANTIC_LEGEND.tokenModifiers.indexOf(name);
        if (index >= 0) bits |= 1 << index;
    }
    return bits;
}

// ── Commands ──────────────────────────────────────────────────────────────

const quoteIfNeeded = value => /\s/.test(value) ? `"${value}"` : value;

function runInTerminal(name, leadingArgs) {
    const editor = vscode.window.activeTextEditor;
    if (!editor || editor.document.languageId !== LANGUAGE) {
        vscode.window.showInformationMessage('Cufet: open a .cufe file first.');
        return;
    }

    editor.document.save().then(() => {
        const parts    = [resolveExecutable(), ...leadingArgs, editor.document.uri.fsPath];
        const terminal = vscode.window.createTerminal(name);
        terminal.show(true);
        terminal.sendText(parts.map(quoteIfNeeded).join(' '));
    });
}

// ── Lifecycle ─────────────────────────────────────────────────────────────

function activate(context) {
    diagnostics = vscode.languages.createDiagnosticCollection('cufet');
    output      = vscode.window.createOutputChannel('Cufet');
    context.subscriptions.push(diagnostics, output);

    const checkOn = () => vscode.workspace.getConfiguration('cufet').get('checkOn', 'save');

    context.subscriptions.push(
        vscode.workspace.onDidOpenTextDocument(document => {
            if (checkOn() !== 'never') checkDocument(document);
        }),
        vscode.workspace.onDidSaveTextDocument(document => {
            if (checkOn() !== 'never') checkDocument(document);
        }),
        vscode.workspace.onDidChangeTextDocument(event => {
            // ★ A change that leaves the buffer CLEAN did not come from typing — typing makes a
            // document dirty. It is VS Code reloading the file because something outside the
            // editor wrote it: a git checkout, a formatter, another tool. The diagnostics on
            // screen now describe text that is gone, so they are re-run whatever `checkOn` says.
            // Without this the stale squiggle survives until the next save, and it is worse than
            // no squiggle: it points at a line that has already been fixed.
            if (!event.document.isDirty) {
                clearTimeout(debounce);
                checkDocument(event.document);
                return;
            }
            if (checkOn() !== 'type') return;
            clearTimeout(debounce);
            debounce = setTimeout(() => checkDocument(event.document), TYPE_DEBOUNCE_MS);
        }),
        vscode.workspace.onDidCloseTextDocument(document => diagnostics.delete(document.uri)),

        vscode.commands.registerCommand('cufet.check', () => {
            const editor = vscode.window.activeTextEditor;
            if (editor) checkDocument(editor.document);
        }),
        vscode.commands.registerCommand('cufet.run',   () => runInTerminal('Cufet run', [])),
        vscode.commands.registerCommand('cufet.build', () => runInTerminal('Cufet build', ['build'])),

        vscode.languages.registerDocumentSemanticTokensProvider(
            { language: LANGUAGE }, semanticTokensProvider, SEMANTIC_LEGEND)
    );

    // Documents already open when the extension activates never fire onDidOpenTextDocument,
    // and the .cufe file you launched the window on is always one of them.
    if (checkOn() !== 'never')
        for (const document of vscode.workspace.textDocuments)
            if (document.languageId === LANGUAGE) checkDocument(document);
}

function deactivate() {
    clearTimeout(debounce);
}

module.exports = { activate, deactivate };
