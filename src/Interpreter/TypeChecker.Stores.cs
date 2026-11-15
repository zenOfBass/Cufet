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
    /// <summary>A body was checked for it. Without one nothing is known of what it does with a
    /// function it is handed.</summary>
    internal bool Known { get; set; }
    /// <summary>The body reaches names from outside itself.</summary>
    internal bool Captures { get; set; }
    /// <summary>Function parameters the body casts inside a rabbit of its own, or keeps, or hands
    /// to something that does. A function that stores may not be passed for one of these.</summary>
    internal HashSet<int> Wild { get; } = [];
    /// <summary>Function parameters handed straight on, as that parameter of that function.</summary>
    internal List<(int Parameter, StoreSummary To, int ToParameter)> Relays { get; } = [];
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
    // ★ A function HANDED TO ANOTHER FUNCTION is judged where it is handed over. Its captured
    // depths are known there, and so are the other arguments; what is not known is what the
    // receiver will cast it ON. That can only be the receiver's other arguments, what the receiver
    // makes, or what the receiver captured, so the pass is judged as though the function were
    // cast right there on any of those. The one thing that would make this wrong is the receiver
    // casting it somewhere DEEPER, inside a rabbit of its own, so each function records which of
    // its function parameters it does that with (or keeps, or relays to a function that does),
    // and a function that stores is refused for such a parameter. One that stores nothing passes
    // anywhere. A cast through a function TYPE needs no check of its own because of this.
    //
    // ⚠ A function that stores, kept in a field or a series or handed back with `Return`, is
    // refused: nothing follows it there.
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
        /// <summary>The field a field set writes; the target is then the object.</summary>
        public string? Field { get; init; }
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
        /// <summary>Which arguments can hold a container, so that a number's depth is not read.</summary>
        public required bool[] ArgHolds;
        /// <summary>Functions handed over in this call: which argument, and what the function stores.</summary>
        public List<(int Argument, StoreSummary Function)> Passes = [];
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
        /// <summary>Each captured name, with the index it has in a pair: its rabbit depth where
        /// this body was written, or the parameter it is there. See OuterCode.</summary>
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
    // A function PARAMETER read as a value. Taken back off when the read turns out to be a plain
    // hand-over to another function; what is left at the end makes the parameter wild.
    private readonly List<(StoreSummary Owner, int Parameter, int Line, int Column)> _parameterValues = [];

    /// <summary>The index a captured name at this rabbit depth has in a pair.</summary>
    private static int OuterCode(int depth) =>
        depth == CapturedParameterDepth ? OuterUnknownIndex : OuterDepthIndex - depth;
    // A captured name whose depth nothing here can say: a parameter of the function around this
    // one, or a name that function captured in its turn.
    private const int OuterUnknownIndex = int.MinValue;
    private static bool IsOuter(int index) => index <= OuterDepthIndex;
    // A captured name that is a PARAMETER of the body this one was written in (-1 for its
    // receiver). Its depth is known no better there than here, but there it is a parameter like
    // any other, and the pair is carried up to that body's own.
    private const int OuterParameterBase = -1_000_001;
    private static int OuterParameterCode(int parameter) => OuterParameterBase - parameter;
    private static bool IsOuterParameter(int index) => index <= OuterParameterBase + 1 && index != OuterUnknownIndex;
    // The rabbit depth a captured place has in the body it was captured from.
    private static int OuterDepth(int index) => IsOuterParameter(index) ? 0 : OuterDepthIndex - index;

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
        foreach (var name in captured.Keys.ToList())
            captured[name] =
                // What the body around this one captured was counted somewhere further out still.
                _storeFrame is { } around && around.Captured.ContainsKey(name) ? OuterUnknownIndex
                : _storeFrame is { } owner && owner.ParamIdx.TryGetValue(name, out int parameter) ? OuterParameterCode(parameter)
                : OuterCode(captured[name]);
        if (summary != null)
        {
            summary.DefinedIn = _storeFrame;
            summary.Captures  = captured.Count > 0;
        }
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
        if (frame.Summary != null) frame.Summary.Known = true;
        _storeFrames.Add(frame);
    }

    private void Trace(StoreFrame frame)
    {
        if (frame.Traced) return;
        frame.Traced = true;
        if (frame.Stores.Count == 0 && frame.Calls.Count == 0) return;
        var locals = LocalDepthsOf(frame.Body, frame.ParamIdx, frame.RefParamIdxs, frame.Captured, out _,
                                   frame, out var holds);
        HashSet<int> Of(IExpression expr) =>
            ReachOf(expr, frame.ParamIdx, frame.RefParamIdxs, locals, frame.Captured, holds);
        foreach (var store in frame.Stores)
        {
            store.Sources = Of(store.Value);
            // Storing into the name itself reaches only what the name is; storing into something
            // inside it reaches anything it was given as well (O2).
            store.Targets = store.Target is VariableReference
                ? SymbolicExprDepth(store.Target, frame.ParamIdx, frame.RefParamIdxs, locals, frame.Captured)
                : Of(store.Target);
        }
        foreach (var call in frame.Calls)
        {
            call.ArgSources      = [.. call.Args.Select(Of)];
            call.ReceiverSources = call.Receiver is null ? null : Of(call.Receiver);
        }
    }

    /// <summary>Overload every store site uses: the container itself, so the store can be noted.</summary>
    /// <remarks><paramref name="field"/> is the field a field set writes; its target is the object.</remarks>
    private void CheckRegionStore(IExpression valueExpr, CufetType? valueType, IExpression target, int line, int col, string action,
                                  string? field = null)
    {
        CheckRegionStore(valueExpr, valueType, ContainerDepthOf(target), line, col, action);
        NoteStore(valueExpr, valueType, target, line, col, field);
        NoteFloorStore(valueExpr, valueType, target, field, line, col, action);
    }

    private void NoteStore(IExpression value, CufetType? valueType, IExpression target, int line, int col, string? field = null)
    {
        if (_storeFrame is { } frame && HoldsContainer(valueType, null))
            frame.Stores.Add(new StoreNote(value, target, line, col) { Field = field });
    }

    /// <summary>Notes a cast for the store rule, and gives back how deep anything it hands back
    /// can reach (O2's floor of the cast).</summary>
    private DepthTerm NoteCall(
        FunctionType callee, string display, IReadOnlyList<IExpression> args, IExpression? receiver,
        int line, int col, IExpression function)
    {
        var types = args.Select(InferType).ToList();
        var castFloor = NoteCallFloors(callee.Stores, args, types, receiver, function);
        var note = new CallNote
        {
            Callee = callee.Stores, Display = display, Args = args, Receiver = receiver,
            ArgDepths = [.. args.Select((arg, i) =>
                arg is VariableReference held && TryLookup(held.Name, out var info) ? info!.RabbitDepth
                : ValueDepthOf(arg, types[i]))],
            ArgHolds = [.. types.Select(type => IsReferenceType(type) || type is InterfaceType)],
            ReceiverDepth = receiver is null ? 0 : PlaceDepth(receiver),
            CallDepth = _rabbitDepth, Line = line, Column = col, Frame = _storeFrame,
        };
        _callNotes.Add(note);
        _storeFrame?.Calls.Add(note);

        // A function parameter cast anywhere but plainly in its own body, outside every rabbit of
        // that body, may be cast deeper than where it was handed over.
        if (function is VariableReference cast && FunctionParameter(cast.Name) is { } castParameter
            && (!ReferenceEquals(castParameter.Owner, _storeFrame) || _rabbitDepth > 0))
            castParameter.Owner.Summary?.Wild.Add(castParameter.Index);

        for (int i = 0; i < args.Count; i++)
        {
            if (types[i] is not FunctionType handed) continue;
            // Handed over in a call: judged here, so the note that it was read as a value goes.
            if (args[i] is VariableReference named)
            {
                _functionValues.RemoveAll(v => v.Name == named.Name && v.Line == named.Line && v.Column == named.Column);
                if (FunctionParameter(named.Name) is { } relayed)
                {
                    _parameterValues.RemoveAll(v => v.Line == named.Line && v.Column == named.Column);
                    if (ReferenceEquals(relayed.Owner, _storeFrame) && _rabbitDepth == 0)
                        relayed.Owner.Summary?.Relays.Add((relayed.Index, callee.Stores, i));
                    else
                        relayed.Owner.Summary?.Wild.Add(relayed.Index);
                    continue;
                }
            }
            else if (args[i] is LambdaLiteral written)
                _functionValues.RemoveAll(v => v.Name == "this function" && v.Line == written.Line && v.Column == written.Column);
            else
                continue;
            note.Passes.Add((i, handed.Stores));
        }
        return castFloor;
    }

    /// <summary>The body a name is a function-typed PARAMETER of, looking outward from here.</summary>
    private (StoreFrame Owner, int Index)? FunctionParameter(string name)
    {
        if (!TryLookup(name, out var info) || !info!.IsParameter || info.Type is not FunctionType) return null;
        for (var frame = _storeFrame; frame != null; frame = frame.Parent)
            if (frame.ParamIdx.TryGetValue(name, out int index) && index >= 0) return (frame, index);
        return null;
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
        {
            _functionValues.Add((function.Stores, name, named.Line, named.Column));
            if (FunctionParameter(name) is { } parameter && parameter.Owner.Summary is { } owner)
                _parameterValues.Add((owner, parameter.Index, named.Line, named.Column));
        }
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
        // ⚠ In one fixed order, here and wherever a set of places is walked below. Which pair is
        // met first decides which refusal is given, and the checker written in Cufet has to give
        // the same one; the order a set happens to hand its members back in is nothing it can copy.
        if (summary.Interface is not { } interfaceName) return [.. summary.Pairs.Order()];
        var all = new HashSet<(int, int)>();
        foreach (var objectType in _objectDefs.Values)
            if (objectType.ConformedInterfaces.Contains(interfaceName)
                && FindMethodInOtOrPromoted(objectType, summary.Method!) is { } method)
                all.UnionWith(method.Stores.Pairs);
        return [.. all.Order()];
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
            if (pairs.Count > 0)
            {
                Trace(frame);
                foreach (var (source, target) in pairs)
                    if (PlaceSources(frame, call, call.Callee, source) is { } sources
                        && PlaceSources(frame, call, call.Callee, target) is { } targets)
                        yield return (sources, targets, call.Line, call.Column, call.Display);
            }

            // A function handed over here, judged as though it were cast here on anything the
            // receiver could cast it on.
            foreach (var (_, function) in call.Passes)
            {
                var stored = PairsOf(function);
                if (stored.Count == 0) continue;
                Trace(frame);
                var reach = ReceiverReach(frame, call);
                foreach (var (source, target) in stored)
                    yield return (HandedSide(frame, function, source, reach), HandedSide(frame, function, target, reach),
                                  call.Line, call.Column, "the function handed to " + call.Display);
            }
        }
    }

    /// <summary>Everything the receiver of a call could cast a function it is handed on, in the
    /// terms of the body making the call.</summary>
    private static HashSet<int> ReceiverReach(StoreFrame frame, CallNote call)
    {
        var reach = new HashSet<int> { FreshDepthIndex };
        for (int i = 0; i < call.Args.Count; i++)
            if (call.ArgHolds[i]) reach.UnionWith(call.ArgSources![i]);
        if (call.ReceiverSources != null) reach.UnionWith(call.ReceiverSources);
        if (call.Callee.Captures)
        {
            // What the receiver captured. Written in this body, those are this body's names.
            if (ReferenceEquals(call.Callee.DefinedIn, frame)) reach.UnionWith(UntracedDepth(frame.RefParamIdxs));
            else if (ReferenceEquals(call.Callee.DefinedIn, frame.Parent))
                reach.UnionWith(frame.Captured.Values);
            else reach.Add(OuterUnknownIndex);
        }
        return reach;
    }

    private static HashSet<int> HandedSide(StoreFrame frame, StoreSummary function, int place, HashSet<int> reach)
    {
        if (place == FreshDepthIndex) return [FreshDepthIndex];
        if (!IsOuter(place)) return reach;
        return CapturedPlace(frame, function, place);
    }

    /// <summary>A captured place of some function, in the terms of the body that casts or hands it.</summary>
    private static HashSet<int> CapturedPlace(StoreFrame frame, StoreSummary function, int place)
    {
        if (place == OuterUnknownIndex) return [place];
        // Written in this body: the name is one of this body's own, a parameter or made here.
        if (ReferenceEquals(function.DefinedIn, frame))
            return [IsOuterParameter(place) ? OuterParameterBase - place : FreshDepthIndex];
        // Written beside this body: the same name, captured by both, the same way.
        if (ReferenceEquals(function.DefinedIn, frame.Parent)) return [place];
        return [OuterUnknownIndex];
    }

    /// <summary>What one side of a callee's pair is, in the terms of the body making the call.</summary>
    private static HashSet<int>? PlaceSources(StoreFrame frame, CallNote call, StoreSummary callee, int place)
    {
        if (place == FreshDepthIndex) return [FreshDepthIndex];
        if (place == ReceiverDepthIndex) return call.ReceiverSources;
        if (place >= 0) return place < call.Args.Count ? call.ArgSources![place] : null;
        return CapturedPlace(frame, callee, place);
    }

    /// <summary>Whether a function may cast this parameter of its own deeper than it was handed it.</summary>
    private bool IsWild(StoreSummary receiver, int parameter)
    {
        if (receiver.Interface is { } interfaceName)
            return _objectDefs.Values.Any(objectType =>
                objectType.ConformedInterfaces.Contains(interfaceName)
                && FindMethodInOtOrPromoted(objectType, receiver.Method!) is { } method
                && IsWild(method.Stores, parameter));
        return !receiver.Known || receiver.Wild.Contains(parameter);
    }

    private enum StoreVerdict { Nothing, Pair, Refused }

    private static StoreVerdict Judge(StoreFrame frame, int source, int target)
    {
        if (source == target || target == FreshDepthIndex) return StoreVerdict.Nothing;
        bool unknown = source == OuterUnknownIndex || target == OuterUnknownIndex;
        if (unknown) return StoreVerdict.Refused;
        // Both captured, at depths known in the body: CheckRegionStore judged them there. With a
        // parameter of the body around on either side it is that body's pair, like any other.
        if (IsOuter(source) && IsOuter(target) && !IsOuterParameter(source) && !IsOuterParameter(target))
            return StoreVerdict.Nothing;
        return frame.Summary != null || frame.Allowed.Contains((source, target))
            ? StoreVerdict.Pair : StoreVerdict.Refused;
    }

    private void CheckStoresAtCalls(bool judge)
    {
        try
        {
            if (!judge) return;
            // A function parameter read as a value and not simply handed on is kept somewhere.
            foreach (var (owner, parameter, _, _) in _parameterValues) owner.Wild.Add(parameter);

            // What each body stores, to a fixed point: a function that hands its parameter on to
            // one that stores into it stores into it too. The same for a function parameter handed
            // on to a function that casts it deeper.
            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var frame in _storeFrames)
                    if (frame.Summary is { } relaying)
                        foreach (var (parameter, to, toParameter) in relaying.Relays)
                            if (IsWild(to, toParameter)) changed |= relaying.Wild.Add(parameter);
                foreach (var frame in _storeFrames)
                {
                    if (frame.Summary is not { } summary) continue;
                    foreach (var store in StoresOf(frame).ToList())
                        foreach (var source in store.Sources.Order())
                            foreach (var target in store.Targets.Order())
                                if (Judge(frame, source, target) == StoreVerdict.Pair)
                                    changed |= summary.Pairs.Add((source, target));
                }
            }

            foreach (var frame in _storeFrames)
                foreach (var store in StoresOf(frame).ToList())
                    foreach (var source in store.Sources.Order())
                        foreach (var target in store.Targets.Order())
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
                        "keep it where nothing can follow it",
                        "Hold it in a name with 'Define' and cast it by that name, or hand it straight to the function that casts it.");

            foreach (var call in _callNotes)
            {
                foreach (var (source, target) in PairsOf(call.Callee))
                    RefuseAtCall(call, source, target);
                foreach (var (argument, function) in call.Passes)
                    RefuseAtPass(call, argument, function);
            }

            JudgeFloorStores();
        }
        finally
        {
            _storeFrames.Clear();
            _callNotes.Clear();
            _functionValues.Clear();
            _parameterValues.Clear();
            _depthStarts.Clear();
            _depthBounds.Clear();
            _splitFields.Clear();
            _castTerms.Clear();
            _castFloorNotes.Clear();
            _innerFunctions.Clear();
            _floorStores.Clear();
        }
    }

    private void RefuseAtPass(CallNote call, int argument, StoreSummary function)
    {
        var stored = PairsOf(function);
        if (stored.Count == 0) return;
        if (IsWild(call.Callee, argument))
            throw TypeError(
                "this function stores into what it is given or what is around it, and it is handed to something that may cast it in a shorter-lived region",
                $"It is handed to {call.Display}, which casts the function it is given inside a rabbit of its own, or keeps it, or has no body to read",
                call.Line, call.Column,
                $"hand it over as argument {argument + 1}",
                "Hand over a function that gives its result back with 'Return', and store the result here.");

        // As though cast here: what it makes is as deep as this call, and what it is given is as
        // deep as this call or as any container handed over beside it.
        bool written = ReferenceEquals(function.DefinedIn, call.Frame);
        var reach = new List<int> { call.CallDepth };
        for (int i = 0; i < call.ArgDepths.Length; i++)
            if (call.ArgHolds[i]) reach.Add(call.ArgDepths[i]);
        if (call.Receiver != null) reach.Add(call.ReceiverDepth);
        // What the receiver captured is no deeper than where the receiver was written.
        if (call.Callee.Captures) reach.Add(0);

        (int Deepest, int Shallowest)? DepthsOf(int place) =>
            place == FreshDepthIndex ? (call.CallDepth, call.CallDepth)
            : !IsOuter(place) ? (reach.Max(), reach.Min())
            : written && place != OuterUnknownIndex ? (OuterDepth(place), OuterDepth(place))
            : null;

        foreach (var (source, target) in stored)
        {
            if (target == FreshDepthIndex) continue;
            if (DepthsOf(source) is not { } from || DepthsOf(target) is not { } into
                || from.Deepest <= into.Shallowest) continue;
            throw TypeError(
                "this value lives in a shorter-lived region than its destination — it will be gone when that region ends",
                $"The function handed to {call.Display} stores {PlaceName(source)} into {PlaceName(target)}, "
              + $"and {call.Display} can cast it on anything it is given here or makes here",
                call.Line, call.Column,
                $"hand it over as argument {argument + 1}, inside a region, with a container from outside that region in reach",
                "Declare the container inside that region too, so both end at its 'Done.', "
              + "or make the cast before the region opens.");
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
            : written && place != OuterUnknownIndex ? OuterDepth(place)
            : null;

        if (DepthOf(source) is not { } from || DepthOf(target) is not { } into || from <= into) return;
        throw TypeError(
            "this value lives in a shorter-lived region than its destination — it will be gone when that region ends",
            $"This cast reaches {call.Display}, which stores {PlaceName(source)} into {PlaceName(target)}"
          + (source == FreshDepthIndex ? ", and what a function makes lives in the region it is cast in" : ""),
            call.Line, call.Column,
            source == FreshDepthIndex
                ? "cast it inside a region, on a container from outside that region"
                : "cast it with a value from a shorter-lived region than the container",
            "Declare the container inside that region too, so both end at its 'Done.', "
          + "or cast it before the region opens.");
    }

    // ── Where a store's target really lives (docs/SOUNDNESS.md, obligation O2) ─────────────────
    //
    // A name's rabbit depth says where the NAME was made, which is no help when what it reaches
    // was made further out: a second name for an outer series, an object made here holding one
    // from outside, an element of a table. So each binding carries two more numbers, under
    // `name@line`, both starting at its rabbit depth and only ever lowered:
    //   Own   — no deeper than the container the name itself is;
    //   Floor — no deeper than anything reachable through it.
    // Nothing is lowered as it is met. Each statement that could lower one is written down as a
    // BOUND — "this number is no deeper than the least of these numbers and this depth" — and the
    // bounds are solved together once everything has been checked, when what each cast's callee
    // stores is known too. A loop, a second name made before a store through the first, a cast
    // met before what it stores is known: one fixed point covers all of them. Each store of
    // something that holds a container is judged against the solution: storing into the name
    // itself against its Own, into anything reached through it against its Floor.
    //
    // ★ A field of an object WRITTEN OUT in a `Define` is a name of its own, `name@line.field`:
    // the object is held by value, so its field is the very series that was put there, and a
    // store into it says nothing about what else the object holds.
    //
    // ⚠ The checker written in Cufet keeps the same numbers under the same keys
    // (`self-hosting/Cufet/checker/stores.cufe`), and has to refuse the same store first.

    /// <summary>The least of a depth and some names' numbers ("own K" or "floor K").</summary>
    private sealed class DepthTerm
    {
        public int Least = int.MaxValue;
        public readonly List<string> Keys = [];
        public static DepthTerm Of(int depth) => new() { Least = depth };
        public static DepthTerm Ref(string key) { var term = new DepthTerm(); term.Keys.Add(key); return term; }
        public DepthTerm With(DepthTerm other)
        {
            var both = new DepthTerm { Least = Math.Min(Least, other.Least) };
            both.Keys.AddRange(Keys);
            both.Keys.AddRange(other.Keys);
            return both;
        }
    }

    private sealed record FloorStore(int Upper, DepthTerm Limit, int Line, int Column, string Action);
    private sealed record CastPart(int Position, string? Key, bool Exact, DepthTerm Term);
    private sealed record CastFloorNote(StoreSummary Callee, List<CastPart> Parts, int Depth);

    private readonly Dictionary<string, int> _depthStarts = new(StringComparer.Ordinal);
    private readonly List<(string Bounded, DepthTerm Term)> _depthBounds = [];
    // A field of an object written out, by the object's key: its own name, `key.field`.
    private readonly Dictionary<string, HashSet<string>> _splitFields = new(StringComparer.Ordinal);
    // What each cast hands back can reach no deeper than this.
    private readonly Dictionary<CastExpression, DepthTerm> _castTerms = new(ReferenceEqualityComparer.Instance);
    private readonly List<CastFloorNote> _castFloorNotes = [];
    // Functions bound inside another body: what they hand back may be something they captured.
    private readonly HashSet<string> _innerFunctions = new(StringComparer.Ordinal);
    private readonly List<FloorStore> _floorStores = [];

    // A parameter, and the object a method is cast on, can be anything from outside.
    private static int StartDepth(string name, TypeInfo info) =>
        info.IsParameter || name == "one" ? 0 : info.RabbitDepth;

    /// <summary>The key a name is known by here, or null.</summary>
    private string? DepthKey(string name)
    {
        if (!TryLookup(name, out var info)) return null;
        string key = $"{name}@{info!.EstablishingLine}";
        _depthStarts.TryAdd(key, StartDepth(name, info));
        return key;
    }

    private bool IsSplitField(string key, string field) =>
        _splitFields.TryGetValue(key, out var fields) && fields.Contains(field);

    /// <summary>The name a target or a value starts from — a field of an object written out, when
    /// it is reached through one — and whether the expression IS that name.</summary>
    private (string? Key, bool Exact) DepthTarget(IExpression expr)
    {
        IExpression node = expr;
        IExpression? above = null;
        while (IsAccess(node))
        {
            above = node;
            node = node switch
            {
                SeriesAccess access     => access.Target,
                MapLookup lookup        => lookup.Map,
                PossessiveAccess field  => field.Target,
                RecordNamedAccess named => named.Record,
                _ => node,
            };
        }
        if (node is not VariableReference root || DepthKey(root.Name) is not { } key) return (null, false);
        string? member = above switch
        {
            PossessiveAccess field  => field.Member,
            RecordNamedAccess named => named.FieldName,
            _ => null,
        };
        if (member != null && IsSplitField(key, member)) return ($"{key}.{member}", ReferenceEquals(above, expr));
        return (key, expr is VariableReference);
    }

    /// <summary>What lowering a whole name lowers: its floor, and every field of its own.</summary>
    private List<string> WholeTargets(string key, bool fieldsReplaced)
    {
        var targets = new List<string> { "floor " + key };
        if (_splitFields.TryGetValue(key, out var fields))
            foreach (var field in fields.Order(StringComparer.Ordinal))
            {
                targets.Add($"floor {key}.{field}");
                if (fieldsReplaced) targets.Add($"own {key}.{field}");
            }
        return targets;
    }

    /// <summary>What lowering through a target lowers.</summary>
    private List<string> TargetsOf(string key, bool exact, bool fieldsReplaced) =>
        key.Contains('.') || !exact ? ["floor " + key] : WholeTargets(key, fieldsReplaced);

    private void Bound(string bounded, DepthTerm term) => _depthBounds.Add((bounded, term));

    private static bool IsMadeHere(IExpression expr) =>
        expr is SeriesLiteral or MapLiteral or ObjectLiteral or MatrixLiteral or MatrixSized
             or RangeExpression or ChannelCreation or DeliveryExpression;

    /// <summary>How deep the container an expression IS may lie, at the lowest.</summary>
    private DepthTerm OwnTerm(IExpression expr, CufetType? type)
    {
        switch (expr)
        {
            case VariableReference named:
                return DepthKey(named.Name) is { } key ? DepthTerm.Ref("own " + key) : DepthTerm.Of(0);
            case ButVoidDefault either:
                return OwnTerm(either.Voidable, type).With(OwnTerm(either.Default, type));
            case FailureFallback either:
                return OwnTerm(either.Fallible, type).With(OwnTerm(either.Default, type));
        }
        if (IsMadeHere(expr)) return DepthTerm.Of(_rabbitDepth);
        if (IsAccess(expr) && DepthTarget(expr) is { Key: { } field, Exact: true } && field.Contains('.'))
            return DepthTerm.Ref("own " + field);
        return FloorTerm(expr, type);
    }

    /// <summary>How deep anything reachable through an expression may lie, at the lowest.
    /// <paramref name="type"/> is what it holds, when known; it says which items of something
    /// written out can hold a container.</summary>
    private DepthTerm FloorTerm(IExpression expr, CufetType? type)
    {
        switch (expr)
        {
            case VariableReference named:
                return DepthKey(named.Name) is { } key ? DepthTerm.Ref("floor " + key) : DepthTerm.Of(0);
            case SeriesLiteral made:
            {
                var term = DepthTerm.Of(_rabbitDepth);
                var element = (type as SeriesType)?.ElementType;
                if (type is not SeriesType || HoldsContainer(element, null))
                    foreach (var item in made.Elements) term = term.With(FloorTerm(item, element));
                return term;
            }
            case MapLiteral made:
            {
                var term = DepthTerm.Of(_rabbitDepth);
                var mapType = type as MapType;
                bool keys = mapType == null || HoldsContainer(mapType.KeyType, null);
                bool values = mapType == null || HoldsContainer(mapType.ValueType, null);
                foreach (var (key, value) in made.Pairs)
                {
                    if (keys) term = term.With(FloorTerm(key, mapType?.KeyType));
                    if (values) term = term.With(FloorTerm(value, mapType?.ValueType));
                }
                return term;
            }
            case ObjectLiteral made:
            {
                var term = DepthTerm.Of(_rabbitDepth);
                _objectDefs.TryGetValue(made.TypeName, out var madeType);
                for (int i = 0; i < made.PositionalValues.Count; i++)
                {
                    var fieldType = madeType != null && i < madeType.PositionalTypes.Count ? madeType.PositionalTypes[i] : null;
                    if (fieldType == null || FieldHoldsContainer(fieldType, new HashSet<string>(StringComparer.Ordinal)))
                        term = term.With(FloorTerm(made.PositionalValues[i], fieldType));
                }
                foreach (var (name, value) in made.NamedValues)
                    if (CarriedField(madeType, name) is var (carries, fieldType) && carries)
                        term = term.With(FloorTerm(value, fieldType));
                return term;
            }
            case ButVoidDefault either:
                return FloorTerm(either.Voidable, type).With(FloorTerm(either.Default, type));
            case FailureFallback either:
                return FloorTerm(either.Fallible, type).With(FloorTerm(either.Default, type));
            case CastExpression cast:
                return _castTerms.TryGetValue(cast, out var castTerm) ? castTerm : DepthTerm.Of(0);
        }
        if (IsMadeHere(expr)) return DepthTerm.Of(_rabbitDepth);
        if (IsAccess(expr))
            return DepthTarget(expr) is { Key: { } reached }
                ? DepthTerm.Ref("floor " + reached)
                : FloorTerm(AccessRoot(expr), null);
        return DepthTerm.Of(0);
    }

    /// <summary>Whether a named field of something written out can hold a container, and its type.</summary>
    private (bool Carries, CufetType? Type) CarriedField(ObjectType? madeType, string name)
    {
        var fieldType = madeType == null ? null : FindFieldInOtOrPromoted(madeType, name);
        return (fieldType == null || FieldHoldsContainer(fieldType, new HashSet<string>(StringComparer.Ordinal)), fieldType);
    }

    /// <summary>What a `Define`, a `becomes`, an element or a judged subject gives a name, read
    /// where it stands — before the name is bound, since a shadow's value names the one it hides.</summary>
    private sealed record GivenDepths(DepthTerm Own, DepthTerm Floor, List<string> Back,
                                      List<(string Field, DepthTerm Own, DepthTerm Floor)>? Fields);

    private GivenDepths GivenOf(IExpression value, CufetType? type, bool element, bool defining)
    {
        var floor = FloorTerm(value, type);
        var own = element ? floor : OwnTerm(value, type);
        // The name it came from can now be reached through this one, so whatever this one is
        // given reaches that one too.
        var back = DepthTarget(value) is { Key: { } from } reached
            ? TargetsOf(from, reached.Exact, fieldsReplaced: false) : [];
        List<(string, DepthTerm, DepthTerm)>? fields = null;
        if (defining && value is ObjectLiteral made)
        {
            _objectDefs.TryGetValue(made.TypeName, out var madeType);
            fields = [];
            foreach (var (name, fieldValue) in made.NamedValues)
                if (CarriedField(madeType, name) is var (carries, fieldType) && carries)
                    fields.Add((name, OwnTerm(fieldValue, fieldType), FloorTerm(fieldValue, fieldType)));
        }
        return new GivenDepths(own, floor, back, fields);
    }

    /// <summary>A name given a value: its numbers are no deeper than the value's.</summary>
    private void NoteGiven(string name, GivenDepths given, bool replacing)
    {
        if (DepthKey(name) is not { } key) return;
        Bound("own " + key, given.Own);
        Bound("floor " + key, given.Floor);
        foreach (var back in given.Back) Bound(back, DepthTerm.Ref("floor " + key));
        if (replacing && _splitFields.TryGetValue(key, out var held))
            foreach (var field in held.Order(StringComparer.Ordinal))
            {
                Bound($"own {key}.{field}", given.Floor);
                Bound($"floor {key}.{field}", given.Floor);
            }
        if (given.Fields is { } fields)
        {
            var own = _splitFields[key] = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (field, fieldOwn, fieldFloor) in fields)
            {
                string fieldKey = $"{key}.{field}";
                own.Add(field);
                _depthStarts[fieldKey] = _depthStarts[key];
                Bound("own " + fieldKey, fieldOwn);
                Bound("floor " + fieldKey, fieldFloor);
                Bound("floor " + key, DepthTerm.Ref("floor " + fieldKey));
            }
        }
    }

    /// <summary>A store of something that holds a container: written down to judge at the end,
    /// and what the target reaches now reaches what was stored. <paramref name="field"/> names
    /// the field a field set writes, whose target is the object.</summary>
    private void NoteFloorStore(IExpression value, CufetType? valueType, IExpression target, string? field,
                                int line, int col, string action)
    {
        if (!HoldsContainer(valueType, null)) return;
        int upper = ValueDepthOf(value, valueType);
        var (key, exact) = DepthTarget(target);
        var stored = FloorTerm(value, valueType);
        DepthTerm limit;
        if (key == null)
            limit = FloorTerm(target, null);
        else
        {
            limit = DepthTerm.Ref((exact ? "own " : "floor ") + key);
            if (field != null && exact && IsSplitField(key, field))
            {
                // The field is replaced: what it is now is what was stored.
                Bound($"own {key}.{field}", OwnTerm(value, valueType));
                Bound($"floor {key}.{field}", stored);
            }
            else
                Bound("floor " + key, stored);
        }
        if (upper > 0) _floorStores.Add(new FloorStore(upper, limit, line, col, action));
    }

    /// <summary>A cast: written down, so that what its callee stores can lower the floors of what
    /// it was given once that is known. Gives back how deep what it hands back can reach — no
    /// deeper than what it is given, unless it can hand back something it captured.</summary>
    private DepthTerm NoteCallFloors(StoreSummary callee, IReadOnlyList<IExpression> args, List<CufetType?> types,
                                     IExpression? receiver, IExpression function)
    {
        var parts = new List<CastPart>();
        void Part(int position, IExpression given, CufetType? type)
        {
            var (key, exact) = DepthTarget(given);
            parts.Add(new CastPart(position, key, exact, FloorTerm(given, type)));
        }
        for (int i = 0; i < args.Count; i++)
            if (HoldsContainer(types[i], null)) Part(i, args[i], types[i]);
        if (receiver != null && InferType(receiver) is var receiverType && HoldsContainer(receiverType, null))
            Part(ReceiverDepthIndex, receiver, receiverType);
        _castFloorNotes.Add(new CastFloorNote(callee, parts, _rabbitDepth));

        bool named = receiver != null
            || function is VariableReference called && TryLookup(called.Name, out var info)
               && info!.Type is FunctionType && !info.IsParameter
               && info.EstablishingExpr is VariableReference { Line: 0, Column: 0 }
               && !_innerFunctions.Contains($"{called.Name}@{info.EstablishingLine}");
        if (!named) return DepthTerm.Of(0);
        if (parts.Count == 0) return DepthTerm.Of(_rabbitDepth);
        var handed = new DepthTerm();
        foreach (var part in parts) handed = handed.With(part.Term);
        return handed;
    }

    private void NoteCastTerm(CastExpression cast, DepthTerm term) =>
        _castTerms[cast] = _castTerms.TryGetValue(cast, out var held) ? held.With(term) : term;

    /// <summary>Whether what a function stores is known: a body was checked for it, or it is an
    /// interface's method, which stands for every conforming object's.</summary>
    private static bool StoresKnown(StoreSummary callee) => callee.Interface != null || callee.Known;

    /// <summary>What each cast's callee stores into what it was given, as bounds.</summary>
    private void BoundCasts()
    {
        foreach (var note in _castFloorNotes)
        {
            if (!StoresKnown(note.Callee))
            {
                // Nothing known: it may store any of them into any other.
                foreach (var into in note.Parts)
                    if (into.Key is { } key)
                        foreach (var from in note.Parts)
                            if (!ReferenceEquals(from, into))
                                foreach (var bounded in TargetsOf(key, into.Exact, fieldsReplaced: true))
                                    Bound(bounded, from.Term);
                continue;
            }
            foreach (var (source, target) in PairsOf(note.Callee))
            {
                if (note.Parts.FirstOrDefault(part => part.Position == target) is not { Key: { } key } into) continue;
                DepthTerm? term =
                    source == FreshDepthIndex ? DepthTerm.Of(note.Depth)
                    : IsOuter(source) ? DepthTerm.Of(0)
                    : note.Parts.FirstOrDefault(part => part.Position == source)?.Term;
                if (term == null) continue;
                foreach (var bounded in TargetsOf(key, into.Exact, fieldsReplaced: true))
                    Bound(bounded, term);
            }
        }
    }

    private int DepthValue(Dictionary<string, int> values, string bounded)
    {
        if (values.TryGetValue(bounded, out int held)) return held;
        return _depthStarts.GetValueOrDefault(bounded[(bounded.IndexOf(' ') + 1)..], 0);
    }

    private int DepthEval(Dictionary<string, int> values, DepthTerm term)
    {
        int least = term.Least;
        foreach (var key in term.Keys) least = Math.Min(least, DepthValue(values, key));
        return least;
    }

    private void JudgeFloorStores()
    {
        BoundCasts();
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var (bounded, term) in _depthBounds)
            {
                int lowered = DepthEval(values, term);
                if (lowered < DepthValue(values, bounded))
                {
                    values[bounded] = lowered;
                    changed = true;
                }
            }
        }
        foreach (var store in _floorStores)
            if (store.Upper > DepthEval(values, store.Limit))
                throw TypeError(
                    "this value lives in a shorter-lived region than its destination — it will be gone when that region ends",
                    null, store.Line, store.Column,
                    store.Action,
                    "Declare the container inside that region too, so both end at its 'Done.', "
                  + "or make the value before the region opens, so it lives as long as the container.");
    }

    /// <summary>What an item, an entry or a field is read out of, followed to its start.</summary>
    private static IExpression AccessRoot(IExpression expr)
    {
        while (true)
        {
            switch (expr)
            {
                case SeriesAccess access:        expr = access.Target; continue;
                case MapLookup lookup:           expr = lookup.Map; continue;
                case PossessiveAccess field:     expr = field.Target; continue;
                case RecordNamedAccess named:    expr = named.Record; continue;
                default: return expr;
            }
        }
    }

    private static bool IsAccess(IExpression expr) =>
        expr is SeriesAccess or MapLookup or PossessiveAccess or RecordNamedAccess;

    private static string? RootName(IExpression expr) => AccessRoot(expr) is VariableReference root ? root.Name : null;

    // ── What a name in a body holds (O2, for the store rule's walk) ────────────────────────────

    /// <summary>Where an expression's value may come from, and what its root was given.</summary>
    private HashSet<int> ReachOf(
        IExpression expr, Dictionary<string, int> paramIdx, HashSet<int> refParamIdxs,
        Dictionary<string, HashSet<int>> localDepths, Dictionary<string, int> captured,
        Dictionary<string, HashSet<int>> holds)
    {
        var reach = SymbolicExprDepth(expr, paramIdx, refParamIdxs, localDepths, captured);
        if (RootName(expr) is { } root && holds.TryGetValue(root, out var held)) reach.UnionWith(held);
        return reach;
    }

    private static HashSet<int> HeldBy(Dictionary<string, HashSet<int>> holds, string name)
    {
        if (!holds.TryGetValue(name, out var held)) holds[name] = held = [];
        return held;
    }

    private static bool Grow(HashSet<int> into, IEnumerable<int> from)
    {
        if (ReferenceEquals(into, from)) return false;
        bool grew = false;
        foreach (var place in from.ToList()) grew |= into.Add(place);
        return grew;
    }

    /// <summary>One round of what each name of a body holds: what was stored into it or into
    /// anything inside it, what a cast may have stored into it, and, for a second name or an
    /// element, what the name it came from holds.</summary>
    private bool GrowHolds(
        StoreFrame frame, List<object> nodes, Dictionary<string, HashSet<int>> localDepths,
        Dictionary<string, HashSet<int>> holds, Dictionary<string, int> paramIdx, HashSet<int> refParamIdxs,
        Dictionary<string, int> captured)
    {
        HashSet<int> Reach(IExpression expr) => ReachOf(expr, paramIdx, refParamIdxs, localDepths, captured, holds);
        bool grew = false;
        foreach (var node in nodes)
        {
            if (DepthTargetOf(node) is not { } given) continue;
            if (node is DefineStatement or BecomesStatement && given.Value is VariableReference other)
            {
                if (other.Name == given.Name) continue;
                grew |= Grow(HeldBy(holds, given.Name), HeldBy(holds, other.Name));
                grew |= Grow(HeldBy(holds, other.Name), HeldBy(holds, given.Name));
            }
            else if ((node is ForEachStatement or JudgeStatement || IsAccess(given.Value))
                     && RootName(given.Value) is { } root && root != given.Name)
            {
                var rootHeld = HeldBy(holds, root);
                grew |= Grow(localDepths[given.Name], rootHeld);
                grew |= Grow(HeldBy(holds, given.Name), rootHeld);
            }
        }
        foreach (var store in frame.Stores)
            if (RootName(store.Target) is { } into)
                grew |= Grow(HeldBy(holds, into), Reach(store.Value));
        foreach (var call in frame.Calls)
        {
            var parts = new List<IExpression>();
            for (int i = 0; i < call.Args.Count; i++)
                if (call.ArgHolds[i]) parts.Add(call.Args[i]);
            if (call.Receiver != null) parts.Add(call.Receiver);
            for (int i = 0; i < parts.Count; i++)
            {
                if (RootName(parts[i]) is not { } into) continue;
                for (int j = 0; j < parts.Count; j++)
                    if (j != i) grew |= Grow(HeldBy(holds, into), Reach(parts[j]));
            }
        }
        return grew;
    }
}
