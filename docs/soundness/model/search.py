"""
Generates well-typed core programs at random and asks, of each checker:
    is there a program it ACCEPTS whose run reaches freed memory?

    python search.py [count] [seed]            both checkers, with one small example per hole
    python search.py mutants [count] [seed]    checker B with one rule removed at a time

Add the word `tight` to either for programs with few names that are mostly reused.
Add `forward` or `depth` to run on a runtime where a series grows in its own arena (regions.HOME);
checker B then lets a number be stored into any series.
"""
import random
import sys
import time

import regions
from regions import SERIES, ELEM, check_A, check_B, is_safe, show, size


ONLY = ()         # when set, the only removed rules `mutants` tries
TIGHT = False     # few names, mostly reused: programs whose statements act on each other


class Gen:
    def __init__(self, rng):
        self.rng = rng
        self.sid = 0
        self.names = 0

    def fresh_sid(self):
        self.sid += 1
        return self.sid

    def fresh_name(self):
        self.names += 1
        return 'v%d' % self.names

    def expr(self, T, scope, funcs):
        r = self.rng
        if T == 'N':
            return ('num',)
        options = [('new', T)] * (1 if TIGHT else 2)
        same = [x for x, t in scope.items() if t == T]
        if same:
            options += [('var', r.choice(same)) for _ in range(4)]
        if T == 'S':
            outer = [x for x, t in scope.items() if t == 'SS']
            if outer:
                options += [('item', r.choice(outer))] * (4 if TIGHT else 2)
        for f in funcs:
            if f[2] == T:
                args = self.args(f, scope)
                if args is not None:
                    options += [('call', f[0], args)] * 3
        return r.choice(options)

    def args(self, f, scope):
        out = []
        for _, t in f[1]:
            c = [x for x, tt in scope.items() if tt == t]
            if not c:
                return None
            out.append(self.rng.choice(c))
        return out

    def body(self, n, scope, funcs, ret_type, nest):
        """About n statements, and the scope at the end of them."""
        r = self.rng
        scope = dict(scope)
        out = []
        while n > 0:
            kinds = ['def', 'def', 'ins', 'ins', 'ins', 'set', 'set', 'seti', 'seti', 'insitem', 'use']
            if nest < 3:
                kinds += ['rabbit', 'rabbit', 'rabbit', 'if']
            if funcs:
                kinds += ['calls', 'calls', 'calls']
            if ret_type and nest > 0:
                kinds.append('ret')
            if TIGHT and len(scope) >= 4:
                kinds = [k for k in kinds if k != 'def'] + ['if']
            k = r.choice(kinds)
            sid = self.fresh_sid()
            series = [x for x, t in scope.items() if t in SERIES]
            nested = [x for x, t in scope.items() if t == 'SS']
            if k == 'def':
                T = r.choice(['S', 'S', 'SS'])
                x = self.fresh_name()
                out.append(('def', sid, x, T, self.expr(T, scope, funcs)))
                scope[x] = T
            elif k == 'set' and series:
                x = r.choice(series)
                out.append(('set', sid, x, self.expr(scope[x], scope, funcs)))
            elif k == 'ins' and series:
                x = r.choice(series)
                out.append(('ins', sid, x, self.expr(ELEM[scope[x]], scope, funcs)))
            elif k == 'seti' and nested:
                out.append(('seti', sid, r.choice(nested), self.expr('S', scope, funcs)))
            elif k == 'insitem' and nested:
                out.append(('insitem', sid, r.choice(nested), ('num',)))
            elif k == 'use' and series:
                out.append(('use', sid, r.choice(series)))
            elif k == 'rabbit':
                m = r.randint(1, max(1, n - 1))
                out.append(('rabbit', sid, self.body(m, scope, funcs, ret_type, nest + 1)[0]))
                n -= m
            elif k == 'if':
                m = r.randint(1, max(1, (n - 1) // 2))
                out.append(('if', sid, self.body(m, scope, funcs, ret_type, nest + 1)[0],
                            self.body(m, scope, funcs, ret_type, nest + 1)[0]))
                n -= 2 * m
            elif k == 'calls':
                f = r.choice(funcs)
                a = self.args(f, scope)
                if a is None:
                    continue
                out.append(('calls', sid, f[0], a))
            elif k == 'ret':
                out.append(('ret', sid, self.expr(ret_type, scope, funcs)))
                break
            else:
                continue
            n -= 1
        return out, scope

    def program(self, max_funcs=2, fsize=4, msize=8):
        r = self.rng
        if TIGHT:
            max_funcs = 1
        funcs = []
        for i in range(r.randint(0, max_funcs)):
            params = [('p%d' % j, r.choice(['S', 'S', 'SS'])) for j in range(r.randint(0, 2))]
            ret = r.choice([None, 'S', 'SS'])
            stmts, after = self.body(r.randint(1, fsize), dict(params), funcs, ret, 0)
            if ret:
                stmts.append(('ret', self.fresh_sid(), self.expr(ret, after, funcs)))
            funcs.append(('f%d' % i, params, ret, stmts))
        main, scope = self.body(r.randint(2, msize), {}, funcs, None, 0)
        for x in scope:                 # read everything still in scope, so a dangling name is used
            main.append(('use', self.fresh_sid(), x))
        return (funcs, main)


def reductions(prog):
    """Every program one step smaller: a statement dropped, a block replaced by its body,
    an If replaced by one arm, a function dropped."""
    funcs, main = prog

    def bodies(body):
        for i, s in enumerate(body):
            yield body[:i] + body[i + 1:]
            if s[0] == 'rabbit':
                yield body[:i] + s[2] + body[i + 1:]
                for b in bodies(s[2]):
                    yield body[:i] + [(s[0], s[1], b)] + body[i + 1:]
            elif s[0] == 'if':
                yield body[:i] + s[2] + body[i + 1:]
                yield body[:i] + s[3] + body[i + 1:]
                for b in bodies(s[2]):
                    yield body[:i] + [(s[0], s[1], b, s[3])] + body[i + 1:]
                for b in bodies(s[3]):
                    yield body[:i] + [(s[0], s[1], s[2], b)] + body[i + 1:]

    for i in range(len(funcs)):
        yield (funcs[:i] + funcs[i + 1:], main)
    for i, f in enumerate(funcs):
        for b in bodies(f[3]):
            yield (funcs[:i] + [(f[0], f[1], f[2], b)] + funcs[i + 1:], main)
    for b in bodies(main):
        yield (funcs, b)


def shrink(prog, holds):
    """Greedy: keep taking any smaller program the property still holds of."""
    changed = True
    while changed:
        changed = False
        for cand in reductions(prog):
            # a function that gives something back must still end by giving it back
            if any(f[2] and not (f[3] and f[3][-1][0] == 'ret') for f in cand[0]):
                continue
            try:
                if holds(cand):
                    prog = cand
                    changed = True
                    break
            except (KeyError, IndexError, TypeError, AttributeError):
                pass                     # the reduction left a name undefined; not a program
    return prog


def canonical(prog):
    """The program's text with names numbered by first appearance, so shapes can be compared."""
    import re
    seen = {}

    def rename(m):
        name = m.group(0)
        if name not in seen:
            functions = sum(1 for k in seen if k[0] == 'f')
            seen[name] = 'fn%d' % (functions + 1) if name[0] == 'f'                 else 'abcdeghijklmnopqrstuwxyz'[len(seen) - functions]
        return seen[name]
    return re.sub(r'(?<![a-z])[vpf][0-9]+(?![0-9a-z])', rename, show(prog))


def unsafe_under_A(prog):
    ok, ann = check_A(prog)
    return ok and not is_safe(prog, ann)


def unsafe_under_B(prog):
    ok, ann = check_B(prog)
    return ok and not is_safe(prog, ann)


def compare(count, seed):
    rng = random.Random(seed)
    stats = dict.fromkeys(['programs', 'safe as written (no checker, no copies)',
                           'A accepts', 'A accepts, unsafe', 'A refuses, safe as written',
                           'B accepts', 'B accepts, unsafe', 'B refuses, safe as written',
                           'B accepts, with a rabbit', 'B accepts, storing through a parameter',
                           'B accepts, A refuses'], 0)
    smallest = {}
    bad_B = []
    start = time.time()
    for _ in range(count):
        prog = Gen(rng).program()
        stats['programs'] += 1
        okA, annA = check_A(prog)
        okB, annB = check_B(prog)
        plain = is_safe(prog, {})
        stats['safe as written (no checker, no copies)'] += plain
        if okA:
            stats['A accepts'] += 1
            if not is_safe(prog, annA):
                stats['A accepts, unsafe'] += 1
                if len(smallest) < 400:
                    small = shrink(prog, unsafe_under_A)
                    smallest[canonical(small)] = smallest.get(canonical(small), 0) + 1
        elif plain:
            stats['A refuses, safe as written'] += 1
        if okB:
            stats['B accepts'] += 1
            stats['B accepts, with a rabbit'] += 'Pull a rabbit' in show(prog)
            stats['B accepts, storing through a parameter'] += bool(prog[0]) and any(
                s[0] in ('ins', 'seti') and s[2].startswith('p') for f in prog[0] for s in flat(f[3]))
            stats['B accepts, A refuses'] += not okA
            if not is_safe(prog, annB):
                stats['B accepts, unsafe'] += 1
                bad_B.append(prog)
        elif plain:
            stats['B refuses, safe as written'] += 1
    for k, v in stats.items():
        print('%-42s %d' % (k, v))
    print('seconds: %.1f' % (time.time() - start))
    print()
    print('distinct minimal unsafe programs checker A accepts (after shrinking): %d' % len(smallest))
    for text, n in sorted(smallest.items(), key=lambda kv: (len(kv[0]), kv[0])):
        print('-- found %d time(s)' % n)
        print(text)
        print()
    for p in bad_B[:5]:
        print('!! checker B accepted an UNSAFE program:')
        print(show(p))
        print()
    return stats


def flat(body):
    for s in body:
        yield s
        if s[0] == 'rabbit':
            yield from flat(s[2])
        elif s[0] == 'if':
            yield from flat(s[2])
            yield from flat(s[3])


def mutants(count, seed):
    """Removes one rule of checker B at a time. Each removal must let an unsafe program through,
    or the rule is not doing anything the search can see."""
    for name, what in regions.MUTANTS.items():
        if ONLY and name not in ONLY:
            continue
        regions.MUTANT = name
        rng = random.Random(seed)
        found = None
        tried = 0
        for tried in range(1, count + 1):
            prog = Gen(rng).program()
            ok, ann = check_B(prog)
            if ok and not is_safe(prog, ann):
                found = shrink(prog, unsafe_under_B)
                break
        regions.MUTANT = None
        print('== without the rule: %s  [%s]' % (what, name))
        if found is None:
            print('   NOT CAUGHT in %d programs' % tried)
        else:
            print('   caught at program %d; shrunk, the unsafe program it accepts is:' % tried)
            for line in show(found).splitlines():
                print('      ' + line)
        print()
        sys.stdout.flush()


if __name__ == '__main__':
    for mode in ('forward', 'depth'):
        if mode in sys.argv:
            sys.argv.remove(mode)
            regions.HOME = mode
    if 'tight' in sys.argv:
        sys.argv.remove('tight')
        TIGHT = True
    ONLY = tuple(a for a in sys.argv if a in regions.MUTANTS)
    sys.argv = [a for a in sys.argv if a not in regions.MUTANTS]
    if len(sys.argv) > 1 and sys.argv[1] == 'mutants':
        mutants(int(sys.argv[2]) if len(sys.argv) > 2 else 30000,
                int(sys.argv[3]) if len(sys.argv) > 3 else 1)
    else:
        compare(int(sys.argv[1]) if len(sys.argv) > 1 else 200000,
                int(sys.argv[2]) if len(sys.argv) > 2 else 1)
