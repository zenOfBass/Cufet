// Converts a VS Code colour theme into a Monaco theme, at build time.
//
// Doing it here rather than in the browser keeps the conversion out of the payload and makes it
// testable in node. The output is a plain JSON object that app.js hands straight to
// monaco.editor.defineTheme.
//
// The two formats are close but not the same, and the differences all cost something:
//
//   * VS Code matches a rule against the FULL SCOPE STACK, so a selector may be a descendant
//     path — "source.ocaml comment" means a comment inside OCaml. Monaco matches a single token
//     string, so descendant selectors cannot be expressed and are dropped. This costs nothing
//     here: every descendant selector in this theme names another language.
//   * VS Code accepts 8-digit hex (RGBA). Monaco's rule colours must be 6 digits, so alpha is
//     dropped rather than the rule being discarded — a slightly-too-opaque colour beats none.
//   * VS Code themes carry hundreds of workbench colours (side bar, tabs, notifications) that
//     have no meaning in an embedded editor. Only the handful Monaco recognises are passed on.

import { readFile } from 'node:fs/promises';

// Monaco knows many more colour IDs than these, but these are the ones that actually change how
// an editor with no workbench around it looks. Passing keys Monaco does not know is not worth
// the risk of it rejecting the whole theme.
const EDITOR_COLORS = [
    'editor.background',
    'editor.foreground',
    'editor.lineHighlightBackground',
    'editor.lineHighlightBorder',
    'editor.selectionBackground',
    'editor.selectionHighlightBackground',
    'editor.inactiveSelectionBackground',
    'editor.findMatchBackground',
    'editor.findMatchHighlightBackground',
    'editorCursor.foreground',
    'editorLineNumber.foreground',
    'editorLineNumber.activeForeground',
    'editorIndentGuide.background',
    'editorIndentGuide.activeBackground',
    'editorWhitespace.foreground',
    'editorBracketMatch.background',
    'editorBracketMatch.border',
    'editorError.foreground',
    'editorWarning.foreground',
    'scrollbarSlider.background',
    'scrollbarSlider.hoverBackground',
    'scrollbarSlider.activeBackground',
];

// Monaco wants "d1d2d4", not "#d1d2d4" and not "#d1d2d490".
function ruleColor(value) {
    if (typeof value !== 'string') return undefined;
    const hex = value.replace('#', '');
    if (hex.length === 8) return hex.slice(0, 6);   // drop alpha
    if (hex.length === 6) return hex;
    if (hex.length === 3) return hex[0] + hex[0] + hex[1] + hex[1] + hex[2] + hex[2];
    return undefined;
}

// A theme entry's scope is either an array of selectors or one comma-separated string.
function selectorsOf(scope) {
    const raw = Array.isArray(scope) ? scope : typeof scope === 'string' ? scope.split(',') : [];
    return raw
        .map(s => s.trim())
        .filter(Boolean)
        .filter(s => !s.includes(' '));   // descendant selectors — Monaco cannot express them
}

// The page around the editor is styled from the SAME theme, so the chrome cannot drift away from
// what the editor is showing. A VS Code theme already describes every surface an editor sits in —
// title bar, side bar, tab strip, borders — so the page borrows those rather than inventing a
// palette next to them. Each entry falls back down a chain, so a theme that omits a workbench
// colour still produces something coherent instead of nothing.
function chromeVars(colors) {
    const pick = (...keys) => keys.map(k => colors[k]).find(Boolean);

    return {
        // The editor's own background, used for the output pane so the two panes agree.
        '--bg': pick('editor.background'),
        // Chrome: header, footer, pane headings. In most dark themes this is a shade darker
        // than the editor, which is what gives the panes their edge without a heavy border.
        '--bg-raised': pick('titleBar.activeBackground', 'sideBar.background', 'editor.background'),
        '--bg-sunken': pick('editorGroupHeader.tabsBackground', 'sideBar.background', 'editor.background'),
        '--line': pick('panel.border', 'titleBar.border', 'sideBar.border', 'contrastBorder'),
        '--text': pick('editor.foreground', 'foreground'),
        '--muted': pick('statusBar.foreground', 'descriptionForeground', 'tab.inactiveForeground'),
        '--faint': pick('tab.inactiveForeground', 'editorLineNumber.foreground'),
        // The cursor colour is a good accent: themes choose something that must stand out
        // against the editor background, which is exactly what a primary button needs.
        '--accent': pick('editorCursor.foreground', 'tab.activeForeground', 'textLink.foreground'),
        // Text ON the accent. The editor background is the darkest surface the theme defines,
        // so it reads against an accent chosen to be visible against that same background.
        '--accent-text': pick('editor.background'),
        '--link': pick('textLink.foreground', 'tab.activeForeground'),
        '--error': pick('editorError.foreground'),
    };
}

export async function convertTheme(themePath) {
    const vs = JSON.parse(await readFile(themePath, 'utf8'));

    const rules = [];

    // The theme's top-level foreground becomes the default rule. Monaco uses the empty token
    // for "anything not otherwise matched".
    const defaultFg = ruleColor(vs.colors?.['editor.foreground']);
    if (defaultFg) rules.push({ token: '', foreground: defaultFg });

    for (const entry of vs.tokenColors ?? []) {
        const fg = ruleColor(entry.settings?.foreground);
        const bg = ruleColor(entry.settings?.background);
        const fontStyle = entry.settings?.fontStyle || undefined;
        if (!fg && !bg && !fontStyle) continue;

        for (const token of selectorsOf(entry.scope)) {
            const rule = { token };
            if (fg) rule.foreground = fg;
            if (bg) rule.background = bg;
            if (fontStyle) rule.fontStyle = fontStyle;
            rules.push(rule);
        }
    }

    const colors = {};
    for (const key of EDITOR_COLORS)
        if (vs.colors?.[key]) colors[key] = vs.colors[key];

    return {
        monaco: {
            // "type" is optional in a VS Code theme; fall back to reading the background, since a
            // light base under a dark background is far more wrong than the reverse.
            base: (vs.type ?? '').toLowerCase() === 'light' ? 'vs' : 'vs-dark',
            inherit: true,   // anything this theme does not colour keeps Monaco's own defaults
            rules,
            colors,
        },
        chrome: chromeVars(vs.colors ?? {}),
    };
}

// The chrome variables as a stylesheet, so the page picks them up before first paint rather than
// flashing an unstyled palette while the theme JSON is still being fetched.
export function chromeStylesheet(chrome) {
    const body = Object.entries(chrome)
        .filter(([, v]) => v)
        .map(([k, v]) => `    ${k}: ${v};`)
        .join('\n');
    return `/* Generated from the vendored VS Code theme. Do not edit — see build-theme.mjs. */\n:root {\n${body}\n}\n`;
}
