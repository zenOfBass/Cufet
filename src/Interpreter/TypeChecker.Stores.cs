namespace Cufet.Interpreter;

/// <summary>What a function's body stores into what it was given.</summary>
/// <remarks>
/// A pair says "this may be stored into that". Each side is a parameter's position, the receiver
/// (-1), or, as a source only, something the function makes (-2). An interface's method has no body
/// to read, so it names the interface and the method, and stands for every object's version.
/// </remarks>
public sealed class StoreSummary
{
    public HashSet<(int Source, int Target)> Pairs { get; } = [];
    public string? Interface { get; init; }
    public string? Method { get; init; }
    /// <summary>The body this function was written inside, or null at the top level. A pair that
    /// names a captured depth means that depth THERE.</summary>
    internal object? DefinedIn { get; set; }
}

public sealed partial class TypeChecker
{
    // ── What a function stores into its parameters (docs/SOUNDNESS.md, obligation O4) ─────────
    //
    // Inside a body every parameter has depth 0, and so does everything the body makes. Neither is
    // true where the function is CALLED: what it makes lies in the region open at the call, and
    // what it is given can lie anywhere outside that. So a store between the two cannot be judged
    // in the body. It is written down there, as a pair, and judged at each call, where both depths
    // are known.
    //
    // ★ Judged after every body has been checked, not as each call is met, so that a function
    // called above its own `Bind` is held to the same rule as one called below it.
    //
    // Three things can be on either side of a store in a body, beside names of its own:
    //   a parameter (or the receiver)   — depth known only at the call;
    //   something the body makes        — as deep as the call;
    //   a name it CAPTURED              — depth known where the function was written.
    // A pair may name any of them. One that names a captured depth is judged where the function
    // is cast from the body it was written in, which is the one place that counts rabbits the
    // way that depth was counted. A function written beside it, capturing the same names, takes
    // the pair over as its own. Anywhere else the two counts cannot be compared, and it is refused.
    //
    // ⚠ A function HANDED ON as a value (passed, kept in a field, returned) is still refused if it
    // stores anything: the rule for a cast through a function type is not settled.
    //
    // ⚠ Only a value that HOLDS A CONTAINER counts: a series, a map, or an object with one
    // somewhere inside. A number is in no region, and an object of numbers is copied whole.
    // Text is neither refused nor safe here; it wants a copy, which is the compiler's.

    private sealed class StoreNote(IExpression value, IExpression target, int line, int column)
    {
        public IExpression Value { get; } = value;
        public IExpression Target { get; } = target;
        public int Line { get; } = line;
        public int Column { get; } = column;
        public HashSet<int> Sources = [];
        public HashSet<int> Targets = [];
    }

    private sealed class CallNote
    {
        public required StoreSummary Callee;
        public required string Display;
        public required IReadOnlyList<IExpression> Args;
        public required IExpression? Receiver;
        public required int[] ArgDepths;
        public required int ReceiverDepth;
        public required int CallDepth;
        public required int Line;
        public required int Column;
        public required StoreFrame? Frame;
        public HashSet<int>[]? ArgSources;
        public HashSet<int>? ReceiverSources;
    }

    private sealed class StoreFrame
    {
        /// <summary>Where the pairs go. Null for a body no call can ask: a function value, a getter.</summary>
        public StoreSummary? Summary;
        /// <summary>Pairs such a body may make all the same, because the place that uses it checks them.</summary>
        public HashSet<(int, int)> Allowed = [];
        public required IReadOnlyList<IStatement> Body;
        public required Dictionary<string, int> ParamIdx;
        public required HashSet<int> RefParamIdxs;
        /// <summary>Each captured name, with its rabbit depth where this body was written.</summary>
        public required Dictionary<string, int> Captured;
        public required StoreFrame? Parent;
        public List<StoreNote> Stores = [];
        public List<CallNote> Calls = [];
        public bool Traced;
    }

    private StoreFrame? _storeFrame;
    private readonly List<StoreFrame> _storeFrames = [];
    private readonly List<CallNote> _callNotes = [];
    private readonly List<(StoreSummary Stores, string Name, int Line, int Column)> _functionValues = [];

    /// <summary>The index a captured name at this rabbit depth has in a pair.</summary>
    private static int OuterCode(int depth) =>
        depth == CapturedParameterDepth ? OuterUnknownIndex : OuterDepthIndex - depth;
    // A captured name whose depth nothing here can say: a parameter of the function around this
    // one, or a name that function captured in its turn.
    private const int OuterUnknownIndex = int.MinValue;
    private static bool IsOuter(int index) => index <= OuterDepthIndex;

    private StoreFrame OpenStoreFrame(
        StoreSummary? summary, IReadOnlyList<IStatement> body,
        IEnumerable<(string Name, CufetType? Type)> parameters, bool hasReceiver,
        Dictionary<string, int>? captured)
    {
        var paramIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        var refs     = new HashSet<int>();
        int index    = 0;
        foreach (var (name, type) in parameters)
        {
            paramIdx[name] = index;
            // An interface is no reference type to the rest of the checker, but what is passed as
            // one is an object, and a method reached through it can store into that object.
            if (IsReferenceType(type) || type is InterfaceType) refs.Add(index);
            index++;
        }
        if (hasReceiver)
        {
            paramIdx["one"] = ReceiverDepthIndex;
            refs.Add(ReceiverDepthIndex);
        }
        captured ??= new Dictionary<string, int>(StringComparer.Ordinal);
        // A parameter hides a captured name it shares a spelling with.
        foreach (var name in paramIdx.Keys) captured.Remove(name);
        // What the body around this one captured was counted somewhere further out still.
        if (_storeFrame is { } around)
            foreach (var name in around.Captured.Keys)
                if (captured.ContainsKey(name)) captured[name] = CapturedParameterDepth;
        if (summary != null) summary.DefinedIn = _storeFrame;
        return _storeFrame = new StoreFrame
        {
            Summary = summary, Body = body, ParamIdx = paramIdx, RefParamIdxs = refs,
            Captured = captured, Parent = _storeFrame,
        };
    }

    /// <summary>The names a body written inside another can reach that are not its own.</summary>
    private static Dictionary<string, int> CapturedNames(List<Dictionary<string, TypeInfo>> enclosing)
    {
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var scope in enclosing)
            foreach (var (name, info) in scope)
                if (info.Type is not FunctionType && !info.Permanent)
                    names[name] = info.IsParameter ? CapturedParameterDepth : info.RabbitDepth;
        return names;
    }

    private void CloseStoreFrame(StoreFrame frame)
    {
        // A body written inside another is traced NOW, while the names it captured and the
        // functions bound beside it are still in scope. A top-level body reaches nothing that the
        // top level will not still have at the end, so it waits, and is traced only if asked.
        if (frame.Parent != null || frame.Captured.Count > 0) Trace(frame);
        _storeFrames.Add(frame);
    }

    private void Trace(StoreFrame frame)
    {
        if (frame.Traced) return;
        frame.Traced = true;
        if (frame.Stores.Count == 0 && frame.Calls.Count == 0) return;
        var locals = LocalDepthsOf(frame.Body, frame.ParamIdx, frame.RefParamIdxs, frame.Captured, out _);
        HashSet<int> Of(IExpression expr) =>
            SymbolicExprDepth(expr, frame.ParamIdx, frame.RefParamIdxs, locals, frame.Captured);
        foreach (var store in frame.Stores)
        {
            store.Sources = Of(store.Value);
            store.Targets = Of(store.Target);
        }
        foreach (var call in frame.Calls)
        {
            call.ArgSources      = [.. call.Args.Select(Of)];
            call.ReceiverSources = call.Receiver is null ? null : Of(call.Receiver);
        }
    }

    /// <summary>Overload every store site uses: the container itself, so the store can be noted.</summary>
    private void CheckRegionStore(IExpression valueExpr, CufetType? valueType, IExpression target, int line, int col, string action)
    {
        CheckRegionStore(valueExpr, valueType, ContainerDepthOf(target), line, col, action);
        NoteStore(valueExpr, valueType, target, line, col);
    }

    private void NoteStore(IExpression value, CufetType? valueType, IExpression target, int line, int col)
    {
        if (_storeFrame is { } frame && HoldsContainer(valueType, null))
            frame.Stores.Add(new StoreNote(value, target, line, col));
    }

    private void NoteCall(
        FunctionType callee, string display, IReadOnlyList<IExpression> args, IExpression? receiver, int line, int col)
    {
        var note = new CallNote
        {
            Callee = callee.Stores, Display = display, Args = args, Receiver = receiver,
            ArgDepths = [.. args.Select(PlaceDepth)],
            ReceiverDepth = receiver is null ? 0 : PlaceDepth(receiver),
            CallDepth = _rabbitDepth, Line = line, Column = col, Frame = _storeFrame,
        };
        _callNotes.Add(note);
        _storeFrame?.Calls.Add(note);
    }

    // What is held in a name is as deep as the name, whatever its type: an object seen through an
    // interface is no reference type to ValueDepthOf, and would read as 0.
    private int PlaceDepth(IExpression expr) =>
        expr is VariableReference held && TryLookup(held.Name, out var info) ? info!.RabbitDepth
        : ValueDepthOf(expr, InferType(expr));

    /// <summary>A function's name read as a VALUE, not called. Remembered for the end.</summary>
    private TypeInfo NoteFunctionValue(TypeInfo info, string name, IExpression expr)
    {
        if (info.Type is FunctionType function && expr is VariableReference named)
            _functionValues.Add((function.Stores, name, named.Line, named.Column));
        return info;
    }

    /// <summary>Whether a value of this type carries a series or a map: itself, or inside it.</summary>
    private bool HoldsContainer(CufetType? type, HashSet<string>? seen) => type switch
    {
        SeriesType or MapType or MatrixType or ChannelType or AddressType => true,
        ObjectType objectType => ObjectHoldsContainer(objectType, seen ?? new HashSet<string>(StringComparer.Ordinal)),
        _ => false,
    };

    private bool ObjectHoldsContainer(ObjectType written, HashSet<string> seen)
    {
        // The registered type, since one that was merely written carries no fields.
        if (!_objectDefs.TryGetValue(written.Name, out var objectType)) return true;
        if (!seen.Add(objectType.Name)) return false;
        if (objectType.EmbeddedTypeName is { } embedded
            && (!_objectDefs.TryGetValue(embedded, out var inner) || ObjectHoldsContainer(inner, seen)))
            return true;
        return objectType.PositionalTypes.Any(field => FieldHoldsContainer(field, seen))
            || objectType.NamedFields.Any(field => FieldHoldsContainer(field.FieldType, seen));
    }

    private bool FieldHoldsContainer(CufetType? type, HashSet<string> seen) => type switch
    {
        null or NumberType or BitsType or FactType or VoidType or TextType => false,
        ObjectType objectType => ObjectHoldsContainer(objectType, seen),
        RecordType record     => record.PositionalTypes.Any(field => FieldHoldsContainer(field, seen))
                              || record.NamedFields.Any(field => FieldHoldsContainer(field.Type, seen)),
        // A voidable object can be held behind a pointer, which is a thing in a region.
        VoidableType { Inner: ObjectType or RecordType } => true,
        VoidableType voidable => FieldHoldsContainer(voidable.Inner, seen),
        FailureType failure   => FieldHoldsContainer(failure.Inner, seen),
        UnionType union       => union.Cases == null || union.Cases.Any(one => FieldHoldsContainer(one, seen)),
        // A series, a map, a function and its captures, and anything not named above.
        _ => true,
    };

    private List<(int Source, int Target)> PairsOf(StoreSummary summary)
    {
        if (summary.Interface is not { } interfaceName) return [.. summary.Pairs];
        var all = new HashSet<(int, int)>();
        foreach (var objectType in _objectDefs.Values)
            if (objectType.ConformedInterfaces.Contains(interfaceName)
                && FindMethodInOtOrPromoted(objectType, summary.Method!) is { } method)
                all.UnionWith(method.Stores.Pairs);
        return [.. all];
    }

    /// <summary>Every store a body makes, by its own hand or through a function it calls.</summary>
    private IEnumerable<(HashSet<int> Sources, HashSet<int> Targets, int Line, int Column, string? Through)>
        StoresOf(StoreFrame frame)
    {
        if (frame.Stores.Count > 0) Trace(frame);
        foreach (var store in frame.Stores)
            yield return (store.Sources, store.Targets, store.Line, store.Column, null);
        foreach (var call in frame.Calls)
        {
            var pairs = PairsOf(call.Callee);
            if (pairs.Count == 0) continue;
            Trace(frame);
            foreach (var (source, target) in pairs)
                if (PlaceSources(frame, call, source) is { } sources
                    && PlaceSources(frame, call, target) is { } targets)
                    yield return (sources, targets, call.Line, call.Column, call.Display);
        }
    }

    /// <summary>What one side of a callee's pair is, in the terms of the body making the call.</summary>
    private static HashSet<int>? PlaceSources(StoreFrame frame, CallNote call, int place)
    {
        if (place == FreshDepthIndex) return [FreshDepthIndex];
        if (place == ReceiverDepthIndex) return call.ReceiverSources;
        if (place >= 0) return place < call.Args.Count ? call.ArgSources![place] : null;
        // A captured name of the callee's.
        // Written in this body: the name is one of this body's own, made here.
        if (ReferenceEquals(call.Callee.DefinedIn, frame)) return [FreshDepthIndex];
        // Written beside this body: the same name, captured by both, at the same depth.
        if (ReferenceEquals(call.Callee.DefinedIn, frame.Parent)) return [place];
        return [OuterUnknownIndex];
    }

    private enum StoreVerdict { Nothing, Pair, Refused }

    private static StoreVerdict Judge(StoreFrame frame, int source, int target)
    {
        if (source == target || target == FreshDepthIndex) return StoreVerdict.Nothing;
        bool unknown = source == OuterUnknownIndex || target == OuterUnknownIndex;
        // Both captured: both depths are known in the body, and CheckRegionStore judged them there.
        if (IsOuter(source) && IsOuter(target)) return unknown ? StoreVerdict.Refused : StoreVerdict.Nothing;
        if (unknown) return StoreVerdict.Refused;
        return frame.Summary != null || frame.Allowed.Contains((source, target))
            ? StoreVerdict.Pair : StoreVerdict.Refused;
    }

    private void CheckStoresAtCalls()
    {
        try
        {
            // What each body stores, to a fixed point: a function that hands its parameter on to
            // one that stores into it stores into it too.
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var frame in _storeFrames)
                {
                    if (frame.Summary is not { } summary) continue;
                    foreach (var store in StoresOf(frame).ToList())
                        foreach (var source in store.Sources)
                            foreach (var target in store.Targets)
                                if (Judge(frame, source, target) == StoreVerdict.Pair)
                                    changed |= summary.Pairs.Add((source, target));
                }
            }

            foreach (var frame in _storeFrames)
                foreach (var store in StoresOf(frame).ToList())
                    foreach (var source in store.Sources)
                        foreach (var target in store.Targets)
                            if (Judge(frame, source, target) == StoreVerdict.Refused)
                                throw RefusedInBody(source, target, store.Line, store.Column, store.Through);

            foreach (var (stores, name, line, column) in _functionValues)
                if (PairsOf(stores).Count > 0)
                    throw TypeError(
                        (name == "this function" ? name : $"'{name}'")
                      + " stores into what it is given or what is around it, so it cannot be handed on as a value",
                        "Each cast of it is checked against what it stores, and a cast through a value "
                      + "does not say which function it reaches",
                        line, column,
                        "use it without casting it",
                        "Hold it in a name with 'Define' and cast it by that name.");

            foreach (var call in _callNotes)
                foreach (var (source, target) in PairsOf(call.Callee))
                    RefuseAtCall(call, source, target);
        }
        finally
        {
            _storeFrames.Clear();
            _callNotes.Clear();
            _functionValues.Clear();
        }
    }

    private static TypeException RefusedInBody(int source, int target, int line, int column, string? through)
    {
        string doing = through is null ? "this stores" : $"this casts {through}, which stores";
        if (IsOuter(source) || IsOuter(target))
            return TypeError(
                $"{doing} between what this function was given or made and a name from outside it, "
              + "and nothing here can say which of the two lives longer",
                "That name is a parameter of the function around this one, or comes from further out than that",
                line, column,
                "store where the value may not live long enough",
                "Pass the container in as a parameter, or give the value back with 'Return' and store it where the function is cast.");
        return TypeError(
            $"{doing} a series or an object into something it was given, where no cast can be checked",
            "A getter, a setter, an operator and an unmaker are not cast, so there is no call to check what they store against",
            line, column,
            "store into what it was given",
            "Do the storing in a method, and cast that.");
    }

    private static string PlaceName(int place) =>
        place == FreshDepthIndex ? "something it makes"
        : place == ReceiverDepthIndex ? "the object it is cast on"
        : IsOuter(place) ? "a name from outside it"
        : $"argument {place + 1}";

    private static void RefuseAtCall(CallNote call, int source, int target)
    {
        // A captured depth means something only in the body the callee was written in.
        bool written = ReferenceEquals(call.Callee.DefinedIn, call.Frame);
        int? DepthOf(int place) =>
            place == FreshDepthIndex ? call.CallDepth
            : place == ReceiverDepthIndex ? (call.Receiver is null ? null : call.ReceiverDepth)
            : place >= 0 ? (place < call.ArgDepths.Length ? call.ArgDepths[place] : null)
            : written && place != OuterUnknownIndex ? OuterDepthIndex - place
            : null;

        if (DepthOf(source) is not { } from || DepthOf(target) is not { } into || from <= into) return;
        throw TypeError(
            "this value lives in a shorter-lived region than its destination — it will be gone when that region ends",
            $"{char.ToUpperInvariant(call.Display[0])}{call.Display[1..]} stores {PlaceName(source)} into {PlaceName(target)}"
          + (source == FreshDepthIndex ? ", and what a function makes lives in the region it is cast in" : ""),
            call.Line, call.Column,
            source == FreshDepthIndex
                ? "cast it inside a region, on a container from outside that region"
                : "cast it with a value from a shorter-lived region than the container",
            "Declare the container inside that region too, so both end at its 'Done.', "
          + "or cast it before the region opens.");
    }
}
