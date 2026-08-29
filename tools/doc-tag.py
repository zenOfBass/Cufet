#!/usr/bin/env python3
"""Propose — and apply — a fence tag for every code block in the docs.

Every fenced block in the docs says what it IS, so the test suite can hold each kind to the right
promise instead of guessing:

    ```cufet             a program: it must check clean
    ```cufet-fragment    an illustration: it must PARSE, or fail only by running out of input
    ```cufet-refused     a counter-example: it must STAY refused
    ```output            what the block above it prints

Blocks that are not Cufet at all — shell, C, JSON, directory trees, diagrams — are left alone.

★ The tag is metadata: a renderer uses the first word to pick a highlighter and never prints it.
`cufet` is not a language any highlighter knows, so a tagged block looks exactly as it does today.

⚠ This PROPOSES. Run it, read the diff, and fix what it got wrong by hand — the whole point of the
tags is that a human decided what each block is for, and a heuristic that silently mislabels a
counter-example as a fragment would quietly retire a real check.

    python tools/doc-tag.py                 # report what it would do
    python tools/doc-tag.py --apply         # write the tags into the docs
"""

import argparse
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from importlib import import_module

sweep = import_module("doc-sweep") if os.path.exists(
    os.path.join(os.path.dirname(os.path.abspath(__file__)), "doc-sweep.py")) else None

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
FILES = ["README.md", "docs/BOOKS.md", "docs/GRAMMAR.md", "docs/REFERENCE.md"]
ANNOTATION = re.compile(r"\s*[←→].*$")

# A parse error caused by the sample simply STOPPING — the block showed a statement head, or a
# block opener, and did not go on. That is what an illustration looks like, and it is the one
# parse failure a fragment is allowed. Any other parse error means the sample no longer matches
# the language, which is exactly what these tags exist to catch.
RAN_OUT = re.compile(r"got Eof|the file ended before")

# What a counter-example says about itself, in the annotation the docs already write beside the
# offending line. This is intent stated by a person, so it outranks anything inferred from the
# error — see the note in classify().
SAYS_REFUSED = re.compile(
    r"[←→][^\n]*?(ERROR|error:|type error|refused|TypeException|RuntimeException|ParseException)"
    r"|//[^\n]*?(type error|refused)", re.I)

# Failures that mean "this is a real Cufet shape that just cannot stand alone" — it referenced
# names no fence defines, or a book it never pulled.
NEEDS_CONTEXT = re.compile(
    r"isn't defined|is not a defined object type|not in scope|top-level data|"
    r"'return' used outside a function")


def fences(path):
    """Every fence: (index-of-opening-line, existing-tag, body-text, body-lines)."""
    lines = open(os.path.join(ROOT, path), encoding="utf-8").read().replace("\r\n", "\n").split("\n")
    out, i = [], 0
    while i < len(lines):
        stripped = lines[i].lstrip()
        if stripped.startswith("```"):
            indent = len(lines[i]) - len(stripped)
            tag = stripped[3:].strip()
            open_at, i, body = i, i + 1, []
            while i < len(lines) and not lines[i].lstrip().startswith("```"):
                body.append(lines[i][indent:] if lines[i][:indent].strip() == "" else lines[i])
                i += 1
            out.append((open_at, tag, "\n".join(body)))
        i += 1
    return lines, out


def classify(cufet, scratch, raw, previous_tag):
    """The tag this block should carry, or None to leave it alone."""
    reason = sweep.skip_reason(raw)

    # Not Cufet at all. `diagnostic` and `not-code` right after a Cufet block are what that block
    # printed; anywhere else they are prose the docs quote, and prose is left untagged.
    if reason in ("diagnostic", "not-code"):
        return "output" if previous_tag == "cufet" else None
    if reason:
        return None

    src = "\n".join(ANNOTATION.sub("", l) for l in raw.split("\n"))
    open(scratch, "w", encoding="utf-8", newline="\n").write(src + "\n")
    p = subprocess.run(["dotnet", cufet, "check", scratch], capture_output=True, text=True)
    if p.returncode == 0:
        return "cufet"

    err = (p.stdout + p.stderr).strip().split("\n")[0]

    # ★★ The docs already SAY which blocks are counter-examples, in the arrow annotation beside
    # the offending line — `← TypeException: x already exists`, `← CHECK ERROR`, `// type error`.
    # That is a statement of intent by whoever wrote the sample, and it beats every inference
    # about the failure: a block that announces it is refused IS the counter-example, whatever
    # shape its error takes. Checked before the fragment rules below, because an annotated
    # counter-example may also happen to look incomplete.
    if SAYS_REFUSED.search(raw):
        return "cufet-refused"

    if RAN_OUT.search(err) or NEEDS_CONTEXT.search(err):
        return "cufet-fragment"

    # ── Shapes that are illustrations, not counter-examples ───────────────────────────────────
    #
    # ⚠ These three all reached `refused` on the first pass, and every one of them would have been
    # WRONG in the expensive direction: a block tagged `refused` is asserted to keep failing, so
    # mislabelling an illustration retires nothing today and then hides a genuine break later.
    body = [l for l in raw.strip().split("\n") if l.strip()]

    # A catalogue of statement HEADS — `If x is 5:`, `While count < bound, repeat:`,
    # `In case of failure:`. The docs list these to show surface forms; a body would be noise.
    #
    # ⚠ TWO block-opening heads in a row is NOT this shape, even though it looks like it: the
    # second lands inside the first, and a construct with a placement rule (an operator overload
    # is top-level only) then refuses it for a reason that has nothing to do with being cut short.
    # Those belong untagged, like the phrase tables laid out in columns.
    if body and all(l.rstrip().endswith((":", ",")) or l.lstrip().startswith("←") for l in body):
        return "cufet-fragment"

    # A catalogue of PHRASES — `nums sorted`, `the length of s`. No statement ends here at all,
    # so nothing was ever meant to run.
    if body and not any(l.rstrip().endswith(".") for l in body):
        return "cufet-fragment"

    # ★★ Everything left over becomes a FRAGMENT, never a refusal, and the asymmetry is the whole
    # reason. `cufet-refused` asserts a block keeps FAILING — get that wrong and the mistake is
    # silent forever, because a sample that quietly started working looks exactly like one that
    # never did. `cufet-fragment` asserts it PARSES — get that wrong and the suite says so on the
    # next run, with the error attached.
    #
    # So the only tag this tool guesses toward is the one whose mistakes announce themselves, and
    # `refused` is claimed only where a person already wrote it down (SAYS_REFUSED, above). The
    # residue arrives as a list of test failures, which is a better review queue than a list of
    # proposals: each one comes with the reason it did not parse.
    return "cufet-fragment"


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--apply", action="store_true", help="write the tags into the docs")
    args = ap.parse_args()

    cufet = sweep.find_cufet()
    scratch = os.path.join(ROOT, "tools", ".doc-tag.cufe")
    counts, changed, review = {}, 0, []

    try:
        for path in FILES:
            lines, blocks = fences(path)
            previous_tag = None
            edits = []
            for open_at, existing, raw in blocks:
                if existing:                      # already tagged — never second-guess a person
                    previous_tag = existing
                    continue
                tag = classify(cufet, scratch, raw, previous_tag)
                previous_tag = tag
                if tag is None:
                    counts["(left alone)"] = counts.get("(left alone)", 0) + 1
                    continue
                counts[tag] = counts.get(tag, 0) + 1
                edits.append((open_at, tag))
                # ⚠ A counter-example mislabelled as a fragment quietly retires a real check, and
                # a live sample mislabelled `refused` hides a broken doc. Both are invisible to
                # everything downstream, so this is the category a person has to read.
                if tag == "cufet-refused":
                    review.append(f"  {path}:{open_at + 2}  |  " + raw.strip().split(chr(10))[0][:62])
            if args.apply and edits:
                for open_at, tag in edits:
                    lines[open_at] = lines[open_at].replace("```", "```" + tag, 1)
                open(os.path.join(ROOT, path), "w", encoding="utf-8",
                     newline="\n").write("\n".join(lines))
                changed += len(edits)
            elif edits:
                changed += len(edits)
    finally:
        try:
            os.remove(scratch)
        except OSError:
            pass

    print(f"{'tagged' if args.apply else 'would tag'} {changed} fences\n")
    for tag, n in sorted(counts.items(), key=lambda kv: -kv[1]):
        print(f"  {tag:16} {n}")
    if review:
        print()
        print("proposed cufet-refused — read these, they are what judgement decides:")
        for line in review:
            print(line)
    if not args.apply:
        print("\nrun again with --apply to write them")
    return 0


if __name__ == "__main__":
    sys.exit(main())
