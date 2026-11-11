import re, os

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))  # the repo

# concept -> regex (multiline). Lesson-grain concepts.
C = {
    "state":       r"^\s*State\b",
    "define":      r"^\s*Define (?!object|interface|shorthand)",
    "becomes":     r"\bbecomes\b|^\s*(Increment|Decrement) ",
    "if":          r"^\s*(If|Otherwise)\b",
    "when":        r" when .*,\s*(\n\s*)?otherwise ",
    "while":       r"^\s*While .*repeat:|^\s*Repeat:",
    "for-each":    r"^\s*For each ",
    "range":       r"\brange \S+ to ",
    "constant":    r"\bpermanently\b",
    "interp":      r"\"[^\"\n]*\{[^}\n]+\}[^\"\n]*\"",
    "text-ops":    r"\bsplit by\b|\bstarts with\b|\bends with\b|\breplace .* with .* in\b|\bthe position of\b|\btrimmed\b|\bin (upper|lower)case\b|\bconverted to number\b|\bcharacters? \S+ (to|through)\b",
    "series":      r"\bseries\b",
    "series-mut":  r"^\s*(Insert|Remove) ",
    "void":        r"\bvoid\b",
    "functions":   r"^\s*Bind (?!making|unmaking|overloading)",
    "hof":         r"\b(?:number|text|fact|void|bits|[\w-]+) function(?: [\w-]+)? given \(",
    "lambda":      r"\ba function\b(?: given \([^()]*\))?:",
    "map":         r"\ba map from\b|\bthe entry for\b|\bhas a key\b",
    "record":      r"\brecord (with|like)\b",
    "catalogue":   r"\b(catalogue|atlas)\b",
    "sorted":      r"\bsorted\b",
    "object":      r"^\s*Define object ",
    "methods":     r"\bone's\b|\bunto\b",
    "getset":      r"^\s*(Get|Set) \S+ ",
    "makers":      r"\bBind (making|unmaking)\b",
    "interface":   r"\bDefine interface\b",
    "overload":    r"\bBind overloading\b",
    "union":       r"\((?:the )?[a-z-]+( or [a-z-]+)+\)|\ba catalogue of \(",
    "is-a":        r"\bis (not )?an? (number|text|fact|[a-z-]+)\b",
    "judge":       r"^\s*Judge ",
    "shorthand":   r"\bDefine shorthand\b",
    "failure":     r"\bor failure\b|\ba failure \"|^\s*Try to:|^\s*In case of\b",
    "input":       r"\bfrom the input\b",
    "files":       r"\bthe file\b|\bthe directory\b",
    "process":     r"\brun \"[^\"]+\" with\b",
    "args-env":    r"\bthe arguments\b|\benvironment variable\b",
    "rabbit":      r"\bPull an? (automatic |sequential |parallel )?rabbit\b",
    "tasks":       r"\bstart a task\b",
    "channels":    r"\bchannel of\b|^\s*Send .* through\b",
    "modes":       r"\bPull an? (automatic|sequential) rabbit\b",
    "stash":       r"\bbury\b|\bunbury\b",
    "interrupt":   r"\ban interrupt is requested\b",
    "books":       r"\bPull a book on\b",
    "modules":     r"\band module\b",
    "patterns":    r"\bDefine regex\b|\bPull a book on regex\b",
    "bits":        r"\bbits\b|\b0x[0-9A-F]+\b|\b0b[01]+\b",
    "set":         r"\ba set of\b",
    "chase":       r"\bchase\b",
    "matrix":      r"\bmatrix\b|\bmatrices\b",
    "chance":      r"\bPull a book on chance\b|\ba random\b",
    "char-class":  r"\bis (not )?(alphabetic|numeric|whitespace)\b",
}
RX = {k: re.compile(v, re.M) for k, v in C.items()}

def drop_block_comments(s):
    out, depth, i = [], 0, 0
    while i < len(s):
        if s.startswith("/*", i): depth += 1; i += 2; continue
        if depth and s.startswith("*/", i): depth -= 1; i += 2; continue
        if depth:
            if s[i] == "\n": out.append("\n")
            i += 1; continue
        out.append(s[i]); i += 1
    return "".join(out)

def strip(src):
    src = drop_block_comments(src)
    out = []
    for l in src.splitlines():
        l = l.split("\u2190")[0]
        l = re.sub(r"//.*$", "", l)
        out.append(l)
    return "\n".join(out)

def blank_strings(s):
    # keep the quotes and any {...} holes (code), blank the literal words
    def rep(m):
        body = m.group(0)[1:-1]
        holes = " ".join(re.findall(r"\{[^}]*\}", body))
        return '"' + holes + '"'
    return re.sub(r"\"[^\"\n]*\"", rep, s)

def concepts_of(text):
    found = {k for k, r in RX.items() if k == "interp" and r.search(text)}
    code = blank_strings(text)
    found |= {k for k, r in RX.items() if k != "interp" and r.search(code)}
    if has_closure(code): found.add("closure")
    return found

def has_closure(code):
    """A Bind or lambda whose body sits inside another function's body (stack of ':' openers)."""
    stack = []
    for line in code.splitlines():
        s = line.strip()
        if not s: continue
        if s.startswith("Done"):
            if stack: stack.pop()
            continue
        is_fn = s.startswith("Bind ") or bool(RX["lambda"].search(s))
        if is_fn and "fn" in stack: return True
        if s.endswith(":"): stack.append("fn" if is_fn else "blk")
    return False

def files(sub):
    out = []
    for d, _, fs in os.walk(os.path.join(ROOT, sub)):
        for f in fs:
            if f.endswith(".cufe"):
                out.append(os.path.join(d, f))
    return sorted(out)

def load(sub):
    return [(os.path.relpath(p, ROOT), concepts_of(strip(open(p, encoding="utf-8").read()))) for p in files(sub)]
