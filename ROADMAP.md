# Cufet Roadmap

Cufet 0.1.0 is the complete core: imperative language, static type system, and
first-class functions. Everything below is planned but not yet built. Items are
grouped by kind, roughly ordered within each group, but not strictly sequenced.

---

## Planned features

### Language

- **Text operations** — Cufet has `text` values but no way to join or manipulate them. `+` is deliberately *not* overloaded for concatenation, so text-joining needs its own construct (a word, e.g. `join`, or a `followed by` form). To design.

- **Text and general ordering via a `by` modifier** — ordering comparisons currently work on numbers only. Extend ordering to text (and other dimensions) with an explicit basis modifier rather than new operators or a hidden default: `is less than X by length`, `is greater than X by character code`. The basis is always stated, which avoids undefined-collation problems (case / locale / Unicode become named bases, not silent assumptions). Generalizes past text to any orderable dimension (e.g. a series `by size`). Undesigned in detail; this is the intended shape.

- **Constant declarations** — Cufet is mutable-by-default (a `Define`d value can be reassigned with `becomes`). Add an optional, explicit way to declare a value that cannot change, for the cases that deserve protection. Backward-compatible to add. Form undecided.

- **Range** — a way to produce a series of consecutive numbers without building it by hand (e.g. 1 to 100), so for each n in 1 to 100 works. Hit when writing iteration over a numeric span. Small; likely sugar that produces a normal series.

### Types and data structures

- **Records** — named, typed fields grouped together (e.g. `a person with a name (text) and an age (number)`). This is the primary intended way to hold heterogeneous data — fields are named and individually typed, so it stays fully statically typed (no `any`-shaped hole). Solves "different types together" without weakening the type system. Likely the next major type after the core.

- **Maps** — typed key→value collections (e.g. a map from text to numbers). The natural structure for lookups like word→frequency. Fully typeable (keys one type, values one type).

- **Heterogeneous data is served by records and maps, not by heterogeneous series.** Series stay homogeneous. An anonymous, mixed-type ("any") collection is intentionally *not* planned — records and maps cover the real needs without breaking static, strong typing.

### Functions

- **Closures** — functions that capture variables from an enclosing scope. Currently a returned or passed function can only be a top-level function referenced by name (no captured state), so a "specialized" function (the classic `make-adder` that captures its argument) is not yet expressible. Closures are forced only by **nested function declarations** or **anonymous/inline function literals**, neither of which exists yet — so adding either is what brings closures into play. Closures are what make function-return genuinely powerful.

- **Anonymous / inline functions (lambdas)** — function literals written inline rather than declared at top level with `Bind`. Tied to closures (an inline function that references its surroundings captures them).

### Possible, further out

- **Objects** — only noted because it would be the point at which the type system might need **variance / subtyping** in function-signature matching (currently exact-match only, which was chosen deliberately and is sufficient without a type hierarchy). Not committed; flagged so the exact-match decision is understood as expandable if a hierarchy ever arrives.

### Tooling

- **Style linter** — a layer separate from the parser that flags legal-but-unclear code with recommendations (warnings, not errors). First intended rule: **warn on nested bare-`it` loops** (shadowing is legal and well-defined — innermost wins — but a reader may lose track, so the linter would suggest naming iterators when nesting). The style guide also recommends capitalizing the start of a statement, which the parser does not enforce — the linter is the natural home for that kind of guidance.

---

## Known minor issues

- **Nested-function-type parameter placeholder names** — in a function-type annotation, a *nested* function-type parameter requires a placeholder name that is parsed and discarded (the name is meaningless there, since it's describing a type, not declaring a usable parameter). Minor syntax wart. Acceptable for now; revisit only if nested function types become common (they likely won't).

---

## Maintenance notes (for future development)

- **`CufetType` equality is explicit, not record-automatic.** The type records were converted from C# `record` to `class` with hand-written `Equals` / `GetHashCode` so that `FunctionType` can do deep (`SequenceEqual`) comparison of parameter-type lists — required for exact signature matching. **Any new type kind (records, maps, etc.) must implement `Equals` / `GetHashCode` correctly, including deep equality for any collection members, or exact-match silently breaks.**

- **The type checker is single-pass and will need to become multi-pass when use-before-declaration is allowed for functions.** Currently functions are top-level and hoisted in a static pass-1 scan, which already supports forward references and recursion. If/when nested functions, or any genuinely forward-referencing construct beyond the current hoist, are added, the checker converts to multi-pass. **The right multi-pass shape depends on a design choice:** because Cufet signatures have *explicitly declared* parameter and return types (not inferred-across-calls), the conversion is a clean "gather signatures, then check bodies" two-pass — *not* Hindley-Milner unification. Preserve explicit signature types to keep this cheap.

- **Recursion depth (`MaxCallDepth`) vs. native stack** — the language enforces a graceful recursion limit (default 1000) that fires a kind "missing base case?" error before the native stack overflows. The interpreter runs on a dedicated large-stack thread (16 MB) so that limit is reachable gracefully; the test harness does the same via `RunOnLargeStack`. Keep the *language* recursion limit decoupled from any host/test stack size — a test environment's stack must never silently dictate the language's recursion ceiling (this caused a false-positive recursion error early on).

- **Possible static-coverage gap in ToNumber** — The runtime ToNumber check fires for non-number arithmetic — verify whether the type checker should catch all such cases statically, or whether this is a genuine runtime backstop for SeriesPending/unresolved paths. Currently flagged with a code comment; investigate whether the static checker has a hole here. 

---

*Cufet is pre-1.0. The language may still change. Versioning is semantic: features bump the minor version, and 1.0.0 will mark the point at which the language is considered stable.*