"""
Random programs over the WHOLE of what the region rules govern, held to the real tools, with
AddressSanitizer as the judge instead of the model.

    python fuzz_cufet.py [count] [seed] [workers]     (from PowerShell: it calls cufet and wsl)

against_cufet.py asks the model which programs are unsafe, so it can only search the model's core
language: series, nested series, rabbits, first-order functions. This one writes Cufet directly,
with text, maps, an object with fields, a method and a getter, functions as values (named and
lambdas), closures that capture, a series of functions, and functions nested in functions; and, in
three programs of four, chases, sets, a record, a union, a voidable, a matrix, `permanently`
constants (read by functions, defined in rabbits), functions bound in a rabbit, and methods written
`unto` their type in a block. Each program `check` accepts is compiled with `-fsanitize=address` under WSL and run with `chance`
true and false. Every block an arena holds is its own malloc, so ASan reports any read of freed
memory on the first run, whatever the allocator would have done with it.

Reported, into against-out/:
    hole-*       ASan found a read of freed memory (or a double free): `check` accepted a program
                 that is not safe
    differs-*    the compiled output differs from the interpreted one, with no ASan report
                 (another kind of bug, or a program that read freed memory ASan cannot see)

The tool is CUFET below (the C# build in the repository) and gcc in WSL.
"""
import concurrent.futures
import hashlib
import os
import random
import shutil
import subprocess
import sys
import tempfile
import threading
import time

CUFET = os.environ.get('CUFET', r'C:\dev\Cufet\src\App\bin\Debug\net10.0\Cufet.App.exe')
HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, 'against-out')

WORDS = {
    'T': 'text',
    'S': 'series of number',
    'SS': 'series of series of number',
    'ST': 'series of text',
    'M': 'map from text to series of number',
    'B': 'bag',
    'F': 'void function given (the series of number)',
    'FF': 'series of void function given (the series of number)',
    'C': 'chase',
    'Q': 'set of text',
    'SC': 'series of chase',
    'SQ': 'series of set of text',
    'MT': 'map from text to text',
    'R': 'record like (the series of number items, the text label)',
    'U': '(series of number or text)',
    'V': 'voidable series of number',
    'MX': 'matrix',
    'K': 'crate',
}
RICH = ['C', 'C', 'Q', 'Q', 'SC', 'SQ', 'MT', 'R', 'U', 'V', 'MX', 'K']
DECLARED = ('U', 'V')       # defined with their type written, since no value spells it
PARAM_TYPES = ['S', 'S', 'SS', 'ST', 'M', 'B', 'F', 'T']
PRINTABLE = ('T', 'S', 'SS', 'ST', 'C', 'Q', 'SC', 'SQ', 'MX')

PRELUDE = '''Define object bag with (the series of number items, the text label):
    Get payload as series of number:
        Return one's items.
    Done.
Done.
Bind void to keep unto bag, given (the series of number row):
    one's items becomes row.
Done.
'''


CRATE = '''Define object crate with (the chase letters, the set of text words).
Bind void to load unto crate, given (the text word):
    Insert word into one's words.
    Insert word into one's letters.
Done.
Bind void to swap unto crate, given (the chase other):
    one's letters becomes other.
Done.
'''


class Ctx:
    """What a body can see: its names and their types, and the functions it may call."""

    def __init__(self, scope, funcs, ret=None, nest=0, in_func=False, rabbit=0,
                 readonly=frozenset(), in_task=False, in_try=False, fixed=frozenset(), hoist=False, decl=True):
        self.in_try = in_try
        self.fixed = fixed          # names `becomes` may not touch: constants, `one`, `it`
        self.decl = decl            # a function may be bound here: not in an `If`, a loop or a `Try`
        self.hoist = hoist          # a line here is the program's, or a rabbit's of it: constants, functions
        self.scope = dict(scope)
        self.funcs = funcs          # [(name, [(param, T)], ret)]; ret 'SF' is `series of number or failure`
        self.ret = ret
        self.nest = nest
        self.in_func = in_func
        self.rabbit = rabbit        # how many rabbits are open around here: a task needs one
        self.readonly = readonly    # names a task captured, which it may read and not change
        self.in_task = in_task

    def copy(self):
        return Ctx(self.scope, self.funcs, self.ret, self.nest, self.in_func, self.rabbit,
                   self.readonly, self.in_task, self.in_try, self.fixed, self.hoist, self.decl)

    def inner(self, **change):
        c = self.copy()
        c.nest += 1
        c.hoist = c.decl = False
        for k, v in change.items():
            setattr(c, k, v)
        return c

    def of(self, T):
        return [x for x, t in self.scope.items() if t == T]

    def writable(self, T):
        return [x for x in self.of(T) if x not in self.readonly]


class Gen:
    def __init__(self, rng):
        self.rng = rng
        self.n = 0
        self.rich = rng.random() < 0.75
        self.pending = []       # constants functions may read, not given a value yet: [(name, T)]
        self.shared = {}        # constants of the program met so far: name -> T
        self.bound = []         # functions bound in a rabbit's block: seen from everywhere after
        self.methods = []       # methods written `unto` bag in a block

    def name(self, stem='v'):
        self.n += 1
        return '%s%d' % (stem, self.n)

    def pick(self, xs):
        return self.rng.choice(xs)

    # ── expressions ─────────────────────────────────────────────────────

    def expr(self, T, c, depth=0):
        r = self.rng
        opts = []
        same = c.of(T)
        opts += [lambda x=x: x for x in same] * 2
        deep = depth < 2
        if T == 'T':
            opts.append(lambda: '"ab" joined to "cd"')
            for x in same:
                opts.append(lambda x=x: '(%s joined to "z")' % x)
            for x in c.of('ST'):
                opts.append(lambda x=x: '(item 1 of %s)' % x)
            for x in c.of('B'):
                opts.append(lambda x=x: "(%s's label)" % x)
            for x in same:
                opts += [lambda x=x: '"{%s}!"' % x, lambda x=x: '(%s in uppercase)' % x,
                         lambda x=x: '(the characters from 1 to 2 of %s)' % x]
            for x in c.of('C'):
                opts += [lambda x=x: '(%s converted to text)' % x, lambda x=x: '(item 1 of %s)' % x]
            for x in c.of('R'):
                opts.append(lambda x=x: '(the label of %s)' % x)
            for x in c.of('MT'):
                opts.append(lambda x=x: '((the entry for "k" in %s) but void is "none")' % x)
        elif T == 'C':
            opts.append(lambda: 'a chase')
            for x in same:
                opts.append(lambda x=x: '(%s sorted)' % x)
            for x in c.of('SC'):
                opts.append(lambda x=x: '(item 1 of %s)' % x)
            for x in c.of('K'):
                opts.append(lambda x=x: "(%s's letters)" % x)
        elif T == 'Q':
            opts.append(lambda: 'a set of text')
            if deep:
                opts.append(lambda: 'a set of text with (%s)' % self.expr('T', c, depth + 1))
            for x in c.of('SQ'):
                opts.append(lambda x=x: '(item 1 of %s)' % x)
            for x in c.of('K'):
                opts.append(lambda x=x: "(%s's words)" % x)
        elif T == 'SC':
            opts.append(lambda: '(a series of chase)')
        elif T == 'SQ':
            opts.append(lambda: '(a series of set of text)')
        elif T == 'MT':
            opts.append(lambda: '(a map from text to text)')
        elif T == 'R':
            if deep:
                opts.append(lambda: '(a record with (the items %s, the label %s))'
                            % (self.expr('S', c, depth + 1), self.expr('T', c, depth + 1)))
        elif T in ('U', 'V'):
            if deep:
                opts.append(lambda: self.expr('S', c, depth + 1))
                if T == 'U':
                    opts.append(lambda: self.expr('T', c, depth + 1))
        elif T == 'MX':
            opts.append(lambda: '(a matrix with ((1, 2), (3, 4)))')
        elif T == 'K':
            if deep:
                opts.append(lambda: '(a new crate { the letters %s, the words %s })'
                            % (self.expr('C', c, depth + 1), self.expr('Q', c, depth + 1)))
        elif T == 'S':
            opts += [lambda: '(a series of number with (1, 2))', lambda: '(a series of number)']
            for x in c.of('SS'):
                opts += [lambda x=x: '(item 1 of %s)' % x] * 2
            for x in c.of('B'):
                opts += [lambda x=x: "(%s's items)" % x, lambda x=x: "(%s's payload)" % x]
            for x in same:
                opts += [lambda x=x: '(%s sorted)' % x, lambda x=x: '(%s in reverse)' % x]
            for x in c.of('R'):
                opts.append(lambda x=x: '(the items of %s)' % x)
            for x in c.of('V'):
                opts.append(lambda x=x: '(%s but void is (a series of number))' % x)
            if deep and same:
                opts.append(lambda: '(%s when chance, otherwise %s)'
                            % (self.expr('S', c, depth + 1), self.expr('S', c, depth + 1)))
            for x in c.of('G'):
                if deep:
                    opts.append(lambda x=x: '(cast %s on (%s))' % (x, self.expr('S', c, depth + 1)))
        elif T == 'SS':
            opts.append(lambda: '(a series of series of number)')
        elif T == 'ST':
            opts.append(lambda: '(a series of text)')
            for x in c.of('T'):
                opts.append(lambda x=x: '(%s split by "c")' % x)
        elif T == 'M':
            opts.append(lambda: '(a map from text to series of number)')
        elif T == 'B':
            if deep:
                opts.append(lambda: '(a new bag { the items %s, the label %s })'
                            % (self.expr('S', c, depth + 1), self.expr('T', c, depth + 1)))
        elif T == 'F':
            for f, params, ret in c.funcs:
                if ret is None and [t for _, t in params] == ['S']:
                    opts.append(lambda f=f: f)
            for x in c.of('FF'):
                opts.append(lambda x=x: '(item 1 of %s)' % x)
        elif T == 'FF':
            opts.append(lambda: '(a series of void function given (the series of number))')
        if deep and T == 'S' and not c.in_try:      # in a Try body, a failure goes to its handler
            for f, params, ret in c.funcs:
                if ret == 'SF':
                    args = self.args(params, c, depth + 1)
                    if args is not None:
                        opts += [lambda f=f, a=args: '(cast %s on (%s) but on failure (a series of number))'
                                 % (f, a)] * 2
        if deep:
            for f, params, ret in self.callable(c):
                if ret == T:
                    args = self.args(params, c, depth + 1)
                    if args is not None:
                        opts += [lambda f=f, a=args: '(cast %s on (%s))' % (f, a)] * 2
        if not opts:
            return None
        return r.choice(opts)()

    def callable(self, c):
        return c.funcs + ([] if c.in_task else [f for f in self.bound if f not in c.funcs])

    def args(self, params, c, depth):
        out = []
        for _, t in params:
            e = self.expr(t, c, depth)
            if e is None:
                return None
            out.append(e)
        return ', '.join(out)

    # ── statements ──────────────────────────────────────────────────────

    def body(self, n, c, pad):
        """About n statements at indent `pad`; returns (lines, ctx at the end)."""
        r = self.rng
        c = c.copy()
        out = []
        tries = 0
        while n > 0 and tries < 60:
            tries += 1
            kinds = ['def'] * 4 + ['set'] * 3 + ['ins'] * 5 + ['seti'] * 2 + ['mapset', 'mapget',
                     'field', 'field', 'callf', 'callf', 'callm', 'calls', 'use', 'lambda', 'lambda', 'foreach']
            if self.rich:
                kinds += ['def'] * 2 + ['rich'] * 4 + ['each2'] * 2 + ['judge', 'narrow']
                if c.hoist:
                    kinds += ['const'] * (4 if self.pending else 2)
                    if c.rabbit:
                        kinds += ['bindfn'] * 2
                if c.nest < 2 and c.decl and not c.in_task:
                    kinds += ['unto', 'callu']
            if c.nest < 3:
                kinds += ['rabbit'] * 4 + ['if'] * 2 + ['try'] * 2
            if c.rabbit and not c.in_task and c.nest < 3:
                kinds += ['task'] * 3
            if c.in_task:
                kinds = [k for k in kinds if k not in ('calls', 'callf', 'callm', 'nested')]
            if c.in_func and c.nest < 2 and c.decl:
                kinds.append('nested')
            if c.ret and c.nest > 0:
                kinds.append('ret')
            k = r.choice(kinds)
            lines = self.stmt(k, c, n, pad)
            if lines is None:
                continue
            got, used = lines
            out += got
            n -= used
            if k == 'ret':
                break
        return out, c

    def stmt(self, k, c, n, pad):
        r = self.rng
        p = '    ' * pad
        if k == 'def':
            T = r.choice(['S', 'S', 'SS', 'ST', 'T', 'M', 'B', 'FF', 'F'] + (RICH if self.rich else []))
            e = self.expr(T, c)
            if e is None:
                return None
            x = self.name()
            c.scope[x] = T
            if T in DECLARED:
                return ['%sDefine the %s %s as %s.' % (p, WORDS[T], x, e)], 1
            return ['%sDefine %s as %s.' % (p, x, e)], 1
        if k == 'set':
            xs = [x for x, t in c.scope.items() if t in WORDS and not x.startswith('row')
                  and x not in c.readonly and x not in c.fixed]
            if not xs:
                return None
            x = r.choice(xs)
            e = self.expr(c.scope[x], c)
            if e is None or e == x:
                return None
            return ['%s%s becomes %s.' % (p, x, e)], 1
        if k == 'ins':
            targets = []
            for x, t in c.scope.items():
                if t in ('S', 'SS', 'ST', 'FF') and x not in c.readonly:
                    targets.append((x, t))
            for x in c.writable('SS'):
                targets.append(('item 1 of %s' % x, 'S'))
            for x in c.writable('B'):
                targets.append(("%s's items" % x, 'S'))
            if not targets:
                return None
            target, t = r.choice(targets)
            elem = {'S': '5', 'SS': None, 'ST': None, 'FF': None}[t]
            if elem is None:
                elem = self.expr({'SS': 'S', 'ST': 'T', 'FF': 'F'}[t], c)
            if elem is None:
                return None
            return ['%sInsert %s into %s.' % (p, elem, target)], 1
        if k == 'rich':
            # What a chase, a set, a map of text, a crate or a record can have done to it.
            opts = []
            for x in c.writable('C'):
                opts += [lambda x=x: 'Insert %s into %s.' % (self.expr('T', c), x),
                         lambda x=x: 'Insert "abcdefghijklmnopqrstuvwxyz" into %s.' % x,
                         lambda x=x: 'Insert 65 into %s.' % x,
                         lambda x=x: 'Insert %s into the start of %s.' % (self.expr('T', c), x)]
            for x in c.writable('Q'):
                opts += [lambda x=x: 'Insert %s into %s.' % (self.expr('T', c), x)] * 2
            for x in c.writable('SC'):
                opts += [lambda x=x: 'Insert %s into %s.' % (self.expr('C', c), x),
                         lambda x=x: 'Insert "abcdefghijklmnopqrstuvwxyz" into item 1 of %s.' % x]
            for x in c.writable('SQ'):
                opts += [lambda x=x: 'Insert %s into %s.' % (self.expr('Q', c), x),
                         lambda x=x: 'Insert %s into item 1 of %s.' % (self.expr('T', c), x)]
            for x in c.writable('MT'):
                opts += [lambda x=x: 'In %s, the entry for %s becomes %s.' % (x, self.expr('T', c), self.expr('T', c)),
                         lambda x=x: 'In %s, the entry for "k" becomes %s.' % (x, self.expr('T', c))]
            for x in c.writable('K'):
                opts += [lambda x=x: 'Cast load on (%s, %s).' % (x, self.expr('T', c)),
                         lambda x=x: 'Cast swap on (%s, %s).' % (x, self.expr('C', c)),
                         lambda x=x: "%s's letters becomes %s." % (x, self.expr('C', c)),
                         lambda x=x: "%s's words becomes %s." % (x, self.expr('Q', c)),
                         lambda x=x: "Insert \"abcdefghijklmnopqrstuvwxyz\" into %s's letters." % x]
            for x in c.writable('R'):
                opts += [lambda x=x: 'The items of %s becomes %s.' % (x, self.expr('S', c)),
                         lambda x=x: 'The label of %s becomes %s.' % (x, self.expr('T', c)),
                         lambda x=x: 'Insert 5 into the items of %s.' % x]
            if not opts:
                return None
            return [p + r.choice(opts)()], 1
        if k == 'each2':
            # A loop over a set, a chase, texts, chases, sets, and a map's pairs.
            xs = [(x, t) for x, t in c.scope.items() if t in ('Q', 'C', 'ST', 'SC', 'SQ', 'MT', 'M')]
            if not xs:
                return None
            x, t = r.choice(xs)
            row = self.name('row')
            inner = c.inner()
            m = r.randint(1, 2)
            extra = []
            if t in ('MT', 'M'):
                q, held = self.name(), 'T' if t == 'MT' else 'S'
                extra = ['%s    Define %s as the %s of %s.' % (p, q, 'key' if r.random() < 0.4 else 'value', row)]
                inner.scope[q] = 'T' if 'the key' in extra[0] else held
            else:
                inner.scope[row] = {'Q': 'T', 'C': 'T', 'ST': 'T', 'SC': 'C', 'SQ': 'Q'}[t]
            lines, _ = self.body(m, inner, pad + 1)
            return ['%sFor each %s in %s, repeat:' % (p, row, x)] + extra + lines + [p + 'Done.'], 1 + m
        if k == 'judge':
            xs = [x for x in c.of('U') if 'it' not in c.scope]
            if not xs:
                return None
            out = ['%sJudge %s, where it is:' % (p, r.choice(xs))]
            for words, t in (('text', 'T'), ('series of number', 'S')):
                inner = c.inner(fixed=c.fixed | {'it'})
                inner.nest += 1
                inner.scope['it'] = t
                lines, _ = self.body(1, inner, pad + 2)
                out += ['%s    A %s:' % (p, words)] + (lines or [p + '        State "".']) + [p + '    Done.']
            return out + [p + 'Done.'], 3
        if k == 'narrow':
            xs = [x for x in c.of('V') if x not in c.readonly]
            if not xs:
                return None
            x = r.choice(xs)
            inner = c.inner(fixed=c.fixed | {x})
            inner.scope[x] = 'S'
            lines, _ = self.body(r.randint(1, 2), inner, pad + 1)
            return ['%sIf %s is not void:' % (p, x)] + lines + [p + 'Done.'], 2
        if k == 'const':
            # A constant, sometimes one a function above already reads. Its value is made from the
            # program's own names, so that every body sees it, or now and then from whatever is here.
            planned = bool(self.pending) and r.random() < 0.8
            x, T = self.pending.pop(0) if planned else (self.name('k'), r.choice(
                ['S', 'SS', 'ST', 'T', 'C', 'Q', 'SC', 'B', 'K', 'R', 'MT', 'M']))
            closed = r.random() < (0.85 if planned else 0.5)
            e = self.expr(T, Ctx(self.shared, c.funcs) if closed else c)
            if e is None:
                return None
            c.scope[x] = T
            c.fixed = c.fixed | {x}
            if closed:
                self.shared[x] = T
            return ['%sDefine %s as %s permanently.' % (p, x, e)], 1
        if k == 'bindfn':
            f = self.name('h')
            params = [('a%d' % j, r.choice(PARAM_TYPES + ['C', 'Q'])) for j in range(r.randint(1, 2))]
            params = [(self.name('a'), t) for _, t in params]
            ret = r.choice([None, None, 'S', 'T', 'C', 'Q', 'ST'])
            inner = Ctx({**self.shared, **dict(params)}, list(c.funcs), ret, 1, True, fixed=frozenset(self.shared))
            for x, t in c.scope.items():        # one of the block's own, now and then: `check` decides
                if x in c.fixed and x not in inner.scope and r.random() < 0.2:
                    inner.scope[x] = t
                    inner.fixed = inner.fixed | {x}
            m = r.randint(1, 4)
            lines, after = self.body(m, inner, pad + 1)
            if ret:
                e = self.expr(ret, after)
                if e is None:
                    return None
                lines.append('%s    Return %s.' % (p, e))
            self.bound.append((f, params, ret))
            return [self.head(p, f, params, ret)] + (lines or [p + '    State "".']) + [p + 'Done.'], 1 + m
        if k == 'unto':
            f = self.name('m')
            q, t = self.name('q'), r.choice(['S', 'T', 'C'])
            inner = Ctx({**self.shared, q: t, 'one': 'B'}, [], None, 1, True,
                        fixed=frozenset(self.shared) | {'one'})
            m = r.randint(1, 3)
            lines, _ = self.body(m, inner, pad + 1)
            self.methods.append((f, t))
            return (['%sBind void to %s unto bag, given (the %s %s):' % (p, f, WORDS[t], q)]
                    + (lines or [p + '    State "".']) + [p + 'Done.']), 1 + m
        if k == 'callu':
            bs = c.of('B')
            if not bs or not self.methods:
                return None
            f, t = r.choice(self.methods)
            return ['%sCast %s on (%s, %s).' % (p, f, r.choice(bs), self.expr(t, c))], 1
        if k == 'seti':
            xs = [(x, t) for x, t in c.scope.items() if t in ('SS', 'ST', 'FF') and x not in c.readonly]
            if not xs:
                return None
            x, t = r.choice(xs)
            e = self.expr({'SS': 'S', 'ST': 'T', 'FF': 'F'}[t], c)
            if e is None:
                return None
            return ['%sItem 1 of %s becomes %s.' % (p, x, e)], 1
        if k == 'mapset':
            ms = c.writable('M')
            if not ms:
                return None
            e = self.expr('S', c)
            return ['%sIn %s, the entry for "%s" becomes %s.' % (p, r.choice(ms), r.choice('kq'), e)], 1
        if k == 'mapget':
            ms = c.of('M')
            if not ms:
                return None
            x = self.name()
            c.scope[x] = 'S'
            return ['%sDefine %s as (the entry for "%s" in %s) but void is (a series of number).'
                    % (p, x, r.choice('kq'), r.choice(ms))], 1
        if k == 'field':
            bs = c.writable('B')
            if not bs:
                return None
            b = r.choice(bs)
            if r.random() < 0.6:
                return ["%s%s's items becomes %s." % (p, b, self.expr('S', c))], 1
            return ["%s%s's label becomes %s." % (p, b, self.expr('T', c))], 1
        if k == 'callf':
            f = self.expr('F', c)
            if f is None:
                return None
            return ['%sCast %s on (%s).' % (p, f, self.expr('S', c))], 1
        if k == 'callm':
            bs = c.of('B')
            if not bs:
                return None
            return ['%sCast keep on (%s, %s).' % (p, r.choice(bs), self.expr('S', c))], 1
        if k == 'calls':
            fs = [f for f in self.callable(c) if f[2] is None]
            if not fs:
                return None
            f, params, _ = r.choice(fs)
            a = self.args(params, c, 0)
            if a is None:
                return None
            return ['%sCast %s on (%s).' % (p, f, a)], 1
        if k == 'use':
            xs = [x for x, t in c.scope.items() if t in PRINTABLE]
            if not xs:
                return None
            return ['%sState %s.' % (p, r.choice(xs))], 1
        if k == 'lambda':
            # A function value, defined or given anew; its body sees everything here (a closure).
            row = self.name('row')
            returns = r.random() < 0.35
            inner = c.inner(ret='S' if returns else None, in_func=True, rabbit=0, decl=True)
            inner.scope[row] = 'S'
            m = r.randint(1, 3)
            lines, after = self.body(m, inner, pad + 1)
            if returns:
                lines.append('%s    Return %s.' % (p, self.expr('S', after)))
            T = 'G' if returns else 'F'
            olds = c.writable(T)
            if olds and r.random() < 0.4:
                head = '%s%s becomes a function given (the series of number %s):' % (p, r.choice(olds), row)
            else:
                x = self.name()
                c.scope[x] = T
                head = '%sDefine %s as a function given (the series of number %s):' % (p, x, row)
            return [head] + lines + [p + 'Done.'], 1 + m
        if k == 'nested':
            # A function in a function: it captures the enclosing names.
            f = self.name('g')
            q = self.name('q')
            inner = c.inner(ret=None, rabbit=0, decl=True)
            inner.scope[q] = 'S'
            m = r.randint(1, 3)
            lines, _ = self.body(m, inner, pad + 1)
            c.funcs = c.funcs + [(f, [(q, 'S')], None)]
            return (['%sBind void to %s, given (the series of number %s):' % (p, f, q)] + lines
                    + [p + 'Done.']), 1 + m
        if k == 'foreach':
            xs = c.of('SS')
            if not xs:
                return None
            row = self.name('row')
            inner = c.inner()
            inner.scope[row] = 'S'
            m = r.randint(1, 2)
            lines, _ = self.body(m, inner, pad + 1)
            return ['%sFor each %s in %s, repeat:' % (p, row, r.choice(xs))] + lines + [p + 'Done.'], 1 + m
        if k == 'rabbit':
            m = r.randint(1, max(1, n - 1))
            lines, _ = self.body(m, c.inner(rabbit=c.rabbit + 1, hoist=c.hoist, decl=c.hoist), pad + 1)
            for x, t in self.shared.items():        # a rabbit's constant is the program's after it
                if c.hoist and x not in c.scope:
                    c.scope[x] = t
                    c.fixed = c.fixed | {x}
            return [p + 'Pull a rabbit.'] + lines + [p + 'Done.'], 1 + m
        if k == 'task':
            # A task reads what it captured and may not change it; it may make and change its own.
            # Named, it hands a series back to be awaited; or it sends one through a channel.
            inner = c.inner(in_task=True, ret=None, readonly=frozenset(c.scope) | c.readonly)
            # ⚠ A function value cannot be handed to a task's thread compiled ("a channel of
            # 'function' is not supported"), though `check` passes it — a known gap, not searched.
            inner.scope = {x: t for x, t in inner.scope.items() if t not in ('F', 'FF', 'G')}
            m = r.randint(1, 3)
            lines, after = self.body(m, inner, pad + 1)
            shape = r.choice(['plain', 'named', 'named', 'channel', 'channel'])
            T = r.choice(['S', 'T', 'C', 'Q', 'ST', 'SS']) if self.rich else 'S'
            fresh = {'S': '(a series of number)', 'T': '"none"', 'C': 'a chase', 'Q': 'a set of text',
                     'ST': '(a series of text)', 'SS': '(a series of series of number)'}[T]
            if shape == 'plain':
                return [p + 'Have rabbit start a task:'] + lines + [p + 'Done.'], 1 + m
            got = self.name()
            if shape == 'named':
                t = self.name('t')
                out = ([p + 'Have rabbit start a task as %s:' % t] + lines
                       + ['%s    Return %s.' % (p, self.expr(T, after)), p + 'Done.',
                          '%sDefine %s as the awaited result of %s.' % (p, got, t)])
            else:
                ch = self.name('ch')
                out = (['%sDefine %s as a channel of %s.' % (p, ch, WORDS[T]),
                        p + 'Have rabbit start a task:'] + lines
                       + ['%s    Send %s through %s.' % (p, self.expr(T, after), ch),
                          '%s    Close %s.' % (p, ch), p + 'Done.',
                          '%sDefine %s as (the delivery from %s) but void is %s.' % (p, got, ch, fresh)])
            c.scope[got] = T
            return out, 2 + m
        if k == 'try':
            # A failure caught, or an exception (an item that is not there) suppressed.
            m = r.randint(1, max(1, (n - 1) // 2))
            tried = c.inner(in_try=True)
            fallible = [f for f in c.funcs if f[2] == 'SF' and self.args(f[1], tried, 1) is not None]
            first = []
            if fallible and r.random() < 0.7:
                f, params, _ = r.choice(fallible)
                first = ['%s    Define %s as cast %s on (%s).' % (p, self.name(), f, self.args(params, tried, 1))]
                handler = 'In case of failure:'
            else:
                handler = 'In case of exception:'
            a, _ = self.body(m, c.inner(in_try=True), pad + 1)
            b, _ = self.body(m, c.inner(), pad + 1)
            if handler == 'In case of exception:':
                b.append('%s    Suppress the exception.' % p)
            return ([p + 'Try to:'] + first + a + [p + 'Done.', p + handler] + b + [p + 'Done.']), 1 + 2 * m
        if k == 'if':
            m = r.randint(1, max(1, (n - 1) // 2))
            a, _ = self.body(m, c.inner(), pad + 1)
            b, _ = self.body(m, c.inner(), pad + 1)
            return [p + 'If chance:'] + a + [p + 'Done.', p + 'Otherwise:'] + b + [p + 'Done.'], 1 + 2 * m
        if k == 'ret':
            e = self.expr(c.ret, c)
            if e is None:
                return None
            return ['%sReturn %s.' % (p, e)], 1
        return None

    def head(self, p, f, params, ret):
        shown = ', '.join('the void function %s given (the series of number)' % x if t == 'F'
                          else 'the %s %s' % (WORDS[t], x) for x, t in params)
        returned = 'series of number or failure' if ret == 'SF' else WORDS[ret] if ret else 'void'
        return '%sBind %s to %s, given (%s):' % (p, returned, f, shown)

    # ── a program ───────────────────────────────────────────────────────

    def program(self):
        r = self.rng
        funcs = []
        lines = []
        types, rets = PARAM_TYPES, [None, None, 'S', 'SS', 'T', 'SF', 'SF']
        planned = {}
        if self.rich:
            types = types + ['C', 'C', 'Q', 'Q', 'SC', 'K', 'MT']
            rets = rets + ['C', 'Q', 'ST']
            self.pending = [('k%da' % i, r.choice(['S', 'S', 'SS', 'ST', 'T', 'C', 'Q', 'SC', 'B', 'K']))
                            for i in range(r.randint(0, 3))]
            planned = dict(self.pending)
        for i in range(r.randint(0, 2)):
            f = 'f%d' % i
            params = [('p%d' % j, r.choice(types)) for j in range(r.randint(1, 3))]
            ret = r.choice(rets)
            gives = 'S' if ret == 'SF' else ret        # a fallible one returns a series when it does not fail
            read = {x: t for x, t in planned.items() if r.random() < 0.6}
            c = Ctx({**read, **dict(params)}, list(funcs), gives, 0, True, fixed=frozenset(read))
            body, after = self.body(r.randint(2, 6), c, 1)
            if ret:
                e = self.expr(gives, after)
                if e is None:
                    continue
                body.append('    Return %s.' % e)
            if ret == 'SF':
                body.insert(0, '    If chance, return a failure "no".')
            lines.append(self.head('', f, params, ret))
            lines += body if body else ['    State "".']
            lines.append('Done.')
            funcs.append((f, params, ret))
        main, c = self.body(r.randint(4, 16 if self.rich else 12), Ctx({}, funcs, hoist=True), 0)
        # A constant a function reads that no line got round to.
        late = []
        while self.pending:
            x, t = self.pending.pop(0)
            ex = self.expr(t, Ctx(self.shared, []))
            late.append('Define %s as %s permanently.' % (x, ex))
            self.shared[x] = t
        lines += main + late
        for x, t in c.scope.items():
            if t in PRINTABLE:
                lines.append('State %s.' % x)
            elif t == 'M':
                lines.append('State the size of %s.' % x)
            elif t == 'B':
                lines.append("State %s's items." % x)
            elif t == 'MT':
                lines.append('State the size of %s.' % x)
            elif t == 'K':
                lines += ["State %s's letters." % x, "State %s's words." % x]
            elif t == 'R':
                lines.append('State %s.' % x)
        # What every function left behind, read once more after everything that could free it.
        for f, params, ret in self.bound:
            if ret in PRINTABLE:
                a = self.args(params, Ctx(self.shared, []), 1)
                if a is not None:
                    lines.append('State cast %s on (%s).' % (f, a))
        for x, t in self.shared.items():
            if t in PRINTABLE:
                lines.append('State %s.' % x)
        if not self.rich:
            return PRELUDE + '\n'.join(lines) + '\n'
        text = PRELUDE + CRATE + '\n'.join(lines)
        return 'Pull a book on collections.\n' + '\n'.join('    ' + l for l in text.split('\n')) + '\nDone.\n'


def source(body, chance):
    return 'Define chance as %s permanently.\n%s' % ('true' if chance else 'false', body)


# ── running ─────────────────────────────────────────────────────────────

def run(args, cwd=None, timeout=60):
    try:
        r = subprocess.run(args, cwd=cwd, capture_output=True, text=True, encoding='utf-8',
                           errors='replace', timeout=timeout)
        return r.returncode, r.stdout, r.stderr
    except subprocess.TimeoutExpired:
        return -1, '', 'timeout'


def wsl_path(p):
    p = os.path.abspath(p).replace('\\', '/')
    return '/mnt/%s%s' % (p[0].lower(), p[2:])


_rt_lock = threading.Lock()
_rt_done = {}


def runtime_object(job_dir, cache):
    """The runtime, compiled once per distinct runtime source."""
    src = open(os.path.join(job_dir, 'cufet-runtime.c'), 'rb').read()
    h = hashlib.sha1(src).hexdigest()[:12]
    obj = os.path.join(cache, 'rt-%s.o' % h)
    with _rt_lock:
        if h not in _rt_done:
            if not os.path.exists(obj):
                code, out, err = run(['wsl', '-e', 'bash', '-lc',
                                      'cd %s && gcc -c cufet-runtime.c -o %s -fsanitize=address -g -O0 -pthread'
                                      % (wsl_path(job_dir), wsl_path(obj))], timeout=300)
                if not os.path.exists(obj):
                    raise RuntimeError('runtime did not compile: ' + (out + err)[-400:])
            _rt_done[h] = obj
    return _rt_done[h]


def try_one(n, body, work, cache):
    """None when `check` refused it; else (verdict, detail)."""
    d = os.path.join(work, 'j%d' % n)
    os.makedirs(d, exist_ok=True)
    try:
        sides = []
        for chance in (True, False):
            name = 'p_%s' % ('t' if chance else 'f')
            path = os.path.join(d, name + '.cufe')
            open(path, 'w', encoding='utf-8', newline='\n').write(source(body, chance))
            code, out, err = run([CUFET, 'emit-c', path, os.path.join(d, name + '.c')], timeout=60)
            if code != 0 or not os.path.exists(os.path.join(d, name + '.c')):
                if chance:
                    return ('refused', out + err)
                return ('odd', 'one side refused: ' + (out + err)[-300:])
            _, interp, ierr = run([CUFET, path], timeout=20)
            sides.append((name, interp, ierr))
        obj = runtime_object(d, cache)
        script = ' ; '.join(
            'gcc %s.c %s -o %s -fsanitize=address -g -O0 -pthread -lm 2> %s.gccerr ; '
            'ASAN_OPTIONS=detect_leaks=0 timeout 20 ./%s > %s.out 2> %s.err'
            % (s, wsl_path(obj), s, s, s, s, s) for s, _, _ in sides)
        run(['wsl', '-e', 'bash', '-lc', 'cd %s && %s' % (wsl_path(d), script)], timeout=200)
        verdicts = []
        for s, interp, ierr in sides:
            def read(ext):
                f = os.path.join(d, s + ext)
                return open(f, encoding='utf-8', errors='replace').read() if os.path.exists(f) else ''
            if ierr == 'timeout':
                verdicts.append(('timeout', ''))
                continue
            if not os.path.exists(os.path.join(d, s)):
                verdicts.append(('nobuild', read('.gccerr')[-400:]))
                continue
            err = read('.err')
            if 'AddressSanitizer' in err:
                head = [l for l in err.splitlines() if 'ERROR' in l or 'SUMMARY' in l]
                verdicts.append(('hole', '\n'.join(head[:2])))
            elif read('.out').strip() != interp.strip():
                verdicts.append(('differs', 'compiled:\n%s\ninterpreted:\n%s' % (read('.out')[-300:], interp[-300:])))
            else:
                verdicts.append(('same', ''))
        rank = ['hole', 'differs', 'nobuild', 'timeout', 'same']
        worst = min(verdicts, key=lambda v: rank.index(v[0]))
        return worst[0], '\n'.join('chance %s: %s %s' % (side, v, det)
                                   for side, (v, det) in zip(('true', 'false'), verdicts))
    finally:
        shutil.rmtree(d, ignore_errors=True)


def main():
    numbers = [int(a) for a in sys.argv[1:] if a.isdigit()]
    count = numbers[0] if numbers else 200
    seed = numbers[1] if len(numbers) > 1 else 1
    workers = numbers[2] if len(numbers) > 2 else 6
    rng = random.Random(seed)
    bodies = [Gen(rng).program() for _ in range(count)]
    work = tempfile.mkdtemp(prefix='cufet-fuzz-')
    cache = os.path.join(tempfile.gettempdir(), 'cufet-fuzz-rt')
    os.makedirs(cache, exist_ok=True)
    os.makedirs(OUT, exist_ok=True)
    tally = {}
    reasons = {}
    started = time.time()
    with concurrent.futures.ThreadPoolExecutor(workers) as pool:
        futures = {pool.submit(try_one, n, b, work, cache): n for n, b in enumerate(bodies)}
        for done, fut in enumerate(concurrent.futures.as_completed(futures), 1):
            n = futures[fut]
            try:
                verdict, detail = fut.result()
            except Exception as e:                     # a harness fault, not a finding
                verdict, detail = 'harness', repr(e)
            tally[verdict] = tally.get(verdict, 0) + 1
            if verdict == 'refused':
                line = next((l for l in detail.splitlines() if 'error' in l), detail.strip()[:120])
                key = 'region' if 'shorter-lived region' in detail or 'store' in detail else line.split('error:')[-1].strip()[:90]
                reasons[key] = reasons.get(key, 0) + 1
            elif verdict in ('hole', 'differs', 'odd', 'harness', 'nobuild'):
                name = '%s-fz%d-%d.cufe' % (verdict, seed, n)
                open(os.path.join(OUT, name), 'w', encoding='utf-8', newline='\n').write(
                    '/*  %s\n%s  */\n\n%s' % (name, detail.replace('*/', '* /'), source(bodies[n], True)))
                print('#%d %s -> %s' % (n, verdict, name))
                sys.stdout.flush()
            if done % 200 == 0:
                print('  %d done, %s  [%.0f s]' % (done, tally, time.time() - started))
                sys.stdout.flush()
    print('programs %d: %s  [%.0f s, seed %d]' % (count, tally, time.time() - started, seed))
    print('refused for:')
    for k, v in sorted(reasons.items(), key=lambda kv: -kv[1])[:15]:
        print('  %5d  %s' % (v, k))


if __name__ == '__main__':
    main()
