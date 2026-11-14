# Soundness of the region model

*Written 2026-11-13 against the checker and compiler as they stood at 0.29.0, and updated the same
day after the first repair.*

This document answers one question: **can a program that passes `cufet check` read memory after
the `Done.` that freed it?**

**Where it stands.** Yes, still, but less often than at 0.29.0. Nineteen small programs were found
that pass `cufet check`, run correctly interpreted, and read freed memory when compiled. They come
from four places where the rules assume something that is not true (section 4). Two of the four
have been closed. One was closed in the runtime, and six of the nineteen programs are right on
both backends because of it. One was closed in the checker, and five more are refused by `check`.
The other eight still read freed memory. Closing them takes two more checker rules, which refuse
programs, and that has not been done.

The rest of the document is in three parts.

1. **What is wrong.** Section 4 sets out the four obligations, with the programs that break each
   and what each does now. The programs are in [soundness/probes/](soundness/probes/).
2. **A repaired set of rules is sound, for a core of the language.** Section 5 gives the rules,
   section 6 proves that a program they accept never reaches freed memory.
3. **The proof was checked by machine as far as a search can check it.** A small executable model
   of the compiled runtime and of both sets of rules is in [soundness/model/](soundness/model/).
   Section 7 gives what it found, and section 9 what has been built from it and what is left.

---

## 1. The claim

DESIGN.md states the invariant as *a value may escape to a longer-lived region but never inward
to a shorter-lived one*. The interpreter does not need it: its values are garbage collected. The
compiled program does, so the property worth proving is about the compiled program:

> **Safety.** A program the checker accepts never reads or writes an object after the arena
> holding it has been freed.

## 2. What the compiled runtime does

The proof stands on five facts about the generated C. Each was read from the code generator, not
assumed. They are given as they were at 0.29.0, because that is the runtime sections 4 to 7 are
about. F4 has changed since, and the change is described under it.

| | Fact | Where |
|---|---|---|
| F1 | There is one stack of arenas per thread. `Pull a rabbit.` pushes one, `Done.` frees the top one and everything allocated in it. | `cufet_arena_push`, `cufet_arena_pop` |
| F2 | **A function call pushes no arena.** What a function allocates lands in whatever arena was on top when it was called. | `EnterFrame`: "a plain call pushes no arena" |
| F3 | A `Return` out of one or more rabbits does not free their arenas. It hands their blocks to the arena below. | `cufet_arena_merge_down` |
| F4 | A series is a pointer to a header, and the header points at a buffer. Binding or passing a series copies the pointer, so two names share one series. Growing a series allocates a **new buffer in whatever arena the allocator currently points at**. | `cser_N_new`, `cser_N_ensure` |
| F5 | The allocator points at the top arena, unless a store has set an override to a chosen depth for its own duration. | `cufet_alloc_override`, `EmitStoreWithEscape` |

F2 and F4 are the two that matter most. F2 means a depth written down inside a function is
relative to wherever the function happened to be called. F4 meant a series that was made outside
a rabbit could have its buffer moved into the rabbit by a single `Insert`.

**F4 now.** A series header, and a map header, records the arena it was made in, and growing it
allocates the new buffer in that arena whatever rabbit is open (`cufet_arena_here`,
`cufet_arena_alloc_in`). An arena is its own heap block, so it can be named; and an arena that a
`Return` merges into the one below (F3) stays behind as a stub that forwards to where its blocks
went, so a series carried out of a rabbit still finds the right place. Section 9 has the reason
it has to be the arena and not its depth.

## 3. The core language

The proof is about a small language that keeps what makes regions hard and leaves the rest out.

```text
type       number | series of number | series of series of number
value      5 | a name | a series of T          (a new, empty one)
             | item 1 of x | cast f on (x, ...)
statement  Define x as value.          x becomes value.
           Insert value into x.        Item 1 of x becomes value.
           Insert value into item 1 of x.
           Pull a rabbit. ... Done.    If ...: ... Otherwise: ... Done.
           Return value.               State x.          Cast f on (x, ...).
function   Bind T to f, given (...): ... Done.     first-order, sees no outer names
```

Kept: sharing of a series between names, series inside series, rabbits to any depth, functions
that are handed series and may store into them, functions that return series, a `Return` from
inside a rabbit, and a branch whose arms leave a name pointing at different things.

Left out, and so **not covered by the theorem**: text, records and objects, maps, function values
and what they capture, tasks and channels, failures and exceptions, loops, recursion. Section 8
says what each would need.

## 4. The checker's rules, and the four obligations they do not meet

The checker gives every name one number, the rabbit depth of its `Define`, and a parameter the
number 0. A store is refused when the value's number is greater than the target's
(`CheckRegionStore`). Only a series, map, object, matrix, channel or address can be refused; text,
and anything wrapped in a record, never is. When a value is not refused but is deeper than its
target, the compiler is told to copy it into the target's arena and to point the allocator there
for the store (`EscapeDepthFor`).

One number is asked to do two different jobs. When a name is **read**, the number has to be an
upper bound: the object is no deeper than this. When a name is **stored into**, the number has to
be exact: the object lives in precisely this arena, because that is where its new contents and its
new buffer must go. A number that is right as an upper bound is not right as a location, and most
of what follows is that one confusion.

Any set of rules that is going to be sound has to meet four obligations. At 0.29.0 none was met
completely. O1 and O3 are met now; O2 and O4 are not.

### O1. What is read must not be deeper than the checker thinks

*Met, in both checkers. What a call hands back is as deep as the call, unless all of it comes from
the function's parameters; `it` in a `Judge` is as deep as its subject. What follows is how it
was.*

*Broken by a call's result.* A function that makes a series and returns it is taken to return it
at depth 0. By F2 it was allocated wherever the call happened, which inside a rabbit is the
rabbit.

```text
Bind series of number to make-it:
    Return a series of number with (1, 2, 3).
Done.
Define outer as a series of number.
Pull a rabbit.
    outer becomes cast make-it on ().       accepted; outer dangles after Done.
Done.
```

The return-depth signature (DESIGN.md, hole #1) repairs the case where a function returns its
*parameter*. It does not help here, because nothing flows from a parameter, and it has gaps of its
own: the walk that computes it does not enter a `Judge`, does not see a parameter that was
`Insert`ed into the series being returned, and treats an object literal as fresh whatever is put
in it. *Also broken by a binder:* the `it` of a `Judge` arm is registered with no depth and so
reads as 0.

### O2. A store needs to know exactly where its target lives

*Not met for storing anything that is itself in an arena: a series, an object, a text. No longer
needed for growing a series or storing a number in it: a series finds its own arena now, so
nothing has to know where it is.*

*Broken by a second name.* `Define alias as keeper.` inside a rabbit gives `alias` the rabbit's
depth, though the series it names lives outside. A store through `alias` is then checked against
the wrong depth, and passes.

*Broken by a target that is not a plain name.* `Insert x into b's items` takes the target to live
at the current depth (`ContainerDepthOf`), which is the one depth that can never refuse anything.

*Broken by an element.* A row of a table can have been made further out than the table. The
`For each` name is given the table's depth and `item 1 of table` the current depth, and growing
the row then moved its buffer to an arena that ends before the row does. That one is fixed, since
a row grows where the row lives. Storing a *series* into such a row is still checked against the
wrong depth.

### O3. A series must grow in its own region

*Met, in the runtime (F4 now).*

At 0.29.0 a growing series got a new buffer wherever the allocator pointed, and the allocator was
only redirected when the checker thought the *value* was deeper than the target. So an outer
series, given an outer value, inside a rabbit, grew into the rabbit:

```text
Define word as "in" joined to "ner".
Define names as a series of text.
Pull a rabbit.
    Insert word into names.                 accepted; names' buffer was freed at Done.
Done.
```

Nothing here uses a function or a second name. The model found this one; it was not among the
programs written by hand.

### O4. What a function stores into its parameters is its caller's business

*Not met. A function that only appends numbers to what it is given is safe now, by O3. One that
stores a series, an object, or a text it made is not (`param-2`, `param-3`).*

Inside a function every parameter has depth 0 and so does every series the function makes. By F2
the second is really the caller's current arena and the first can be anything outside it. A
function that stores what it made into what it was given checks clean on its own and is unsafe
from any call site that sits inside a rabbit and passes something from outside.

```text
Bind void to push-into, given (the series of series of number bag):
    Define fresh as a series of number with (1, 2, 3).
    Insert fresh into bag.
Done.
Define keeper as a series of series of number.
Pull a rabbit.
    Cast push-into on (keeper).             accepted; keeper holds a freed series after Done.
Done.
```

Hole #2 in DESIGN.md is this obligation for one shape, a nested function storing a *captured*
parameter. A function storing its *own* parameter is the general case.

### The programs

Every row passes `cufet check` and gives the right answer interpreted. The third column is the
compiled program at 0.29.0 on Windows, over three runs of the whole set. Three of them were also
built under AddressSanitizer on Linux (`result-1`, `param-1`, `alias-2`), and each reported
`heap-use-after-free`, freed by `cufet_arena_pop`. The last column is the compiled program now.

| Probe | Breaks | At 0.29.0 | Now |
|---|---|---|---|
| `grow-1-outer-value-into-outer-series` | O3 | crashes, or wrong output | **right** |
| `grow-2-outer-text-into-outer-series` | O3 | wrong output | **right** |
| `alias-1-append-through-a-second-name` | O2 | wrong output | **right** |
| `element-1-grow-a-row-in-for-each` | O2 | crashes | **right** |
| `element-2-grow-an-item` | O2 | crashes | **right** |
| `param-1-helper-appends-numbers` | O4 | crashes | **right** |
| `alias-2-store-through-a-second-name` | O2 | crashes | crashes |
| `target-1-store-into-a-field` | O2 | crashes | crashes |
| `param-2-helper-stores-what-it-made` | O4 | crashes | crashes |
| `param-3-helper-stores-text-it-made` | O4 | crashes | wrong output |
| `param-4-method-stores-its-argument` | O4 | crashes | crashes |
| `param-5-method-stores-text` | O4 | wrong output | wrong output |
| `param-6-lambda-stores-its-argument` | O4 | crashes | crashes |
| `param-7-nested-function-stores-its-argument` | O4 | crashes | crashes |
| `result-1-fresh-series-from-a-call` | O1 | crashes | **refused** |
| `result-2-return-inside-a-judge` | O1 | crashes | **refused** |
| `result-3-parameter-wrapped-in-a-series` | O1 | crashes, or wrong output | **refused** |
| `result-4-parameter-wrapped-in-an-object` | O1 | crashes | **refused** |
| `binder-1-it-in-a-judge` | O1 | crashes | **refused** |
| `control-nothing-escapes` | nothing | same on both | same on both |

"Wrong output" is the worse result: the program exits 0. Which of the two a failing row shows is
chance, since both are a read of freed memory.

The five that are refused are fixtures too, as `escape-*.cufe`, with a sixth that returns its
parameter only from inside an `If` arm.

The six that are right were only ever growing something, and the three under O2 and O4 among them
show that the growth was all that was wrong with them. They are fixtures now, in
`tests/fixtures/soundness/` as `*-legal.cufe`. Three of them were run under AddressSanitizer
(`grow-2`, `element-1`, `param-1`), with a fourth program that returns series out of nested
rabbits and grows them afterwards, and all four are clean.

## 5. Repaired rules

These are rules for the core language of section 3, written for the runtime of 0.29.0. They need
**no change to that runtime**: they use the allocation override that already exists (F5), and
nothing else. On the runtime as it is now they ask for more than is needed, and section 9 says
which parts fall away.

### What the checker knows about a name

Write `b` for the arena that was on top when the current function was called, and `d` for the
number of rabbits open inside the function at the point being checked. The top arena is then
`b + d`. Call the arena an object's header sits in its **level**.

Every series-typed name has a **slot**, the value of `d` at its `Define` (0 for a parameter), and
a **region**, which is one of four claims about the level of the series it names now:

| Region | Claim |
|---|---|
| `here(k)` | exactly `b + k`: made by this function, `k` rabbits in |
| `within(k)` | no deeper than `b + k` |
| `param(i)` | it is the very series passed as parameter `i` |
| `under(P)` | no deeper than one of the parameters in the set `P` |

Each region has a **bound**, the deepest it can be relative to `b`: `k` for `here(k)` and
`within(k)`, and 0 for the other two, since whatever was passed in was alive at the call and so is
no deeper than `b`.

To **weaken** a region is to forget that it is exact: `here(k)` becomes `within(k)`, `param(i)`
becomes `under({i})`, the others stay. The **join** of two regions is the least claim both
satisfy: equal regions join to themselves, two parameter regions join to `under` of both sets, and
anything else joins to `within` of the larger bound.

### The rules

| | Construct | Rule |
|---|---|---|
| B1 | `a series of T` | Region `here(d)`. |
| B2 | `item 1 of x` | The region of `x`, weakened. |
| B3 | `Define x as v.` | Slot `d`. Region: that of `v`. |
| B4 | `x becomes v.` | Refused unless the bound of `v` is at most the slot of `x`. The region of `x` becomes that of `v`. |
| B5 | `Insert v into x.` and `Item 1 of x becomes v.`, where `x` is `here(k)` | Refused unless the bound of `v` is at most `k`. The store runs with the allocator pointed at depth `k`. |
| B6 | the same, where `x` is `param(i)` | Refused unless the bound of `v` is 0. The store runs with the allocator pointed at depth 0. Parameter `i` is recorded as one this function **stores into**. |
| B7 | the same, where `x` is anything else, and every store into `item 1 of x` | Refused. |
| B8 | `cast f on (a, ...)` | For each parameter `i` that `f` stores into: the argument must be `here(d)`, or else be `param(j)` with `d` = 0, and then `j` is recorded as one the caller stores into. Otherwise refused. |
| B9 | the result of that call | If `f` returns only series it made: `here(d)`. If it always returns its parameter `i`: the region of that argument. If what it returns is always reached from parameters in `P`: the join of those arguments' regions, weakened. Otherwise `within(d)`. |
| B10 | `If` | Each arm is checked from the same starting regions. Afterwards a name has the join of what the arms left it. |
| B11 | `Pull a rabbit.` ... `Done.` | The body is checked at `d + 1`. Names defined in it end with it. |
| B12 | `Return v.` | Recorded for B9: `here(k)` as "made here", `within(k)` as "no deeper than the caller", the parameter regions as themselves. A function's summary is the join over its returns. |

In B5 and B6 the value is worked out first and the allocator is pointed afterwards, so no call
ever runs with the allocator redirected.

Against section 4: O1 is B1, B2 and B9, which never report a level shallower than the truth. O2 is
B5 to B7, which store only where the level is exact. O3 is the allocator clause of B5 and B6. O4
is B6 with B8.

Eight of the probes can be written in the core language, and `witnesses.py` runs them. Two of
them were never wrong programs, only wrongly allocated ones: these rules **accept** `grow-1` and
`alias-1` and run them safely, because B5 sends the growth home. The other six
(`alias-2`, `element-2`, `param-1`, `param-2`, `result-1`, `result-3`) are refused. The same
functions called where they are safe, at the depth of the series they are given, are accepted.

## 6. The theorem

### The state

A state of a running program is: the stack of arenas, with levels 0 to `t`; the objects, each a
series with a level, a buffer level and a list of elements; and the stack of function frames, each
with its base `b`, its open rabbits, and its names. For a frame that is waiting on a call, read
`d` as the depth of that call, so the frame above has base `b + d`.

A state is **well formed** when all of these hold.

- **W1, buffers stay home.** Every object's buffer is at the object's own level.
- **W2, pointers go down.** If a series holds another, the one held is at the same level or a
  shallower one.
- **W3, slots.** In every frame, the series a name holds is no deeper than `b` + its slot, and its
  slot is at most the frame's `d`.
- **W4, regions are true.** In every frame, each name's region, as the checker computed it for the
  point the frame has reached, is a true claim about the series the name holds.
- **W5, stored-into parameters are at the base.** In every frame, each parameter the function
  stores into was passed a series at level exactly `b`. Every parameter was passed a series no
  deeper than `b`.

**Lemma 1 (nothing dangles).** In a well-formed state every object reachable from any name in any
frame is at a level between 0 and `t`, so is alive.

*Proof.* A name in the running frame holds a series at level at most `b` + slot ≤ `b + d` = `t`,
by W3. A name in a waiting frame holds one at most at that frame's `b + d`, which is the base of
the frame above and so at most `t`. Everything reached from there is no deeper, by W2. Arenas 0 to
`t` are exactly the ones not freed. ∎

**Lemma 2 (bounds).** In a well-formed state, a value whose region has bound `k` is at level at
most `b + k`, and `k` ≤ `d`.

*Proof.* For `here` and `within` the first part is W4. For `param(i)` and `under(P)` the bound is
0, and by W5 every parameter is at level at most `b`. For the second part: B1 gives `d`; a name's
region was given by B3 or B4, and both leave its bound at most its slot, which is at most `d` by
W3; B2 does not raise a bound; B9 gives `d` or a join of arguments' regions; B10 takes a
maximum. ∎

### The steps

**Lemma 3 (values).** In a well-formed state, working out a value the checker accepted reaches no
freed object, leaves the state well formed, and gives a value for which the region the checker
computed is true.

*Proof.* A name: Lemma 1, and W4. A new series: it is allocated at `t` = `b + d` with no
override in force, so `here(d)` is true, and it holds nothing, so W1 and W2 are untouched.
`item 1 of x`: the series `x` holds is alive by Lemma 1, and by W2 its first element is no deeper
than it is. If `x` is `here(k)` or `within(k)` the element is at most `b + k`, which is
`within(k)`. If `x` is `param(i)` the element is no deeper than parameter `i`, which is
`under({i})`; `under(P)` likewise. A call: Lemma 5. ∎

**Lemma 4 (statements).** If a well-formed state runs one statement the checker accepted, it
reaches no freed object and the state after is well formed, with the regions the checker computed
for the point after the statement.

*Proof,* by cases, using Lemma 3 for the value first.

*Define.* The new name has slot `d`, and any live series is at most `t` = `b + d`: W3. Its region
is the value's: W4.

*Becomes (B4).* The value is at most `b` + its bound by Lemma 2, and B4 made that at most `b` +
the slot of `x`: W3. Its region is the value's: W4. No object changed.

*A store into `here(k)` (B5).* The target is at level exactly `b + k` by W4, and is alive. The
value is at most `b` + its bound ≤ `b + k`, so putting it in the target keeps W2. If the series
grows, the new buffer is allocated at depth `k`, which is the target's own level: W1. That arena
exists because `k` ≤ `d` by Lemma 2. No name's region changed, and no level changed, so W3 to W5
stand.

*A store into `param(i)` (B6).* B6 recorded `i` as stored into, so by W5 the target is at level
exactly `b`. The value has bound 0, so it is at most `b`: W2. Growth is allocated at depth 0,
which is `b`: W1.

*A rabbit (B11).* The push makes `t + 1` the top and the body runs at `d + 1`, well formed by
induction on the body. At `Done.` the names defined inside are gone. Every remaining name of this
frame has slot at most `d`, so holds a series at most at `b + d` = `t − 1` by W3; names of waiting
frames hold series at most at this frame's `b`; and by W2 nothing at level `t − 1` or below holds
anything at level `t`. So no remaining name reaches arena `t`, and freeing it frees nothing
reachable. The remaining regions are claims about levels at most `t − 1`, which did not move.

*If (B10).* The arm that ran leaves its regions true by induction. A join is a weaker claim than
either side, so the joined regions are true as well.

*Return (B12).* By F3 each arena of this frame above `b` is merged into the one below, so every
object above level `b` is now at `b` and no other level moved. Moving levels this way cannot turn
"same or shallower" into "deeper", so W1 and W2 stand. The frame is gone. Waiting frames hold only
series at most at `b`, untouched.

*State, and a call written as a statement.* `State x` reads only what Lemma 1 says is alive. A
call is Lemma 5. ∎

**Lemma 5 (calls).** Suppose a well-formed state calls `f`, accepted by the checker, with
arguments accepted by B8. Then the call reaches no freed object; when it returns the state is well
formed for the caller; and the region B9 gives the result is true.

*Proof.* The new frame has base `b′` = `t`. Each argument is alive, so at most `b′`. Each
parameter has slot 0 and region `param(i)`, true by construction: W3, W4. For W5, take a
parameter `i` that `f` stores into. B8 required the argument to be `here(d)`, which by W4 is at
level `b + d` = `t` = `b′`; or to be the caller's `param(j)` with `d` = 0, in which case `j` was
recorded as stored into by the caller, the caller's own W5 puts it at the caller's `b`, and
`b′` = `b + 0`. So the callee starts well formed, and by Lemma 4 on each statement of its body it
stays so, by induction on the length of the run. A recursive call is one more step of the same
run, which is why the induction is on the run and not on the shape of the program.

On return, the top is `b′` again (a `Return` merges, an ordinary end leaves no rabbit open). No
object that existed before the call changed level. The caller's names hold what they held. So W3
and W4 hold for the caller as before, and W1, W2 held throughout.

The result. If every `Return` in `f` gave a `here(k)` value, the value was at `b′ + k` and the
merge put it at `b′` = `b + d`: `here(d)`. If every `Return` gave `param(i)`, the result is the
argument itself, with the argument's region. If every `Return` gave a value under parameters in
`P`, it is no deeper than one of those arguments, which is the join of their regions weakened.
Otherwise it was alive at the return, so at most `b′`: `within(d)`. ∎

**Theorem.** A core program accepted by rules B1 to B12 never reads or writes a freed object.

*Proof.* The starting state, with one arena, one frame and no names, is well formed. By Lemmas 4
and 5, every state the run reaches is well formed and no step reaches a freed object. ∎

### What the proof depends on

Facts F1 to F5, and one thing asked of the compiler: that the allocator override is in force only
while the store itself runs, never while the value being stored is worked out. Today's compiler
does not do that. For `Insert`, the value's expression is emitted inside the statement that
`EmitStoreWithEscape` wraps, so a call in it runs with the allocator redirected, and what the
callee thinks it made "here" lands somewhere shallower. B1 and B9 are false of such a call, so the
value would have to be worked out into a temporary first.

On the runtime as it is now, growth no longer uses the override at all, so rules B5 and B6 would
not set it and this dependency goes. The override is still what carries an escape *copy* to its
destination, and a call inside a store still runs under it.

## 7. The mechanical check

A proof on paper can be wrong in the same way the rules it replaces were wrong, so the model in
[soundness/model/](soundness/model/) tests it three ways.

`regions.py` holds a machine that follows F1 to F5, and two checkers: **A**, today's rules as
section 4 describes them, and **B**, the rules of section 5. `search.py` generates random
well-typed core programs, asks each checker whether it accepts, and runs every accepted program
down every path of its `If`s, once with every growth reallocating and once with none. After every
statement it walks everything reachable from every name in every frame. Reaching a freed object
is a failure.

**The search.** Three runs of 400,000 programs each. "Tight" is a second generator that uses few names and mostly reuses them, so that statements act on each other more often.

| | ordinary, seed 7 | ordinary, seed 11 | tight, seed 13 |
|---|---:|---:|---:|
| programs generated | 400,000 | 400,000 | 400,000 |
| accepted by A (today) | 359,735 | 359,401 | 381,502 |
| accepted by A, and unsafe | 6,801 | 6,780 | 4,617 |
| accepted by B (repaired) | 312,890 | 312,479 | 344,559 |
| **accepted by B, and unsafe** | **0** | **0** | **0** |
| accepted by B, with a rabbit in it | 285,526 | 285,078 | 308,642 |
| accepted by B, with a function storing into a parameter | 89,970 | 89,417 | 60,954 |

The last two rows are there so that "B accepted nothing unsafe" cannot be read as "B accepted
nothing interesting".

**Does the model match the real compiler?** Checker A in the model accepts unsafe programs of the
same shapes as the probes of section 4, found without being told where to look. One of them, an
outer value inserted into an outer series inside a rabbit, was found by the model first and
confirmed on the real compiler afterwards (`grow-1`, `grow-2`).

**Is each rule needed, and could the search see it missing?** A search that finds nothing proves
little unless it can be shown to find something. So each rule of B was removed in turn, and the
search run again. The number is how many random programs it took before one was accepted and unsafe (seed 5).

| Rule | Removed | First unsafe program accepted: ordinary | tight |
|---|---|---:|---:|
| B5 | a store into a series made here does not compare levels | 14 | 57 |
| B5, B6 | growth is not sent to the series being grown | 3 | 7 |
| B6 | a store into a parameter is not recorded for the caller | 19 | 41 |
| B7 | a store is allowed where the level is not known exactly | 1,253 | 4,526 |
| B8 | a stored-into parameter may be passed from any depth | 19 | 41 |
| B9 | a fresh result is taken to be at depth 0, as today | 23 | 644 |
| B2 | an element of a parameter is taken to be the parameter | 42,569 | not in 400,000 |
| B2 | an element is taken to live exactly where its series does | 61,276 | 26,134 |
| B4 | a reassignment does not compare the value against the slot | 23 | 161 |
| B10 | after an `If`, the first arm is believed | 345,745 | 42,891 |

Every removal lets an unsafe program through, and the search finds it. The rarer ones are also pinned by directed programs in `witnesses.py`, each refused by B and accepted-and-unsafe once its rule is gone. The numbers say how hard a search has to look: a rule that takes 40,000 programs to miss is one that 400,000 programs test about ten times.

The limits of this check are the limits of a search: it covers small programs in the core
language, it is not exhaustive, and it shares the model's reading of the runtime. It is evidence
for the proof, not a replacement for it.

## 8. What is not covered

The theorem is about the core. For each thing left out, this is what a proof would have to add.
None of it has been done.

- **Text.** Immutable, so it cannot be stored *into*. What applies is O1 and the copy: a text is as
  deep as where it was made, and a text stored outward has to be copied. `param-3` and `param-5`
  show the copy missing where a function stores a text into its parameter. (`grow-2` was a series
  of text growing in the wrong place, and is fixed.)
- **Records and objects.** They are C structs copied by value, so a field that is a series behaves
  as a second name for it. A store into a field is a store into a target that is not a plain name
  (O2), and a method's receiver is a parameter (O4).
- **Maps.** A series with a second buffer; the same four obligations.
- **Function values and captures.** A captured name is a parameter that does not appear in the
  parameter list, so O4 applies to it. Holes #2 and the closure-escape rules in DESIGN.md are the
  existing work here.
- **Loops.** A loop is an `If` taken many times: the regions after it are the fixed point of
  joining the body's result into its start. Regions are bounded by the nesting depth and the
  number of parameters, so the fixed point is reached. The `For each` name is an element, which B2
  weakens.
- **Recursion.** Lemma 5 already goes through. The summaries of B6 and B12 for a function that
  calls itself are a fixed point too.
- **Failures, exceptions, `Stop`, `Skip`.** Each leaves rabbits early and frees their arenas. The
  rabbit case of Lemma 4 covers a free at any exit, provided the exit frees exactly the arenas of
  the rabbits it leaves. The "does not drift arena depth" tests pin that.
- **Tasks and channels.** Values cross by copy. The obligation is that the copy is complete and
  lands in the receiving thread's arena.

## 9. What the repaired rules cost, and what was built instead

The rules of section 5 were chosen to need no runtime change, and that has a price. On the runtime
of 0.29.0 they refuse three things that are ordinary:

1. **Calling a function that stores into its parameter, from inside a rabbit, on a series from
   outside.** This was `param-1`. It was unsafe, and with that runtime there was no safe way to
   compile it, so the refusal was correct.
2. **Growing or storing into an element.** `Insert 5 into item 1 of table`, and `Insert 5 into row`
   inside `For each row in table`. The checker cannot know where an element lives, only that it
   is no deeper than the series it came from (`element-1`, `element-2`).
3. **Storing into a series whose region was joined across an `If`.**

In the model's random programs, B refuses about one program in six that would in fact have run
safely. The rules of 0.29.0 refuse about one in sixteen, and accept unsafe ones.

The second refusal is the one that would hurt real programs: a table of rows is the commonest
nested shape there is. It comes entirely from O3. If the series header records which arena it was
allocated in, a growing series can always put its new buffer there, and O3 is met by the runtime
instead of by the checker. B5 and B6 then need exact levels only for stores of *series into
series*; a number can be stored into any series at all.

That variant was put in the model first (`regions.HOME`), and the search run on it, 60,000
programs, seed 21:

| Runtime | B accepts | B accepts, and unsafe | B refuses, though safe as written | A accepts, and unsafe |
|---|---:|---:|---:|---:|
| 0.29.0 | 46,804 | 0 | 9,032 | 1,034 |
| header remembers its arena, a merged arena forwards | 53,374 | **0** | 3,865 | 212 |
| header remembers only its depth | 53,374 | **82** | 3,864 | 294 |

Two things to read off it. The runtime change alone removes about four fifths of what the rules
of 0.29.0 get wrong, and no program is refused for it. And the cheap version of it is unsound: a
`Return` out of a rabbit merges that rabbit's arena into the one below (F3), so a series made
inside and returned is no longer at the depth it remembers, and its next growth inside some later
rabbit lands in that rabbit. The header has to name the arena, and a merged arena has to forward
to the one it was merged into.

### What was built

The second row. Both compilers give a series and a map header its arena and grow it there, and
`cufet_arena_merge_down` leaves a forwarding stub. That is "F4 now" in section 2, and it is why six
probes are right. Nothing was refused that was accepted before.

It is checked by the model's search and by the test suite, **not proved**. W1 holds by
construction on this runtime and the rest of section 6 should go through unchanged, but that has
not been written out.

Then O1, in both checkers, which is rule B9 fitted to the checker as it is. The checker keeps its
one number per name; a function's return-depth signature gained a third kind of entry beside "this
parameter" and "the receiver", meaning "something the function made, or anything not traced to a
parameter". A call whose signature holds it is as deep as the call. The walk that builds the
signature no longer names the statements it enters: it reaches every `Return`, `Define` and
`becomes` of a body and stops only at a body that returns for itself, and anything it cannot trace
counts as made. That is coarser than B9, which can tell "exactly here" from "no deeper than here",
and the difference costs nothing until a store needs the exact answer, which is O2.

What it refused that was accepted before, measured: none of the 229 programs in the repository,
and three of the 1,128 hand-written programs the two checkers are compared on. All three were
the hole itself, written down earlier as accepted: a series a call made inside a rabbit, inserted
into a series outside it.

### What is left

The rest of the checker half: O2 for stores of a series or an object, and O4. On the runtime as
it is now that is rules B5 to B8, and B5 to B7 only where the value stored is itself a series. Of the three refusals above, the first then applies only to a function that
stores a series or an object into its parameter, and the second only to storing one into an
element. Appending a number is refused nowhere.

Text is outside the core and needs its own line here. It is never refused, only copied, and it is
copied only where the checker sees it escape. A text made inside a function and stored into a
parameter is not seen to (`param-3`, `param-5`), so O4 has a second half for text: the copy has to
happen at such a store as well.

These rules refuse programs that are accepted today, so they are a change to the language. The
one question that was open in them is settled: a function called inside a rabbit that returns a
series it made, kept in a name outside, is refused and not copied out. Copying would keep the
program and make the name outside a different series from the one the function returned, which
the interpreter would not show.

## 10. Running it again

```text
pwsh -File docs/soundness/probes/run.ps1             the nineteen probes, on the installed cufet
pwsh -File docs/soundness/probes/run.ps1 -Cufet <path>     the same, on a build of your choosing
python docs/soundness/model/search.py 400000 7       both checkers over 400,000 programs, seed 7
python docs/soundness/model/search.py mutants 400000 5
python docs/soundness/model/search.py 60000 21 forward   the same, on a runtime where a series grows at home
python docs/soundness/model/witnesses.py             the directed programs, and the probes in the core
```

A probe is fixed when `check` refuses it or the compiled program agrees with the interpreted one.
The eleven that are fixed are also in `tests/fixtures/soundness/`: six as `*-legal.cufe`, five as
`escape-*.cufe`. The eight that are not stay out of it: that folder's tests require every
`escape-*` file to be refused, and these are not refused yet.
