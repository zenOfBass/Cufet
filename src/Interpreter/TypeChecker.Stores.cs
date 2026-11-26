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
    /// <summary>The pairs that can reach something the target HOLDS, and not only the target itself:
    /// `Insert row into item 1 of table`, where `Insert row into table` is the table alone.</summary>
    public HashSet<(int Source, int Target)> Within { get; } = [];
    /// <summary>What the value it hands back can reach: a parameter whose series it wrapped, as well
    /// as one it handed back. The return-depth signature says only where the value itself is.</summary>
    internal HashSet<int> Reaches { get; } = [];
    /// <summary>What the value it hands back may itself BE: one of these, or an item of one.</summary>
    internal HashSet<int> HandsBack { get; } = [];
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
    /// <summary>The body reads a constant that is or holds a container, or casts something that does.</summary>
    internal bool UsesConstants { get; set; }
    /// <summary>Read as a value: cast where nothing says which function it is.</summary>
    internal bool IsValue { get; set; }
    /// <summary>The names that lead to a container it captured, by the place each has in a pair, and the
    /// key each is known by (O2).</summary>
    internal SortedDictionary<int, SortedDictionary<string, string>> CapturedAs { get; } = [];
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
        /// <summary>The target is a parameter itself, and not something reached through one.</summary>
        public bool Plain { get; init; }
        /// <summary>What is stored is copied, so this is no store to judge; what it leads to is shared.</summary>
        public bool Copied { get; init; }
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
        /// <summary>How deep each argument, and the receiver, may live at the DEEPEST: what a store out
        /// of one is judged by, as the two above are what a store into one is.</summary>
        public required int[] SourceDepths;
        public required int ReceiverSource;
        /// <summary>Which arguments, and whether the receiver, are a parameter of the body itself.</summary>
        public required bool[] ArgPlain;
        public required bool ReceiverPlain;
        /// <summary>Where each argument that holds a container, and the receiver, may really live (O2).</summary>
        public required List<CastPart> Parts;
        public required int CallDepth;
        public required int Line;
        public required int Column;
        public required StoreFrame? Frame;
        public HashSet<int>[]? ArgSources;
        public HashSet<int>? ReceiverSources;
        /// <summary>Which arguments can hold a container, so that a number's depth is not read.</summary>
        public required bool[] ArgHolds;
        /// <summary>Those, and the ones that hold a function.</summary>
        public required bool[] ArgLeads;
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
        /// <summary>The parameters that hold a function, which nothing can be stored into.</summary>
        public required HashSet<int> FunctionParams;
        /// <summary>Each captured name, with the index it has in a pair: its rabbit depth where
        /// this body was written, or the parameter it is there. See OuterCode.</summary>
        public required Dictionary<string, int> Captured;
        public required StoreFrame? Parent;
        /// <summary>The names this body gives another value.</summary>
        public HashSet<string> Rebound => _rebound ??= ReboundNames(Body);
        private HashSet<string>? _rebound;
        /// <summary>What this body hands back that leads to a container, and what that can reach.</summary>
        public List<IExpression> Returns = [];
        public HashSet<int> Reached = [];
        public HashSet<int> HandedBack = [];
        public bool UsesConstants;
        /// <summary>As last traced: where each of its names comes from, and what each holds.</summary>
        public Dictionary<string, HashSet<int>>? Locals;
        public Dictionary<string, HashSet<int>>? Holds;
        public List<StoreNote> Stores = [];
        public List<HomeCopy> HomeCopies = [];
        public List<CallNote> Calls = [];
        public bool Traced;
        /// <summary>What each name looked up while it was first traced was, there — the names it
        /// can reach are out of scope when it is traced again at the end (O2).</summary>
        public Dictionary<string, TypeInfo?>? Lookups;
    }

    private StoreFrame? _storeFrame;
    private readonly List<StoreFrame> _storeFrames = [];
    private readonly List<CallNote> _callNotes = [];
    private readonly Dictionary<CastExpression, CallNote> _castNotes = new(ReferenceEqualityComparer.Instance);
    // Each literal with something in it that leads to a container, and what it is.
    private readonly Dictionary<IExpression, CufetType> _carriers = new(ReferenceEqualityComparer.Instance);

    private T NoteCarrier<T>(IExpression literal, T type) where T : CufetType?
    {
        if (type != null && LeadsToContainer(type)) _carriers[literal] = type;
        return type;
    }

    private void NoteReturned(IExpression? value, CufetType? type)
    {
        if (value != null && _storeFrame is { } frame && (type == null || LeadsToContainer(type))) frame.Returns.Add(value);
    }
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
    // A `permanently` constant the body can reach: it lives as long as the program, wherever it is
    // named, so a store into one is judged wherever the body is cast (2026-11-25, `constant-4`).
    private const int ConstantIndex = int.MinValue + 1;
    // A captured name that is a PARAMETER of the body this one was written in (-1 for its
    // receiver). Its depth is known no better there than here, but there it is a parameter like
    // any other, and the pair is carried up to that body's own.
    private const int OuterParameterBase = -1_000_001;
    private static int OuterParameterCode(int parameter) => OuterParameterBase - parameter;
    private static bool IsOuterParameter(int index) => index <= OuterParameterBase + 1 && index > ConstantIndex;
    // The rabbit depth a captured place has in the body it was captured from.
    private static int OuterDepth(int index) => IsOuterParameter(index) || index == ConstantIndex ? 0 : OuterDepthIndex - index;

    private StoreFrame OpenStoreFrame(
        StoreSummary? summary, IReadOnlyList<IStatement> body,
        IEnumerable<(string Name, CufetType? Type)> parameters, bool hasReceiver,
        Dictionary<string, int>? captured)
    {
        var paramIdx = new Dictionary<string, int>(StringComparer.Ordinal);
        var refs     = new HashSet<int>();
        var functions = new HashSet<int>();
        int index    = 0;
        foreach (var (name, type) in parameters)
        {
            paramIdx[name] = index;
            // An interface is no reference type to the rest of the checker, but what is passed as
            // one is an object, and a method reached through it can store into that object.
            if (IsReferenceType(type) || type is InterfaceType || HoldsFunction(type)) refs.Add(index);
            if (HoldsFunction(type)) functions.Add(index);
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
            foreach (var name in NamesIn(body).Order(StringComparer.Ordinal))
                if (captured.TryGetValue(name, out int place) && TryLookup(name, out var info)
                    && LeadsToContainer(info!.Type) && DepthKey(name) is { } key)
                    NoteCapturedAs(summary, place, name, key);
        }
        return _storeFrame = new StoreFrame
        {
            Summary = summary, Body = body, ParamIdx = paramIdx, RefParamIdxs = refs,
            Captured = captured, Parent = _storeFrame, FunctionParams = functions,
        };
    }

    private bool _capturedGrew;

    private void NoteCapturedAs(StoreSummary summary, int place, string name, string key)
    {
        if (!summary.CapturedAs.TryGetValue(place, out var names))
            summary.CapturedAs[place] = names = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (names.TryAdd(name, key)) _capturedGrew = true;
    }

    private void ReachThrough(StoreFrame frame, StoreSummary beside)
    {
        if (frame.Summary is not { } own || beside.CapturedAs.Count == 0
            || ReferenceEquals(beside, own) || !ReferenceEquals(beside.DefinedIn, frame.Parent)) return;
        foreach (var (place, names) in beside.CapturedAs)
            foreach (var (name, key) in names) NoteCapturedAs(own, place, name, key);
    }

    /// <summary>A constant that is or leads to a container.</summary>
    private bool HoldsConstant(TypeInfo info) => info.Type is not (FunctionType or StashType) && LeadsToContainer(info.Type);

    private T NoteConstantUse<T>(string name, T result)
    {
        if (_storeFrame != null && TryLookup(name, out var info) && info!.Shared && !info.IsParameter && HoldsConstant(info))
            for (var frame = _storeFrame; frame != null; frame = frame.Parent) frame.UsesConstants = true;
        return result;
    }

    // A value nothing traces may be a constant, when some function nothing can name hands one back: a
    // function with no name, one read as a value, a getter or an operator.
    private bool _looseConstants;
    // Each read of a member that is a getter.
    private readonly HashSet<IExpression> _getterReads = new(ReferenceEqualityComparer.Instance);
    private const string LooseKey = "getter#";

    private bool UsesConstants(StoreSummary summary)
    {
        if (summary.Interface is not { } interfaceName) return summary.UsesConstants;
        return _objectDefs.Values.Any(objectType => objectType.ConformedInterfaces.Contains(interfaceName)
            && FindMethodInOtOrPromoted(objectType, summary.Method!) is { } method && method.Stores.UsesConstants);
    }

    /// <summary>Which bodies reach a constant, by their own hand or through a function they cast.</summary>
    private bool GrowConstantUses()
    {
        bool grew = false;
        foreach (var frame in _storeFrames)
        {
            if (!frame.UsesConstants)
                frame.UsesConstants = frame.Calls.Any(call => UsesConstants(call.Callee) || call.Passes.Any(pass => UsesConstants(pass.Function)));
            if (!frame.UsesConstants) continue;
            if (frame.Summary is { UsesConstants: false } summary) { summary.UsesConstants = true; grew = true; }
            if (!_looseConstants && (frame.Summary == null || frame.Summary.IsValue)) { _looseConstants = true; grew = true; }
        }
        return grew;
    }

    private bool ReadsThroughGetter(IExpression expr)
    {
        for (; IsAccess(expr); expr = expr switch
             {
                 SeriesAccess access     => access.Target,
                 MapLookup lookup        => lookup.Map,
                 PossessiveAccess field  => field.Target,
                 RecordNamedAccess named => named.Record,
                 _ => expr,
             })
            if (_getterReads.Contains(expr)) return true;
        return false;
    }

    private static HashSet<string> NamesIn(IReadOnlyList<IStatement> body)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        AstSearch.Visit(body, node =>
        {
            if (node is VariableReference read) names.Add(read.Name);
            else if (node is BecomesStatement given) names.Add(given.Name);
        });
        return names;
    }

    /// <summary>The names a body written inside another can reach that are not its own.</summary>
    private Dictionary<string, int> CapturedNames(List<Dictionary<string, TypeInfo>> enclosing)
    {
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var scope in enclosing)
            foreach (var (name, info) in scope)
                if (!info.Shared && (info.Type is not FunctionType || !IsFreeFunction(name, info)))
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
        if (frame.Stores.Count == 0 && frame.Calls.Count == 0 && frame.HomeCopies.Count == 0 && frame.Returns.Count == 0) return;
        var outerLookups = _traceLookups;
        var outerFrame = _traceFrame;
        _traceFrame = frame;
        _traceLookups = frame.Lookups ??= new Dictionary<string, TypeInfo?>(StringComparer.Ordinal);
        // Written beside this body: what it captured, this one reaches through it.
        foreach (var call in frame.Calls)
        {
            ReachThrough(frame, call.Callee);
            foreach (var (_, function) in call.Passes) ReachThrough(frame, function);
        }
        try
        {
            var locals = LocalDepthsOf(frame.Body, frame.ParamIdx, frame.RefParamIdxs, frame.Captured, out _,
                                       frame, out var holds);
            HashSet<int> Of(IExpression expr) =>
                ReachOf(expr, frame.ParamIdx, frame.RefParamIdxs, locals, frame.Captured, holds);
            foreach (var store in frame.Stores)
            {
                store.Sources = Of(store.Value);
                // Storing into the name itself — or into a field of an object written out, which is
                // a name of its own — reaches only what it is; storing into something inside it
                // reaches anything it was given as well (O2).
                store.Targets = store.Field != null || FieldRoot(store.Target, locals).Exact
                    ? SymbolicExprDepth(store.Target, frame.ParamIdx, frame.RefParamIdxs, locals, frame.Captured)
                    : Of(store.Target);
            }
            foreach (var call in frame.Calls)
            {
                call.ArgSources      = [.. call.Args.Select(Of)];
                call.ReceiverSources = call.Receiver is null ? null : Of(call.Receiver);
            }
            foreach (var copy in frame.HomeCopies) copy.Targets = Of(copy.Target);
            frame.Reached = [.. frame.Returns.SelectMany(Of)];
            frame.HandedBack = [.. frame.Returns.SelectMany(value =>
                IsOf(value, frame.ParamIdx, frame.RefParamIdxs, locals, frame.Captured, holds))];
        }
        finally { _traceLookups = outerLookups; _traceFrame = outerFrame; }
    }

    // ── A text stored where the body cannot see how long it lives (the text half of O4) ─────────
    //
    // A text, or anything else that lives in a region and holds no container, is never refused: an
    // escaping one is COPIED to where it is stored (`EscapeDepthFor`). That needs the depth of the
    // target, and inside a body a parameter, the object a method is cast on and a captured name
    // all have no depth the body can know. So a store into anything reached from one of them is
    // annotated `HomeEscape`, and the compiler copies the value, when the program runs, into the
    // region the target itself lives in. Decided once every body has been checked, by the same walk
    // that finds what a body stores into its parameters.

    /// <summary>The escape annotation meaning: copy into the region the target itself lives in,
    /// found when the program runs.</summary>
    public const int HomeEscape = -1;

    private sealed class HomeCopy(IExpression target, Action<int?> set)
    {
        public IExpression Target { get; } = target;
        public Action<int?> Set { get; } = set;
        public HashSet<int> Targets = [];
    }

    /// <summary>A store of something held in a region that holds no container, met inside a body.</summary>
    private void NoteHomeCopy(IExpression target, IExpression value, CufetType? valueType, int? copiedTo, Action<int?> set)
    {
        if (!IsRegionBearing(valueType) || HoldsContainer(valueType, null) || valueType is FunctionType or StashType) return;
        // The target may live further out than its name was made, wherever it is written (O2).
        int upper = EscapingFrom(value, valueType);
        if (upper > 0) _copiedStores.Add(new CopiedStore(upper, LimitOf(target), copiedTo, set));
        if (_storeFrame is { } frame) frame.HomeCopies.Add(new HomeCopy(target, set));
    }

    private sealed record CopiedStore(int Upper, DepthTerm Limit, int? CopiedTo, Action<int?> Set);

    private readonly List<CopiedStore> _copiedStores = [];

    /// <summary>How far out the place a target names may be.</summary>
    private DepthTerm LimitOf(IExpression target)
    {
        var (key, exact) = DepthTarget(target);
        return key != null && exact ? DepthTerm.Ref("own " + key) : FloorTerm(target, null);
    }

    /// <summary>Every such store whose target may be something from outside the body, or something
    /// that outlives every region (a top-level constant), copies into the target's own region.</summary>
    private void MarkHomeCopies()
    {
        foreach (var frame in _storeFrames)
        {
            if (frame.HomeCopies.Count == 0) continue;
            frame.Traced = false;
            Trace(frame);
            foreach (var copy in frame.HomeCopies)
                if (copy.Targets.Count == 0 || copy.Targets.Any(place => place != FreshDepthIndex))
                    copy.Set(HomeEscape);
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
        if (_storeFrame is { } frame && LeadsToContainer(valueType))
            frame.Stores.Add(new StoreNote(value, target, line, col)
            {
                Field = field, Plain = IsPlainTarget(frame, target), Copied = !HoldsContainer(valueType, null),
            });
    }

    private static HashSet<string> ReboundNames(IReadOnlyList<IStatement> body)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        AstSearch.Visit(body, node => { if (node is BecomesStatement given) names.Add(given.Name); });
        return names;
    }

    /// <summary>The expression is a parameter of this body, or its receiver, named and nothing more —
    /// and never given another value, which could be something another parameter holds.</summary>
    private bool IsPlainParameter(StoreFrame frame, IExpression expr) =>
        expr is VariableReference named && frame.ParamIdx.ContainsKey(named.Name)
        && TryLookup(named.Name, out var info) && info!.IsParameter
        && !frame.Rebound.Contains(named.Name);

    /// <summary>The same, or a name it captured: a store into just that.</summary>
    private bool IsPlainTarget(StoreFrame frame, IExpression expr) =>
        IsPlainParameter(frame, expr)
        || expr is VariableReference named && frame.Captured.ContainsKey(named.Name) && !frame.Rebound.Contains(named.Name);

    /// <summary>How deep what an expression gives may live, at the deepest.</summary>
    /// <remarks>
    /// ★ Not `ValueDepthOf`, whose answer for a value with no name is 0: a store of one COPIES it, so
    /// there 0 is right. Handed to a function it is not copied, and 0 let a series made in a rabbit be
    /// kept outside it (2026-11-25, docs/SOUNDNESS.md, `unnamed-1`). An item, an entry and a field live no
    /// deeper than what they are read out of.
    /// </remarks>
    private int LivesAt(IExpression expr) => expr switch
    {
        VariableReference named  => TryLookup(named.Name, out var info) ? info!.RabbitDepth : 0,
        SeriesAccess access      => LivesAt(access.Target),
        MapLookup lookup         => LivesAt(lookup.Map),
        PossessiveAccess field   => _possessiveDepthCache.TryGetValue(field, out var depth) ? depth : LivesAt(field.Target),
        RecordNamedAccess field  => _rnaDepthCache.TryGetValue(field, out var depth) ? depth : LivesAt(field.Record),
        CastExpression cast      => _castDepthCache.TryGetValue(cast, out var depth) ? depth : _rabbitDepth,
        ButVoidDefault either    => Math.Max(LivesAt(either.Voidable), LivesAt(either.Default)),
        FailureFallback either   => Math.Max(LivesAt(either.Fallible), LivesAt(either.Default)),
        ConditionalExpression either => Math.Max(LivesAt(either.Value), LivesAt(either.Alternative)),
        FailurePropagate passed  => LivesAt(passed.Fallible),
        LambdaLiteral written    => _capturing.Contains(written) ? _rabbitDepth : 0,
        _ => _rabbitDepth,
    };

    // The functions written out that use a name from around them.
    private readonly HashSet<LambdaLiteral> _capturing = new(ReferenceEqualityComparer.Instance);

    private void NoteCapturing(LambdaLiteral lambda)
    {
        var own = lambda.Parameters.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var name in NamesIn(lambda.Body))
            if (!own.Contains(name) && TryLookup(name, out var info) && !info!.Shared && !info.IsPulledModule
                && !(info.Type is FunctionType && IsFreeFunction(name, info)))
                _capturing.Add(lambda);
    }

    /// <summary>A function bound by name at the top of the program, outside every block.</summary>
    private bool IsFreeFunction(string name, TypeInfo info) =>
        info is { IsParameter: false, RabbitDepth: 0, EstablishingExpr: VariableReference { Line: 0, Column: 0 } }
        && !_innerFunctions.Contains($"{name}@{info.EstablishingLine}");

    /// <summary>Notes a cast for the store rule, and gives back how deep anything it hands back
    /// can reach (O2's floor of the cast).</summary>
    private DepthTerm NoteCall(
        FunctionType callee, string display, IReadOnlyList<IExpression> args, IExpression? receiver,
        int line, int col, IExpression function, CastExpression? cast = null)
    {
        var types = args.Select(InferType).ToList();
        var castFloor = NoteCallFloors(callee, args, types, receiver, function, out var parts);
        var note = new CallNote
        {
            Callee = callee.Stores, Display = display, Args = args, Receiver = receiver,
            ArgDepths = [.. args.Select((arg, i) =>
                arg is VariableReference held && TryLookup(held.Name, out var info) ? info!.RabbitDepth
                : ValueDepthOf(arg, types[i]))],
            ArgHolds = [.. types.Select(type => LeadsToContainer(type) && !HoldsFunction(type))],
            ArgLeads = [.. types.Select(LeadsToContainer)],
            ReceiverDepth = receiver is null ? 0 : PlaceDepth(receiver),
            SourceDepths = [.. args.Select(LivesAt)],
            ReceiverSource = receiver is null ? 0 : LivesAt(receiver),
            ArgPlain = [.. args.Select(arg => _storeFrame is { } body && IsPlainParameter(body, arg))],
            ReceiverPlain = receiver != null && _storeFrame is { } around && IsPlainParameter(around, receiver),
            Parts = parts,
            CallDepth = _rabbitDepth, Line = line, Column = col, Frame = _storeFrame,
        };
        _callNotes.Add(note);
        _storeFrame?.Calls.Add(note);
        if (cast != null) _castNotes[cast] = note;

        // A function parameter cast anywhere but plainly in its own body, outside every rabbit of
        // that body, may be cast deeper than where it was handed over.
        if (function is VariableReference castName && FunctionParameter(castName.Name) is { } castParameter
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
            function.Stores.IsValue = true;
            _functionValues.Add((function.Stores, name, named.Line, named.Column));
            if (FunctionParameter(name) is { } parameter && parameter.Owner.Summary is { } owner)
                _parameterValues.Add((owner, parameter.Index, named.Line, named.Column));
        }
        return info;
    }

    /// <summary>Whether a value of this type carries a series or a map: itself, or inside it.</summary>
    private bool HoldsContainer(CufetType? type, HashSet<string>? seen) => type switch
    {
        SeriesType or MapType or SetType or ChaseType or MatrixType or ChannelType or AddressType => true,
        ObjectType objectType => ObjectHoldsContainer(objectType, seen ?? new HashSet<string>(StringComparer.Ordinal)),
        _ => HoldsFunction(type),
    };

    /// <summary>A function value, or something copied with one inside: what a function captured cannot
    /// be copied with it, so it stays where it was made, as a container does.</summary>
    private static bool HoldsFunction(CufetType? type) => type switch
    {
        FunctionType or StashType => true,
        VoidableType voidable => HoldsFunction(voidable.Inner),
        FailureType failure   => HoldsFunction(failure.Inner),
        RecordType record     => record.PositionalTypes.Any(HoldsFunction) || record.NamedFields.Any(f => HoldsFunction(f.Type)),
        UnionType { Cases: { } cases } => cases.Any(HoldsFunction),
        _ => false,
    };

    /// <summary>Whether a value of this type can lead to a container: one itself, or a record, a
    /// voidable, a union or a failure with one inside.</summary>
    /// <remarks>★ Wider than `HoldsContainer`, which asks what a store must not move: one of these is
    /// copied where it is stored. What it leads to is shared all the same, so a name given one can
    /// reach an outer container through it.</remarks>
    private bool LeadsToContainer(CufetType? type) => type switch
    {
        null => false,
        RecordType record     => record.PositionalTypes.Any(LeadsToContainer) || record.NamedFields.Any(f => LeadsToContainer(f.Type)),
        VoidableType voidable => LeadsToContainer(voidable.Inner),
        FailureType failure   => LeadsToContainer(failure.Inner),
        UnionType union       => union.Cases == null || union.Cases.Any(LeadsToContainer),
        MappingType pair      => LeadsToContainer(pair.KeyType) || LeadsToContainer(pair.ValueType),
        InterfaceType         => true,
        _ => HoldsContainer(type, null),
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

    /// <summary>Whether one of a function's pairs can reach something its target holds.</summary>
    private bool ReachesWithin(StoreSummary summary, int source, int target)
    {
        if (summary.Interface is not { } interfaceName) return summary.Within.Contains((source, target));
        foreach (var objectType in _objectDefs.Values)
            if (objectType.ConformedInterfaces.Contains(interfaceName)
                && FindMethodInOtOrPromoted(objectType, summary.Method!) is { } method
                && method.Stores.Within.Contains((source, target)))
                return true;
        return false;
    }

    /// <summary>Every store a body makes, by its own hand or through a function it calls, and whether
    /// it can reach something its target holds.</summary>
    private IEnumerable<(HashSet<int> Sources, HashSet<int> Targets, bool Within, int Line, int Column, string? Through)>
        StoresOf(StoreFrame frame)
    {
        if (frame.Stores.Count > 0) Trace(frame);
        foreach (var store in frame.Stores.Where(store => !store.Copied))
            yield return (store.Sources, store.Targets, !store.Plain, store.Line, store.Column, null);
        foreach (var call in frame.Calls)
        {
            var pairs = PairsOf(call.Callee);
            if (pairs.Count > 0)
            {
                Trace(frame);
                foreach (var (source, target) in pairs)
                    if (PlaceSources(frame, call, call.Callee, source) is { } sources
                        && PlaceSources(frame, call, call.Callee, target, ReachesWithin(call.Callee, source, target)) is { } targets)
                        yield return (sources, targets, ReachesWithin(call.Callee, source, target) || !PlainAt(call, target),
                                      call.Line, call.Column, call.Display);
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
                    yield return (HandedSide(frame, function, source, reach), HandedSide(frame, function, target, reach, ReachesWithin(function, source, target)), true,
                                  call.Line, call.Column, "the function handed to " + call.Display);
            }
        }
    }

    private static bool PlainAt(CallNote call, int place) =>
        place == ReceiverDepthIndex ? call.ReceiverPlain
        : place >= 0 && place < call.ArgPlain.Length && call.ArgPlain[place];

    /// <summary>Everything the receiver of a call could cast a function it is handed on, in the
    /// terms of the body making the call.</summary>
    private HashSet<int> ReceiverReach(StoreFrame frame, CallNote call)
    {
        var reach = new HashSet<int> { FreshDepthIndex };
        if (_looseConstants || UsesConstants(call.Callee)) reach.Add(ConstantIndex);
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

    private HashSet<int> HandedSide(StoreFrame frame, StoreSummary function, int place, HashSet<int> reach, bool within = true)
    {
        if (place == FreshDepthIndex) return [FreshDepthIndex];
        if (!IsOuter(place)) return reach;
        return CapturedPlace(frame, function, place, within);
    }

    /// <summary>A captured place of some function, in the terms of the body that casts or hands it.</summary>
    private HashSet<int> CapturedPlace(StoreFrame frame, StoreSummary function, int place, bool within = true)
    {
        if (place == ConstantIndex) return [place];
        function.CapturedAs.TryGetValue(place, out var names);
        // Written in this body, capturing names of its own: wherever those may come from.
        if (names != null && ReferenceEquals(function.DefinedIn, frame) && frame.Locals is { } locals)
        {
            var from = new HashSet<int>();
            foreach (var name in names.Keys)
            {
                from.UnionWith(NamePlaces(frame, locals, name));
                if (within && frame.Holds!.TryGetValue(name, out var held)) from.UnionWith(held);
            }
            return from;
        }
        if (place == OuterUnknownIndex) return [place];
        // Written in this body: the name is one of this body's own, a parameter or made here.
        if (ReferenceEquals(function.DefinedIn, frame))
            return [IsOuterParameter(place) ? OuterParameterBase - place : FreshDepthIndex];
        // Written beside this body: the same name, captured by both, the same way.
        if (ReferenceEquals(function.DefinedIn, frame.Parent)) return [place];
        return [OuterUnknownIndex];
    }

    /// <summary>Where a name of a body comes from, whatever is in scope now.</summary>
    private HashSet<int> NamePlaces(StoreFrame frame, Dictionary<string, HashSet<int>> locals, string name)
    {
        var places = new HashSet<int>();
        bool known = false;
        if (frame.ParamIdx.TryGetValue(name, out int parameter))
        {
            known = true;
            if (frame.RefParamIdxs.Contains(parameter)) places.Add(parameter);
        }
        else if (frame.Captured.TryGetValue(name, out int captured)) { known = true; places.Add(captured); }
        if (locals.TryGetValue(name, out var given)) { known = true; places.UnionWith(given); }
        return known ? places : UntracedDepth(frame.RefParamIdxs, frame.Captured);
    }

    /// <summary>What one side of a callee's pair is, in the terms of the body making the call.</summary>
    private HashSet<int>? PlaceSources(StoreFrame frame, CallNote call, StoreSummary callee, int place, bool within = true)
    {
        if (place == FreshDepthIndex) return [FreshDepthIndex];
        if (place == ReceiverDepthIndex) return call.ReceiverSources;
        if (place >= 0) return place < call.Args.Count ? call.ArgSources![place] : null;
        return CapturedPlace(frame, callee, place, within);
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
        if (source == target || target == FreshDepthIndex || frame.FunctionParams.Contains(target)) return StoreVerdict.Nothing;
        // A constant outlives everything.
        if (source == ConstantIndex) return StoreVerdict.Nothing;
        if (target == ConstantIndex && source != OuterUnknownIndex)
            return frame.Summary != null ? StoreVerdict.Pair : StoreVerdict.Refused;
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
                changed = _capturedGrew = false;
                // What a body's names hold depends on what the functions it casts store, which is
                // what this loop is finding out: each round traces again with what is known now.
                foreach (var frame in _storeFrames) frame.Traced = false;
                foreach (var frame in _storeFrames)
                    if (frame.Summary is { } relaying)
                        foreach (var (parameter, to, toParameter) in relaying.Relays)
                            if (IsWild(to, toParameter)) changed |= relaying.Wild.Add(parameter);
                foreach (var frame in _storeFrames)
                {
                    if (frame.Summary is not { } summary) continue;
                    if (frame.Returns.Count > 0)
                    {
                        Trace(frame);
                        changed |= Grow(summary.Reaches, frame.Reached);
                        changed |= Grow(summary.HandsBack, frame.HandedBack);
                    }
                    foreach (var store in StoresOf(frame).ToList())
                        foreach (var source in store.Sources.Order())
                            foreach (var target in store.Targets.Order())
                                if (Judge(frame, source, target) == StoreVerdict.Pair)
                                {
                                    changed |= summary.Pairs.Add((source, target));
                                    if (store.Within) changed |= summary.Within.Add((source, target));
                                }
                }
                changed |= _capturedGrew;
                changed |= GrowConstantUses();
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
            _castNotes.Clear();
            _carriers.Clear();
            _innerFunctions.Clear();
            _floorStores.Clear();
            _capturing.Clear();
            _getterReads.Clear();
            _looseConstants = false;
            _copiedStores.Clear();
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
            if (call.ArgHolds[i]) { reach.Add(call.ArgDepths[i]); reach.Add(call.SourceDepths[i]); }
        if (call.Receiver != null) { reach.Add(call.ReceiverDepth); reach.Add(call.ReceiverSource); }
        // What the receiver captured is no deeper than where the receiver was written.
        if (call.Callee.Captures || _looseConstants || UsesConstants(call.Callee)) reach.Add(0);

        (int Deepest, int Shallowest)? DepthsOf(int place) =>
            place == FreshDepthIndex ? (call.CallDepth, call.CallDepth)
            : place == ConstantIndex ? (0, 0)
            : !IsOuter(place) ? (reach.Max(), reach.Min())
            : written && place != OuterUnknownIndex ? (OuterDepth(place), OuterDepth(place))
            : null;

        foreach (var (source, target) in stored)
        {
            if (target == FreshDepthIndex) continue;
            if (DepthsOf(source) is not { } from || DepthsOf(target) is not { } into
                || from.Deepest <= into.Shallowest) continue;
            throw RefusedAtPass(call, argument, source, target);
        }
    }

    private static TypeException RefusedAtPass(CallNote call, int argument, int source, int target) =>
        TypeError(
            "this value lives in a shorter-lived region than its destination — it will be gone when that region ends",
            $"The function handed to {call.Display} stores {PlaceName(source)} into {PlaceName(target)}, "
          + $"and {call.Display} can cast it on anything it is given here or makes here",
            call.Line, call.Column,
            $"hand it over as argument {argument + 1}, inside a region, with a container from outside that region in reach",
            "Declare the container inside that region too, so both end at its 'Done.', "
          + "or make the cast before the region opens.");

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
            : place == ConstantIndex ? 0
            : written && place != OuterUnknownIndex ? OuterDepth(place)
            : null;

        if (SourceAtCall(call, source) is not { } from || DepthOf(target) is not { } into || from <= into) return;
        throw RefusedAtCall(call, source, target);
    }

    /// <summary>How deep one of a callee's sources may live where it is cast, or null when nothing can say.</summary>
    private static int? SourceAtCall(CallNote call, int place) =>
        place == FreshDepthIndex ? call.CallDepth
        : place == ReceiverDepthIndex ? (call.Receiver is null ? null : call.ReceiverSource)
        : place >= 0 ? (place < call.SourceDepths.Length ? call.SourceDepths[place] : null)
        : ReferenceEquals(call.Callee.DefinedIn, call.Frame) && place != OuterUnknownIndex ? OuterDepth(place)
        : null;

    /// <summary>A cast whose target's name was made deep enough, judged by where the container it
    /// names may really be (O2): a second name for an outer one, an object holding one.</summary>
    private void RefuseAtCallByFloor(CallNote call, int source, int target, Dictionary<string, int> values)
    {
        if (SourceAtCall(call, source) is not { } from || from <= 0) return;
        if (IsOuter(target))
        {
            if (from > CapturedLimit(call, call.Callee, source, target, values)) throw RefusedAtCall(call, source, target);
            return;
        }
        if (call.Parts.FirstOrDefault(part => part.Position == target) is not { } into) return;
        var limit = into is { Key: { } key, Exact: true } && !ReachesWithin(call.Callee, source, target)
            ? DepthTerm.Ref("own " + key) : into.Term;
        if (from > DepthEval(values, limit)) throw RefusedAtCall(call, source, target);
    }

    /// <summary>How far out the names a function captured at a place may really be, where it was written.</summary>
    private int CapturedLimit(CallNote call, StoreSummary function, int source, int target, Dictionary<string, int> values)
    {
        if (!ReferenceEquals(function.DefinedIn, call.Frame) || !function.CapturedAs.TryGetValue(target, out var names)) return int.MaxValue;
        string side = ReachesWithin(function, source, target) ? "floor " : "own ";
        return names.Values.Min(key => DepthValue(values, side + key));
    }

    /// <summary>A function handed over, judged by where the containers it can reach may really be.</summary>
    private void RefuseAtPassByFloor(CallNote call, int argument, StoreSummary function, Dictionary<string, int> values)
    {
        foreach (var (source, target) in PairsOf(function))
        {
            if (target == FreshDepthIndex) continue;
            int from = IsOuter(source)
                ? ReferenceEquals(function.DefinedIn, call.Frame) && source != OuterUnknownIndex ? OuterDepth(source) : 0
                : source == FreshDepthIndex ? call.CallDepth
                : Math.Max(call.CallDepth, Math.Max(call.ReceiverSource,
                      call.SourceDepths.Where((_, i) => call.ArgHolds[i]).DefaultIfEmpty(0).Max()));
            int limit = IsOuter(target) ? CapturedLimit(call, function, source, target, values)
                      : call.Parts.Select(part => DepthEval(values, part.Term)).DefaultIfEmpty(int.MaxValue).Min();
            if (from > limit) throw RefusedAtPass(call, argument, source, target);
        }
    }

    private static TypeException RefusedAtCall(CallNote call, int source, int target) =>
        TypeError(
            "this value lives in a shorter-lived region than its destination — it will be gone when that region ends",
            $"This cast reaches {call.Display}, which stores {PlaceName(source)} into {PlaceName(target)}"
          + (source == FreshDepthIndex ? ", and what a function makes lives in the region it is cast in" : ""),
            call.Line, call.Column,
            source == FreshDepthIndex
                ? "cast it inside a region, on a container from outside that region"
                : "cast it with a value from a shorter-lived region than the container",
            "Declare the container inside that region too, so both end at its 'Done.', "
          + "or cast it before the region opens.");

    // ── Where a store's target really lives (docs/SOUNDNESS.md, obligation O2) ─────────────────
    //
    // A name's rabbit depth says where the NAME was made, which is no help when what it reaches
    // was made further out: a second name for an outer series, an object made here holding one
    // from outside, an element of a table. So each binding carries two more numbers, under
    // `name@<its own number>`, both starting at its rabbit depth and only ever lowered:
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
    // ★ A field of an object WRITTEN OUT in a `Define` is a name of its own, `name@<number>.field`:
    // the object is held by value, so its field is the very series that was put there, and a
    // store into it says nothing about what else the object holds.
    //
    // ⚠ The number, and not the line: a line is one file's, and a program is many, so a name in a
    // pulled book shared its numbers with a program's own on the same line (2026-11-25).
    //
    // ⚠ The checker written in Cufet keeps the same numbers
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
    private sealed record CastFloorNote(StoreSummary Callee, List<CastPart> Parts, int Depth, string Result, bool Named);

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
        string key = $"{name}@{info!.Serial}";
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
        expr is SeriesLiteral or MapLiteral or SetLiteral or ChaseLiteral or ObjectLiteral or MatrixLiteral or MatrixSized
             or RangeExpression or ChannelCreation or DeliveryExpression or AwaitedResultExpression
             or TextSplit or SortExpression or SeriesReverse;

    /// <summary>How deep the container an expression IS may lie, at the lowest.</summary>
    private DepthTerm OwnTerm(IExpression expr, CufetType? type)
    {
        if (type is FunctionType or StashType) return new DepthTerm();
        // An object is held by value, so a name given one has its own, wherever it came from. So is a
        // record, and anything else that is copied.
        if (type is ObjectType or RecordType) return DepthTerm.Of(_rabbitDepth);
        switch (expr)
        {
            case VariableReference named:
                return DepthKey(named.Name) is { } key ? DepthTerm.Ref("own " + key) : DepthTerm.Of(0);
            case ButVoidDefault either:
                return OwnTerm(either.Voidable, type).With(OwnTerm(either.Default, type));
            case FailureFallback either:
                return OwnTerm(either.Fallible, type).With(OwnTerm(either.Default, type));
            case ConditionalExpression either:
                return OwnTerm(either.Value, type).With(OwnTerm(either.Alternative, type));
            case FailurePropagate passed:
                return OwnTerm(passed.Fallible, type);
            case CastExpression cast when _castNotes.ContainsKey(cast):
                return _castTerms.TryGetValue(cast, out var term) ? OwnOfResult(term) : DepthTerm.Of(0);
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
        // Nothing can be stored into a function, so nothing is reached through one.
        if (type is FunctionType or StashType) return new DepthTerm();
        switch (expr)
        {
            case VariableReference named:
                return DepthKey(named.Name) is { } key ? DepthTerm.Ref("floor " + key) : DepthTerm.Of(0);
            case SeriesLiteral made:
            {
                var term = DepthTerm.Of(_rabbitDepth);
                var element = (type as SeriesType)?.ElementType;
                if (type is not SeriesType || LeadsToContainer(element))
                    foreach (var item in made.Elements) term = term.With(FloorTerm(item, element));
                return term;
            }
            case MapLiteral made:
            {
                var term = DepthTerm.Of(_rabbitDepth);
                var mapType = type as MapType;
                bool keys = mapType == null || LeadsToContainer(mapType.KeyType);
                bool values = mapType == null || LeadsToContainer(mapType.ValueType);
                foreach (var (entryKey, entryValue) in made.Pairs)
                {
                    if (keys) term = term.With(FloorTerm(entryKey, mapType?.KeyType));
                    if (values) term = term.With(FloorTerm(entryValue, mapType?.ValueType));
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
            case ConditionalExpression either:
                return FloorTerm(either.Value, type).With(FloorTerm(either.Alternative, type));
            case FailurePropagate passed:
                return FloorTerm(passed.Fallible, type);
            case RecordLiteral made:
            {
                var term = DepthTerm.Of(_rabbitDepth);
                var record = type as RecordType;
                for (int i = 0; i < made.PositionalFields.Count; i++)
                {
                    var fieldType = record != null && i < record.PositionalTypes.Count ? record.PositionalTypes[i] : null;
                    if (fieldType == null || LeadsToContainer(fieldType)) term = term.With(FloorTerm(made.PositionalFields[i], fieldType));
                }
                foreach (var (name, value) in made.NamedFields)
                {
                    var fieldType = record?.NamedFields.FirstOrDefault(f => f.Name == name).Type;
                    if (fieldType == null || LeadsToContainer(fieldType)) term = term.With(FloorTerm(value, fieldType));
                }
                return term;
            }
            // A new series of the same things.
            case SortExpression sorted:
                return DepthTerm.Of(_rabbitDepth).With(FloorTerm(sorted.Series, type));
            case SeriesReverse reversed:
                return DepthTerm.Of(_rabbitDepth).With(FloorTerm(reversed.Series, type));
            case CastExpression cast:
                return _castTerms.TryGetValue(cast, out var castTerm) ? castTerm : DepthTerm.Of(0);
        }
        if (IsMadeHere(expr)) return DepthTerm.Of(_rabbitDepth);
        if (IsAccess(expr))
        {
            var term = DepthTarget(expr) is { Key: { } reached } ? DepthTerm.Ref("floor " + reached) : FloorTerm(AccessRoot(expr), null);
            return ReadsThroughGetter(expr) ? term.With(DepthTerm.Ref("floor " + LooseKey)) : term;
        }
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
        // An object met in a loop is a copy, as one in a `Define` is.
        var own = element && type is SeriesType { ElementType: ObjectType or RecordType } ? DepthTerm.Of(_rabbitDepth)
                : element ? floor : OwnTerm(value, type);
        // The name it came from can now be reached through this one, so whatever this one is
        // given reaches that one too.
        var back = DepthTarget(value) is { Key: { } from } reached
            ? TargetsOf(from, reached.Exact, fieldsReplaced: false) : [];
        // And through a literal, a choice or a call, every name in it that leads to a container.
        if (!IsAccess(value) && value is not VariableReference)
            foreach (var name in SharedNames(value))
                if (TryLookup(name, out var shared) && LeadsToContainer(shared!.Type) && DepthKey(name) is { } sharedKey)
                    back.AddRange(TargetsOf(sharedKey, exact: false, fieldsReplaced: false));
        // An object written out straight into a name: each field that can hold a container is a
        // name of its own, `key.field`, starting from what it was written with.
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
        if (!LeadsToContainer(valueType)) return;
        // A value with no name is copied to where the target's own name was made, when that is
        // further out than here; otherwise it stays where it is.
        int upper = (CopiesOut(valueType, []) ? EscapeDepthFor(value, valueType, ContainerDepthOf(target)) : null)
                 ?? LivesAt(value);
        var (key, exact) = DepthTarget(target);
        var stored = FloorTerm(value, valueType);
        var limit = LimitOf(target);
        if (key != null)
        {
            if (field != null && exact && IsSplitField(key, field))
            {
                // The field is replaced: what it is now is what was stored.
                Bound($"own {key}.{field}", OwnTerm(value, valueType));
                Bound($"floor {key}.{field}", stored);
            }
            else
                Bound("floor " + key, stored);
        }
        // One that is copied is never refused; what it leads to is in the target's reach all the same.
        if (upper > 0 && HoldsContainer(valueType, null)) _floorStores.Add(new FloorStore(upper, limit, line, col, action));
    }

    /// <summary>A cast: written down, so that what its callee stores can lower the floors of what
    /// it was given once that is known. Gives back how deep what it hands back can reach — no
    /// deeper than what it is given, unless it can hand back something it captured.</summary>
    private DepthTerm NoteCallFloors(FunctionType callee, IReadOnlyList<IExpression> args, List<CufetType?> types,
                                     IExpression? receiver, IExpression function, out List<CastPart> noted)
    {
        var parts = noted = [];
        void Part(int position, IExpression given, CufetType? type)
        {
            var (key, exact) = DepthTarget(given);
            parts.Add(new CastPart(position, key, exact, FloorTerm(given, type)));
        }
        for (int i = 0; i < args.Count; i++)
            if (LeadsToContainer(types[i])) Part(i, args[i], types[i]);
        if (receiver != null && InferType(receiver) is var receiverType && LeadsToContainer(receiverType))
            Part(ReceiverDepthIndex, receiver, receiverType);

        bool named = receiver != null
            || function is VariableReference called && TryLookup(called.Name, out var info)
               && info!.Type is FunctionType && !info.IsParameter
               && info.EstablishingExpr is VariableReference { Line: 0, Column: 0 }
               && !_innerFunctions.Contains($"{called.Name}@{info.EstablishingLine}");
        // A name of its own for what it hands back, bounded once it is known what that can reach.
        string result = $"cast#{_castFloorNotes.Count}";
        _depthStarts[result] = _rabbitDepth;
        _castFloorNotes.Add(new CastFloorNote(callee.Stores, parts, _rabbitDepth, result, named));
        return DepthTerm.Ref("floor " + result);
    }

    /// <summary>Where what a cast hands back IS, given where what it reaches is.</summary>
    private static DepthTerm OwnOfResult(DepthTerm floor)
    {
        var own = new DepthTerm { Least = floor.Least };
        own.Keys.AddRange(floor.Keys.Select(key => key.StartsWith("floor cast#", StringComparison.Ordinal) ? "own " + key[6..] : key));
        return own;
    }

    /// <summary>The names a value can share something with: those in it, but for what only names or
    /// measures one.</summary>
    private static IEnumerable<string> SharedNames(IExpression value)
    {
        switch (value)
        {
            case VariableReference named: return [named.Name];
            case SeriesLiteral made:      return made.Elements.SelectMany(SharedNames);
            case MapLiteral made:         return made.Pairs.SelectMany(pair => SharedNames(pair.Key).Concat(SharedNames(pair.Value)));
            case RecordLiteral made:      return made.PositionalFields.Concat(made.NamedFields.Select(f => f.Value)).SelectMany(SharedNames);
            case ObjectLiteral made:      return made.PositionalValues.Concat(made.NamedValues.Select(f => f.Value)).SelectMany(SharedNames);
            case ButVoidDefault either:   return SharedNames(either.Voidable).Concat(SharedNames(either.Default));
            case FailureFallback either:  return SharedNames(either.Fallible).Concat(SharedNames(either.Default));
            case ConditionalExpression either: return SharedNames(either.Value).Concat(SharedNames(either.Alternative));
            case FailurePropagate passed: return SharedNames(passed.Fallible);
            case SortExpression sorted:   return SharedNames(sorted.Series);
            case SeriesReverse reversed:  return SharedNames(reversed.Series);
            case CastExpression cast:
                return (cast.Function is PossessiveAccess on ? SharedNames(on.Target) : []).Concat(cast.Args.SelectMany(SharedNames));
            default: return IsAccess(value) ? SharedNames(AccessRoot(value)) : [];
        }
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
            BoundResult(note);
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

    /// <summary>What a cast hands back is no deeper than what it can reach of what it was given.</summary>
    private void BoundResult(CastFloorNote note)
    {
        string own = "own " + note.Result, floor = "floor " + note.Result;
        if (!note.Named)
        {
            Bound(own, DepthTerm.Of(0));
            Bound(floor, DepthTerm.Of(0));
            return;
        }
        bool known = StoresKnown(note.Callee);
        var reaches = known ? PlacesOf(note.Callee, summary => summary.Reaches) : [];
        var handsBack = known ? PlacesOf(note.Callee, summary => summary.HandsBack) : [];
        foreach (var part in note.Parts)
        {
            if (!known || reaches.Contains(part.Position)) Bound(floor, part.Term);
            if (!known || handsBack.Contains(part.Position)) Bound(own, part.Term);
        }
        if (reaches.Any(IsOuter)) Bound(floor, DepthTerm.Of(0));
        if (handsBack.Any(IsOuter)) Bound(own, DepthTerm.Of(0));
    }

    private HashSet<int> PlacesOf(StoreSummary summary, Func<StoreSummary, HashSet<int>> places)
    {
        if (summary.Interface is not { } interfaceName) return places(summary);
        var all = new HashSet<int>();
        foreach (var objectType in _objectDefs.Values)
            if (objectType.ConformedInterfaces.Contains(interfaceName)
                && FindMethodInOtOrPromoted(objectType, summary.Method!) is { } method)
                all.UnionWith(places(method.Stores));
        return all;
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
        _depthStarts[LooseKey] = int.MaxValue;
        if (_looseConstants) Bound("floor " + LooseKey, DepthTerm.Of(0));
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
        foreach (var call in _callNotes)
        {
            foreach (var (source, target) in PairsOf(call.Callee))
                RefuseAtCallByFloor(call, source, target, values);
            foreach (var (argument, function) in call.Passes)
                RefuseAtPassByFloor(call, argument, function, values);
        }
        // One that is copied goes to where its target lives, when that may be further out than the
        // depth its name says.
        foreach (var store in _copiedStores)
            if (DepthEval(values, store.Limit) is var limit && store.Upper > limit && limit < (store.CopiedTo ?? int.MaxValue))
                store.Set(HomeEscape);
        MarkHomeCopies();
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

    // While a body is traced: what each name it looked up was, the first time it was traced.
    private Dictionary<string, TypeInfo?>? _traceLookups;

    /// <summary>TryLookup, answered from what a body saw when it was first traced.</summary>
    private bool TraceLookup(string name, out TypeInfo? info)
    {
        if (_traceLookups is not { } memo) return TryLookup(name, out info);
        if (!memo.TryGetValue(name, out info))
            memo[name] = info = TryLookup(name, out var found) ? found : null;
        return info != null;
    }

    /// <summary>The name an expression starts from in the walk — `L.f` for a field of an object
    /// the body writes out — and whether the expression IS that name.</summary>
    private static (string? Name, bool Exact) FieldRoot(IExpression expr, Dictionary<string, HashSet<int>> localDepths)
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
        if (node is not VariableReference root) return (null, false);
        string? member = above switch
        {
            PossessiveAccess field  => field.Member,
            RecordNamedAccess named => named.FieldName,
            _ => null,
        };
        if (member != null && localDepths.ContainsKey($"{root.Name}.{member}"))
            return ($"{root.Name}.{member}", ReferenceEquals(above, expr));
        return (root.Name, expr is VariableReference);
    }

    /// <summary>Where an expression's value may come from, what its root was given, and everything
    /// written into it: what it can lead to.</summary>
    private HashSet<int> ReachOf(
        IExpression expr, Dictionary<string, int> paramIdx, HashSet<int> refParamIdxs,
        Dictionary<string, HashSet<int>> localDepths, Dictionary<string, int> captured,
        Dictionary<string, HashSet<int>> holds)
    {
        HashSet<int> Of(IExpression part) => ReachOf(part, paramIdx, refParamIdxs, localDepths, captured, holds);
        HashSet<int> Made(IEnumerable<IExpression> parts)
        {
            var made = new HashSet<int> { FreshDepthIndex };
            foreach (var part in parts) made.UnionWith(Of(part));
            return made;
        }
        switch (expr)
        {
            case ButVoidDefault either:        return [.. Of(either.Voidable), .. Of(either.Default)];
            case FailureFallback either:       return [.. Of(either.Fallible), .. Of(either.Default)];
            case ConditionalExpression either: return [.. Of(either.Value), .. Of(either.Alternative)];
            case FailurePropagate passed:      return Of(passed.Fallible);
            case SortExpression sorted:        return Made([sorted.Series]);
            case SeriesReverse reversed:       return Made([reversed.Series]);
            case AwaitedResultExpression or DeliveryExpression or TextSplit: return [FreshDepthIndex];
            case SeriesLiteral made:
                return Made(_carriers.ContainsKey(made) ? made.Elements : []);
            case MapLiteral made:
                return Made(_carriers.TryGetValue(made, out var mapped) && mapped is MapType map
                    ? made.Pairs.SelectMany(pair => new[] { (pair.Key, map.KeyType), (pair.Value, map.ValueType) })
                          .Where(part => LeadsToContainer(part.Item2)).Select(part => part.Item1)
                    : []);
            case RecordLiteral made:
                return Made(_carriers.TryGetValue(made, out var typed) && typed is RecordType record
                    ? made.PositionalFields.Where((_, i) => LeadsToContainer(record.PositionalTypes[i]))
                          .Concat(made.NamedFields.Where(f => LeadsToContainer(record.NamedFields.First(t => t.Name == f.Name).Type)).Select(f => f.Value))
                    : []);
            case ObjectLiteral made:
                _objectDefs.TryGetValue(made.TypeName, out var madeType);
                return Made(made.PositionalValues
                    .Where((_, i) => madeType == null || i >= madeType.PositionalTypes.Count || LeadsToContainer(madeType.PositionalTypes[i]))
                    .Concat(made.NamedValues.Where(f => CarriedField(madeType, f.Name).Carries).Select(f => f.Value)));
            case CastExpression cast when _castNotes.TryGetValue(cast, out var call):
            {
                if (StoresKnown(call.Callee)) return HandedBack(call, PlacesOf(call.Callee, summary => summary.Reaches), Of);
                var reach = SymbolicExprDepth(expr, paramIdx, refParamIdxs, localDepths, captured);
                for (int i = 0; i < call.Args.Count; i++)
                    if (call.ArgLeads[i]) reach.UnionWith(Of(call.Args[i]));
                if (call.Receiver != null) reach.UnionWith(Of(call.Receiver));
                return reach;
            }
        }
        if (IsAccess(expr) && AccessRoot(expr) is not VariableReference && FieldRoot(expr, localDepths).Name is null)
            return _looseConstants && ReadsThroughGetter(expr) ? [ConstantIndex, .. Of(AccessRoot(expr))] : Of(AccessRoot(expr));
        var reached = SymbolicExprDepth(expr, paramIdx, refParamIdxs, localDepths, captured);
        if (_looseConstants && ReadsThroughGetter(expr)) reached.Add(ConstantIndex);
        if (FieldRoot(expr, localDepths).Name is { } root && holds.TryGetValue(root, out var held)) reached.UnionWith(held);
        return reached;
    }

    // The body being traced, for what a cast in it captured.
    private StoreFrame? _traceFrame;

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

    /// <summary>A name given a value, in the walk: a second name for another shares what it holds;
    /// an element of another holds what that one holds.</summary>
    private bool GiveHolds(
        string name, IExpression value, bool naming, Dictionary<string, HashSet<int>> localDepths,
        Dictionary<string, HashSet<int>> holds, Func<IExpression, HashSet<int>> itself,
        Func<IExpression, HashSet<int>> reach)
    {
        var (root, exact) = FieldRoot(value, localDepths);
        if (root == name) return false;
        bool grew = false;
        // A literal, a choice, a call: it holds what it was written with, and each name in it that
        // leads anywhere now leads to whatever is put into it.
        if (root == null)
        {
            if (!Carries(value)) return false;
            grew |= Grow(HeldBy(holds, name), reach(value));
            grew |= Grow(localDepths[name], naming ? itself(value) : reach(value));
            foreach (var shared in SharedNames(value))
                if (shared != name) grew |= GrowShared(shared, HeldBy(holds, name), localDepths, holds);
            return grew;
        }
        if (naming && exact)
        {
            grew |= Grow(HeldBy(holds, name), HeldBy(holds, root));
            grew |= GrowShared(root, HeldBy(holds, name), localDepths, holds);
        }
        else if (!naming || IsAccess(value))
        {
            var rootHeld = HeldBy(holds, root);
            grew |= Grow(localDepths[name], rootHeld);
            grew |= Grow(HeldBy(holds, name), rootHeld);
        }
        return grew;
    }

    /// <summary>What a name shares with another grows with it, and so does each field that is a name of
    /// its own.</summary>
    private static bool GrowShared(string name, HashSet<int> held, Dictionary<string, HashSet<int>> localDepths, Dictionary<string, HashSet<int>> holds)
    {
        bool grew = Grow(HeldBy(holds, name), held);
        if (!name.Contains('.'))
            foreach (var field in localDepths.Keys.Where(key => key.StartsWith(name + ".", StringComparison.Ordinal)).ToList())
                grew |= Grow(HeldBy(holds, field), held);
        return grew;
    }

    /// <summary>A value with something in it that leads to a container, as far as its form says.</summary>
    private bool Carries(IExpression value) => value switch
    {
        SeriesLiteral or MapLiteral or RecordLiteral => _carriers.ContainsKey(value),
        ObjectLiteral made => _carriers.ContainsKey(made),
        ButVoidDefault either => Carries(either.Voidable) || Carries(either.Default) || IsNameOrAccess(either.Voidable) || IsNameOrAccess(either.Default),
        FailureFallback either => Carries(either.Fallible) || Carries(either.Default) || IsNameOrAccess(either.Default),
        ConditionalExpression either => Carries(either.Value) || Carries(either.Alternative) || IsNameOrAccess(either.Value) || IsNameOrAccess(either.Alternative),
        FailurePropagate passed => Carries(passed.Fallible),
        SortExpression sorted => IsNameOrAccess(sorted.Series) || Carries(sorted.Series),
        SeriesReverse reversed => IsNameOrAccess(reversed.Series) || Carries(reversed.Series),
        CastExpression cast => _castNotes.ContainsKey(cast),
        _ => false,
    };

    /// <summary>What an expression's value may itself BE: the name, an item of what it is read out of,
    /// or what a function hands back of what it was given.</summary>
    private HashSet<int> IsOf(
        IExpression expr, Dictionary<string, int> paramIdx, HashSet<int> refParamIdxs,
        Dictionary<string, HashSet<int>> localDepths, Dictionary<string, int> captured,
        Dictionary<string, HashSet<int>> holds)
    {
        HashSet<int> Is(IExpression part) => IsOf(part, paramIdx, refParamIdxs, localDepths, captured, holds);
        HashSet<int> Of(IExpression part) => ReachOf(part, paramIdx, refParamIdxs, localDepths, captured, holds);
        switch (expr)
        {
            case ButVoidDefault either:        return [.. Is(either.Voidable), .. Is(either.Default)];
            case FailureFallback either:       return [.. Is(either.Fallible), .. Is(either.Default)];
            case ConditionalExpression either: return [.. Is(either.Value), .. Is(either.Alternative)];
            case FailurePropagate passed:      return Is(passed.Fallible);
            case CastExpression cast when _castNotes.TryGetValue(cast, out var call) && StoresKnown(call.Callee):
                return HandedBack(call, PlacesOf(call.Callee, summary => summary.HandsBack), Of);
        }
        if (IsAccess(expr) && !FieldRoot(expr, localDepths).Exact) return Of(expr);
        if (IsMadeHere(expr) || expr is RecordLiteral) return [FreshDepthIndex];
        return SymbolicExprDepth(expr, paramIdx, refParamIdxs, localDepths, captured);
    }

    /// <summary>Some places of a function, in the terms of the body casting it.</summary>
    private HashSet<int> HandedBack(CallNote call, HashSet<int> places, Func<IExpression, HashSet<int>> reach)
    {
        var all = new HashSet<int>();
        foreach (int place in places.Order())
        {
            if (place == FreshDepthIndex) all.Add(FreshDepthIndex);
            else if (place == ReceiverDepthIndex) { if (call.Receiver != null) all.UnionWith(reach(call.Receiver)); }
            else if (place >= 0) { if (place < call.Args.Count && call.ArgLeads[place]) all.UnionWith(reach(call.Args[place])); }
            else if (_traceFrame is { } frame) all.UnionWith(CapturedPlace(frame, call.Callee, place));
        }
        return all;
    }

    private static bool IsNameOrAccess(IExpression expr) => expr is VariableReference || IsAccess(expr);

    /// <summary>One round of what each name of a body holds: what was stored into it or into
    /// anything inside it, what a cast stores into it, and, for a second name or an element, what
    /// the name it came from holds. A field of an object the body writes out is a name of its own.</summary>
    private bool GrowHolds(
        StoreFrame frame, List<object> nodes, Dictionary<string, HashSet<int>> localDepths,
        Dictionary<string, HashSet<int>> holds, Dictionary<string, int> paramIdx, HashSet<int> refParamIdxs,
        Dictionary<string, int> captured,
        Dictionary<string, List<(string Field, List<IExpression> Values)>> split)
    {
        HashSet<int> Reach(IExpression expr) => ReachOf(expr, paramIdx, refParamIdxs, localDepths, captured, holds);
        HashSet<int> Is(IExpression expr) => SymbolicExprDepth(expr, paramIdx, refParamIdxs, localDepths, captured);
        HashSet<int> Itself(IExpression expr) => IsOf(expr, paramIdx, refParamIdxs, localDepths, captured, holds);
        bool grew = false;
        foreach (var node in nodes)
        {
            if (DepthTargetOf(node) is not { } given) continue;
            bool naming = node is DefineStatement or BecomesStatement;
            grew |= GiveHolds(given.Name, given.Value, naming, localDepths, holds, Itself, Reach);
            // An object written out given a new value: each field of its own is now that one's.
            if (node is BecomesStatement && split.TryGetValue(given.Name, out var replaced))
                foreach (var (field, _) in replaced)
                {
                    var reach = Reach(given.Value);
                    grew |= Grow(localDepths[$"{given.Name}.{field}"], reach);
                    grew |= Grow(HeldBy(holds, $"{given.Name}.{field}"), reach);
                }
        }
        foreach (var (name, fields) in split)
            foreach (var (field, values) in fields)
            {
                string fieldName = $"{name}.{field}";
                foreach (var value in values)
                {
                    grew |= Grow(localDepths[fieldName], Is(value));
                    grew |= GiveHolds(fieldName, value, naming: true, localDepths, holds, Itself, Reach);
                }
                // The object holds what its field is and holds.
                grew |= Grow(HeldBy(holds, name), localDepths[fieldName]);
                grew |= Grow(HeldBy(holds, name), HeldBy(holds, fieldName));
            }
        foreach (var store in frame.Stores)
        {
            if (store.Field != null && store.Target is VariableReference written
                && localDepths.TryGetValue($"{written.Name}.{store.Field}", out var fieldIs))
            {
                // A field of its own is set: it IS what was stored.
                grew |= Grow(fieldIs, Reach(store.Value));
                continue;
            }
            foreach (var into in HoldersOf(store.Target, localDepths))
                grew |= GrowShared(into, Reach(store.Value), localDepths, holds);
        }
        foreach (var call in frame.Calls)
        {
            if (StoresKnown(call.Callee))
            {
                // What the callee really stores, argument into argument.
                IExpression? At(int place) =>
                    place == ReceiverDepthIndex ? call.Receiver
                    : place >= 0 && place < call.Args.Count ? call.Args[place] : null;
                foreach (var (source, target) in PairsOf(call.Callee))
                {
                    if (At(target) is not { } targetExpr) continue;
                    HashSet<int>? from =
                        source == FreshDepthIndex ? [FreshDepthIndex]
                        : IsOuter(source) ? CapturedPlace(frame, call.Callee, source)
                        : At(source) is { } sourceExpr ? Reach(sourceExpr) : null;
                    if (from != null)
                        foreach (var into in HoldersOf(targetExpr, localDepths)) grew |= GrowShared(into, from, localDepths, holds);
                }
                continue;
            }
            // Nothing known of the callee: it may store any of them into any other.
            var parts = new List<IExpression>();
            for (int i = 0; i < call.Args.Count; i++)
                if (call.ArgLeads[i]) parts.Add(call.Args[i]);
            if (call.Receiver != null) parts.Add(call.Receiver);
            for (int i = 0; i < parts.Count; i++)
            {
                foreach (var into in HoldersOf(parts[i], localDepths))
                    for (int j = 0; j < parts.Count; j++)
                        if (j != i) grew |= GrowShared(into, Reach(parts[j]), localDepths, holds);
            }
        }
        return grew;
    }

    /// <summary>The names a target may be reached through.</summary>
    private static IEnumerable<string> HoldersOf(IExpression target, Dictionary<string, HashSet<int>> localDepths) =>
        FieldRoot(target, localDepths).Name is { } root ? [root] : SharedNames(target).Distinct();
}
