"""
An executable model of Cufet's region discipline.

  * one core language (series, nested series, rabbits, first-order functions),
  * one machine that mirrors the compiled runtime (a stack of arenas, a call pushes none,
    `Done.` frees the top arena, a `Return` out of a rabbit merges it down, a series is a
    header plus a buffer that is reallocated where the allocator currently points),
  * checker A: the rules as the C# checker implements them today (abstracted),
  * checker B: the repaired rules that the soundness proof is about.

A checker returns (accepted, annotations). The annotations are what the compiler would be told:
for a store, the arena an allocation override points at, and whether the value is deep-copied.

Programs are plain tuples so they can be generated, printed and compared.

  type  : 'N' | 'S' | 'SS'                 number, series of number, series of series of number
  expr  : ('num',) | ('var', x) | ('new', T) | ('item', x) | ('call', f, [x, ...])
  stmt  : ('def', sid, x, T, e)            Define x as e.
          ('set', sid, x, e)               x becomes e.
          ('ins', sid, x, e)               Insert e into x.
          ('insitem', sid, x, e)           Insert e into item 1 of x.      (a computed target)
          ('seti', sid, x, e)              Item 1 of x becomes e.
          ('rabbit', sid, [stmt])          Pull a rabbit. ... Done.
          ('if', sid, [stmt], [stmt])      If <unknown>: ... Otherwise: ... Done.
          ('ret', sid, e)                  Return e.
          ('use', sid, x)                  State x.
          ('calls', sid, f, [x, ...])      Cast f on (...).
  func  : (name, [(param, T)], retT | None, [stmt])
  prog  : ([func], [stmt])
"""

SERIES = ('S', 'SS')
ELEM = {'S': 'N', 'SS': 'S'}


# ───────────────────────────── the machine ─────────────────────────────

# How a series finds the arena to grow in.
#   None        today's runtime: wherever the allocator points (the top arena, or an override)
#   'forward'   the header remembers its ARENA, and an arena merged down by a Return forwards to
#               the one it was merged into
#   'depth'     the header remembers only its DEPTH, a number, which a merge leaves stale
HOME = None


class Unsafe(Exception):
    """A freed object was reached."""


class Halt(Exception):
    """The program stopped itself (an index out of range). Safe."""


class Ret(Exception):
    def __init__(self, value):
        self.value = value


class Arena:
    __slots__ = ('freed', 'parent')

    def __init__(self):
        self.freed = False
        self.parent = None          # set when merged down into another arena


def find(a):
    while a.parent is not None:
        a = a.parent
    return a


class Obj:
    __slots__ = ('hdr', 'buf', 'elems', 'T', 'depth')

    def __init__(self, T, arena, depth=0):
        self.T = T
        self.depth = depth          # the stack index it was made at; only 'depth' mode reads it
        self.hdr = arena
        self.buf = arena
        self.elems = []


class Frame:
    __slots__ = ('base', 'scopes')

    def __init__(self, base, params):
        self.base = base            # index of the arena that was on top when the call began
        self.scopes = [params]


class Machine:
    def __init__(self, prog, ann, choices=(), realloc=True, strong=True, fuel=400):
        self.funcs = {f[0]: f for f in prog[0]}
        self.main = prog[1]
        self.ann = ann
        self.choices = list(choices)
        self.choice_at = 0
        self.asked = 0              # how many choices the run wanted
        self.realloc = realloc
        self.strong = strong
        self.fuel = fuel
        self.stack = [Arena()]
        self.override = None
        self.frames = []

    # -- allocation and liveness
    def arena(self):
        return self.stack[self.override] if self.override is not None else self.stack[-1]

    def alive(self, o):
        return not find(o.hdr).freed and not find(o.buf).freed

    def chk(self, v):
        if isinstance(v, Obj) and not self.alive(v):
            raise Unsafe()
        return v

    def deep(self, v, seen=None):
        if not isinstance(v, Obj):
            return
        seen = seen if seen is not None else set()
        if id(v) in seen:
            return
        seen.add(id(v))
        self.chk(v)
        for e in v.elems:
            self.deep(e, seen)

    def copy_into(self, v, arena):
        if not isinstance(v, Obj):
            return v
        self.chk(v)
        c = Obj(v.T, arena, self.stack.index(arena))
        c.elems = [self.copy_into(e, arena) for e in v.elems]
        return c

    def check_everything(self):
        for fr in self.frames:
            for sc in fr.scopes:
                for v in sc.values():
                    self.deep(v)

    # -- variables
    def lookup(self, x):
        for sc in reversed(self.frames[-1].scopes):
            if x in sc:
                return sc[x]
        raise KeyError(x)

    def assign(self, x, v):
        for sc in reversed(self.frames[-1].scopes):
            if x in sc:
                sc[x] = v
                return
        raise KeyError(x)

    # -- expressions
    def eval(self, e):
        k = e[0]
        if k == 'num':
            return 0
        if k == 'var':
            return self.lookup(e[1])
        if k == 'new':
            a = self.arena()
            return Obj(e[1], a, self.stack.index(a))
        if k == 'item':
            o = self.chk(self.lookup(e[1]))
            if not o.elems:
                raise Halt()
            return o.elems[0]
        if k == 'call':
            return self.call(e[1], [self.lookup(a) for a in e[2]])
        raise ValueError(k)

    def call(self, name, args):
        f = self.funcs[name]
        fr = Frame(len(self.stack) - 1, {p: a for (p, _), a in zip(f[1], args)})
        self.frames.append(fr)
        try:
            self.block(f[3], scoped=False)
            return None
        except Ret as r:
            return r.value
        finally:
            self.frames.pop()

    # -- statements
    def block(self, body, scoped=True):
        fr = self.frames[-1]
        if scoped:
            fr.scopes.append({})
        try:
            for s in body:
                self.step(s)
        finally:
            if scoped:
                fr.scopes.pop()

    def store_value(self, sid, v):
        a = self.ann.get(sid)
        if a and a.get('copy') is not None:
            v = self.copy_into(v, self.stack[self.frames[-1].base + a['copy']])
        return v

    def grow(self, sid, o):
        if not self.realloc:
            return
        if HOME == 'forward':
            o.buf = o.hdr                    # find() follows a merge to where the header now is
            return
        if HOME == 'depth':
            o.buf = self.stack[min(o.depth, len(self.stack) - 1)]
            return
        a = self.ann.get(sid)
        saved = self.override
        if a and a.get('ov') is not None:
            self.override = self.frames[-1].base + a['ov']
        o.buf = self.arena()
        self.override = saved

    def step(self, s):
        self.fuel -= 1
        if self.fuel < 0:
            raise Halt()
        k, sid = s[0], s[1]
        fr = self.frames[-1]
        if k == 'def':
            fr.scopes[-1][s[2]] = self.eval(s[4])
        elif k == 'set':
            self.assign(s[2], self.store_value(sid, self.eval(s[3])))
        elif k == 'ins':
            v = self.eval(s[3])
            o = self.chk(self.lookup(s[2]))
            v = self.store_value(sid, v)
            self.grow(sid, o)
            o.elems.append(v)
        elif k == 'insitem':
            v = self.eval(s[3])
            outer = self.chk(self.lookup(s[2]))
            if not outer.elems:
                raise Halt()
            o = self.chk(outer.elems[0])
            v = self.store_value(sid, v)
            self.grow(sid, o)
            o.elems.append(v)
        elif k == 'seti':
            v = self.eval(s[3])
            o = self.chk(self.lookup(s[2]))
            if not o.elems:
                raise Halt()
            o.elems[0] = self.store_value(sid, v)
        elif k == 'rabbit':
            self.stack.append(Arena())
            depth_scopes = len(fr.scopes)
            try:
                self.block(s[2])
            except Ret:
                # a Return out of a rabbit: the arena is merged down, not freed
                top = self.stack.pop()
                top.parent = self.stack[-1]
                del fr.scopes[depth_scopes:]
                raise
            else:
                self.stack.pop().freed = True
        elif k == 'if':
            self.asked += 1
            bit = self.choices[self.choice_at] if self.choice_at < len(self.choices) else 0
            self.choice_at += 1
            self.block(s[2] if bit else s[3])
        elif k == 'ret':
            raise Ret(self.eval(s[2]))
        elif k == 'use':
            self.deep(self.lookup(s[2]))
        elif k == 'calls':
            self.call(s[2], [self.lookup(a) for a in s[3]])
        else:
            raise ValueError(k)
        if self.strong:
            self.check_everything()

    def run(self):
        self.frames.append(Frame(0, {}))
        try:
            self.block(self.main, scoped=False)
            if self.strong:
                self.check_everything()
            return 'ok'
        except Halt:
            return 'ok'
        except Unsafe:
            return 'unsafe'


def count_ifs(prog):
    def n(body):
        t = 0
        for s in body:
            if s[0] == 'if':
                t += 1 + n(s[2]) + n(s[3])
            elif s[0] == 'rabbit':
                t += n(s[2])
        return t
    return n(prog[1]) + sum(n(f[3]) for f in prog[0])


def is_safe(prog, ann, strong=True):
    """Runs every path, with and without reallocation on growth. True when nothing freed is reached."""
    import itertools
    # a function called twice asks its `if` twice, so let the run say how many choices it wanted
    for realloc in (True, False):
        pending = [()]
        seen = set()
        while pending:
            ch = pending.pop()
            if ch in seen:
                continue
            seen.add(ch)
            m = Machine(prog, ann, ch, realloc=realloc, strong=strong)
            if m.run() == 'unsafe':
                return False
            if m.asked > len(ch) and len(ch) < 10:
                for tail in itertools.product((0, 1), repeat=min(m.asked, 10) - len(ch)):
                    pending.append(ch + tail)
    return True


# ───────────────────────────── typing (shared) ─────────────────────────────

class Reject(Exception):
    pass


def expr_type(e, types, funcs):
    k = e[0]
    if k == 'num':
        return 'N'
    if k == 'var':
        return types[e[1]]
    if k == 'new':
        return e[1]
    if k == 'item':
        return ELEM[types[e[1]]]
    if k == 'call':
        return funcs[e[1]][2]
    raise ValueError(k)


# ───────────────────────────── checker A: as implemented ─────────────────────────────
# One number per variable: the rabbit depth it was DEFINED at; a parameter is 0.
#   ValueDepthOf      : variable -> its depth; a literal -> the current depth; a call -> the max
#                       of the arguments its return-depth signature names; anything else -> 0.
#   ContainerDepthOf  : variable -> its depth; anything else -> the current depth.
#   CheckRegionStore  : refuse when value depth > target depth.
#   EscapeDepthFor    : the same arithmetic, except "anything else" is the current depth; when the
#                       value is deeper than the target, copy it into the target's arena and point
#                       the allocator there for the store.
#   Return-depth signature: a syntactic walk over Define / becomes / Return (not over stores).

def check_A(prog):
    funcs = {}
    ann = {}
    sigs = {}
    try:
        for f in prog[0]:
            _A_body(f[3], {p: 0 for p, _ in f[1]}, {p: t for p, t in f[1]}, 0, funcs, sigs, ann)
            if f[2] in SERIES:
                sigs[f[0]] = _A_signature(f, funcs, sigs)
            funcs[f[0]] = f
        _A_body(prog[1], {}, {}, 0, funcs, sigs, ann)
    except Reject:
        return False, {}
    return True, ann


def _A_signature(f, funcs, sigs):
    params = {p: i for i, (p, t) in enumerate(f[1]) if t in SERIES}
    all_ref = set(params.values())
    local = {}
    result = set()

    def sym(e):
        k = e[0]
        if k == 'var':
            if e[1] in params:
                return {params[e[1]]}
            return set(local.get(e[1], ()))
        if k == 'call':
            r = set()                    # callees are checked first, so the signature is known
            for i in sigs.get(e[1], ()):
                r |= sym(('var', e[2][i]))
            return r
        if k == 'item':
            return sym(('var', e[1]))
        return set()

    def walk(body):
        for s in body:
            if s[0] == 'def':
                local[s[2]] = sym(s[4])
            elif s[0] == 'set':
                local.setdefault(s[2], set()).update(sym(s[3]))
                if s[2] in params:           # a reassigned parameter is still read as the parameter
                    pass
            elif s[0] == 'ret':
                result.update(sym(s[2]))
            elif s[0] == 'if':
                walk(s[2]); walk(s[3])
            elif s[0] == 'rabbit':
                walk(s[2])
    walk(f[3])
    return sorted(result)


def _A_body(body, depth, types, d, funcs, sigs, ann):
    depth = dict(depth)
    types = dict(types)

    def value_depth(e):
        if expr_type(e, types, funcs) not in SERIES:
            return 0
        k = e[0]
        if k == 'var':
            return depth[e[1]]
        if k == 'new':
            return d
        if k == 'call':
            return max([depth[e[2][i]] for i in sigs.get(e[1], [])
                        if types[e[2][i]] in SERIES] + [0])
        return 0

    def escape(e, target):
        if expr_type(e, types, funcs) not in SERIES:
            vd = d
        elif e[0] == 'var':
            vd = depth[e[1]]
        elif e[0] == 'call':
            vd = value_depth(e)
        else:
            vd = d
        return target if vd > target else None

    def store(sid, e, target):
        if value_depth(e) > target:
            raise Reject()
        t = escape(e, target)
        if t is not None:
            is_series = expr_type(e, types, funcs) in SERIES
            ann[sid] = {'ov': t, 'copy': t if is_series else None}

    for s in body:
        k, sid = s[0], s[1]
        if k == 'def':
            depth[s[2]] = d
            types[s[2]] = s[3]
        elif k == 'set':
            store(sid, s[3], depth[s[2]])
        elif k in ('ins', 'seti'):
            store(sid, s[3], depth[s[2]])
        elif k == 'insitem':
            store(sid, s[3], d)
        elif k == 'rabbit':
            _A_body(s[2], depth, types, d + 1, funcs, sigs, ann)
        elif k == 'if':
            _A_body(s[2], depth, types, d, funcs, sigs, ann)
            _A_body(s[3], depth, types, d, funcs, sigs, ann)


# ───────────────────────────── checker B: the repaired rules ─────────────────────────────
# A series-typed name carries a REGION, a claim about the level of the object it names, relative
# to the frame's base b:
#     ('E', k)   exactly  b + k       made in this frame, at rabbit depth k
#     ('M', k)   at most  b + k
#     ('A', i)   it IS the object passed as parameter i
#     ('B', P)   at most the level of some parameter in P
# and a SLOT depth, the rabbit depth its Define sits at.

MUTANT = None      # set to one of MUTANTS to remove a single rule of checker B

MUTANTS = {
    'store-no-bound':       'a store into a series made here does not compare levels',
    'param-store-unsummarised': 'a store into a parameter is not recorded for the caller to check',
    'param-store-any-depth': 'a parameter that is stored into may be passed from any depth',
    'store-into-anything':  'a store is allowed into a series whose level is not known exactly',
    'becomes-no-bound':     'a reassignment does not compare the value against the slot',
    'item-keeps-exact':     'an element is taken to live exactly where its series does',
    'fresh-result-at-zero': 'a call that returns a fresh series is taken to return it at depth 0',
    'below-as-same':        'a value reached THROUGH a parameter is taken to be that parameter',
    'no-override':          'growth is not redirected to the series being grown',
    'join-takes-left':      'after an If, the first arm is believed',
}


def ub(r):
    return r[1] if r[0] in ('E', 'M') else 0


def join(r1, r2):
    if r1 is None or r2 is None:
        return r1 if r2 is None else r2
    if r1 == r2:
        return r1
    if r1[0] in 'AB' and r2[0] in 'AB':
        p = lambda r: frozenset([r[1]]) if r[0] == 'A' else r[1]
        return ('B', p(r1) | p(r2))
    return ('M', max(ub(r1), ub(r2)))


def weaken(r):
    if r[0] == 'E':
        return ('M', r[1])
    if r[0] == 'A':
        return ('B', frozenset([r[1]]))
    return r


def join_ret(a, b):
    if a is None:
        return b
    if b is None or a == b:
        return a
    if a != 'F' and b != 'F' and a != 'M0' and b != 'M0':
        return join(a, b)
    return 'M0'


class BEnv:
    def __init__(self, slot, region, types, d):
        self.slot, self.region, self.types, self.d = slot, region, types, d

    def copy(self):
        return BEnv(dict(self.slot), dict(self.region), dict(self.types), self.d)


def check_B(prog):
    summaries = {}       # f -> (ret summary, set of mutated parameter indices)
    funcs = {}
    ann = {}
    try:
        for f in prog[0]:
            env = BEnv({p: 0 for p, _ in f[1]},
                       {p: ('A', i) for i, (p, t) in enumerate(f[1]) if t in SERIES},
                       {p: t for p, t in f[1]}, 0)
            st = {'ret': None, 'mut': set()}
            _B_body(f[3], env, funcs, summaries, ann, st)
            summaries[f[0]] = (st['ret'], frozenset(st['mut']))
            funcs[f[0]] = f
        _B_body(prog[1], BEnv({}, {}, {}, 0), funcs, summaries, ann, {'ret': None, 'mut': set()})
    except Reject:
        return False, {}
    return True, ann


def _B_body(body, env, funcs, summaries, ann, st):
    """Checks a block in place: `env` is updated to the state after it. Names it defines are dropped."""
    before = set(env.slot)

    def call(f, args):
        ret, mut = summaries[f]
        for i in mut:
            r = env.region[args[i]]
            if r == ('E', env.d) or MUTANT == 'param-store-any-depth':
                continue
            if r[0] == 'A' and env.d == 0:
                st['mut'].add(r[1])
                continue
            raise Reject()
        if funcs[f][2] not in SERIES:
            return None
        if ret == 'F':
            return ('E', 0 if MUTANT == 'fresh-result-at-zero' else env.d)
        if ret == 'M0' or ret is None:
            return ('M', env.d)
        if ret[0] == 'A':
            return env.region[args[ret[1]]]
        out = None
        for i in ret[1]:
            out = join(out, weaken(env.region[args[i]]))
        return out

    def region(e):
        k = e[0]
        if k == 'num':
            return None
        if k == 'var':
            env.types[e[1]]                      # an unknown name is not a program
            return env.region.get(e[1])
        if k == 'new':
            return ('E', env.d)
        if k == 'item':
            if env.types[e[1]] != 'SS':
                return None
            r = env.region[e[1]]
            if MUTANT == 'item-keeps-exact' and r[0] == 'E':
                return r
            if MUTANT == 'below-as-same' and r[0] == 'A':
                return r
            return weaken(r)
        if k == 'call':
            return call(e[1], e[2])
        raise ValueError(k)

    def store_into(sid, target, e):
        r = region(e)
        if HOME and r is None:
            return                           # no pointer is stored, and growth finds its own way
        if target[0] == 'E':
            if r is not None and ub(r) > target[1] and MUTANT != 'store-no-bound':
                raise Reject()
            ann[sid] = {'ov': target[1], 'copy': None}
        elif target[0] == 'A':
            if r is not None and ub(r) > 0:
                raise Reject()
            if MUTANT != 'param-store-unsummarised':
                st['mut'].add(target[1])
            ann[sid] = {'ov': 0, 'copy': None}
        elif MUTANT == 'store-into-anything':
            if r is not None and ub(r) > ub(target):
                raise Reject()
            ann[sid] = {'ov': ub(target), 'copy': None}
        else:
            raise Reject()
        if MUTANT == 'no-override':
            ann[sid] = {'ov': None, 'copy': None}

    for s in body:
        k, sid = s[0], s[1]
        if k == 'def':
            r = region(s[4])
            env.slot[s[2]] = env.d
            env.types[s[2]] = s[3]
            if r is not None:
                env.region[s[2]] = r
        elif k == 'set':
            r = region(s[3])
            if r is not None:
                if ub(r) > env.slot[s[2]] and MUTANT != 'becomes-no-bound':
                    raise Reject()
                env.region[s[2]] = r
        elif k in ('ins', 'seti'):
            store_into(sid, env.region[s[2]], s[3])
        elif k == 'insitem':
            # the target is an element, whose region is weakened, so its level is never known
            # exactly and the store is always refused
            store_into(sid, region(('item', s[2])), s[3])
        elif k == 'rabbit':
            env.d += 1
            _B_body(s[2], env, funcs, summaries, ann, st)
            env.d -= 1
        elif k == 'if':
            left, right = env.copy(), env.copy()
            _B_body(s[2], left, funcs, summaries, ann, st)
            _B_body(s[3], right, funcs, summaries, ann, st)
            for x in list(env.region):
                if MUTANT == 'join-takes-left':
                    env.region[x] = left.region[x]
                else:
                    env.region[x] = join(left.region[x], right.region[x])
        elif k == 'ret':
            r = region(s[2])
            if r is not None:
                norm = 'F' if r[0] == 'E' else 'M0' if r[0] == 'M' else r
                st['ret'] = join_ret(st['ret'], norm)
        elif k == 'calls':
            call(s[2], s[3])

    for x in set(env.slot) - before:
        del env.slot[x]
        env.types.pop(x, None)
        env.region.pop(x, None)


# ───────────────────────────── printing ─────────────────────────────

TYPE_WORDS = {'N': 'number', 'S': 'series of number', 'SS': 'series of series of number'}


def show_expr(e):
    k = e[0]
    if k == 'num':
        return '5'
    if k == 'var':
        return e[1]
    if k == 'new':
        return 'a ' + TYPE_WORDS[e[1]]
    if k == 'item':
        return 'item 1 of ' + e[1]
    return 'cast %s on (%s)' % (e[1], ', '.join(e[2]))


def show_body(body, ind):
    out = []
    pad = '    ' * ind
    for s in body:
        k = s[0]
        if k == 'def':
            out.append('%sDefine %s as %s.' % (pad, s[2], show_expr(s[4])))
        elif k == 'set':
            out.append('%s%s becomes %s.' % (pad, s[2], show_expr(s[3])))
        elif k == 'ins':
            out.append('%sInsert %s into %s.' % (pad, show_expr(s[3]), s[2]))
        elif k == 'insitem':
            out.append('%sInsert %s into item 1 of %s.' % (pad, show_expr(s[3]), s[2]))
        elif k == 'seti':
            out.append('%sItem 1 of %s becomes %s.' % (pad, s[2], show_expr(s[3])))
        elif k == 'rabbit':
            out.append(pad + 'Pull a rabbit.')
            out += show_body(s[2], ind + 1)
            out.append(pad + 'Done.')
        elif k == 'if':
            out.append(pad + 'If chance:')
            out += show_body(s[2], ind + 1)
            out.append(pad + 'Otherwise:')
            out += show_body(s[3], ind + 1)
            out.append(pad + 'Done.')
        elif k == 'ret':
            out.append('%sReturn %s.' % (pad, show_expr(s[2])))
        elif k == 'use':
            out.append('%sState %s.' % (pad, s[2]))
        elif k == 'calls':
            out.append('%sCast %s on (%s).' % (pad, s[2], ', '.join(s[3])))
    return out


def show(prog):
    out = []
    for name, params, ret, body in prog[0]:
        head = 'Bind %s to %s' % (TYPE_WORDS[ret] if ret else 'void', name)
        if params:
            head += ', given (%s)' % ', '.join('the %s %s' % (TYPE_WORDS[t], p) for p, t in params)
        out.append(head + ':')
        out += show_body(body, 1)
        out.append('Done.')
    out += show_body(prog[1], 0)
    return '\n'.join(out)


def size(prog):
    def n(body):
        return sum(1 + (n(s[2]) if s[0] in ('rabbit', 'if') else 0) + (n(s[3]) if s[0] == 'if' else 0)
                   for s in body)
    return n(prog[1]) + sum(n(f[3]) + 1 for f in prog[0])
