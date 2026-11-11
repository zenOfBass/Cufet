"""Split 'function values' into its parts and count each, examples vs the self-hosted compiler.
lambda:    a function written in place, `a function ...:` (with or without `given (...)`)
closure:   a Bind (or a lambda) whose body sits INSIDE another function's body — tracked by a
           stack of block openers (a line ending in ':' opens, a line 'Done.' closes)
fn-type:   a function TYPE written out (`<type> function [name] given (`), i.e. a function handed
           around as a parameter, field, series element or return
cast-held: `cast <name>` where <name> is declared by NO Bind anywhere in the folder — so it is a
           held value (parameter, field, local), not a declared function"""
import os, re, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from concepts import files, strip, blank_strings

LAMBDA = re.compile(r"\ba function\b(?: given \([^()]*\))?:")
FNTYPE = re.compile(r"\b(?:number|text|fact|void|bits|[\w-]+) function(?: [\w-]+)? given \(")
BINDNAME = re.compile(r"^[ \t]*Bind [^\n]*\bto ([\w-]+)(?: unto [\w-]+)?(?:,|:)", re.M)
CAST = re.compile(r"\bcast ([\w-]+)(?: on\b|\.)")

def closures(code):
    stack, found = [], 0
    for line in code.split("\n"):
        s = line.strip()
        if not s: continue
        if s.startswith("Done"):
            if stack: stack.pop()
            continue
        opens = s.endswith(":")
        is_fn = s.startswith("Bind ") or bool(LAMBDA.search(s))
        if is_fn and "fn" in stack: found += 1
        if opens: stack.append("fn" if is_fn else "blk")
    return found

for sub in ("examples", "self-hosting"):
    fs = files(sub)
    codes = {p: blank_strings(strip(open(p, encoding="utf-8").read())) for p in fs}
    declared = set()
    for c in codes.values(): declared |= set(BINDNAME.findall(c))
    hits = {k: [] for k in ("lambda", "closure", "fn-type", "cast-held", "any")}
    for p, code in codes.items():
        found = set()
        if LAMBDA.search(code): found.add("lambda")
        if closures(code): found.add("closure")
        if FNTYPE.search(code): found.add("fn-type")
        if any(n not in declared for n in CAST.findall(code)): found.add("cast-held")
        if found: found.add("any")
        for k in found: hits[k].append(os.path.splitext(os.path.basename(p))[0])
    print(f"{sub} ({len(fs)} files)")
    for k, v in hits.items():
        print(f"  {k:10} {len(v):3}  {', '.join(v)}")
