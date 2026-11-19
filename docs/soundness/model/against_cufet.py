"""
The model's random programs, held to the REAL checker and compiler rather than to a model of them.

    python against_cufet.py [count] [seed] [tight]      (from PowerShell, so gcc is on PATH)

For each program the generator makes, the model's machine says whether some run of it reaches
freed memory on the runtime as built (a series grows in the arena its header names, a merged arena
forwards: regions.HOME = 'forward'). Only those it calls UNSAFE go to `cufet check`, since a safe
program cannot be a hole. One that `check` ACCEPTS is a candidate, and is built and run both ways,
with `chance` true and false, after a churn that hands freed memory out again. A candidate whose
compiled output differs from the interpreted one, or which crashes, is a hole shown; one that runs
right is listed too, for a person to read, since the model's runs take every path and these take
two.

The tool is `CUFET` below: the C# build in the repository, so that what is tested is the code.
"""
import os
import random
import subprocess
import sys
import tempfile
import time

import regions
from regions import is_safe, show
import search

CUFET = os.environ.get('CUFET', r'C:\dev\Cufet\src\App\bin\Debug\net10.0\Cufet.App.exe')
CHURN = '''
Define churn as a series of text.
Define tick as 0.
While tick is less than 400, repeat:
    Insert ("zzzzzzzz" joined to "yyyyyyyy") into churn.
    tick becomes tick + 1.
Done.
'''


def source(prog, chance):
    """The program as Cufet, with the reads at its end moved after a churn."""
    funcs, main = prog
    tail = 0
    while tail < len(main) and main[len(main) - 1 - tail][0] == 'use':
        tail += 1
    body, uses = main[:len(main) - tail], main[len(main) - tail:]
    text = show((funcs, body)) + '\n' + CHURN + '\n'.join(regions.show_body(uses, 0)) + '\n'
    return 'Define chance as %s permanently.\n%s' % ('true' if chance else 'false', text)


def run(args, cwd=None, timeout=60):
    try:
        r = subprocess.run(args, cwd=cwd, capture_output=True, text=True, encoding='utf-8',
                           errors='replace', timeout=timeout)
        return r.returncode, r.stdout.strip(), r.stderr.strip()
    except subprocess.TimeoutExpired:
        return -1, '', 'timeout'


def main():
    words = [a for a in sys.argv[1:] if not a.isdigit()]
    numbers = [int(a) for a in sys.argv[1:] if a.isdigit()]
    count = numbers[0] if numbers else 2000
    seed = numbers[1] if len(numbers) > 1 else 1
    search.TIGHT = 'tight' in words
    regions.HOME = 'forward'
    rng = random.Random(seed)
    work = tempfile.mkdtemp(prefix='cufet-against-')
    out_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'against-out')
    os.makedirs(out_dir, exist_ok=True)

    tried = unsafe = refused = accepted = shown = quiet = odd = 0
    started = time.time()
    for n in range(count):
        prog = search.Gen(rng).program()
        if is_safe(prog, {}):
            tried += 1
            continue
        tried += 1
        unsafe += 1
        path = os.path.join(work, 'p.cufe')
        open(path, 'w', encoding='utf-8', newline='\n').write(source(prog, True))
        code, said, err = run([CUFET, 'check', path])
        if code != 0:
            refused += 1
            if 'shorter-lived region' not in said + err and 'stores' not in said + err:
                odd += 1          # refused for some other reason: the generator wrote something the language refuses
                if odd <= 5:
                    print('  (refused for another reason, #%d: %s)' % (n, (said + err).splitlines()[0][-160:]))
            continue
        accepted += 1
        verdicts = []
        for chance in (True, False):
            open(path, 'w', encoding='utf-8', newline='\n').write(source(prog, chance))
            _, interp, _ = run([CUFET, path])
            exe = os.path.join(work, 'p.exe')
            if os.path.exists(exe):
                os.remove(exe)
            run([CUFET, 'build', 'p.cufe'], cwd=work, timeout=180)
            if not os.path.exists(exe):
                verdicts.append('did not build')
                continue
            code, got, _ = run([exe])
            if code not in (0, 1) and code != -1:
                verdicts.append('CRASH %#x' % (code & 0xffffffff))
            elif got != interp:
                verdicts.append('DIFFERS')
            else:
                verdicts.append('same')
        bad = any(v != 'same' for v in verdicts)
        shown += bad
        quiet += not bad
        name = '%s-%d-%d.cufe' % ('hole' if bad else 'candidate', seed, n)
        open(os.path.join(out_dir, name), 'w', encoding='utf-8', newline='\n').write(
            '/*  %s: the model says some run reaches freed memory; `check` accepts it.\n'
            '    chance true: %s; chance false: %s  */\n\n%s' % (name, verdicts[0], verdicts[-1], source(prog, True)))
        print('#%d accepted, model unsafe: %s -> %s' % (n, ', '.join(verdicts), name))
        sys.stdout.flush()
    print('programs %d, model-unsafe %d, refused %d (%d for another reason), ACCEPTED %d: %d shown wrong, %d ran right  [%.0f s, seed %d%s]'
          % (tried, unsafe, refused, odd, accepted, shown, quiet, time.time() - started, seed, ', tight' if search.TIGHT else ''))


if __name__ == '__main__':
    main()
