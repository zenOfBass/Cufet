using System.Collections;
using System.Runtime.CompilerServices;

namespace Cufet.Interpreter;

/// <summary>
/// Replaces every written `stash of T` in the tree with the closure it lowers to, so that no
/// `StashType` survives the front end.
/// </summary>
/// <remarks>
/// <para>
/// ★★ Why this exists at all. StashTransform rewrites every burying function into a factory handing
/// back a CLOSURE — nothing takes nothing and gives back `voidable T` — and that closure is the only
/// value a stash ever is. But a type the writer SPELLS OUT is not touched by that rewrite: the
/// `stash of number` on a parameter, an object field, or a series element stays exactly as written,
/// and the back end then meets one concept wearing two names.
/// </para>
/// <para>
/// ⚠ The alternative was normalising inside the compiler, and it was measured before being
/// rejected. Declared types are RECORDED in 56 places there and read in far fewer, so "normalise on
/// read" looked cheap — but each missed read is silent until somebody writes the program that trips
/// it, and fixing the first one immediately surfaced two more (equality, then printing). That is the
/// one-rule-N-copies shape this codebase has been bitten by seven times over; see the reflection-walk
/// registry in ExhaustivenessTests. Substituting once, here, makes all 56 correct at a stroke and
/// gives any future back end the same guarantee for free.
/// </para>
/// <para>
/// The front end keeps the two spellings apart deliberately, and that is not in tension with this:
/// `stash of number` is what makes an error say "stash of number", and what stops a stash being
/// called directly instead of unburied. Those are the type CHECKER's job, and it has finished by the
/// time this runs.
/// </para>
/// </remarks>
internal static class StashTypeSubstitution
{
    /// <summary>Substitutes throughout, returning the ORIGINAL list when nothing mentioned a stash.</summary>
    public static IReadOnlyList<IStatement> Apply(IReadOnlyList<IStatement> statements) =>
        TryRebuild(statements, out var rebuilt)
            ? (IReadOnlyList<IStatement>)rebuilt!
            : statements;

    // ── Types ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The substitution itself. Returns the SAME instance when there was no stash inside, which is
    /// what lets the tree walk leave untouched programs entirely alone.
    /// </summary>
    /// <remarks>
    /// ⚠ ObjectType is deliberately NOT descended into. It is nominal and may be recursive — a
    /// `node` holding a `series of node` would not terminate — and it does not need to be: an
    /// object's field types reach the back end through its ObjectDefinition, which IS rewritten
    /// here, and a field READ hands back the field's own type, which is substituted where it lands.
    /// </remarks>
    private static CufetType Substitute(CufetType type)
    {
        switch (type)
        {
            case StashType stash:
                return new FunctionType([], new VoidableType(Substitute(stash.ElementType)));

            case SeriesType series:
                return Same(series.ElementType, out var element)
                    ? series : new SeriesType(element);

            case VoidableType voidable:
                return Same(voidable.Inner, out var inner)
                    ? voidable : new VoidableType(inner);

            case FailureType failure:
                return Same(failure.Inner, out var failed)
                    ? failure : new FailureType(failed);

            case ChannelType channel:
                return Same(channel.ElementType, out var carried)
                    ? channel : new ChannelType(carried);

            case MapType map:
            {
                bool unchanged = Same(map.KeyType, out var key) & Same(map.ValueType, out var value);
                return unchanged ? map : new MapType(key, value);
            }

            case RecordType record:
            {
                var positional = record.PositionalTypes.Select(Substitute).ToList();
                var named      = record.NamedFields.Select(f => (f.Name, Type: Substitute(f.Type))).ToList();
                bool unchanged = positional.Zip(record.PositionalTypes).All(p => ReferenceEquals(p.First, p.Second))
                              && named.Zip(record.NamedFields).All(p => ReferenceEquals(p.First.Type, p.Second.Type));
                return unchanged ? record : new RecordType(positional, named);
            }

            case UnionType union when union.Cases != null:
            {
                var cases = union.Cases.Select(Substitute).ToList();
                return cases.Zip(union.Cases).All(p => ReferenceEquals(p.First, p.Second))
                    ? union : new UnionType(cases);
            }

            case FunctionType function:
            {
                var parameters = function.ParameterTypes.Select(Substitute).ToList();
                var returned   = function.ReturnType == null ? null : Substitute(function.ReturnType);
                bool unchanged = parameters.Zip(function.ParameterTypes).All(p => ReferenceEquals(p.First, p.Second))
                              && ReferenceEquals(returned, function.ReturnType);
                if (unchanged) return function;
                // ⚠ Carried, not dropped. The checker writes this to propagate rabbit depth through
                // a call, and a rebuilt FunctionType without it would silently claim depth 0.
                return new FunctionType(parameters, returned)
                    { ReturnDepthSignature = function.ReturnDepthSignature };
            }

            default:
                return type;   // scalars, ObjectType, InterfaceType, matrices, streams, markers
        }
    }

    private static bool Same(CufetType original, out CufetType substituted)
    {
        substituted = Substitute(original);
        return ReferenceEquals(substituted, original);
    }

    // ── The tree ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds <paramref name="node"/> if anything under it changed; answers false to mean
    /// "untouched, keep what you had".
    /// </summary>
    /// <remarks>
    /// ★ Reflective rather than a node-by-node rewrite, and for the reason the reflection-walk
    /// registry states: a hand-written list of the ~15 node types that carry a type is a list that
    /// falls behind the next feature, silently. Records make the generic version safe — every AST
    /// node here is positional, so its constructor parameters and its properties are the same names
    /// in the same order.
    /// </remarks>
    private static bool TryRebuild(object? node, out object? result)
    {
        result = node;
        switch (node)
        {
            case null or string:
                return false;

            case CufetType type:
            {
                var substituted = Substitute(type);
                if (ReferenceEquals(substituted, type)) return false;
                result = substituted;
                return true;
            }

            // Parameter lists and field lists are tuples, and a tuple is not in our namespace.
            case ITuple tuple:
            {
                var items   = new object?[tuple.Length];
                bool changed = false;
                for (int i = 0; i < tuple.Length; i++)
                    changed |= TryRebuild(tuple[i], out items[i]);
                if (!changed) return false;
                result = Activator.CreateInstance(node.GetType(), items);
                return true;
            }

            case IEnumerable sequence:
            {
                var elementType = ElementTypeOf(node.GetType());
                if (elementType == null) return false;

                var rebuilt = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(elementType))!;
                bool changed = false;
                foreach (var item in sequence)
                {
                    changed |= TryRebuild(item, out var replacement);
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
                    // doing it — the same silent-no-op shape as the init-setter bug above, which
                    // took an hour to see precisely because it looked like it had worked. Every AST
                    // node is a positional record, so this cannot fire; if a future one is not, the
                    // pass says so instead of mis-lowering the program.
                    var property = type.GetProperty(parameters[i].Name!)
                        ?? throw new InvalidOperationException(
                            $"{type.Name}.{parameters[i].Name} has no matching property — "
                          + "StashTypeSubstitution can only rebuild positional records.");
                    changed |= TryRebuild(property.GetValue(node), out arguments[i]);
                }
                if (!changed) return false;

                var replacement = constructor.Invoke(arguments);
                CarryWritableProperties(type, node, replacement,
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
    /// ⚠ Not optional, and not cosmetic. Eight AST properties are side channels filled in by the
    /// type checker — five `EscapeToDepth`s, a `KeyEscapeToDepth`, and `IsTypeCheck.StaticTargetType`
    /// — and `EscapeToDepth` is the one that tells the compiler to COPY a value into an outer
    /// arena. A rebuilt node without it reads as "no escape", so the copy never happens and the
    /// program keeps a pointer into a region that has been freed. Losing it would be silent, and
    /// would look like memory corruption rather than like a dropped field.
    ///
    /// ⚠⚠ <paramref name="fromConstructor"/> is what makes this safe, and leaving it out cost an
    /// hour. A positional record's properties are `init`-only, and an `init` setter is a PUBLIC
    /// setter as far as reflection can tell — so copying "everything writable" copied every
    /// substituted value straight back to what it had been. The rebuild reported success and the
    /// tree came out unchanged. Only the properties the constructor did NOT set are side channels.
    /// </remarks>
    private static void CarryWritableProperties(
        Type type, object original, object replacement, HashSet<string> fromConstructor)
    {
        foreach (var property in type.GetProperties())
        {
            if (fromConstructor.Contains(property.Name)) continue;
            if (property.SetMethod is not { IsPublic: true } || property.GetIndexParameters().Length > 0)
                continue;
            var value = property.GetValue(original);
            // A carried type gets substituted too — StaticTargetType is one of these.
            if (value is CufetType carried) value = Substitute(carried);
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
