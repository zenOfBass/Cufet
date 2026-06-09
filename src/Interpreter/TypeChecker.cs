using Cufet.Lexer;

namespace Cufet.Interpreter;

// CufetType is a value-equality class hierarchy.
// CufetType.Number / .Text / .Fact are canonical singletons for the three scalars.
// All == comparisons use structural / deep equality.
public abstract class CufetType
{
    public static readonly CufetType Number = new NumberType();
    public static readonly CufetType Text   = new TextType();
    public static readonly CufetType Fact   = new FactType();

    public abstract override bool Equals(object? obj);
    public abstract override int GetHashCode();

    public static bool operator ==(CufetType? left, CufetType? right)
        => left is null ? right is null : left.Equals(right);
    public static bool operator !=(CufetType? left, CufetType? right)
        => !(left == right);
}

public sealed class NumberType : CufetType
{
    public override bool Equals(object? obj) => obj is NumberType;
    public override int GetHashCode() => typeof(NumberType).GetHashCode();
}

public sealed class TextType : CufetType
{
    public override bool Equals(object? obj) => obj is TextType;
    public override int GetHashCode() => typeof(TextType).GetHashCode();
}

public sealed class FactType : CufetType
{
    public override bool Equals(object? obj) => obj is FactType;
    public override int GetHashCode() => typeof(FactType).GetHashCode();
}

public sealed class SeriesType : CufetType
{
    public CufetType ElementType { get; }
    public SeriesType(CufetType elementType) => ElementType = elementType;
    public override bool Equals(object? obj) => obj is SeriesType s && ElementType == s.ElementType;
    public override int GetHashCode() => HashCode.Combine(typeof(SeriesType), ElementType);
}

public sealed class FunctionType : CufetType
{
    public IReadOnlyList<CufetType> ParameterTypes { get; }
    public CufetType? ReturnType { get; }   // null = void

    public FunctionType(IReadOnlyList<CufetType> parameterTypes, CufetType? returnType)
    {
        ParameterTypes = parameterTypes;
        ReturnType = returnType;
    }

    public override bool Equals(object? obj) =>
        obj is FunctionType ft &&
        ReturnType == ft.ReturnType &&
        ParameterTypes.SequenceEqual(ft.ParameterTypes);

    public override int GetHashCode()
    {
        var h = HashCode.Combine(typeof(FunctionType), ReturnType);
        foreach (var pt in ParameterTypes)
            h = HashCode.Combine(h, pt);
        return h;
    }
}

public record TypeInfo(CufetType Type, IExpression EstablishingExpr, int EstablishingLine);

public sealed class TypeException : Exception
{
    public TypeException(string message) : base(message) { }
}

public sealed class TypeChecker
{
    private readonly Dictionary<string, TypeInfo> _env = new();

    // Return context — set when entering a Bind body.
    private bool       _inFunction             = false;
    private CufetType? _expectedReturnType     = null; // null = void function
    private int        _functionDeclarationLine = 0;

    public void Check(Program program)
    {
        Pass1Hoist(program);
        foreach (var stmt in program.Statements)
            CheckStatement(stmt);
    }

    // Pass 1: register all top-level Bind signatures before checking any body.
    // This enables forward references and self-/mutual-recursion.
    private void Pass1Hoist(Program program)
    {
        foreach (var stmt in program.Statements)
        {
            if (stmt is not BindStatement bind) continue;
            var paramTypes = bind.Parameters.Select(p => p.Type).ToList();
            _env[bind.Name] = new TypeInfo(
                new FunctionType(paramTypes, bind.ReturnType),
                new VariableReference(bind.Name),
                bind.Line);
        }
    }

    private void CheckStatement(IStatement stmt)
    {
        switch (stmt)
        {
            case DefineStatement define:
                CheckDefine(define);
                break;
            case BecomesStatement becomes:
                CheckBecomes(becomes);
                break;
            case StateStatement state:
                _ = InferType(state.Value);
                break;
            case IfStatement ifStmt:
                foreach (var arm in ifStmt.Arms)
                {
                    _ = InferType(arm.Condition);
                    foreach (var s in arm.Body)
                        CheckStatement(s);
                }
                if (ifStmt.ElseBody != null)
                    foreach (var s in ifStmt.ElseBody)
                        CheckStatement(s);
                break;
            case WhileStatement whileStmt:
                _ = InferType(whileStmt.Condition);
                foreach (var s in whileStmt.Body)
                    CheckStatement(s);
                break;
            case RepeatUntilStatement repeatUntil:
                foreach (var s in repeatUntil.Body)
                    CheckStatement(s);
                _ = InferType(repeatUntil.Condition);
                break;
            case ForEachStatement forEach:
                CheckForEach(forEach);
                break;
            case SeriesAddStatement add:
                CheckSeriesAdd(add);
                break;
            case SeriesRemoveValueStatement removeVal:
                CheckSeriesRemoveValue(removeVal);
                break;
            case SeriesSetStatement seriesSet:
                CheckSeriesSet(seriesSet);
                break;
            case SeriesRemoveAtStatement removeAt:
                CheckSeriesRemoveAt(removeAt);
                break;
            case BindStatement bind:
                CheckBind(bind);
                break;
            case CastStatement cs:
            {
                var (funcType, displayName, declLine) = ResolveForCast(cs.Function, cs.Line);
                if (funcType != null) ValidateCastArgs(funcType, displayName, declLine, cs.Args, cs.Line);
                break;
            }
            case ReturnStatement ret:
                CheckReturn(ret);
                break;
        }
    }

    private void CheckDefine(DefineStatement define)
    {
        var type = InferType(define.Value);
        if (type == null)
            throw new TypeException(FormatTypeError(
                $"the type of the value for '{define.Name}' can't be determined",
                null,
                define.Line,
                "define a variable without a clear starting type",
                "Start with a literal value or a defined variable so the type is clear from the beginning."));
        _env[define.Name] = new TypeInfo(type, define.Value, define.Line);
    }

    private void CheckBecomes(BecomesStatement becomes)
    {
        if (!_env.TryGetValue(becomes.Name, out var existing))
            return;

        var rhsType = InferType(becomes.Value);
        if (rhsType == null) return;

        if (rhsType != existing.Type)
            throw new TypeException(FormatTypeError(
                $"'{becomes.Name}' holds {FormatTypePlural(existing.Type)}",
                $"You set it to {FormatExpr(existing.EstablishingExpr)} on line {existing.EstablishingLine}, so it can only ever hold {FormatTypePlural(existing.Type)}",
                becomes.Line,
                $"give it a {FormatType(rhsType)} value",
                $"Variables keep their type for life. If you need a {FormatType(rhsType)} value here, define a new name for it instead."));
    }

    private void CheckBind(BindStatement bind)
    {
        // Build function body env: only function signatures + parameters (no globals).
        // This mirrors runtime scoping — functions cannot see the caller's variables.
        var snapshot = new Dictionary<string, TypeInfo>(_env);
        _env.Clear();
        foreach (var (k, v) in snapshot.Where(kv => kv.Value.Type is FunctionType))
            _env[k] = v;
        foreach (var (type, name) in bind.Parameters)
            _env[name] = new TypeInfo(type, new VariableReference(name), bind.Line);

        var prevInFunction        = _inFunction;
        var prevReturnType        = _expectedReturnType;
        var prevFunctionLine      = _functionDeclarationLine;
        _inFunction               = true;
        _expectedReturnType       = bind.ReturnType;
        _functionDeclarationLine  = bind.Line;

        try
        {
            foreach (var stmt in bind.Body)
                CheckStatement(stmt);
        }
        finally
        {
            _inFunction               = prevInFunction;
            _expectedReturnType       = prevReturnType;
            _functionDeclarationLine  = prevFunctionLine;
            _env.Clear();
            foreach (var (k, v) in snapshot) _env[k] = v;
        }

        if (bind.ReturnType != null && !DefinitelyReturns(bind.Body))
            throw new TypeException(FormatTypeError(
                $"'{bind.Name}' is declared to give back a {FormatType(bind.ReturnType)}, but it can reach its end without returning one",
                null,
                bind.Line,
                "define a function that might not return a value",
                "Make sure every path through the function ends with a return statement."));
    }

    private void CheckReturn(ReturnStatement ret)
    {
        if (_expectedReturnType == null) // void function (or _inFunction guard is parser-level)
        {
            if (ret.Value != null)
                throw new TypeException(FormatTypeError(
                    "this function is declared void — it gives nothing back",
                    null,
                    ret.Line,
                    "return a value from a void function",
                    "Remove the value, or change the function's return type if you need to produce a result."));
            // bare return in void → ok
        }
        else // non-void function
        {
            if (ret.Value == null)
                throw new TypeException(FormatTypeError(
                    $"this function is declared to give back a {FormatType(_expectedReturnType)}",
                    $"You declared the return type as {FormatType(_expectedReturnType)} on line {_functionDeclarationLine}",
                    ret.Line,
                    "return without a value",
                    $"Provide a {FormatType(_expectedReturnType)} value to return."));

            var returnType = InferType(ret.Value);
            if (returnType != null && returnType != _expectedReturnType)
                throw new TypeException(FormatTypeError(
                    $"this function is declared to give back a {FormatType(_expectedReturnType)}",
                    $"You declared the return type as {FormatType(_expectedReturnType)} on line {_functionDeclarationLine}",
                    ret.Line,
                    $"return a {FormatType(returnType)} value",
                    $"Change the returned value to a {FormatType(_expectedReturnType)}."));
        }
    }

    // Validates arg count and types against a resolved FunctionType.
    private void ValidateCastArgs(
        FunctionType funcType, string displayName, int declLine,
        IReadOnlyList<IExpression> args, int callLine)
    {
        if (args.Count != funcType.ParameterTypes.Count)
            throw new TypeException(FormatTypeError(
                $"{displayName} expects {funcType.ParameterTypes.Count} argument(s), but you passed {args.Count}",
                $"You declared it on line {declLine} with {funcType.ParameterTypes.Count} parameter(s)",
                callLine,
                $"call it with {args.Count} argument(s)",
                args.Count < funcType.ParameterTypes.Count
                    ? "Add the missing argument(s)."
                    : "Remove the extra argument(s)."));

        for (int i = 0; i < args.Count; i++)
        {
            var argType = InferType(args[i]);
            if (argType == null || argType == funcType.ParameterTypes[i]) continue;
            throw new TypeException(FormatTypeError(
                $"argument {i + 1} of {displayName} must be a {FormatType(funcType.ParameterTypes[i])}, but you passed a {FormatType(argType)}",
                $"You declared {displayName} on line {declLine}, so argument {i + 1} must be a {FormatType(funcType.ParameterTypes[i])}",
                callLine,
                $"pass a {FormatType(argType)} as argument {i + 1}",
                $"Change argument {i + 1} to a {FormatType(funcType.ParameterTypes[i])}."));
        }
    }

    private void CheckForEach(ForEachStatement forEach)
    {
        if (!_env.TryGetValue(forEach.SeriesName, out var seriesInfo))
            return; // undeclared series — runtime catches; skip body to avoid false positives

        if (seriesInfo.Type is not SeriesType seriesType)
            throw new TypeException(FormatTypeError(
                $"'{forEach.SeriesName}' holds {FormatTypePlural(seriesInfo.Type)}",
                $"You set it to {FormatExpr(seriesInfo.EstablishingExpr)} on line {seriesInfo.EstablishingLine}, so it can only ever hold {FormatTypePlural(seriesInfo.Type)}",
                forEach.Line,
                "loop over it as if it were a series",
                "Only series can be looped over. Define a series if that's what you need."));

        var iterKey = forEach.IteratorName ?? "it";
        var hadPrev = _env.TryGetValue(iterKey, out var prev);
        _env[iterKey] = new TypeInfo(seriesType.ElementType, new VariableReference(forEach.SeriesName), forEach.Line);
        try
        {
            foreach (var s in forEach.Body)
                CheckStatement(s);
        }
        finally
        {
            if (hadPrev) _env[iterKey] = prev!;
            else _env.Remove(iterKey);
        }
    }

    private void CheckSeriesAdd(SeriesAddStatement add)
    {
        if (add.AfterIndex != null) CheckIndex(add.AfterIndex, add.Line);
        if (!_env.TryGetValue(add.SeriesName, out var seriesInfo)) return;
        if (seriesInfo.Type is not SeriesType seriesType) return;

        var valueType = InferType(add.Value);
        if (valueType != null && valueType != seriesType.ElementType)
            throw new TypeException(FormatTypeError(
                $"'{add.SeriesName}' holds {FormatTypePlural(seriesType.ElementType)}",
                $"You defined it on line {seriesInfo.EstablishingLine} as a series of {FormatTypePlural(seriesType.ElementType)}, so it can only accept {FormatTypePlural(seriesType.ElementType)}",
                add.Line,
                $"add a {FormatType(valueType)} value to it",
                $"Change the value to a {FormatType(seriesType.ElementType)}, or define a separate series that holds {FormatTypePlural(valueType)}."));
    }

    private void CheckSeriesRemoveValue(SeriesRemoveValueStatement removeVal)
    {
        if (!_env.TryGetValue(removeVal.SeriesName, out var seriesInfo)) return;
        if (seriesInfo.Type is not SeriesType seriesType) return;

        var valueType = InferType(removeVal.Value);
        if (valueType != null && valueType != seriesType.ElementType)
            throw new TypeException(FormatTypeError(
                $"'{removeVal.SeriesName}' holds {FormatTypePlural(seriesType.ElementType)}",
                $"You defined it on line {seriesInfo.EstablishingLine} as a series of {FormatTypePlural(seriesType.ElementType)}, so only {FormatTypePlural(seriesType.ElementType)} can be removed from it",
                removeVal.Line,
                $"remove a {FormatType(valueType)} value from it",
                $"Make sure the value you're removing is a {FormatType(seriesType.ElementType)}."));
    }

    private void CheckSeriesSet(SeriesSetStatement seriesSet)
    {
        if (seriesSet.Index != null) CheckIndex(seriesSet.Index, seriesSet.Line);
        if (!_env.TryGetValue(seriesSet.SeriesName, out var seriesInfo)) return;
        if (seriesInfo.Type is not SeriesType seriesType) return;

        var valueType = InferType(seriesSet.Value);
        if (valueType != null && valueType != seriesType.ElementType)
            throw new TypeException(FormatTypeError(
                $"'{seriesSet.SeriesName}' holds {FormatTypePlural(seriesType.ElementType)}",
                $"You defined it on line {seriesInfo.EstablishingLine} as a series of {FormatTypePlural(seriesType.ElementType)}, so its items can only be set to {FormatTypePlural(seriesType.ElementType)}",
                seriesSet.Line,
                $"set an item to a {FormatType(valueType)} value",
                $"Change the new value to a {FormatType(seriesType.ElementType)}."));
    }

    private void CheckSeriesRemoveAt(SeriesRemoveAtStatement removeAt)
    {
        if (removeAt.Index != null) CheckIndex(removeAt.Index, removeAt.Line);
    }

    private static void CheckIndex(IExpression index, int line)
    {
        if (index is NumberLiteral { Value: var v } && v % 1 != 0)
            throw new TypeException(FormatTypeError(
                "item positions must be whole numbers",
                null,
                line,
                $"use {v} as a position",
                "Positions are counted 1, 2, 3 and so on. Use a whole number."));
    }

    // Returns null for genuine inference gaps (undeclared variable, unhandled expression form).
    // Returns a concrete CufetType for anything we can type statically.
    // Throws TypeException for operand type mismatches.
    private CufetType? InferType(IExpression expr) => expr switch
    {
        NumberLiteral                                                           => CufetType.Number,
        StringLiteral                                                           => CufetType.Text,
        UnaryExpression unary                                                   => InferUnary(unary),
        BinaryExpression bin                                                    => InferBinary(bin),
        VariableReference { Name: var n } when _env.TryGetValue(n, out var ti) => ti.Type,
        VariableReference                                                       => null,
        SeriesLiteral lit                                                       => InferSeriesLiteral(lit),
        SeriesAccess acc                                                        => InferSeriesAccess(acc),
        SeriesLength                                                            => CufetType.Number,
        CastExpression cast                                                     => InferCastExpr(cast),
        _                                                                       => null,
    };

    private CufetType? InferCastExpr(CastExpression cast)
    {
        var (funcType, displayName, declLine) = ResolveForCast(cast.Function, cast.Line);
        if (funcType == null) return null;

        ValidateCastArgs(funcType, displayName, declLine, cast.Args, cast.Line);

        if (funcType.ReturnType == null)
            throw new TypeException(FormatTypeError(
                $"{displayName} gives nothing back — it can't be used as a value",
                $"You declared it as void on line {declLine}",
                cast.Line,
                "use its result as a value",
                "Cast it as a statement instead, or change its return type if you need a result."));

        return funcType.ReturnType;
    }

    // Resolves the function expression to (FunctionType, display name, declaration line).
    // Returns (null, ...) if the type is unknown at compile time — runtime catches it.
    // Throws TypeException if the expression's type is known but is not a function.
    private (FunctionType? funcType, string displayName, int declLine) ResolveForCast(
        IExpression funcExpr, int callLine)
    {
        // Fast path: named variable — we have rich info from _env.
        if (funcExpr is VariableReference vr && _env.TryGetValue(vr.Name, out var info))
        {
            if (info.Type is not FunctionType ft)
                throw new TypeException(FormatTypeError(
                    $"'{vr.Name}' holds a {FormatType(info.Type)}, not a function — you can only cast functions",
                    null,
                    callLine,
                    "cast something that isn't a function",
                    "Only functions can be cast. Make sure the name you're casting refers to a function."));
            return (ft, $"'{vr.Name}'", info.EstablishingLine);
        }

        // General path: infer the type of the expression.
        var exprType = InferType(funcExpr);
        if (exprType == null) return (null, "this function", callLine);
        if (exprType is not FunctionType funcType)
            throw new TypeException(FormatTypeError(
                $"this expression holds a {FormatType(exprType)}, not a function — you can only cast functions",
                null,
                callLine,
                "cast something that isn't a function",
                "Only functions can be cast."));
        return (funcType, "this function", callLine);
    }

    private CufetType InferSeriesLiteral(SeriesLiteral lit)
    {
        CufetType? inferred = null;
        for (int i = 0; i < lit.Elements.Count; i++)
        {
            var elemType = InferType(lit.Elements[i]);
            if (elemType == null) continue;
            if (inferred == null)
            {
                inferred = elemType;
            }
            else if (inferred != elemType)
            {
                throw new TypeException(FormatTypeError(
                    "every item in a series must be the same type",
                    $"The first item is a {FormatType(inferred)}, so all items must be {FormatTypePlural(inferred)}",
                    lit.Line,
                    $"make item {i + 1} a {FormatType(elemType)}",
                    $"Remove the mismatched item, or define two separate series — one for {FormatTypePlural(inferred)} and one for {FormatTypePlural(elemType)}."));
            }
        }

        if (lit.Annotation != null)
        {
            if (inferred != null && inferred != lit.Annotation)
                throw new TypeException(FormatTypeError(
                    $"you said this is a series of {FormatTypePlural(lit.Annotation)}",
                    $"That annotation fixes the element type as {FormatType(lit.Annotation)}",
                    lit.Line,
                    $"put a {FormatType(inferred)} item in it",
                    "Fix the annotation to match the elements, or change the elements to match the annotation."));
            return new SeriesType(lit.Annotation);
        }

        if (inferred == null)
            throw new TypeException(FormatTypeError(
                "an empty series has no items to infer its type from",
                null,
                lit.Line,
                "define an empty series without saying what type of items it will hold",
                "Add an annotation to declare the element type: a series of numbers (), a series of text (), or a series of facts ()."));

        return new SeriesType(inferred);
    }

    private CufetType? InferSeriesAccess(SeriesAccess acc)
    {
        if (acc.Index != null) CheckIndex(acc.Index, acc.Line);
        if (!_env.TryGetValue(acc.SeriesName, out var seriesInfo)) return null;
        if (seriesInfo.Type is SeriesType st) return st.ElementType;
        return null;
    }

    private CufetType? InferUnary(UnaryExpression unary)
    {
        var operand = InferType(unary.Operand);
        if (operand == null) return null;
        if (operand == CufetType.Number) return CufetType.Number;
        throw new TypeException(FormatTypeError(
            "unary minus works on numbers only",
            null,
            unary.Line,
            $"negate a {FormatType(operand)} value",
            "Make sure the value you're negating is a number."));
    }

    private CufetType? InferBinary(BinaryExpression bin)
    {
        var left  = InferType(bin.Left);
        var right = InferType(bin.Right);

        if (left == null || right == null) return null;

        var l = left;
        var r = right;

        return bin.Op switch
        {
            TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash
                when l == CufetType.Number && r == CufetType.Number
                => CufetType.Number,
            TokenType.Plus or TokenType.Minus or TokenType.Star or TokenType.Slash
                => throw new TypeException(FormatTypeError(
                    "arithmetic requires numbers on both sides",
                    null,
                    bin.Line,
                    $"use {FormatOp(bin.Op)} with {FormatType(l)} and {FormatType(r)}",
                    "If you meant arithmetic, both sides need to be numbers.\nIf you meant to join text, that isn't available in Cufet yet.")),
            TokenType.Equal or TokenType.NotEqual
                when l == r
                => CufetType.Fact,
            TokenType.Equal or TokenType.NotEqual
                => throw new TypeException(FormatTypeError(
                    "equality comparison requires matching types",
                    null,
                    bin.Line,
                    $"compare a {FormatType(l)} to a {FormatType(r)}",
                    $"A {FormatType(l)} and a {FormatType(r)} can never be equal — this is likely a mistake. Check which side has the wrong type.")),
            TokenType.Lt or TokenType.Gt or TokenType.Lte or TokenType.Gte
                when l == CufetType.Number && r == CufetType.Number
                => CufetType.Fact,
            TokenType.Lt or TokenType.Gt or TokenType.Lte or TokenType.Gte
                => throw new TypeException(FormatTypeError(
                    "ordering works on numbers only",
                    null,
                    bin.Line,
                    $"order a {FormatType(l)} and a {FormatType(r)}",
                    "Ordering comparisons (>, <, >=, <=) require both sides to be numbers.")),
            _ => null
        };
    }

    // Scans a statement list for definite return paths.
    // Returns true only when every execution path through stmts ends at a return.
    private static bool DefinitelyReturns(IReadOnlyList<IStatement> stmts)
    {
        foreach (var stmt in stmts)
        {
            if (stmt is ReturnStatement) return true;
            if (stmt is IfStatement ifStmt && ifStmt.ElseBody != null)
            {
                bool allArmsReturn = ifStmt.Arms.All(a => DefinitelyReturns(a.Body));
                if (allArmsReturn && DefinitelyReturns(ifStmt.ElseBody)) return true;
            }
            // Loops are not counted: while/for-each may execute zero times,
            // repeat-until exits after one iteration without requiring a return.
        }
        return false;
    }

    private static string FormatTypeError(
        string context,
        string? established,
        int violationLine,
        string action,
        string fix)
    {
        var est = established != null ? $"\n{established}." : "";
        return $"That doesn't work: {context}.{est}\nHere on line {violationLine}, you're trying to {action}.\n\n{fix}";
    }

    private static string FormatType(CufetType type) => type switch
    {
        NumberType                           => "number",
        TextType                             => "text",
        FactType                             => "fact",
        SeriesType { ElementType: var elem } => $"series of {FormatTypePlural(elem)}",
        FunctionType ft                      => FormatFunctionType(ft),
        _                                    => "<unknown>",
    };

    private static string FormatFunctionType(FunctionType ft)
    {
        var ret = ft.ReturnType == null ? "void" : FormatType(ft.ReturnType);
        if (ft.ParameterTypes.Count == 0)
            return $"{ret} function";
        var paramTypes = string.Join(", ", ft.ParameterTypes.Select(FormatType));
        return $"{ret} function given ({paramTypes})";
    }

    private static string FormatTypePlural(CufetType type) => type switch
    {
        NumberType                           => "numbers",
        TextType                             => "text",
        FactType                             => "facts",
        SeriesType { ElementType: var elem } => $"series of {FormatTypePlural(elem)}",
        FunctionType                         => "functions",
        _                                    => "<unknown>",
    };

    private static string FormatExpr(IExpression expr) => expr switch
    {
        NumberLiteral    { Value: var v } => v.ToString(),
        StringLiteral    { Value: var v } => $"\"{v}\"",
        VariableReference { Name: var n } => n,
        _                                 => "<expression>",
    };

    private static string FormatOp(TokenType op) => op switch
    {
        TokenType.Plus  => "+",
        TokenType.Minus => "-",
        TokenType.Star  => "*",
        TokenType.Slash => "/",
        _               => op.ToString().ToLower(),
    };
}
