#!/usr/bin/env python3
"""Extract every fenced code block from the docs and run `cufet check` on it.

    python tools/doc-sweep.py                 # summary + grouped failures
    python tools/doc-sweep.py --list-ok       # also list the blocks that pass
    python tools/doc-sweep.py --strict        # exit 1 if anything fails (for CI)

WHY THIS EXISTS
---------------
A documented sample that no longer runs is worse than no sample: a reader copies it, it fails, and
they conclude the language is broken rather than the doc. Four defects were found in two days by
executing samples and none by reading them — a false claim about a recursive-shape example, a
sample naming its variable `stream` (a reserved word), a refusal message pointing at the wrong
alternative, and a form that was documented but never implemented.

Reading cannot find these. Only running can.

WHAT COUNTS AS A FAILURE
------------------------
Not every block is a program, and not every failure is a bug. Three kinds are expected:

  * FRAGMENTS — a block opening without its `Done.`, a bare `Return`, or two alternative examples
    sharing one fence. These teach a shape rather than being runnable.
  * COUNTER-EXAMPLES — GRAMMAR is a constraints reference. Samples marked TYPE ERROR or REFUSED are
    supposed to be rejected; a high failure rate there is correct.
  * NON-CUFET — shell, C, JSON, directory trees, diagrams, quoted diagnostics.

So this is a triage aid, not a pass/fail gate — except under --strict, which is only honest once the
expected failures have been driven out. Read the grouped output; the shape of an error usually says
which kind it is.
"""

import argparse
import json
import os
import re
import subprocess
import sys
from collections import Counter, defaultdict

sys.stdout.reconfigure(encoding="utf-8")

ROOT  = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FILES = ["README.md", "GRAMMAR.md", "REFERENCE.md"]

# Docs annotate samples with a trailing arrow to show a value or a note. That is prose, not code —
# and it points BOTH ways, which is easy to miss: handling only `←` leaves every `→` sample failing
# to lex, which was 29 false failures on this tool's first run.
ANNOTATION = re.compile(r"\s*[←→].*$")


def find_cufet():
    """The freshest built CLI. Debug and Release may both exist; take whichever is newer."""
    found = []
    for config in ("Debug", "Release"):
        base = os.path.join(ROOT, "src", "App", "bin", config)
        if not os.path.isdir(base):
            continue
        for framework in os.listdir(base):
            dll = os.path.join(base, framework, "Cufet.App.dll")
            if os.path.exists(dll):
                found.append((os.path.getmtime(dll), dll))
    if not found:
        sys.exit("no build found — run: dotnet build src/App/Cufet.App.csproj")
    return max(found)[1]


def blocks(path):
    """Every fenced block in a file, as (line-number-of-first-content-line, text)."""
    lines = open(os.path.join(ROOT, path), encoding="utf-8").read().split("\n")
    out, i = [], 0
    while i < len(lines):
        if lines[i].lstrip().startswith("```"):
            indent = len(lines[i]) - len(lines[i].lstrip())
            start, i, body = i + 1, i + 1, []
            while i < len(lines) and not lines[i].lstrip().startswith("```"):
                body.append(lines[i][indent:] if lines[i][:indent].strip() == "" else lines[i])
                i += 1
            out.append((start + 1, "\n".join(body)))
        i += 1
    return out


def skip_reason(text):
    """None if this looks like a Cufet program, else why it is not one."""
    t = text.strip()
    if not t:
        return "empty"
    # `#` and `$` are checked without \b — it would demand a word character straight after them,
    # which "# Run all tests" does not have.
    if t.startswith("#") or t.startswith("$") or t.startswith("PS "):
        return "shell"
    if re.match(r"^\s*(cufet|dotnet|gcc|npm|git|Copy-Item|New-Item|cd |ls |mkdir)\b", t):
        return "shell"
    if "$PWD" in t or "$env:" in t:
        return "shell"
    if "#include" in t or "typedef " in t or "int main(" in t:
        return "C"
    if t.startswith("{") and '"' in t:
        return "json"
    if "├" in t or "└" in t or re.search(r"(?m)^\S+/\s*$", t):
        return "tree"
    if "──" in t or "│" in t:
        return "diagram"
    # A diagnostic quoted so a reader recognises it when they hit it.
    if re.search(r"That doesn't work:|Here on line \d+|^\s*Line \d+, column", t):
        return "diagnostic"
    # `...` stands for code the sample deliberately is not showing.
    if re.search(r"(^|\s)\.\.\.(\s|$)", t):
        return "elided"
    if re.search(r"<[a-z][a-z -]*>", t) or re.search(r"\bitem N\b|\bN of\b", t):
        return "metavariable"
    # No statement word anywhere: almost certainly expected output or prose.
    if not re.search(r"\b(State|Define|If|For|While|Bind|Cast|Return|Add|Remove|Pull|Have|Send|"
                     r"With|Try|In|Write|Append|Repeat|Stop|Skip|Item|Run|Get|Set|Done|Output|Seed)\b",
                     t, re.I):
        return "not-code"
    return None


def error_shape(err):
    """Normalise names and numbers out of an error so like failures group together."""
    e = re.sub(r"'[^']*'", "'X'", err)
    e = re.sub(r'"[^"]*"', '"X"', e)
    return re.sub(r"\b\d+\b", "N", e)[:110]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--list-ok", action="store_true", help="also list blocks that pass")
    ap.add_argument("--strict", action="store_true", help="exit 1 if any runnable block fails")
    ap.add_argument("--json", metavar="PATH", help="write full results as JSON")
    args = ap.parse_args()

    cufet = find_cufet()
    scratch = os.path.join(ROOT, "tools", ".doc-sweep.cufe")
    results = []

    try:
        for path in FILES:
            for line_no, raw in blocks(path):
                reason = skip_reason(raw)
                head = raw.strip().split("\n")[0][:60]
                if reason:
                    results.append((path, line_no, "SKIP", reason, head))
                    continue

                src = "\n".join(ANNOTATION.sub("", l) for l in raw.split("\n"))
                open(scratch, "w", encoding="utf-8", newline="\n").write(src + "\n")
                p = subprocess.run(["dotnet", cufet, "check", scratch], capture_output=True, text=True)

                err = (p.stdout + p.stderr).strip().split("\n")[0]
                err = re.sub(r"^.*?\.doc-sweep\.cufe:\d+:\d+:\s*(error|warning):\s*", "", err)
                err = re.sub(r"^Line \d+, column \d+:\s*", "", err)
                results.append((path, line_no, "OK" if p.returncode == 0 else "FAIL",
                                "" if p.returncode == 0 else err, head))
    finally:
        try:
            os.remove(scratch)
        except OSError:
            pass

    ok   = [r for r in results if r[2] == "OK"]
    fail = [r for r in results if r[2] == "FAIL"]
    skip = [r for r in results if r[2] == "SKIP"]

    print(f"blocks {len(results)}   ran {len(ok) + len(fail)}   "
          f"OK {len(ok)}   FAIL {len(fail)}   skipped {len(skip)}\n")

    per_file = Counter((r[0], r[2]) for r in results)
    for f in FILES:
        print(f"  {f:15} OK {per_file[(f, 'OK')]:4}   FAIL {per_file[(f, 'FAIL')]:4}   "
              f"skip {per_file[(f, 'SKIP')]:4}")
    print()

    groups = defaultdict(list)
    for path, ln, _, err, head in fail:
        groups[error_shape(err)].append((path, ln, head))

    for shape, items in sorted(groups.items(), key=lambda kv: -len(kv[1])):
        print(f"[{len(items):3}]  {shape}")
        for path, ln, head in items[:3]:
            print(f"         {path}:{ln}  |  {head}")
        if len(items) > 3:
            print(f"         … and {len(items) - 3} more")
        print()

    if args.list_ok:
        print("passing:")
        for path, ln, _, _, head in ok:
            print(f"  {path}:{ln}  |  {head}")

    if args.json:
        with open(args.json, "w", encoding="utf-8") as fh:
            json.dump([{"file": a, "line": b, "status": c, "err": d, "head": e}
                       for a, b, c, d, e in results], fh, indent=1)

    return 1 if (args.strict and fail) else 0


if __name__ == "__main__":
    sys.exit(main())
