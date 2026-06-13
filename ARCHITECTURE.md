# Cufet Architecture Map

**Purpose:** Post-compact navigation index. Read this file instead of whole source files.
Targeted `Read` with `offset`/`limit` replaces broad re-reads.

**Maintenance contract:** Updating this map is part of "done" for every feature — same commit
as the code change. A stale line-number map is worse than no map (it sends readers to the wrong
place). When a feature adds or moves code, update its entry here before closing the PR.

---

## File Inventory

| File | Lines | Role |
|------|-------|------|
| `src/Lexer/TokenType.cs` | 101 | Token enum — all keyword/symbol constants |
| `src/Lexer/Lexer.cs` | 256 | Source → token stream; keyword switch in `ReadWord` |
| `src/Interpreter/Ast.cs` | 181 | All AST node record types |
| `src/Interpreter/Parser.cs` | 1613 | Token stream → AST |
| `src/Interpreter/TypeChecker.cs` | 1616 | Static type checking; also holds `CufetType` hierarchy |
| `src/Interpreter/Interpreter.cs` | 917 | AST → execution |

Lexer, Ast, and TokenType are small enough to read in full when needed.
Parser, TypeChecker, and Interpreter require targeted reads — use this map.

---

## TokenType.cs — Token Enum

Small file; read in full when adding tokens. Section comments label each feature area:
- Core keywords (State/Define/As/Becomes/Dot/Colon): L13–16
- Control flow (If/Otherwise/Done): L18–20
- Arithmetic operators: L23–24
- Comparison symbols: L27–30
- Comparison word-form (Is/Not/Greater/Less/Than/Or/And/More): L32–41
- Loops (Comma/While/Repeat/Until/Stop/Skip): L43–49
- Collections — literal (Series/Record/With/Like): L51–55
- Objects & Interfaces (Object/Interface/New/One/Possessive/LBrace/RBrace): L57–64
- For-each (For/Each/In): L66–70
- Collections — operations (Ordinal/Item/Of/NumberKw/Add/To/Start/After/Remove/From): L72–81
- Functions (Bind/Cast/Given/Return/Void/On/FunctionKw): L83–90
- **Text operations (LengthKw/Joined/Converted): L92–95** ← Slice 1
- Parser-generated sentinel (NotEqual): L97–98

---

## Lexer.cs

Small file; read in full when touching lexer. Key methods:
- `Tokenize()`: L16–52 — main loop; dispatches to ReadWord/ReadNumber/ReadString/ReadSymbol
- `ReadWord()`: L54–158 — keyword switch at L83–149; text op entries at L146–148
- `ReadSymbol()`: L160–187 — single-char and digraph symbols (`<=`, `>=`)
- `ReadPossessive()`: L189–198 — `'s` token
- `ReadNumber()`: L200–213 — decimal literals; `.` consumed as decimal point only when followed by digit
- `ReadString()`: L216–239 — `""` as escaped quote

---

## Ast.cs — AST Nodes

Small file; read in full when adding nodes or checking an existing node's shape. Sections:

- Core interfaces (`IExpression`, `IStatement`) + scalar nodes: L5–12
- Series nodes (SeriesLiteral/SeriesAccess/SeriesLength/SeriesAddStatement/
  SeriesRemoveAtStatement/SeriesRemoveValueStatement/SeriesSetStatement): L14–48
- Record nodes (RecordLiteral/RecordNamedAccess/RecordNamedSetStatement): L50–67
- Basic statement nodes (StateStatement/DefineStatement/BecomesStatement/ConditionArm/
  IfStatement/WhileStatement/RepeatUntilStatement/StopStatement/SkipStatement/
  ForEachStatement): L69–99
- Function nodes (BindStatement/CastExpression/CastStatement/ReturnStatement): L100–129
- Object & Interface nodes (InterfaceDefinition/ObjectDefinition/ObjectLiteral/
  PossessiveAccess): L131–165
- **Text operation nodes (TextJoin/TextConvert/TextLength): L167–175** ← Slice 1
- Misc (PossessiveSetStatement/Program): L177–181

---

## Parser.cs (1613 lines)

### Core — touch these on almost any task

| Section | Lines | Notes |
|---------|-------|-------|
| `ParseStatement` dispatch | L28–53 | Add a new `TokenType.X => ParseX()` case here for new statements |
| `IsFieldNameToken` | L1283–1306 | **Exclusion list for `forAccess: true`** at L1303 — add new keyword tokens here if they could collide with named record access (`the X of y`) |
| `IsNamedAccessPattern` | L1270–1278 | Lookahead: `the <name> of` → true |
| `IsNamedFieldStart` | L1253–1265 | Lookahead: `the <name> <not-of>` → true |
| `ParsePrimary` (full) | L1047–1190 | Expression leaf + postfix loops (possessive L1162–1170, converted L1174–1187) |
| `SkipNoise` / `Consume` / `Advance` / `Peek` / `PeekAfterCurrent` | L1580–1611 | Parser primitives |

### Expression precedence chain (top → bottom = loosest → tightest)

```
ParseExpression  L944  → ParseExprOr
ParseExprOr      L946  → ParseExprAnd  ( "or" ParseExprAnd )*
ParseExprAnd     L958  → ParseExprNot  ( "and" ParseExprNot )*
ParseExprNot     L970  → ParseComparison  |  "not" ParseExprNot
ParseComparison  L981  → ParseJoinedTo  ( = < > <= >= ParseJoinedTo )*
ParseJoinedTo    L997  → ParseAddition  ( "joined to" ParseAddition )*    ← Text Slice 1
ParseAddition    L1013 → ParseMultiplication  ( + - ParseMultiplication )*
ParseMultiplication L1025 → ParseUnary  ( * / % ParseUnary )*
ParseUnary       L1037 → ParsePrimary  |  "-" ParseUnary
ParsePrimary     L1047 → literals, variables, parens, series ops, "the length of", postfixes
```

New precedence levels go between `ParseComparison` and `ParseAddition` (see `ParseJoinedTo`
as the pattern). Update `ParseComparison`, `ParseSingleCondition`, and `ParseWordComparison`
to call the new method instead of what they currently call.

### Condition grammar (If / Otherwise if context)

| Method | Lines | Notes |
|--------|-------|-------|
| `ParseCondition` → `ParseLogicalOr` | L828 | Entry point |
| `ParseLogicalOr` | L830–841 | `( "or" ParseLogicalAnd )*` |
| `ParseLogicalAnd` | L843–854 | `( "and" ParseCondNot )*` |
| `ParseCondNot` | L856–865 | `"not" ParseCondNot | ParseSingleCondition` |
| `ParseSingleCondition` | L867–879 | calls `ParseJoinedTo`; then optional `is` → `ParseWordComparison` |
| `ParseWordComparison` | L881–930 | handles `is not`, `is greater than`, `is less than`, `is N or more/less`, `is N` |

When adding a new expression precedence level, update `ParseSingleCondition` L870 and
`ParseWordComparison` L889/897/905/912 to call the new top-of-chain method.

### Per-feature parse methods

| Feature | Method | Lines |
|---------|--------|-------|
| Define statement | `ParseDefineStatement` | L65–83 |
| Object definition | `ParseObjectDefinition` | L86–143 |
| Interface definition | `ParseInterfaceDefinitionBody` | L147–177 |
| Interface method sig | `ParseInterfaceMethodSig` | L182–221 |
| Object literal (`new T{...}`) | `ParseObjectLiteralExpr` | L224–252 |
| Series literal | `ParseSeriesLiteralExpr` | L254–309 |
| Type annotation | `ParseTypeAnnotation` | L314–354 |
| Record shape `with(...)` | `ParseRecordShapeAnnotation` / `ParseRecordShapeBody` | L359–413 |
| Becomes statement | `ParseBecomesStatement` | L415–429 |
| Possessive set (`x's f becomes`) | `ParsePossessiveSetStatement` | L438–451 |
| If / Otherwise if | `ParseIfStatement` | L453–482 |
| If body (comma vs colon) | `ParseIfBody` | L488–505 |
| While loop | `ParseWhileStatement` | L507–524 |
| Repeat-until loop | `ParseRepeatUntilStatement` | L544–560 |
| For-each loop | `ParseForEachStatement` | L596–625 |
| Series access target | `ParseAccessTarget` | L631–654 |
| Ordinal → index | `OrdinalToIndex` | L657–672 |
| Named record set (`the x of r becomes`) | `ParseRecordNamedSetStatement` | L677–693 |
| Series set (ordinal/item index) | `ParseSeriesSetStatement` | L695–705 |
| Series add | `ParseSeriesAddStatement` | L707–763 |
| Series remove | `ParseSeriesRemoveStatement` | L765–808 |
| Bind (function declaration) | `ParseBindStatement` | L1310–1359 |
| Return type annotation | `ParseReturnType` | L1364–1386 |
| Parameter (typed) | `ParseParameter` | L1392–1431 |
| Function param type list | `ParseFunctionParamTypeList` | L1470–1490 |
| Function body | `ParseFunctionBody` | L1493–1505 |
| Cast statement wrapper | `ParseCastStatementWrapper` | L1507–1513 |
| Cast expression | `ParseCastExpression` | L1519–1561 |
| Return statement | `ParseReturnStatement` | L1563–1578 |
| Record literal | `ParseRecordLiteralExpr` | L1196–1224 |
| **Text: `the length of`** (primary case) | inside `ParsePrimary` | L1139–1147 |
| **Text: `joined to`** | `ParseJoinedTo` | L997–1011 |
| **Text: `converted to text`** (postfix) | inside `ParsePrimary` | L1174–1187 |

---

## TypeChecker.cs (1616 lines)

### CufetType class hierarchy (top of file)

| Type | Lines | Notes |
|------|-------|-------|
| `CufetType` abstract base + singletons | L1–21 | `.Number`, `.Text`, `.Fact` singletons |
| `NumberType` / `TextType` / `FactType` | L23–39 | Scalar types |
| `SeriesType` | L41–47 | Wraps element type |
| `RecordType` | L49–86 | Positional + named fields; structural equality |
| `FunctionType` | L88–111 | Param types + return type (null = void) |
| `ObjectType` | L113–143 | Nominal (equality by name); carries fields/methods/embed/interfaces |
| `InterfaceType` | L145–152 | Nominal; equality by name |
| `TypeInfo` record + `TypeException` | L154–159 | Env entry + error wrapper |

### Core — touch on almost any task

| Section | Lines | Notes |
|---------|-------|-------|
| `TypeChecker` fields + `Check` entry | L161–177 | `_env`, `_objectDefs`, `_interfaceDefs`, return-context fields |
| `Pass1Hoist` | L179–209 | Registers interfaces (1a), object types (1b), function sigs (1c) before checking |
| `CheckStatement` dispatch | L211–284 | Add case here for new statements |
| `InferType` dispatch switch | L698–718 | Add case here for new expression nodes; text ops at L714–716 |
| `FormatTypeError` | L1542–1551 | Standard error format — always use this |
| `FormatType` | L1553–1564 | Type → display string |
| `FormatTypePlural` | L1583–1594 | Type → plural display string |
| `FormatFunctionType` | L1574–1581 | FunctionType display |
| `FormatRecordType` | L1566–1572 | RecordType display |
| `FormatExpr` | L1596–1602 | Expression → short display string |
| `FormatOp` | L1604–1614 | Operator token → symbol string |
| `DefinitelyReturns` | L1524–1540 | Static return-path analysis for non-void functions |

### Per-feature statement checkers

| Feature | Method | Lines |
|---------|--------|-------|
| Define | `CheckDefine` | L287–297 |
| Becomes | `CheckBecomes` | L299–314 |
| Bind (function body) | `CheckBind` | L317–355 |
| Return | `CheckReturn` | L357–389 |
| Cast arg validation | `ValidateCastArgs` | L391–442 |
| For-each | `CheckForEach` | L444–470 |
| Series add | `CheckSeriesAdd` | L472–486 |
| Series remove value | `CheckSeriesRemoveValue` | L488–501 |
| Series set / object set / record set | `CheckSeriesSet` | L503–592 |
| Named record/object set | `CheckRecordNamedSet` | L594–632 |
| Possessive set | `CheckPossessiveSet` | L634–645 |
| Object named set (shared helper) | `CheckObjectNamedSet` | L647–677 |
| Series remove at + index validation | `CheckSeriesRemoveAt` / `CheckIndex` | L679–693 |
| Object definition (embedding + conformance) | `CheckObjectDefinition` | L1215–1262 |

### Per-feature expression inferencers

| Feature | Method | Lines |
|---------|--------|-------|
| Cast expression | `InferCastExpr` | L720–736 |
| Cast resolution + method dispatch | `ResolveForCast` / `TryMethodDispatch` | L738–848 |
| Series literal | `InferSeriesLiteral` | L850–893 |
| Series access (positional) | `InferSeriesAccess` | L895–959 |
| Series length (`the number of`) | `InferSeriesLength` | L961–970 |
| Record literal | `InferRecordLiteral` | L972–1007 |
| Named record/object access | `InferRecordNamedAccess` | L1009–1060 |
| Object literal (`new T{...}`) | `InferObjectLiteral` | L1264–1329 |
| Possessive access (`alice's name`) | `InferPossessiveAccess` | L1331–1376 |
| **Text: `joined to`** | `InferTextJoin` | L1393–1415 |
| **Text: `converted to text`** | `InferTextConvert` | L1417–1429 |
| **Text: `the length of`** | `InferTextLength` | L1431–1442 |
| Unary (`not`, `-`) | `InferUnary` | L1444–1466 |
| Binary (arithmetic / comparison / logic) | `InferBinary` | L1468–1522 |

### Object/embedding helpers

| Helper | Lines | Notes |
|--------|-------|-------|
| `FindFieldInOtOrPromoted` | L1070–1082 | Field lookup through embed chain; own → embed handle → promoted |
| `FindMethodInOtOrPromoted` | L1085–1094 | Method lookup through embed chain |
| `GetAllPositionalTypes` | L1097–1103 | Positional types: own + embedded (recursive) |
| `GetAllNamedFields` | L1106–1112 | Named fields: own + embedded (recursive) |
| `GetAllPromotedFieldNames` | L1115–1121 | For collision detection at definition time |
| `GetAllPromotedMethodNames` | L1124–1130 | For collision detection at definition time |
| `ValidateObjectEmbedding` | L1133–1165 | Checks embed exists + no name collisions |
| `ResolveParamType` | L1170–1179 | Converts ObjectType shell → InterfaceType or full ObjectType |
| `ValidateObjectConformance` | L1183–1213 | Checks declared interface conformance |
| `Levenshtein` (TypeChecker copy) | L1378–1389 | Used in `InferRecordNamedAccess` for "did you mean?" |

---

## Interpreter.cs (917 lines)

### Core types (always read when touching runtime values)

| Type | Lines | Notes |
|------|-------|-------|
| Exception types (Stop/Skip/Return) | L11–17 | Internal control flow; never escape loop handlers |
| `FunctionValue` | L19–23 | Holds parameter names + body; stored in `_env` |
| `ObjectValue` | L25–53 | Runtime object; includes `EmbeddedObject` for embed chain |
| `RecordValue` | L57–76 | Positional + named fields |
| `_objectDefs`, `_callDepth`, constructor | L78–87 | Instance state |

### Core dispatch (touch on most tasks)

| Section | Lines | Notes |
|---------|-------|-------|
| `Execute(Program)` — hoisting + main loop | L89–111 | Hoists object defs, then functions, then runs statements |
| `Execute(IStatement)` dispatch | L113–346 | Add case here for new statements |
| `Evaluate(IExpression)` dispatch | L394–419 | Add case here for new expression nodes; text ops at L415–417 |
| `Format` | L889–898 | Value → display string; used by `State` and `TextConvert` |
| `FormatRecord` / `FormatObject` | L900–915 | Record/object display formatting |

### Per-feature expression evaluators

| Feature | Method / Location | Lines |
|---------|-------------------|-------|
| Series access | `EvaluateSeriesAccess` | L514–549 |
| Named record/object access | `EvaluateRecordNamedAccess` | L551–570 |
| **Text: `joined to`** | `EvaluateTextJoin` | L572–581 |
| Unary (`not`, `-`) | `EvaluateUnary` | L583–593 |
| Binary (arithmetic / comparison / logic) | `EvaluateBinary` | L595–633 |
| Deep value equality (`is` / `is not`) | `ValuesEqual` | L639–682 |
| Number coercion backstop | `ToNumber` | L687–688 |
| Object literal construction | `EvaluateObjectLiteral` | L802–808 |
| Possessive access | `EvaluatePossessiveAccess` | L810–819 |
| **Text: `converted to text`** | inline in `Evaluate` | L416 |
| **Text: `the length of`** | inline in `Evaluate` | L417 |

### Infrastructure

| Helper | Lines | Notes |
|--------|-------|-------|
| `ExpectSeries` | L348–357 | Looks up series by name; throws if missing or wrong type |
| `ResolveIndex` | L360–380 | Expression? index → 0-based list index; null → "last" |
| `OrdinalSuffix` | L382–392 | 1 → "1st", etc. (error messages) |
| `ExecuteCallExpr` | L430–457 | Three dispatch forms: PossessiveAccess / method / free function |
| `ExecuteCall` | L461–512 | Evaluates args, snapshots env, isolates scope, runs body |
| `UndefinedVariableMessage` / `FindSuggestion` | L690–712 | Levenshtein "did you mean?" for runtime errors |
| `Levenshtein` (Interpreter copy) | L714–725 | |

### Object/embedding helpers

| Helper | Lines | Notes |
|--------|-------|-------|
| `TryFindNamedFieldValue` | L730–743 | Field lookup in ObjectValue chain (includes embed handle) |
| `FindOwnerForNamedField` | L746–750 | Returns the ObjectValue that directly owns the field (for mutation) |
| `FindOwnerForPositional` | L753–761 | Returns (owner, idx) for a positional field through the embed chain |
| `BuildObjectValue` | L764–800 | Routes flat field lists to own vs. embedded levels during construction |
| `DispatchMethod` | L821–840 | Walks ObjectValue embed chain to find and call a method |
| `ExecuteMethod` | L842–887 | Method call: scope isolation, `one` binding, return handling |

---

## Recipes

### Add a new expression type (e.g., new binary/postfix operator)

Touch these spots in order:

1. **TokenType.cs** — add token(s) under the appropriate section comment
2. **Lexer.cs L83–149** — add `"word" => TokenType.X` case(s) to keyword switch
3. **Ast.cs** — add `public sealed record MyExpr(...)` node; add at end of the relevant section
4. **Parser.cs**:
   - If the token could appear after `the` before `of`: add to `IsFieldNameToken` forAccess exclusion at L1303
   - If postfix (tighter than `joined to`): add `while(Peek().Type == ...) { ... }` loop inside `ParsePrimary` after the possessive loop at L1162, before `return baseExpr` at L1189
   - If new precedence level (between comparison and arithmetic): add `ParseMyLevel()` method modeled on `ParseJoinedTo` L997–1011; update `ParseComparison` L983, `ParseSingleCondition` L870, and all four `ParseJoinedTo()` calls in `ParseWordComparison` L889/897/905/912 to call `ParseMyLevel` instead of `ParseJoinedTo`
   - If prefix/primary (like `the length of`): add a `case TokenType.X:` inside the `ParsePrimary` switch at L1069–1158
5. **TypeChecker.cs L714–716** — add `MyExpr me => InferMyExpr(me),` to `InferType` switch
6. **TypeChecker.cs ~L1391** — add `private CufetType InferMyExpr(MyExpr me) { ... }` method;
   pattern to copy: `InferTextJoin` L1393–1415 (binary, type-checked operands),
   `InferTextConvert` L1417–1429 (postfix, input-type gated), `InferTextLength` L1431–1442 (postfix, returns different type)
7. **Interpreter.cs L415–417** — add `MyExpr me => EvaluateMyExpr(me),` to `Evaluate` switch
8. **Interpreter.cs ~L572** — add `private object EvaluateMyExpr(MyExpr me) { ... }`;
   pattern to copy: `EvaluateTextJoin` L572–581

### Add a new statement type

1. Same Lexer / TokenType / Ast steps as above
2. **Parser.cs L31–52** — add `TokenType.X => ParseMyStatement(),` to `ParseStatement`
3. **Parser.cs** — add `ParseMyStatement()` method; place near related statements
4. **TypeChecker.cs L214–283** — add `case MyStatement ms:` to `CheckStatement` switch
5. **TypeChecker.cs** — add `private void CheckMyStatement(MyStatement ms) { ... }` helper
6. **Interpreter.cs L116–345** — add `case MyStatement ms:` to `Execute` switch

### Add a new keyword token only (no new AST node — extends existing syntax)

1. **TokenType.cs** — add token to enum
2. **Lexer.cs L83–149** — add to keyword switch in `ReadWord`
3. **Parser.cs** — consume the new token in whichever parse method handles the surrounding syntax
4. No TypeChecker or Interpreter changes needed if the AST shape is unchanged

---

## Known gotchas

- **`the X of Y` disambiguation:** Any new keyword token that should be a valid named-record
  field name in *read* position (e.g., `the score of player`) needs no special handling.
  Only tokens that should *not* act as field names in access position (after `the`, before `of`)
  need to be excluded from `IsFieldNameToken(forAccess: true)` at Parser.cs L1303.
  Current exclusions: `Ordinal`, `NumberKw`, `Start`, `LengthKw`.

- **`text` is still an Identifier:** The word `text` is not a keyword token — it's checked
  by lexeme comparison in `ParsePrimary` (L1182) and `ParseTypeAnnotation` (L325–329).
  Don't add it to the keyword switch unless intending a breaking change.

- **Ordinals are not identifiers:** `first`–`tenth` and `last` lex as `Ordinal`, not
  `Identifier`. They cannot be used as variable names in test programs.

- **`Format()` vs. `converted to text`:** `TextConvert` at runtime calls `Format(Evaluate(tc.Value))`
  (Interpreter.cs L416). This means conversion output is always identical to what `State` prints.
  If `Format` changes, both change automatically.

- **Object scope in methods:** Inside method bodies, `_env` contains only `FunctionValue`
  entries + parameters + `one` (self). Globals are not visible. TypeChecker mirrors this in
  `CheckBind` L320–325 and `CheckObjectDefinition` L1225–1232.

- **Pass1Hoist ordering:** Interfaces → object types → function sigs (TypeChecker L179–209).
  A new hoistable definition type must be added in a fourth pass before or after existing passes,
  depending on whether it's referenced by the existing three.
