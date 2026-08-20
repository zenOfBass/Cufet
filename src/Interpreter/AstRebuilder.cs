using System.Collections;
using System.Runtime.CompilerServices;

namespace Cufet.Interpreter;

/// <summary>
/// Rebuilds an AST with every written type passed through a substitution, sharing nothing that
/// changed and everything that did not.
/// </summary>
/// <remarks>
/// <para>
/// ★ Extracted from StashTypeSubstitution when generic instantiation needed the same walk. Two
/// passes now replace types wholesale — a `stash of T` becoming its closure, and a template's blank
/// becoming its filling — and the walk is identical in both; only the substitution differs. A second
/// copy is exactly the one-rule-N-copies shape the reflection-walk registry in ExhaustivenessTests
/// exists to prevent.
/// </para>
/// <para>
/// ★ Reflective rather than node-by-node, for the reason that registry states: a hand-written list
/// of the ~15 node types carrying a type falls behind the next feature, silently. Records make the
/// generic version safe — every AST node here is positional, so its constructor parameters and its
/// properties share names and order.
/// </para>
/// </remarks>
internal static class AstRebuilder
{
    /// <summary>
    /// Applies <paramref name="leaf"/> to a type and, where it declines, to everything inside it.
    /// </summary>
    /// <remarks>
    /// ⚠ Written because leaving it out is a silent no-op that looks like success. A substitution
    /// matching only the top level replaces a bare `element` and walks straight past
    /// `series of element` — the tree rebuilds, reports "changed", and the blank survives inside the
    /// compound where nearly every real use of it lives.
    ///
    /// ObjectType is NOT descended into: it is nominal and may be recursive (a `node` holding a
    /// `series of node` would not terminate), and its fields travel with its own definition.
    /// </remarks>
    public static CufetType SubstituteDeep(CufetType type, Func<CufetType, CufetType> leaf)
    {
        var direct = leaf(type);
        if (!ReferenceEquals(direct, type)) return direct;

        CufetType Inner(CufetType t) => SubstituteDeep(t, leaf);
        bool Same(CufetType original, out CufetType result)
        {
            result = Inner(original);
            return ReferenceEquals(result, original);
        }

        switch (type)
        {
            case SeriesType series:
                return Same(series.ElementType, out var element) ? series : new SeriesType(element);
            case VoidableType voidable:
                return Same(voidable.Inner, out var inner) ? voidable : new VoidableType(inner);
            case FailureType failure:
                return Same(failure.Inner, out var failed) ? failure : new FailureType(failed);
            case ChannelType channel:
                return Same(channel.ElementType, out var carried) ? channel : new ChannelType(carried);
            case StashType stash:
                return Same(stash.ElementType, out var buried) ? stash : new StashType(buried);
            case ReadableStreamType readable:
                return Same(readable.ElementType, out var read) ? readable : new ReadableStreamType(read);
            case WritableStreamType writable:
                return Same(writable.ElementType, out var written) ? writable : new WritableStreamType(written);
            case MapType map:
            {
                bool unchanged = Same(map.KeyType, out var key) & Same(map.ValueType, out var value);
                return unchanged ? map : new MapType(key, value);
            }
            case MappingType mapping:
            {
                bool unchanged = Same(mapping.KeyType, out var key) & Same(mapping.ValueType, out var value);
                return unchanged ? mapping : new MappingType(key, value);
            }
            case RecordType record:
            {
                var positional = record.PositionalTypes.Select(Inner).ToList();
                var named      = record.NamedFields.Select(f => (f.Name, Type: Inner(f.Type))).ToList();
                bool unchanged = positional.Zip(record.PositionalTypes).All(p => ReferenceEquals(p.First, p.Second))
                              && named.Zip(record.NamedFields).All(p => ReferenceEquals(p.First.Type, p.Second.Type));
                return unchanged ? record : new RecordType(positional, named);
            }
            case UnionType { Cases: { } cases } union:
            {
                var replaced = cases.Select(Inner).ToList();
                return replaced.Zip(cases).All(p => ReferenceEquals(p.First, p.Second))
                    ? union : new UnionType(replaced);
            }
            case FunctionType function:
            {
                var parameters = function.ParameterTypes.Select(Inner).ToList();
                var returned   = function.ReturnType == null ? null : Inner(function.ReturnType);
                bool unchanged = parameters.Zip(function.ParameterTypes).All(p => ReferenceEquals(p.First, p.Second))
                              && ReferenceEquals(returned, function.ReturnType);
                if (unchanged) return function;
                // ⚠ Carried, not dropped — the checker writes it to propagate rabbit depth through a
                // call, and a rebuilt FunctionType without it silently claims depth 0.
                return new FunctionType(parameters, returned)
                    { ReturnDepthSignature = function.ReturnDepthSignature };
            }
            default:
                return type;   // scalars, ObjectType, InterfaceType, matrices, markers
        }
    }

    /// <summary>Rebuilds a statement list, handing back the ORIGINAL when nothing changed.</summary>
    public static IReadOnlyList<IStatement> Apply(
        IReadOnlyList<IStatement> statements,
        Func<CufetType, CufetType> substitute,
        Func<IStatement, IStatement?>? replace = null) =>
        TryRebuild(statements, substitute, out var rebuilt, replace)
            ? (IReadOnlyList<IStatement>)rebuilt!
            : statements;

    /// <summary>Rebuilds a single node, handing back the ORIGINAL when nothing changed.</summary>
    public static T Rebuild<T>(T node, Func<CufetType, CufetType> substitute) where T : class =>
        TryRebuild(node, substitute, out var rebuilt) ? (T)rebuilt! : node;

    /// <summary>
    /// Rebuilds <paramref name="node"/> if anything under it changed; false means "keep what you had".
    /// </summary>
    /// <param name="replace">
    /// Swaps whole STATEMENTS, where <paramref name="substitute"/> swaps types. Null (the usual
    /// case) means no statement is ever replaced.
    /// </param>
    /// <remarks>
    /// ★ Why the statement hook lives here rather than in a walk of its own. A pass that rewrites
    /// one statement kind into another — a stash `For each` into the drain loop it stands for — has
    /// to reach every statement in the program, and the containers are not all
    /// <c>IStatement</c>: <c>ConditionArm</c> and <c>JudgeArm</c> implement neither AST interface
    /// and are the two a hand-written walk forgets. The reflective walk cannot forget them, because
    /// it keys on the NAMESPACE and they are in it.
    /// </remarks>
    public static bool TryRebuild(
        object? node, Func<CufetType, CufetType> substitute, out object? result,
        Func<IStatement, IStatement?>? replace = null)
    {
        result = node;

        // ⚠ Descend INTO the replacement, not past it: a stash loop nested in a stash loop is
        // replaced outside-in, and the body carried into the new node still holds the inner one.
        // It terminates because a replacement is never itself replaceable — `replace` answers for
        // the node kind it rewrites, and it rewrites that kind away.
        if (replace != null && node is IStatement original && replace(original) is { } expanded
            && !ReferenceEquals(expanded, original))
        {
            TryRebuild(expanded, substitute, out result, replace);
            return true;
        }

        switch (node)
        {
            case null or string:
                return false;

            // ⚠ An ENUM lives in this namespace too — ReadForm, FileReadForm, PathCheckKind,
            // OpenMode — and has no constructor to rebuild it with, so the default arm below threw
            // "Sequence contains no elements" on it. There is nothing inside one to substitute
            // anyway.
            //
            // ★ Latent since the walk was written, and NOT only a new-caller problem: any burying
            // program that also opened a file would have hit it. It stayed hidden because the walk
            // ran only for programs containing a `bury`, and no test combined the two.
            case Enum:
                return false;

            case CufetType type:
            {
                var substituted = substitute(type);
                if (ReferenceEquals(substituted, type)) return false;
                result = substituted;
                return true;
            }

            // Parameter lists and field lists are tuples, and a tuple is not in our namespace.
            case ITuple tuple:
            {
                var items    = new object?[tuple.Length];
                bool changed = false;
                for (int i = 0; i < tuple.Length; i++)
                    changed |= TryRebuild(tuple[i], substitute, out items[i], replace);
                if (!changed) return false;
                result = Activator.CreateInstance(node.GetType(), items);
                return true;
            }

            case IEnumerable sequence:
            {
                var elementType = ElementTypeOf(node.GetType());
                if (elementType == null) return false;

                var rebuilt  = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
                bool changed = false;
                foreach (var item in sequence)
                {
                    changed |= TryRebuild(item, substitute, out var replacement, replace);
                    rebuilt.Add(replacement);
                }
                if (!changed) return false;
                result = rebuilt;
                return true;
            }

            default:
            {
                var type = node.GetType();
                if (type.Namespace != typeof(Program).Namespace) return false;

                var constructor = type.GetConstructors()
                    .OrderByDescending(c => c.GetParameters().Length).First();
                var parameters = constructor.GetParameters();
                var arguments  = new object?[parameters.Length];
                bool changed   = false;

                for (int i = 0; i < parameters.Length; i++)
                {
                    // ⚠ Loudly, not quietly. Answering "unchanged" here would ALSO discard the
                    // substitutions already made to earlier parameters, and report success while
                    // doing it — the same silent-no-op shape as the init-setter trap below, which
                    // took an hour to see precisely because it looked like it had worked.
                    var property = type.GetProperty(parameters[i].Name!)
                        ?? throw new InvalidOperationException(
                            $"{type.Name}.{parameters[i].Name} has no matching property — "
                            + "AstRebuilder can only rebuild positional records.");
                    changed |= TryRebuild(property.GetValue(node), substitute, out arguments[i], replace);
                }
                if (!changed) return false;

                var replacement = constructor.Invoke(arguments);
                CarryWritableProperties(type, node, replacement, substitute,
                    parameters.Select(p => p.Name!).ToHashSet(StringComparer.Ordinal));
                result = replacement;
                return true;
            }
        }
    }

    /// <summary>
    /// Copies the properties the checker WRITES onto a node after constructing it.
    /// </summary>
    /// <remarks>
    /// ⚠ Not cosmetic. Eight AST properties are side channels filled in by the type checker — five
    /// `EscapeToDepth`s, a `KeyEscapeToDepth`, and `IsTypeCheck.StaticTargetType` — and
    /// `EscapeToDepth` is the one telling the compiler to COPY a value into an outer arena. A rebuilt
    /// node without it reads as "no escape", so the copy never happens and the program keeps a
    /// pointer into a freed region. Losing it would look like memory corruption, not a dropped field.
    ///
    /// ⚠⚠ <paramref name="fromConstructor"/> is what makes this safe, and leaving it out cost an
    /// hour. A positional record's properties are `init`-only, and an `init` setter is a PUBLIC
    /// setter as far as reflection can tell — so copying "everything writable" copied every
    /// substituted value straight back to what it had been. The rebuild reported success and the tree
    /// came out unchanged. Only properties the constructor did NOT set are side channels.
    /// </remarks>
    private static void CarryWritableProperties(
        Type type, object original, object replacement,
        Func<CufetType, CufetType> substitute, HashSet<string> fromConstructor)
    {
        foreach (var property in type.GetProperties())
        {
            if (fromConstructor.Contains(property.Name)) continue;
            if (property.SetMethod is not { IsPublic: true } || property.GetIndexParameters().Length > 0)
                continue;
            var value = property.GetValue(original);
            // A carried type gets substituted too — StaticTargetType is one of these.
            if (value is CufetType carried) value = substitute(carried);
            property.SetValue(replacement, value);
        }
    }

    private static Type? ElementTypeOf(Type collection)
    {
        if (collection.IsArray) return collection.GetElementType();
        foreach (var contract in collection.GetInterfaces())
            if (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return contract.GetGenericArguments()[0];
        return null;
    }
}
