"""The whole of Cufet as a net. Rule: a topic OPENS when every HARD prerequisite is done AND
(it is a door OR any RELATED neighbour is done). A hard prerequisite is not itself an opener
unless there is also a related edge to it — that is what lets the search keep hubs from
opening six topics at once.

Evaluation is by random playthroughs (uniform choice among open topics)."""
import random, collections, sys

TRUNK = "trunk"
# topic: (hard prerequisites, note)
TOPICS = {
    "text":          (set(), "split, contains, converted to number (void)"),
    "maps":          (set(), "lookups give void"),
    "records":       (set(), "values with named parts; map keys"),
    "sorting":       (set(), "sorted, in reverse"),
    "things":        (set(), "objects: fields, made with new"),
    "methods":       (set(), "methods: one's, unto"),
    "getset":        (set(), "getters and setters"),
    "makers":        (set(), "named makers, unmakers"),
    "interfaces":    (set(), "interfaces"),
    "embedding":     (set(), "embedding"),
    "overloading":   (set(), "operator overloading"),
    "kinds":         (set(), "unions, is a, Judge"),
    "catalogues":    ({"kinds"}, "catalogue and atlas"),
    "shorthands":    ({"kinds"}, "shorthands"),
    "failure":       (set(), "or failure, Try, In case of"),
    "files":         ({"failure"}, "reading and writing files"),
    "input":         (set(), "read a line from the input (void at end)"),
    "args":          (set(), "arguments, environment, exit status"),
    "processes":     ({"failure"}, "run a program"),
    "directories":   ({"failure"}, "making, listing, the current directory"),
    "rabbits":       (set(), "pull a rabbit; things end at Done"),
    "tasks":         ({"rabbits"}, "tasks, awaited results"),
    "channels":      ({"tasks"}, "channels, send, delivery"),
    "modes":         ({"channels"}, "automatic, sequential rabbits"),
    "pipes":         ({"tasks"}, "streaming stages"),
    "interrupts":    (set(), "an interrupt is requested"),
    "stashes":       ({"rabbits"}, "bury, unbury"),
    "books":         (set(), "pull a book on math, collections"),
    "modules":       ({"things"}, "an object that is a module"),
    "projects":      ({"modules"}, "directories as namespaces, blueprints"),
    "own-book":      ({"books", "modules"}, "a book of your own"),
    "patterns":      ({"text"}, "regex book"),
    "matrices":      (set(), "matrix book (reached through Sets, which needs Books)"),
    "sets":          ({"books"}, "sets"),
    "chase":         ({"books", "text"}, "editable text"),
    "bits":          (set(), "bit patterns"),
    "chance":        ({"books"}, "random draws"),
    "char-classes":  ({"text"}, "alphabetic, numeric, code points"),
    "foreign":       (set(), "axioms: C code from Cufet"),
    "cufet-source":  ({"books"}, "holes: Cufet code from Cufet"),
}
# candidate RELATED edges with evidence (E = examples co-occurrence, R = reference section uses it,
# L = the lesson would naturally lead there)
CAND = {
    ("trunk", "text"): "door", ("trunk", "things"): "door", ("trunk", "rabbits"): "door",
    ("trunk", "kinds"): "door", ("trunk", "books"): "door", ("trunk", "maps"): "door",
    ("text", "failure"): "E 62%: failure with text work",
    ("text", "maps"): "E markov, wordfreq",
    ("text", "books"): "E 69%: text work with books",
    ("text", "input"): "L: what someone types is text",
    ("text", "args"): "E: args with text in 67%",
    ("text", "patterns"): "R: a pattern is held by a text",
    ("text", "chase"): "L: editable text",
    ("text", "char-classes"): "R",
    ("text", "files"): "E: search, easter, readings",
    ("text", "sorting"): "L: sorting words",
    ("maps", "records"): "R: a record of keys",
    ("maps", "kinds"): "E: netlist, config, gates",
    ("maps", "sets"): "R: a set is a map with the value removed (BOOKS.md, Sets)",
    ("sets", "matrices"): "L: the user's line of book side quests",
    ("maps", "books"): "L: the collections book",
    ("records", "things"): "L: records copy, objects are shared",
    ("records", "sorting"): "R: sorting series of records",
    ("records", "overloading"): "L: values with arithmetic",
    ("things", "kinds"): "E 78%: Judge over objects",
    ("things", "failure"): "R: a maker that can fail",
    ("things", "rabbits"): "E: trees (arbtree, bst)",
    ("things", "getset"): "R", ("things", "makers"): "R", ("things", "interfaces"): "R",
    ("things", "embedding"): "R", ("things", "overloading"): "R", ("things", "modules"): "R",
    ("trunk", "records"): "door",
    ("things", "methods"): "R: methods are declared on an object (REFERENCE, Objects)",
    ("methods", "getset"): "L: the user's line: a getter or setter is a method",
    ("methods", "interfaces"): "R: an interface is a set of method signatures (REFERENCE, Interfaces)",
    ("makers", "overloading"): "L: the user's line",
    ("getset", "makers"): "L: controlling how one is made and read",
    ("makers", "interfaces"): "L: the user's line: reading a field, making one, then how objects relate",
    ("makers", "failure"): "R: making a point or failure",
    ("makers", "rabbits"): "R: unmakers run when the rabbit ends",
    ("interfaces", "kinds"): "E: interfaces.cufe (two ways to say 'one of several')",
    ("interfaces", "embedding"): "L",
    ("overloading", "matrices"): "E geometry",
    ("kinds", "catalogues"): "R", ("kinds", "shorthands"): "R",
    ("catalogues", "maps"): "R",
    ("failure", "files"): "R", ("failure", "processes"): "R", ("failure", "directories"): "R",
    ("failure", "tasks"): "R: when a task goes wrong",
    ("files", "directories"): "L", ("files", "args"): "E 100%", ("files", "input"): "L",
    ("args", "processes"): "L: exit status", ("processes", "pipes"): "L",
    ("input", "interrupts"): "R: the interrupt example reads input",
    ("rabbits", "tasks"): "R", ("rabbits", "stashes"): "R", ("rabbits", "foreign"): "E foreign-memory",
    ("tasks", "channels"): "R", ("tasks", "pipes"): "R", ("tasks", "interrupts"): "L",
    ("channels", "modes"): "R", ("channels", "kinds"): "E actor-mailbox",
    ("books", "matrices"): "R", ("books", "sets"): "R", ("books", "chance"): "R",
    ("books", "patterns"): "R", ("books", "modules"): "R: a book is a module",
    ("books", "foreign"): "R", ("books", "cufet-source"): "R",
    ("modules", "rabbits"): "R: a module may own a lifetime; a rabbit is the one that does (DESIGN, book vs module)",
    ("modules", "projects"): "R", ("modules", "own-book"): "R",
    ("bits", "text"): "R: crossing over", ("bits", "foreign"): "E foreign",
    ("bits", "books"): "E 5 programs", ("bits", "maps"): "E circuits",
    ("foreign", "cufet-source"): "R: Cufet source is 'the same surface as a foreign one' (BOOKS.md)",
    ("own-book", "foreign"): "L",
    ("foreign", "patterns"): "L: the third language held in [ … ] (the user's line: Cufet source, C, regex)", ("projects", "files"): "L",
}
NODES = [TRUNK] + list(TOPICS)
IDX = {n: i for i, n in enumerate(NODES)}

def build(edges):
    nb = {n: set() for n in NODES}
    for a, b in edges: nb[a].add(b); nb[b].add(a)
    return nb

def playthroughs(edges, runs, rng, optional=()):
    nb = build(edges)
    sizes, stuck_runs = [], 0
    for _ in range(runs):
        done, todo = {TRUNK}, set(TOPICS) - set(optional)
        while todo - done:
            o = [n for n in TOPICS if n not in done and TOPICS[n][0] <= done and nb[n] & done]
            sizes.append(len(o))
            if not o: stuck_runs += 1; break
            done.add(rng.choice(o))
    return sizes, stuck_runs

MUST = {("things", "kinds"), ("failure", "files"), ("rabbits", "tasks"),
        ("tasks", "channels"), ("channels", "modes"), ("kinds", "catalogues"), ("books", "patterns"),
        ("text", "patterns"), ("modules", "projects"), ("things", "modules"),
        ("maps", "records")}
STEPS = len(TOPICS)
def target(step):
    return 3 + 3 * step / STEPS          # ~3 at the start, ~6 at the end

def playthroughs_steps(edges, runs, rng):
    nb = build(edges)
    out, stuck = [], 0
    for _ in range(runs):
        done = {TRUNK}
        step = 0
        while len(done) < len(NODES):
            o = [n for n in TOPICS if n not in done and TOPICS[n][0] <= done and nb[n] & done]
            if not o: stuck += 1; break
            out.append((step, len(o)))
            done.add(rng.choice(o)); step += 1
    return out, stuck

def score(edges, rng, runs=60):
    if not MUST <= set(edges): return 1e9, None
    pts, stuck = playthroughs_steps(edges, runs, rng)
    if stuck: return 1e9, None
    dev = sum(abs(k - target(st)) for st, k in pts) / len(pts)
    narrow = sum(1 for st, k in pts if k <= 1 and st < STEPS - 3) / len(pts)
    early = [k for st, k in pts if st < 4]
    early_crowd = sum(1 for k in early if k >= 5) / max(1, len(early))
    s = dev + 3 * narrow + 3 * early_crowd - 0.01 * len(edges)
    mean = sum(k for _, k in pts) / len(pts)
    return s, (round(mean, 2), round(dev, 2), round(narrow, 3), round(early_crowd, 3), max(k for _, k in pts))

def search(iters, seed):
    rng = random.Random(seed)
    keys = list(CAND)
    doors = [e for e in keys if e[0] == TRUNK]
    cur = set(k for k in keys if k[0] != TRUNK) | {("trunk", "text"), ("trunk", "things"), ("trunk", "rabbits")}
    cur |= MUST
    best = cur; bs, binfo = score(cur, rng)
    cs = bs
    T = 0.3
    for i in range(iters):
        nxt = set(cur)
        k = rng.choice(keys)
        if k in nxt: nxt.discard(k)
        else: nxt.add(k)
        if sum(1 for e in nxt if e[0] == TRUNK) != 3: continue
        s, info = score(nxt, rng)
        if s < cs or rng.random() < pow(2.718, -(s - cs) / max(T, 1e-3)):
            cur, cs = nxt, s
            if s < bs: best, bs, binfo = set(nxt), s, info
        T *= 0.998
    return best, bs, binfo

if __name__ == "__main__":
    results = []
    for seed in range(int(sys.argv[1]) if len(sys.argv) > 1 else 4):
        best, bs, info = search(int(sys.argv[2]) if len(sys.argv) > 2 else 1500, seed)
        results.append((bs, info, best))
        print(f"seed {seed}: score {bs:.3f} mean/dev/narrow/earlycrowd/max {info}  edges {len(best)}", flush=True)
    results.sort(key=lambda t: t[0])
    bs, info, best = results[0]
    rng = random.Random(99)
    sizes, stuck = playthroughs(best, 3000, rng)
    c = collections.Counter(sizes)
    print("\nBEST: edges", len(best), " distribution of open choices over 3000 playthroughs:")
    print("  ", {k: f"{v/len(sizes):.0%}" for k, v in sorted(c.items())})
    print("  doors:", sorted(b for a, b in best if a == TRUNK))
    dropped = [k for k in CAND if k not in best and k[0] != TRUNK]
    print("  kept related edges:", len([k for k in best if k[0] != TRUNK]), "of", len([k for k in CAND if k[0] != TRUNK]))
    print("  dropped:", dropped)
    import json
    json.dump(sorted(map(list, best)), open("best-net.json", "w"))
