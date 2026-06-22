# Changelog

## 0.6.0 — 2026-06-21

Two arcs shipped together: the **standard-library / books layer** and a **type-system expansion** (union types, narrowing, heterogeneous collections).

### Standard library: books and the `collections` book

- `Pull a book on math.` — loads the math book under the name `math` (or a
  custom alias: `Pull a book on math as the m.`). Provides `math's floor of x`,
  `math's power of (2, 3)`, `math's pi`, etc. Partial functions return
  `voidable number` (e.g. `math's square root of (-1)` → `void`).
- `Pull a book on collections.` — loads the collections book, making `matrix`
  nameable and constructable in that scope.
- Book-member possessive/`of` access; multi-word member names (`absolute value`,
  `square root`); single-arg vs. multi-arg `of (...)` call forms.

### Matrix type (via `collections` book)

- `a matrix with ((1, 2), (3, 4))` — rectangular literal; all elements must be
  numbers; ragged rows are a static error.
- `a matrix with <rows> by <columns>` — sized constructor (all cells zero).
- `a matrix with <rows> by <columns> filled with <value>` — sized with fill.
- `the item at (row, column) of m` — 1-based access.
- `the rows of m` / `the columns of m` — dimension accessors (no pull needed).
- `cast collections's transpose of (m)` — non-mutating transpose.
- `MatrixType` singleton; type annotation `the matrix m` (parameter) / `matrix`
  (return).

### Union types

- `(A or B or C)` — closed union type (parenthesized, `or`-separated).
- Open union — `a catalogue` / `an atlas` with no type annotation.
- Type-agnostic operations (assignment, equality, pass/store) legal on
  un-narrowed unions; type-specific operations are a static error pointing to
  `is a <type>`.

### Narrowing

- `is a <type>` / `is not a <type>` — runtime type-test, generalizing `is void`.
- **In-branch narrowing** — value is narrowed to the tested type inside the
  branch; type-specific operations legal there.
- **Narrowing by elimination** (closed unions) — `Otherwise` arm after all-but-
  one cases automatically narrows to the remaining case.
- Open union `Otherwise` tails remain un-narrowable (agnostic-only).

### Atlas and catalogue (heterogeneous collections)

- `a catalogue of (number or text)` / `a catalogue` — heterogeneous series.
- `an atlas from text to (number or text)` / `an atlas` — heterogeneous map.
- All existing series / map operations apply; `Add` and value-set enforce the
  declared union type.
- Atlas retrieval yields `voidable (union)` — composes cleanly with existing
  voidable machinery.

### Tests

873 → 1011 (+138 tests across books, matrix, union types, narrowing, atlas, catalogue).

---

## 0.5.0 and earlier

See [ROADMAP.md](ROADMAP.md) for the full history of what was built and when.
