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
    /// <remarks>
    /// The tree walk itself lives in <see cref="AstRebuilder"/> — generic instantiation performs the
    /// same walk with a different substitution, and one copy of it is the point.
    /// </remarks>
    public static IReadOnlyList<IStatement> Apply(IReadOnlyList<IStatement> statements) =>
        AstRebuilder.Apply(statements, Substitute);

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
                    {
                        ReturnDepthSignature = function.ReturnDepthSignature,
                        ParameterNames       = function.ParameterNames,
                    };
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
}
