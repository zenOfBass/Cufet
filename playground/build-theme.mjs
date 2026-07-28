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
        // "type" is optional in a VS Code theme; fall back to reading the background, since a
        // light base under a dark background is far more wrong than the reverse.
        base: (vs.type ?? '').toLowerCase() === 'light' ? 'vs' : 'vs-dark',
        inherit: true,   // anything this theme does not colour keeps Monaco's own defaults
        rules,
        colors,
    };
}
