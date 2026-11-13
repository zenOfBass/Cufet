"""Directed programs: one per rule of checker B that random search is unlikely to hit.
Each must be REFUSED by checker B, and ACCEPTED-and-unsafe once its rule is removed."""
import regions
from regions import check_A, check_B, is_safe, show

n = iter(range(1, 1000))
S, SS = 'S', 'SS'

WITNESSES = {
    'item-keeps-exact': ([], [
        ('def', next(n), 'a', S, ('new', S)),
        ('rabbit', next(n), [
            ('def', next(n), 'x', SS, ('new', SS)),
            ('ins', next(n), 'x', ('var', 'a')),
            ('def', next(n), 'y', S, ('item', 'x')),
            ('ins', next(n), 'y', ('num',)),
        ]),
        ('use', next(n), 'a'),
    ]),
    'below-as-same': ([('f', [('p', SS)], None, [
        ('def', next(n), 'y', S, ('item', 'p')),
        ('ins', next(n), 'y', ('num',)),
    ])], [
        ('def', next(n), 'a', S, ('new', S)),
        ('rabbit', next(n), [
            ('def', next(n), 'x', SS, ('new', SS)),
            ('ins', next(n), 'x', ('var', 'a')),
            ('calls', next(n), 'f', ['x']),
        ]),
        ('use', next(n), 'a'),
    ]),
    'join-takes-left': ([], [
        ('def', next(n), 'a', S, ('new', S)),
        ('rabbit', next(n), [
            ('def', next(n), 'y', S, ('new', S)),
            ('if', next(n), [('use', next(n), 'y')], [('set', next(n), 'y', ('var', 'a'))]),
            ('ins', next(n), 'y', ('num',)),
        ]),
        ('use', next(n), 'a'),
    ]),
}
# storing into an element is the same program as believing the element's level: the element
# is lower than the series it was read from, and its growth is sent to the wrong arena
WITNESSES['store-into-anything'] = WITNESSES['item-keeps-exact']

# The probes of docs/soundness/probes that the core language can express. Each must be ACCEPTED
# by checker A and unsafe (the model agrees with the real compiler). Checker B must either REFUSE
# it or accept it and run it safely.
PROBES = {
    'grow-1-outer-value-into-outer-series': ([], [
        ('def', next(n), 'first', S, ('new', S)),
        ('def', next(n), 'keeper', SS, ('new', SS)),
        ('rabbit', next(n), [('ins', next(n), 'keeper', ('var', 'first'))]),
        ('use', next(n), 'keeper'),
    ]),
    'alias-1-append-through-a-second-name': ([], [
        ('def', next(n), 'totals', S, ('new', S)),
        ('rabbit', next(n), [
            ('def', next(n), 'alias', S, ('var', 'totals')),
            ('ins', next(n), 'alias', ('num',)),
        ]),
        ('use', next(n), 'totals'),
    ]),
    'alias-2-store-through-a-second-name': ([], [
        ('def', next(n), 'keeper', SS, ('new', SS)),
        ('ins', next(n), 'keeper', ('new', S)),
        ('rabbit', next(n), [
            ('def', next(n), 'alias', SS, ('var', 'keeper')),
            ('def', next(n), 'inner', S, ('new', S)),
            ('seti', next(n), 'alias', ('var', 'inner')),
        ]),
        ('use', next(n), 'keeper'),
    ]),
    'element-2-grow-an-item': ([], [
        ('def', next(n), 'first', S, ('new', S)),
        ('rabbit', next(n), [
            ('def', next(n), 'table', SS, ('new', SS)),
            ('ins', next(n), 'table', ('var', 'first')),
            ('insitem', next(n), 'table', ('num',)),
        ]),
        ('use', next(n), 'first'),
    ]),
    'param-1-helper-appends-numbers': ([('add-some', [('bag', S)], None, [
        ('ins', next(n), 'bag', ('num',)),
    ])], [
        ('def', next(n), 'totals', S, ('new', S)),
        ('rabbit', next(n), [('calls', next(n), 'add-some', ['totals'])]),
        ('use', next(n), 'totals'),
    ]),
    'param-2-helper-stores-what-it-made': ([('push-into', [('bag', SS)], None, [
        ('def', next(n), 'fresh', S, ('new', S)),
        ('ins', next(n), 'bag', ('var', 'fresh')),
    ])], [
        ('def', next(n), 'keeper', SS, ('new', SS)),
        ('rabbit', next(n), [('calls', next(n), 'push-into', ['keeper'])]),
        ('use', next(n), 'keeper'),
    ]),
    'result-1-fresh-series-from-a-call': ([('make-it', [], S, [
        ('ret', next(n), ('new', S)),
    ])], [
        ('def', next(n), 'outer', S, ('new', S)),
        ('rabbit', next(n), [('set', next(n), 'outer', ('call', 'make-it', []))]),
        ('use', next(n), 'outer'),
    ]),
    'result-3-parameter-wrapped-in-a-series': ([('wrap', [('s', S)], SS, [
        ('def', next(n), 'result', SS, ('new', SS)),
        ('ins', next(n), 'result', ('var', 's')),
        ('ret', next(n), ('var', 'result')),
    ])], [
        ('def', next(n), 'outer', SS, ('new', SS)),
        ('rabbit', next(n), [
            ('def', next(n), 'inner', S, ('new', S)),
            ('set', next(n), 'outer', ('call', 'wrap', ['inner'])),
        ]),
        ('use', next(n), 'outer'),
    ]),
}

# The same programs moved to where they are safe: B must ACCEPT these, so its refusals above are
# about the rabbit and not about the shape of the code.
LEGAL = {
    'helper appends, called at the depth of its argument': (PROBES['param-1-helper-appends-numbers'][0], [
        ('def', next(n), 'totals', S, ('new', S)),
        ('calls', next(n), 'add-some', ['totals']),
        ('rabbit', next(n), [
            ('def', next(n), 'local', S, ('new', S)),
            ('calls', next(n), 'add-some', ['local']),
            ('use', next(n), 'local'),
        ]),
        ('use', next(n), 'totals'),
    ]),
    'helper stores what it made, called at the depth of its argument': (
        PROBES['param-2-helper-stores-what-it-made'][0], [
        ('def', next(n), 'keeper', SS, ('new', SS)),
        ('calls', next(n), 'push-into', ['keeper']),
        ('use', next(n), 'keeper'),
    ]),
    'an outer series grown inside a rabbit': ([], [
        ('def', next(n), 'totals', S, ('new', S)),
        ('rabbit', next(n), [('ins', next(n), 'totals', ('num',))]),
        ('use', next(n), 'totals'),
    ]),
    'an outer value stored in an outer series inside a rabbit': PROBES['grow-1-outer-value-into-outer-series'],
    'a fresh result kept at the depth of the call': (PROBES['result-1-fresh-series-from-a-call'][0], [
        ('rabbit', next(n), [
            ('def', next(n), 'mine', S, ('call', 'make-it', [])),
            ('ins', next(n), 'mine', ('num',)),
            ('use', next(n), 'mine'),
        ]),
    ]),
    'a function returning its parameter, result kept outside': ([('same', [('s', S)], S, [
        ('ret', next(n), ('var', 's')),
    ])], [
        ('def', next(n), 'outer', S, ('new', S)),
        ('def', next(n), 'other', S, ('new', S)),
        ('rabbit', next(n), [
            ('set', next(n), 'outer', ('call', 'same', ['other'])),
            ('ins', next(n), 'outer', ('num',)),
        ]),
        ('use', next(n), 'outer'),
    ]),
}

if __name__ == '__main__':
    for name, prog in WITNESSES.items():
        regions.MUTANT = None
        refused = not check_B(prog)[0]
        regions.MUTANT = name
        ok, ann = check_B(prog)
        unsafe = ok and not is_safe(prog, ann)
        regions.MUTANT = None
        print('%-22s refused by B: %-5s   without the rule, accepted and unsafe: %s'
              % (name, refused, unsafe))
    print()
    for name, prog in PROBES.items():
        okA, annA = check_A(prog)
        okB, annB = check_B(prog)
        verdict = 'refuses' if not okB else 'accepts, and it is safe' if is_safe(prog, annB)             else 'ACCEPTS AND IT IS UNSAFE'
        print('%-42s A accepts and it is unsafe: %-5s   B %s'
              % (name, okA and not is_safe(prog, annA), verdict))
    print()
    for name, prog in LEGAL.items():
        ok, ann = check_B(prog)
        print('%-62s B accepts and it is safe: %s' % (name, ok and is_safe(prog, ann)))
