namespace Cufet.Interpreter;

public sealed partial class TypeChecker
{
    // ── Foreign interoperability: axioms ─────────────────────────────────────
    //
    // An axiom is source in another language, held as a value and taken as given. Cufet cannot
    // check a C listing and does not try; what it checks is the BOUNDARY — that the language is
    // named, that its book is pulled, and that what comes back fits the type it is returned into.

    /// <summary>The books that name a LANGUAGE rather than a library.</summary>
    /// <remarks>
    /// ★ `c-language` and not `c`: the style rule refuses a single-letter name, so C is qualified
    /// whatever the rest do. Each language is named by ear — `sql-language` would say language
    /// twice — so this is a list rather than a `<name>-language` rule.
    ///
    /// A language book has no members. You pull it to write axioms in that language at all; a
    /// bundled collection of ready-made axioms would be an ordinary module that happens to contain
    /// some, with no special status here.
    /// </remarks>
    /// ⚠ A method, not a static field. `BuiltinBooks` is a static field in another partial file and
    /// reads this during its own initializer, where a field here may not be assigned yet — which
    /// showed up as a NullReferenceException inside the type initializer, from nowhere the stack
    /// pointed at. A method has no initialization order to get wrong.
    private static string[] LanguageBookNames() => ["c-language"];

    /// <summary>The same list, for the interpreter's book table — one source, two tables.</summary>
    internal static string[] LanguageBookNamesForBooks() => LanguageBookNames();

    internal static bool IsLanguageBook(string name) =>
        LanguageBookNames().Any(known => IsSameLanguage(known, name));

    /// <summary>
    /// Puts the declaration's language tag on an axiom literal, and hands back the declared type
    /// with the shortened spelling resolved.
    /// </summary>
    /// <remarks>
    /// ★ Called BEFORE the value's type is asked for. `[…]` on its own has no type — the brackets
    /// say "this is verbatim foreign text" and cannot say which foreign — so the tag has to be on
    /// before anything can infer anything.
    /// </remarks>
    private CufetType? TagAxiomDeclaration(DefineStatement define)
    {
        var declared = define.DeclaredType;
        // `Define c-language get-pid as […]` — the shorthand, which parses as an ordinary named
        // type. A language book is the only thing that name can mean in type position.
        if (declared is ObjectType { PositionalTypes.Count: 0, NamedFields.Count: 0, Methods.Count: 0 } shell
            && IsLanguageBook(shell.Name))
            declared = new AxiomType(shell.Name);

        if (define.Value is not AxiomLiteral axiom)
        {
            // ⚠ `given` says what a BODY is handed, and only foreign source is a body a `Define`
            // can hold. On anything else it parses and means nothing, which is worse than being
            // refused — the writer would be left thinking the value takes arguments.
            if (define.HasParameterClause)
                throw TypeError(
                    $"'{define.Name}' is not foreign source, so it cannot be 'given' anything",
                    "Only an axiom — source in another language — takes parameters at a 'Define'",
                    define.Line, define.Column,
                    $"declare parameters for '{define.Name}'",
                    $"Use 'Bind ... to {define.Name}, given (...)' for a function, or drop the clause.");
            return declared;
        }

        if (declared is not AxiomType tag)
            throw TypeError(
                $"'{define.Name}' is foreign source, but nothing says what language it is in",
                "Square brackets say the text is not Cufet; they cannot say which language it is, "
              + "and the tag names who reads it",
                axiom.Line, axiom.Column,
                "write foreign source without naming its language",
                $"Name it in the declaration: 'Define c-language {define.Name} as [ ... ].'");

        RequireLanguagePulled(tag.Language, axiom.Line, axiom.Column);
        axiom.Language = tag.Language;
        axiom.ReturnType = tag.ReturnType is { } declaredResult ? ResolveParamType(declaredResult) : null;
        if (axiom.ReturnType is not null) RequireCrossableResult(axiom);
        CheckAxiomParameters(define, axiom);
        CheckReleaseClause(axiom, axiom.Line, axiom.Column);
        // ⚠ The declared type is COMPLETED from the literal, because a written axiom type has
        // nowhere to say what the axiom takes — `c-language number axiom` is the whole spelling.
        // At a declaration the two describe the same thing, so the parameters come from the one
        // place that has them. Without this, every `given (…)` axiom failed its own declaration
        // with "declared as c-language number axiom, but the value is a c-language number axiom",
        // which is the equality check comparing a shape against itself minus its parameters.
        return new AxiomType(tag.Language, axiom.ReturnType, [.. axiom.Parameters.Select(p => p.Type)]);
    }

    /// <summary>Refuses a declared result the boundary cannot bring back.</summary>
    private void RequireCrossableResult(AxiomLiteral axiom)
    {
        var result = axiom.ReturnType!;
        if (ForeignC.CanCrossBack(result)) return;

        // ⚠ A bare `text` gets its own sentence, because it is the near miss rather than a wrong
        // idea: a `char*` from C is NULL whenever C had nothing to give, and NULL is C's universal
        // "nothing". Saying `voidable text` is what puts that in the mechanism the language
        // already has instead of leaving a promise the C side cannot keep.
        if (result is TextType)
            throw TypeError(
                $"a {axiom.Language} axiom gives back a 'voidable text', never a plain 'text'",
                "C says nothing is there by handing back nothing — `getenv` on an unset name, "
              + "`strerror` on a code it does not know",
                axiom.Line, axiom.Column,
                "declare it as giving back a text",
                $"Write 'Define {axiom.Language} voidable text <name> as [ ... ].'");

        throw TypeError(
            $"a {axiom.Language} axiom cannot give back a {FormatType(result)} yet",
            "A number, a fact and a voidable text cross back; nothing else does",
            axiom.Line, axiom.Column,
            $"declare it as giving back a {FormatType(result)}",
            "Give back a number, a fact or a voidable text, and do the rest inside the source.");
    }

    /// <summary>Checks what an axiom says it takes, and that the foreign text asks for it.</summary>
    /// <remarks>
    /// ★ The parameter list is the only thing that says what C types the arguments have — the
    /// writer names no C type anywhere — so a type the boundary cannot carry has to be refused
    /// here, at the declaration, rather than at whichever call site happens to be written first.
    ///
    /// ⚠ A declared parameter the text never mentions is refused. It is not pedantry: `the paht`
    /// is left in the C verbatim (only DECLARED names are substituted), so a typo would otherwise
    /// surface as a gcc syntax error about a stray `the` — a message about the writer's spelling
    /// mistake, phrased in a language they were not writing.
    /// </remarks>
    private void CheckAxiomParameters(DefineStatement define, AxiomLiteral axiom)
    {
        // ⚠⚠ A resultless axiom is SOURCE, pasted where source goes — there is no call, so there is
        // nowhere for a parameter's value to come from. Splicing one anyway put `cufet_p0` into a
        // file-scope declaration, which named an identifier that exists nowhere: the program checked
        // clean and emitted C that would not build. Refused here, where the writer can see why.
        if (axiom.ReturnType is null && axiom.Parameters.Count > 0)
            throw TypeError(
                $"'{define.Name}' says nothing about what it gives back, so it is source rather "
              + "than something to run — and source takes no parameters",
                "A parameter's value comes from a call, and nothing calls this: it is pasted once, "
              + "above every axiom that can then use what it declares",
                define.Line, define.Column,
                $"give '{define.Name}' parameters",
                $"Drop the 'given' clause — or say what running it gives back, "
              + $"as in 'Define {axiom.Language} number {define.Name}, given (…), as [ … ].'");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (type, name) in axiom.Parameters)
        {
            var resolved = ResolveParamType(type);
            if (!ForeignC.CanPassToForeign(resolved))
                throw TypeError(
                    $"a {FormatType(resolved)} cannot be handed to {axiom.Language} source yet",
                    "A number, a fact and a text cross the boundary; nothing else does",
                    axiom.Line, axiom.Column,
                    $"declare '{name}' as a {FormatType(resolved)}",
                    "Take a number, a fact or a text instead, and do the rest inside the source.");

            if (!seen.Add(name))
                throw TypeError(
                    $"'{name}' is declared twice in this axiom's parameters",
                    "Each `the <name>` in the source has to name exactly one of them",
                    axiom.Line, axiom.Column,
                    $"declare '{name}' twice", "Give them different names.");

            if (!ForeignC.Mentions(axiom.Source, name))
                throw TypeError(
                    $"this {axiom.Language} source never uses 'the {name}'",
                    "A parameter reaches the source by its article — `the text path` here, "
                  + "`the path` in the source",
                    axiom.Line, axiom.Column,
                    $"declare '{name}' without using it",
                    $"Write 'the {name}' where the value belongs, or drop it from 'given'.");
        }

        if (define.HasParameterClause && axiom.Parameters.Count == 0)
            throw TypeError(
                $"'{define.Name}' declares an empty parameter list",
                null, define.Line, define.Column,
                "write 'given ()' with nothing in it",
                "Drop the 'given' clause — an axiom that takes nothing needs none.");
    }

    /// <summary>The type of a bare axiom literal — always tagged by then, or already refused.</summary>
    private CufetType InferAxiomLiteral(AxiomLiteral axiom) =>
        axiom.Language is { } language
            ? new AxiomType(language, axiom.ReturnType, [.. axiom.Parameters.Select(p => p.Type)])
            : throw TypeError(
                "this is foreign source, and nothing says what language it is in",
                "An axiom takes its language from the declaration it is the value of",
                axiom.Line, axiom.Column,
                "write foreign source outside a declaration that names its language",
                "Give it a name first: 'Define c-language <name> as [ ... ].'");

    /// <summary>
    /// A return whose value is an axiom RUNS it, and marshals what comes back.
    /// </summary>
    /// <remarks>
    /// ★ The declared type decides, and that is the existing rule that a declared type is what a
    /// value must fit into rather than a new one. `Bind number to process-id, return get-pid.` runs
    /// the axiom; a `Bind` whose declared type IS an axiom hands it back unrun, which is what keeps
    /// composition possible for a language whose fragments are assembled before they are used.
    ///
    /// ⚠ Everything about the C side is taken on trust; the range check on the way back is not.
    /// A `number` holds every 64-bit integer exactly — 28–29 significant digits against 19 — so
    /// this direction cannot be lossy, which is why it is the one the first slice carries.
    /// </remarks>
    private CufetType RunAxiomOnReturn(ReturnStatement ret, CufetType? expected)
    {
        var axiom = AxiomBehind(ret.Value!);
        if (axiom?.Language is not { } language)
            throw TypeError(
                "an axiom can only be run by returning one that was declared",
                "Foreign source runs from a name, so there is always a declaration to read the "
              + "language and the source off",
                ret.Line, ret.Column,
                "run foreign source that has no declaration",
                "Declare it first: 'Define c-language <name> as [ ... ].' — then return that name.");

        RequireLanguagePulled(language, ret.Line, ret.Column);

        // ★ What it gives back is the axiom's own business now, and the enclosing function's
        // declared return type is checked against it the ordinary way, by the caller of this.
        ret.RunsAxiom = axiom;
        _ = expected;
        return RunResultOf(axiom, ret.Line, ret.Column);
    }

    /// <summary>An axiom whose source cannot be found from where it is being run.</summary>
    /// <remarks>
    /// ★★ The boundary of the narrow slice, said in one place. An axiom is a VALUE now — it can be
    /// bound to a name, and a name bound to another name is followed to the source — but RUNNING
    /// one still needs the source at the call site, because both backends paste or compile the text
    /// itself and neither carries an axiom at run time. An axiom arriving through a PARAMETER, or
    /// handed back by a FUNCTION, is exactly the case with no source to find.
    ///
    /// ⚠ It is refused rather than allowed-and-miscompiled, which is the same reason the old
    /// blanket guard existed: allowing it type-checked a program the compiler could not build.
    /// Measured, when this slice first let it through — `CufetDec cv_alias = cv_answer;` against a
    /// `cv_answer` that was never emitted.
    /// </remarks>
    private TypeException AxiomSourceNotReachable(AxiomType axiom, int line, int column) =>
        TypeError(
            $"this {axiom.Language} axiom cannot be run from here — its source is not known at this point",
            "an axiom is the foreign text itself, and running one pastes that text; a name bound "
          + "to an axiom can be run, but one arriving through a parameter or handed back by a "
          + "function has no text to paste",
            line, column,
            "run an axiom whose source is decided at run time",
            "Run it where it is declared, or bind the axiom to a name and run that name.");

    /// <summary>An axiom reached for anywhere but a return.</summary>
    private TypeException AxiomUsedAsValue(string name, AxiomType axiom, IExpression at)
    {
        var (line, column) = at is VariableReference vr ? (vr.Line, vr.Column) : (0, 0);
        return TypeError(
            $"'{name}' is {axiom.Language} source, and can only be run by returning it",
            "Foreign source is not a value that can be printed, stored, or passed",
            line, column,
            $"use '{name}' as a value",
            $"Wrap it: 'Bind number to <name>, {name}.' — then use that function.");
    }

    /// <summary>A `Define`'s written type, resolved — or null when it declared none.</summary>
    private CufetType? ResolvedDeclaredType(CufetType? declared) =>
        declared is null ? null : ResolveParamType(declared);

    /// <summary>The axiom a `cast` reaches, or null when the call is an ordinary one.</summary>
    private AxiomLiteral? AxiomCalledBy(IExpression function) =>
        function is VariableReference vr && TryLookup(vr.Name, out var info) && info.Type is AxiomType
            ? AxiomBehind(function)
            : null;

    /// <summary>
    /// `cast open-file on (path, flags)` — runs an axiom, and what comes back is the type declared
    /// where the call is used.
    /// </summary>
    /// <remarks>
    /// ★ The same rule as returning one, extended to the only other place an axiom can be reached:
    /// a declared type is what a value must fit into, and for an axiom it is also what decides what
    /// the value IS. Nothing about the C side can say it — `open` gives back an `int` that means a
    /// file descriptor, and only the writer knows that.
    ///
    /// ⚠ Which is why a use site that declares nothing is refused rather than guessed at. It is the
    /// cost of the rule, and it is the one the writer can see and fix.
    /// </remarks>
    private CufetType RunAxiomOnCast(CastExpression cast, AxiomLiteral axiom)
    {
        RequireLanguagePulled(axiom.Language!, cast.Line, cast.Column);
        CheckForeignArguments(cast.Args, axiom, cast.Line, cast.Column);
        cast.RunsAxiom = axiom;
        return RunResultOf(axiom, cast.Line, cast.Column);
    }

    /// <summary>`cast job on (…)` where the axiom arrived as a VALUE — a parameter, or a result.</summary>
    /// <remarks>
    /// ★★ The difference from <see cref="RunAxiomOnCast"/> is what the check reads FROM. There the
    /// literal is in hand, so the parameter names are known and a mismatch can say which one is
    /// wrong. Here only the written type survives — `the c-language number axiom given (the text)`
    /// — so the check is positional and the message says the position. That is the whole cost of
    /// passing an axiom around, and it is the same cost a function value already pays.
    ///
    /// ⚠ The language must still be pulled at the CALL, not merely where the axiom was declared.
    /// Otherwise an axiom could be handed out of a `Pull a book on the c-language.` block and run
    /// somewhere the reader has no line to see that C is involved at all.
    /// </remarks>
    private CufetType RunAxiomValueOnCast(CastExpression cast, AxiomType axiom)
    {
        CheckAxiomValueCall(cast.Args, axiom, cast.Line, cast.Column);
        cast.RunsAxiomValue = true;
        return axiom.ReturnType!;
    }

    /// <summary>The same, for an axiom VALUE called as a statement — `Cast job on (…).`</summary>
    private void RunAxiomValueOnCastStatement(CastStatement cast, AxiomType axiom)
    {
        CheckAxiomValueCall(cast.Args, axiom, cast.Line, cast.Column);
        cast.RunsAxiomValue = true;
    }

    /// <summary>Checks a call against a WRITTEN axiom type, positionally.</summary>
    private void CheckAxiomValueCall(IReadOnlyList<IExpression> args, AxiomType axiom,
                                     int line, int column)
    {
        RequireLanguagePulled(axiom.Language, line, column);

        if (args.Count != axiom.ParameterTypes.Count)
            throw TypeError(
                $"this {axiom.Language} axiom takes {Count(axiom.ParameterTypes.Count, "value")}, "
              + $"and {args.Count} {(args.Count == 1 ? "was" : "were")} given",
                null, line, column,
                $"pass {Count(args.Count, "value")}",
                $"It is written '{FormatType(axiom)}'.");

        for (int i = 0; i < args.Count; i++)
        {
            var expected = axiom.ParameterTypes[i];
            var actual = InferType(args[i]);
            if (actual != null && !IsAssignable(expected, actual))
                throw TypeError(
                    $"value {i + 1} of this {axiom.Language} axiom takes a {FormatType(expected)}, "
                  + $"but a {FormatType(actual)} was given",
                    null, line, column,
                    $"pass a {FormatType(actual)} there",
                    $"Give it a {FormatType(expected)}.");
        }
    }

    /// <summary>The same, for an axiom called as a STATEMENT — its answer thrown away.</summary>
    /// <remarks>
    /// (a) Every check the expression form makes still applies: the language must be pulled and the
    /// arguments must fit. (b) `RunResultOf` runs too, and its answer is discarded rather than
    /// skipped — a declaration that never said what it gives back cannot be wrapped in C at all,
    /// because the wrapper's return type is built from exactly that.
    /// </remarks>
    private void RunAxiomOnCastStatement(CastStatement cast, AxiomLiteral axiom)
    {
        RequireLanguagePulled(axiom.Language!, cast.Line, cast.Column);
        CheckForeignArguments(cast.Args, axiom, cast.Line, cast.Column);
        cast.RunsAxiom = axiom;
        _ = RunResultOf(axiom, cast.Line, cast.Column);
    }

    /// <summary>What running this axiom yields — refusing one that never said.</summary>
    /// <remarks>
    /// ★★ Read off the DECLARATION, which is the whole reason a call can now be written anywhere an
    /// ordinary call can. It used to come from the line USING the axiom, and that cost more than it
    /// looked like on paper: the call had to be the entire right-hand side of a typed binding, so
    /// it could not sit in a condition, in an interpolation, inside arithmetic, or as an argument —
    /// every result went through a named intermediate first.
    /// </remarks>
    private CufetType RunResultOf(AxiomLiteral axiom, int line, int column) =>
        axiom.ReturnType ?? throw TypeError(
            $"this {axiom.Language} axiom does not say what it gives back",
            "Cufet cannot read a C listing to find out — an `int` might be a number, a fact, or a "
          + "handle, and only the person who wrote the source knows which",
            line, column,
            "run foreign source that never declared a result",
            $"Say so where it is declared: 'Define {axiom.Language} number <name> as [ ... ].'");

    /// <summary>Resolves `and free it with &lt;name&gt;` and checks the pair fits together.</summary>
    /// <remarks>
    /// ★ Three things have to hold, and each one is a mistake somebody will make: the releasing
    /// name must be an axiom, it must take exactly one `address`, and the acquiring axiom must
    /// actually hand back an address. The last is the one worth spelling out — a clause on an axiom
    /// that gives back a number is a misunderstanding of what the clause does, not a typo.
    ///
    /// ⚠ Nothing checks that the release function is the RIGHT one. Cufet never reads the foreign
    /// text, so `and free it with fclose` on an `opendir` handle type-checks and corrupts. That is
    /// the residue DESIGN accepts deliberately: refusing it would mean never letting the pointer
    /// exist, which costs `fopen`.
    /// </remarks>
    private void CheckReleaseClause(AxiomLiteral axiom, int line, int column)
    {
        if (axiom.ReleasedBy is not { } name) return;

        if (axiom.ReturnType is not VoidableType { Inner: AddressType })
            throw TypeError(
                $"'{name}' is named to free what this axiom gives back, but it does not give back an address",
                "only an address needs freeing — a number, a fact and a text are copied across the "
              + "boundary and belong to Cufet once they arrive",
                line, column,
                "say how to free something that is not an address",
                "Drop the clause, or declare this axiom 'voidable address'.");

        if (!TryLookup(name, out var info) || info!.EstablishingExpr is not AxiomLiteral release)
            throw TypeError(
                $"'{name}' is named to free this axiom's result, but it is not an axiom",
                "freeing happens in the other language, so the thing that does it has to be "
              + "foreign source too",
                line, column,
                $"free an address with something that is not foreign source",
                $"Declare it, as in 'Define {axiom.Language} number {name}, given (the address held), as [ … ].'");

        if (release.Parameters.Count != 1 || ResolveParamType(release.Parameters[0].Type) is not AddressType)
            throw TypeError(
                $"'{name}' has to take exactly one address to free one, and it takes "
              + (release.Parameters.Count == 0
                    ? "nothing"
                    : Count(release.Parameters.Count, "value")),
                null, line, column,
                $"free an address with an axiom that cannot be handed one",
                $"Declare it 'given (the address held)'.");

        axiom.ReleaseAxiom = release;
    }

    /// <summary>`the text at &lt;address&gt;` — the one read through a foreign pointer.</summary>
    /// <remarks>
    /// ★ The result is `voidable text` whatever the address was, and the void case is real: a void
    /// address reads as void rather than being refused, so `the text at handle but void is "?"`
    /// handles both "C gave nothing" and "there was nothing there" in one place. That is the same
    /// mechanism every other absence on this boundary lands in.
    ///
    /// ★ Accepting a `voidable address` directly is deliberate. Requiring the caller to narrow
    /// first would make the commonest line two lines, and the read has an obvious answer for void.
    /// </remarks>
    private CufetType InferForeignTextAt(ForeignTextAt read)
    {
        var addressType = InferType(read.Address);
        if (addressType is not AddressType && addressType is not VoidableType { Inner: AddressType })
            throw TypeError(
                $"'the text at' reads through a foreign address, and this is a {FormatType(addressType)}",
                "the only thing an address can be read through is this phrase, and the only thing "
              + "this phrase reads is an address",
                read.Line, read.Column,
                $"read text at a {FormatType(addressType)}",
                "Give it an address from foreign source.");
        return new VoidableType(new TextType());
    }

    /// <summary>A foreign pointer may only be held inside a rabbit block.</summary>
    /// <remarks>
    /// ★★ **The rabbit block IS the unsafe marker**, and it needed no new keyword to become one. A
    /// rabbit already means region-scoped memory work, so it is the closest thing Cufet has to
    /// `unsafe` — and the reason it is the rabbit rather than a marker of its own is that a pointer
    /// is a rabbit RESPONSIBILITY: the arena that knows when a region dies is the thing that knows
    /// when a pointer dies. That extends the safety model rather than holing it.
    ///
    /// ⚠ The check is on the BINDING, not on the call. An axiom may be declared anywhere and its
    /// result may be handed straight back to another axiom without ever being named — what must not
    /// happen is a pointer OUTLIVING the region that is answerable for it, and that can only start
    /// with something holding it.
    /// </remarks>
    private void RequireRabbitForAddress(CufetType type, string name, int line, int column)
    {
        if (_rabbitDepth > 0) return;
        if (type is not AddressType && type is not VoidableType { Inner: AddressType }) return;
        throw TypeError(
            $"'{name}' holds a foreign address, and one can only be held inside a rabbit",
            "a rabbit block is where region-scoped memory work happens, so it is also where a "
          + "pointer's lifetime is answerable for — the arena that knows when the region dies is "
          + "what knows when the pointer dies",
            line, column,
            "hold a foreign address outside a rabbit",
            "Wrap the work in 'Pull a rabbit. ... Done.'");
    }

    /// <summary>Checks a call's arguments against what the axiom declared it takes.</summary>
    private void CheckForeignArguments(IReadOnlyList<IExpression> callArgs, AxiomLiteral axiom,
                                       int line, int column)
    {
        if (callArgs.Count != axiom.Parameters.Count)
            throw TypeError(
                $"this {axiom.Language} source takes {Count(axiom.Parameters.Count, "value")}, "
              + $"and {callArgs.Count} {(callArgs.Count == 1 ? "was" : "were")} given",
                null, line, column,
                $"pass {Count(callArgs.Count, "value")}",
                $"It is declared 'given ({string.Join(", ", axiom.Parameters.Select(p => $"the {FormatType(p.Type)} {p.Name}"))})'.");

        for (int i = 0; i < callArgs.Count; i++)
        {
            var expected = ResolveParamType(axiom.Parameters[i].Type);
            var actual = InferType(callArgs[i]);
            if (actual != null && !IsAssignable(expected, actual))
                throw TypeError(
                    $"'{axiom.Parameters[i].Name}' takes a {FormatType(expected)}, but a {FormatType(actual)} was given",
                    null, line, column,
                    $"pass a {FormatType(actual)} for '{axiom.Parameters[i].Name}'",
                    $"Give it a {FormatType(expected)}.");
        }
    }

    private static string Count(int n, string noun) => n == 1 ? $"1 {noun}" : $"{n} {noun}s";

    /// <summary>The axiom an expression stands for, following a chain of names to the literal.</summary>
    /// <remarks>
    /// ★ Chained, because an axiom is a value now: `Define alias as answer.` binds a name whose
    /// establishing expression is another NAME, not a literal, and one hop used to be all this
    /// followed. Every link is a `Define`, so the chain is finite and acyclic by construction —
    /// the depth guard is there for the malformed tree a rebuild could hand back, not for a
    /// program a writer can express.
    ///
    /// ⚠ Null is a real answer and not a failure: an axiom arriving through a PARAMETER or a
    /// function's return has no literal to find here, and is called through its value instead.
    /// </remarks>
    private AxiomLiteral? AxiomBehind(IExpression value)
    {
        for (int hops = 0; hops < 64; hops++)
            switch (value)
            {
                case AxiomLiteral literal: return literal;
                case VariableReference vr when TryLookup(vr.Name, out var info):
                    if (info.EstablishingExpr is AxiomLiteral bound) return bound;
                    if (info.EstablishingExpr is VariableReference next && !ReferenceEquals(next, value))
                    { value = next; continue; }
                    return null;
                default: return null;
            }
        return null;
    }

    /// <summary>Refuses foreign source whose language book is not pulled here.</summary>
    /// <remarks>
    /// ⚠ The book is what admits the language, so it is required even though it has no members to
    /// offer. Foreign source is the one place where being wrong means memory corruption rather than
    /// a wrong number, and a pull is the line a reader can see it on.
    /// </remarks>
    private void RequireLanguagePulled(string language, int line, int column)
    {
        for (int i = _scopes.Count - 1; i >= 0; i--)
            foreach (var info in _scopes[i].Values)
                if (info.Type is BookType { Name: var pulled } && IsSameLanguage(pulled, language))
                    return;
                else if (info.Type is ObjectType { Name: var layer } && IsSameLanguage(layer, language))
                    return;

        throw TypeError(
            $"the {language} book is not in scope",
            null, line, column,
            $"write {language} source without pulling the {language} book",
            $"Add 'Pull a book on the {language}.' around this.");
    }

    private static bool IsSameLanguage(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
