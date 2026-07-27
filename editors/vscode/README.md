# Cufet for Visual Studio Code

Syntax highlighting and error squiggles for [Cufet](../../README.md).

Two pieces, both small:

- **A TextMate grammar** (`syntaxes/cufet.tmLanguage.json`) — pure data, no code.
- **A checker** (`extension.js`) — runs `cufet check --json` on the file you are editing and turns
  what it says into squiggles. About 200 lines of dependency-free JavaScript.

## Installing

The extension needs no build step — there is nothing to compile and no `npm install` to run.
**Copy** the directory into your extensions folder:

```powershell
Copy-Item "$PWD\editors\vscode" "$env:USERPROFILE\.vscode\extensions\cufet" -Recurse
```

```sh
cp -r "$PWD/editors/vscode" ~/.vscode/extensions/cufet
```

> **Using Insiders?** It keeps a completely separate extensions folder — `~/.vscode-insiders/`
> `extensions` rather than `~/.vscode/extensions`. Installing into the wrong one fails exactly
> the way a symlink does: no error, no log entry, the extension simply is not there. Confirm
> with `Help ▸ About` which build you are running.

Then **quit VS Code completely and start it again** — not *Developer: Reload Window*. Reloading
restarts the workbench but can reuse a cached extension scan, so a newly-added extension may not
be picked up until the process restarts. Open a `.cufe` file to confirm: it should be coloured,
and the language indicator in the status bar should read `Cufet`.

> **Do not symlink it.** A junction (`New-Item -ItemType Junction`) or symlink looks like it
> should work and silently does not: Node reports a linked directory with `isDirectory() ===
> false` and `isSymbolicLink() === true`, and the extension scan skips it. Nothing is logged —
> the extension simply never appears. Verified on Windows; not worth the risk elsewhere.

Because it is a copy, editing the files in this repository does **not** change what VS Code is
running. Re-copy after a change, or use the development host below.

### Working on the extension itself

For live editing, skip the extensions folder entirely and use the officially supported path —
it loads the extension straight from the repository, with no copy and no install:

```sh
code --extensionDevelopmentPath="$PWD/editors/vscode" .
```

That opens a second window running this extension from source. Reload that window to pick up
changes to `extension.js` or the grammar.

## The checker needs something to run

Error squiggles come from the real front end, so there has to be a build of it:

```sh
dotnet build
```

The extension finds `src/App/bin/*/net*/Cufet.App` under any workspace folder on its own, and
prefers the most recently built one. If your Cufet lives somewhere else, set `cufet.executable`
to its full path. Failing both, it tries `cufet` on your `PATH`.

If it cannot run anything it says so once, offers to run `dotnet build` for you, and stays quiet
after that.

## Settings

| Setting | Default | What it does |
| --- | --- | --- |
| `cufet.executable` | *(empty)* | Full path to the Cufet executable. Empty means search the workspace, then `PATH`. |
| `cufet.checkOn` | `save` | `save` — check on open and save. `type` — check as you type, after a short pause. `never` — only on command. |
| `cufet.checkNativeCompatibility` | `true` | Also flag constructs the native compiler refuses. These run fine interpreted, so they appear as **warnings**. |

With `checkOn: "type"`, unsaved text is checked through a temporary copy. That is safe because
`check` never runs your program — nothing in it resolves a path relative to the source file.

## Commands

| Command | What it does |
| --- | --- |
| **Cufet: Check File** | Check the active file now, whatever `checkOn` says. |
| **Cufet: Run File** | Save and run it under the interpreter, in a terminal. |
| **Cufet: Build Native Binary** | Save and compile it to a native executable via `cufet build`. |

## Using it from a task instead

If you would rather not have the extension check for you, the repository's
[`.vscode/tasks.json`](../../.vscode/tasks.json) drives the same checker through a build task
(`Ctrl+Shift+B`). Set `cufet.checkOn` to `never` if you want the task to be the only source of
squiggles.

Both routes parse the plain-text form of `cufet check`:

```
/path/to/thing.cufe:12: error: That doesn't work: 'x' holds numbers.
```

This extension contributes that pattern as the **`$cufet`** problem matcher, so your own tasks
can just say `"problemMatcher": "$cufet"`. The repository's `tasks.json` deliberately does
*not* — it spells the matcher out in full, so it still works in a fresh clone where the
extension has not been installed. Referring to `$cufet` before the extension is loaded is an
error, not a silent no-op.

Only `check` produces locatable diagnostics. Interpreting and building report errors as plain
`Line N: ...` with no filename, and a problem matcher has nothing to attach that to — so the
run and build tasks declare no matcher rather than one that would never fire.

## About the highlighting

**A grammar assigns scopes; your theme assigns colours.** So this file names scopes from the
standard vocabulary every theme already styles, and never picks a colour — whatever theme you
use, Cufet arrives already fitting in.

Scope names are three parts, `<standard-prefix>.<cufet-role>.cufet`, so a theme that only knows
`keyword.control` colours them anyway, while a theme with a wide palette can pull
`keyword.control.flow.cufet` and `keyword.control.statement.cufet` apart if it wants to.

**Articles and prepositions get no scope at all.** `a`, `an`, `the`, `of`, `to`, `as`, `with`,
`on`, `by`, `from`, `in`, `at`, `for`, `after` and `through` render as plain body text. Cufet
reads like English *because* it is full of them; colouring them would turn every line into a
wall of keyword. Unscoped, they recede, and the words that carry meaning carry the line. This is
deliberate.

Two consequences worth knowing about:

- **Hyphens are identifier characters**, so `add-edge` is one name and highlights as one name.
  This is why the grammar fences keywords with `(?<![\w-])` instead of `\b` — with `\b`, the
  `add` in `add-edge` would light up as a keyword. `wordPattern` knows it too, so
  double-clicking `add-edge` selects all of it.
- **Binary `-` needs spaces**, and now you can see it: `count - 1` colours the `-` as an
  operator, while `count-1` stays one flat identifier. The sharp edge is visible instead of
  waiting to surprise you.

### Known limits

Several of Cufet's accessor words — `first`, `last`, `value`, `result`, `all` — are *contextual*
rather than reserved: the parser recognises them only in a particular shape, and you are free to
name a variable `first`. The grammar anchors on the article in front of them, which reproduces
that shape closely enough to keep `the key of pair` and `the value of pair` looking alike. A
variable actually named `first` will pick up the accessor colour when written as `the first`.

The checker reports one diagnostic at a time, because the front end stops at the first error.
Fix it and the next one appears.
