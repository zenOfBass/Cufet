"""The tutorial's curriculum net: every topic in Cufet, the edges between them, and the page that
draws it (published at https://claude.ai/artifact/KBhjYe87RQ2GZogduzeh8c).

  fullnet.py        TOPICS (topic: hard prerequisites, note) and CAND (candidate edges + evidence:
                    E = examples, R = reference, L = judgement, door = off the trunk); playthroughs
  best-net.json     the edges KEPT (the user's choices, made by hand 2026-11-09..11)
  concepts.py       regex detectors; usage counts over examples/ and self-hosting/
  layout.py         a crossing-free layout: planar construction, then spread with no move allowed
                    to create a crossing (needs: pip install networkx). `python layout.py <seed>`
  runmany.sh        32 seeds in parallel into lay/ (~10 min); copy the best lay/<n>.json to layout.json
  preview.py        draws layout.json to preview.png (needs: pip install pillow) to LOOK at it
  gen.py            THIS: writes cufet-curriculum-net.html from net-template.html (+ layout.json
                    when it covers exactly the current topics)

Rule: a topic opens when every hard prerequisite is done AND (it is a door OR a related neighbour
is done). After changing topics or edges: re-run the layout, then this."""
import json, sys, os, collections
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from concepts import load
from fullnet import TOPICS, CAND, TRUNK

HERE = os.path.dirname(os.path.abspath(__file__))
best = {tuple(e) for e in json.load(open(os.path.join(HERE, "best-net.json")))}
MAIN = {"methods", "text", "maps", "records", "things", "kinds", "failure", "files", "input", "rabbits",
        "tasks", "channels", "books", "modules"}
LABEL = {
    "trunk": "Trunk", "text": "Text", "maps": "Maps", "records": "Records", "sorting": "Sorting",
    "things": "Objects", "getset": "Getters & setters", "makers": "Makers & unmakers",
    "interfaces": "Interfaces", "embedding": "Embedding", "overloading": "Operator overloading",
    "kinds": "Unions", "catalogues": "Catalogues & atlases", "shorthands": "Shorthands",
    "failure": "Failure", "files": "Files", "input": "Reading input", "args": "Arguments & environment",
    "processes": "Running programs", "directories": "Directories", "rabbits": "Rabbits",
    "tasks": "Tasks", "channels": "Channels", "modes": "Composition modes", "pipes": "Pipes",
    "interrupts": "Interrupts", "stashes": "Stashes", "methods": "Methods",
    "books": "Books", "modules": "Modules", "projects": "Projects", "own-book": "A book of your own",
    "patterns": "Regular expressions", "matrices": "Matrices", "sets": "Sets", "chase": "Chases",
    "bits": "Bits", "chance": "Chance", "char-classes": "Character classes",
    "foreign": "Foreign code", "cufet-source": "Cufet source",
}
# concept detector -> topic, for usage counts
CONCEPT = {
    "text-ops": "text", "map": "maps", "record": "records", "sorted": "sorting",
    "object": "things", "methods": "methods", "getset": "getset", "makers": "makers",
    "interface": "interfaces", "overload": "overloading", "union": "kinds", "is-a": "kinds",
    "judge": "kinds", "catalogue": "catalogues", "shorthand": "shorthands", "failure": "failure",
    "files": "files", "input": "input", "args-env": "args", "process": "processes",
    "rabbit": "rabbits", "tasks": "tasks", "channels": "channels", "modes": "modes",
    "interrupt": "interrupts", "stash": "stashes",  "books": "books",
    "modules": "modules", "patterns": "patterns", "matrix": "matrices", "set": "sets",
    "chase": "chase", "bits": "bits", "chance": "chance", "char-class": "char-classes",
}
TRUNK_CONCEPTS = {"state", "define", "becomes", "if", "when", "while", "for-each", "range",
                  "constant", "interp", "series", "series-mut", "functions", "hof", "void"}

def usage(sub):
    rows = load(sub)
    cnt, progs = collections.Counter(), collections.defaultdict(list)
    for p, cs in rows:
        ts = {CONCEPT[c] for c in cs if c in CONCEPT}
        for t in ts:
            cnt[t] += 1
            progs[t].append(os.path.splitext(os.path.basename(p))[0])
        if not (cs - TRUNK_CONCEPTS):
            progs["trunk"].append(os.path.splitext(os.path.basename(p))[0])
    return len(rows), cnt, progs

nex, cex, pex = usage("examples")
nsh, csh, _ = usage("self-hosting")
detected = set(CONCEPT.values())

nodes = [{"id": "trunk", "label": "Trunk", "kind": "trunk",
          "note": "saying something, names, choosing, repeating (with a series), functions, void",
          "hard": [], "ex": None, "sh": None, "programs": sorted(pex["trunk"])}]
for t, (hard, note) in TOPICS.items():
    nodes.append({"id": t, "label": LABEL[t], "kind": "main" if t in MAIN else "side", "note": note,
                  "hard": sorted(hard),
                  "ex": cex[t] if t in detected else None, "sh": csh[t] if t in detected else None,
                  "programs": sorted(pex[t])})
edges = []
for (a, b), ev in CAND.items():
    edges.append({"a": a, "b": b, "evidence": ev, "kept": (a, b) in best})
data = {"nodes": nodes, "edges": edges, "nex": nex, "nsh": nsh}
if os.path.exists(os.path.join(HERE, "layout.json")):
    lay = json.load(open(os.path.join(HERE, "layout.json")))
    if set(lay) == {n["id"] for n in nodes}:   # only a layout made for exactly these topics
        data["layout"] = lay
html = open(os.path.join(HERE, "net-template.html"), encoding="utf-8").read()
out = html.replace("/*__DATA__*/null", json.dumps(data))
open(os.path.join(HERE, "cufet-curriculum-net.html"), "w", encoding="utf-8").write(out)
print("nodes", len(nodes), "edges", len(edges), "kept", sum(e["kept"] for e in edges))
