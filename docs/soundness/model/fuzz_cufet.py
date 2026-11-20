"""
Random programs over the WHOLE of what the region rules govern, held to the real tools, with
AddressSanitizer as the judge instead of the model.

    python fuzz_cufet.py [count] [seed] [workers]     (from PowerShell: it calls cufet and wsl)

against_cufet.py asks the model which programs are unsafe, so it can only search the model's core
language: series, nested series, rabbits, first-order functions. This one writes Cufet directly,
with text, maps, an object with fields, a method and a getter, functions as values (named and
lambdas), closures that capture, a series of functions, and functions nested in functions. Each
program `check` accepts is compiled with `-fsanitize=address` under WSL and run with `chance`
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
}
PARAM_TYPES = ['S', 'S', 'SS', 'ST', 'M', 'B', 'F', 'T']
PRINTABLE = ('T', 'S', 'SS', 'ST')

PRELUDE = '''Define object bag with (the series of number items, the text label):
    Get payload as series of number:
        Return one's items.
    Done.
Done.
Bind void to keep unto bag, given (the series of number row):
    one's items becomes row.
Done.
'''


class Ctx:
    """What a body can see: its names and their types, and the functions it may call."""

    def __init__(self, scope, funcs, ret=None, nest=0, in_func=False, rabbit=0,
                 readonly=frozenset(), in_task=False, in_try=False):
        self.in_try = in_try
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
                   self.readonly, self.in_task, self.in_try)

    def inner(self, **change):
        c = self.copy()
        c.nest += 1
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
        elif T == 'S':
            opts += [lambda: '(a series of number with (1, 2))', lambda: '(a series of number)']
            for x in c.of('SS'):
                opts += [lambda x=x: '(item 1 of %s)' % x] * 2
            for x in c.of('B'):
                opts += [lambda x=x: "(%s's items)" % x, lambda x=x: "(%s's payload)" % x]
            for x in c.of('G'):
                if deep:
                    opts.append(lambda x=x: '(cast %s on (%s))' % (x, self.expr('S', c, depth + 1)))
        elif T == 'SS':
            opts.append(lambda: '(a series of series of number)')
        elif T == 'ST':
            opts.append(lambda: '(a series of text)')
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
            for f, params, ret in c.funcs:
                if ret == T:
                    args = self.args(params, c, depth + 1)
                    if args is not None:
                        opts += [lambda f=f, a=args: '(cast %s on (%s))' % (f, a)] * 2
        if not opts:
            return None
        return r.choice(opts)()

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
            if c.nest < 3:
                kinds += ['rabbit'] * 4 + ['if'] * 2 + ['try'] * 2
            if c.rabbit and not c.in_task and c.nest < 3:
                kinds += ['task'] * 3
            if c.in_task:
                kinds = [k for k in kinds if k not in ('calls', 'callf', 'callm', 'nested')]
            if c.in_func and c.nest < 2:
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
            T = r.choice(['S', 'S', 'SS', 'ST', 'T', 'M', 'B', 'FF', 'F'])
            e = self.expr(T, c)
            if e is None:
                return None
            x = self.name()
            c.scope[x] = T
            return ['%sDefine %s as %s.' % (p, x, e)], 1
        if k == 'set':
            xs = [x for x, t in c.scope.items() if t in WORDS and not x.startswith('row')
                  and x not in c.readonly]
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
            fs = [f for f in c.funcs if f[2] is None]
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
            inner = c.inner(ret='S' if returns else None, in_func=True, rabbit=0)
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
            inner = c.inner(ret=None, rabbit=0)
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
            lines, _ = self.body(m, c.inner(rabbit=c.rabbit + 1), pad + 1)
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
            if shape == 'plain':
                return [p + 'Have rabbit start a task:'] + lines + [p + 'Done.'], 1 + m
            got = self.name()
            if shape == 'named':
                t = self.name('t')
                out = ([p + 'Have rabbit start a task as %s:' % t] + lines
                       + ['%s    Return %s.' % (p, self.expr('S', after)), p + 'Done.',
                          '%sDefine %s as the awaited result of %s.' % (p, got, t)])
            else:
                ch = self.name('ch')
                out = (['%sDefine %s as a channel of series of number.' % (p, ch),
                        p + 'Have rabbit start a task:'] + lines
                       + ['%s    Send %s through %s.' % (p, self.expr('S', after), ch),
                          '%s    Close %s.' % (p, ch), p + 'Done.',
                          '%sDefine %s as (the delivery from %s) but void is (a series of number).'
                          % (p, got, ch)])
            c.scope[got] = 'S'
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

    # ── a program ───────────────────────────────────────────────────────

    def program(self):
        r = self.rng
        funcs = []
        lines = []
        for i in range(r.randint(0, 2)):
            f = 'f%d' % i
            params = [('p%d' % j, r.choice(PARAM_TYPES)) for j in range(r.randint(1, 3))]
            ret = r.choice([None, None, 'S', 'SS', 'T', 'SF', 'SF'])
            gives = 'S' if ret == 'SF' else ret        # a fallible one returns a series when it does not fail
            c = Ctx(dict(params), list(funcs), gives, 0, True)
            body, after = self.body(r.randint(2, 6), c, 1)
            if ret:
                e = self.expr(gives, after)
                if e is None:
                    continue
                body.append('    Return %s.' % e)
            if ret == 'SF':
                body.insert(0, '    If chance, return a failure "no".')
            shown = ', '.join('the void function %s given (the series of number)' % x if t == 'F'
                              else 'the %s %s' % (WORDS[t], x) for x, t in params)
            returned = 'series of number or failure' if ret == 'SF' else WORDS[ret] if ret else 'void'
            lines.append('Bind %s to %s, given (%s):' % (returned, f, shown))
            lines += body if body else ['    State "".']
            lines.append('Done.')
            funcs.append((f, params, ret))
        main, c = self.body(r.randint(4, 12), Ctx({}, funcs), 0)
        lines += main
        for x, t in c.scope.items():
            if t in PRINTABLE:
                lines.append('State %s.' % x)
            elif t == 'M':
                lines.append('State the size of %s.' % x)
            elif t == 'B':
                lines.append("State %s's items." % x)
        return '\n'.join(lines) + '\n'


def source(body, chance):
    return 'Define chance as %s permanently.\n%s%s' % ('true' if chance else 'false', PRELUDE, body)


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
