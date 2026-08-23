using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Cufet.Interpreter;

namespace Cufet.Compiler;

/// <summary>Runs an axiom for the interpreter, by compiling it and calling it.</summary>
/// <remarks>
/// <para>
/// ★★ The shim IS the compiled axioms — not a fixed prebuilt library, and not something generated
/// from declarations. The compiled backend pastes an axiom into its own C; this compiles the same
/// wrapper (ForeignC, shared by both) into a small shared library and calls it. One artifact, and
/// no separate declaration language to keep in step with it.
/// </para>
/// <para>
/// ★ ONE AXIOM PER LIBRARY, so there is no dispatcher and no index yet. The entry point has a fixed
/// name and a fixed signature, which is what keeps the managed side to a single delegate and no
/// dynamic code generation. When axioms take parameters this grows a switch over the shapes that
/// exist — deliberately a generated switch and not libffi, which earns its keep only once structs
/// by value, varargs, or callbacks arrive.
/// </para>
/// <para>
/// ★ It caches, so gcc runs once per distinct axiom per machine rather than once per run. Keyed by
/// content the same way the runtime object cache is: the source, the wrapper, and the identity of
/// the compiler that would build it. An axiom shared between two programs is built once for both.
/// </para>
/// <para>
/// ⚠ The cost this pays for is real: interpreting a program with foreign source needs a C toolchain
/// the first time it is seen. That is the price of the interpreter staying an oracle for FFI, and
/// FFI is the one area where being wrong means memory corruption rather than a wrong number.
/// </para>
/// </remarks>
public sealed class GccForeignRunner : IForeignRunner
{
    /// <summary>The fixed entry point every whole-number shim exports.</summary>
    private const string WholeEntryPoint = ForeignC.WholeEntryPoint;

    /// <summary>The fixed entry point a floating-point shim exports instead.</summary>
    private const string RealEntryPoint = ForeignC.RealEntryPoint;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ForeignWhole WholeEntry(IntPtr arguments, int count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ForeignReal RealEntry(IntPtr arguments, int count);

    /// <summary>
    /// A loaded shim: exactly one of these is non-null, decided by the axiom's declared result.
    /// </summary>
    /// <remarks>
    /// ⚠ TWO entry points rather than one widened struct. A shim wraps ONE axiom whose result type
    /// is known when it is generated, so it exports the one shape that axiom needs — and keeping
    /// them apart means neither struct carries fields the other's results leave meaningless.
    /// </remarks>
    private readonly record struct LoadedShim(WholeEntry? Whole, RealEntry? Real);

    /// <summary>The converted decimal, matching ForeignC.RealResultType exactly.</summary>
    /// <remarks>
    /// ★ Nothing here converts anything. The shared C already did the whole base-2-to-base-10
    /// conversion — the one DESIGN requires to exist once — and these are the three numbers a
    /// decimal is made of, handed straight to the parts constructor. The compiled backend assembles
    /// the identical three through `cufet_dec_lit`.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct ForeignReal
    {
        public ulong Hi;
        public ulong Lo;
        public int   Scale;
        public int   Sign;
        public int   Ok;
    }

    /// <summary>The result pair, matching ForeignC.WholeResultType exactly.</summary>
    /// <remarks>
    /// ⚠ Sequential and blittable, so the runtime uses the platform's own struct-return convention
    /// — which differs between Windows x64 (a hidden pointer for anything over 8 bytes) and SysV
    /// (two registers). Describing the shape and letting the runtime decide is the only version of
    /// this that is right on both; naming a convention would be right on one.
    ///
    /// ★ `Bits` is unsigned on both sides. A signed result arrives as its two's-complement bits and
    /// is read back with <c>unchecked((long)…)</c>, which is the exact inverse of the C cast that
    /// produced it.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct ForeignWhole
    {
        public ulong Bits;
        public int   IsUnsigned;
    }

    /// <summary>One argument slot, matching ForeignC.ShimArgumentType exactly.</summary>
    /// <remarks>
    /// ⚠ An OVERLAPPED pair, not two fields. The C side is a union, so the managed side has to be
    /// one too — laying them out sequentially would put the text pointer eight bytes past where C
    /// reads it, which is a wrong pointer rather than a wrong number.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit, Size = 8)]
    private struct ShimArgument
    {
        [FieldOffset(0)] public long Whole;
        [FieldOffset(0)] public IntPtr Text;
    }

    private readonly GccInvoker _gcc;
    private readonly string? _cacheRoot;

    // Loaded shims, by the axiom they wrap. A library stays loaded for the life of the process:
    // an axiom returned in a loop must not pay for a load each time round.
    private readonly ConcurrentDictionary<string, LoadedShim> _loaded = new(StringComparer.Ordinal);

    public GccForeignRunner(GccInvoker? gcc = null, string? cacheRoot = null)
    {
        _gcc = gcc ?? new GccInvoker();
        _cacheRoot = cacheRoot ?? new RuntimeCache().Root;
    }

    public void Prepare(string language, string source,
                        IReadOnlyList<(CufetType Type, string Name)> parameters,
                        CufetType result, int line)
        => Entry(language, source, parameters, result, line);

    public object? Run(string language, string source,
                      IReadOnlyList<(CufetType Type, string Name)> parameters,
                      CufetType result, IReadOnlyList<object> arguments, int line)
    {
        var entry = Entry(language, source, parameters, result, line);
        return Call(entry, parameters, result, arguments);
    }

    /// <summary>Reads the 64 bits the wrapper handed back as the Cufet value they stand for.</summary>
    /// <remarks>
    /// ★ The wrapper already made every choice that could be argued about — it is the same
    /// wrapper the compiled backend calls, after the same guard. What is left is which 64 bits.
    ///
    /// ⚠⚠ Called INSIDE the marshalling scope, before the arguments are released, and that is
    /// load-bearing rather than tidy. An axiom may hand back a pointer it was GIVEN —
    /// `[the subject]` is the smallest example, `[strchr(the s, 'x')]` the realistic one — so
    /// reading the text after freeing the argument buffers is a use-after-free. It read as an
    /// empty string here and would read as anything at all elsewhere.
    /// </remarks>
    /// <summary>Calls whichever entry this shim exports and reads what it hands back.</summary>
    /// <remarks>
    /// ⚠⚠ Both branches run INSIDE the marshalling scope, for the use-after-free reason below.
    /// </remarks>
    private static object? Read(LoadedShim entry, IntPtr arguments, int count, CufetType result) =>
        entry.Real is { } real
            ? ReadReal(real(arguments, count))
            : ReadResult(entry.Whole!(arguments, count), result);

    /// <summary>The converted decimal the shared C handed back, as a `voidable number`.</summary>
    /// <remarks>
    /// ★ `Ok` is 0 for NaN, ±infinity and any magnitude no decimal can hold; all three are void.
    /// That is `math`'s existing answer for a computation with no representable result, not a new
    /// rule — `square-root of (-4)` and `log of (0)` are both void today, and the recorded test
    /// there is `!IsFinite` rather than `IsNaN` precisely because `log(0)` is an infinity.
    ///
    /// ⚠ The coefficient arrives as 128 bits and a decimal holds 96, but the shared C already
    /// refused anything wider by clearing `Ok` — so `Hi` never exceeds 32 significant bits here.
    /// </remarks>
    private static object? ReadReal(ForeignReal real)
    {
        if (real.Ok == 0) return null;
        return new decimal(
            lo:       unchecked((int)(uint)real.Lo),
            mid:      unchecked((int)(uint)(real.Lo >> 32)),
            hi:       unchecked((int)(uint)real.Hi),
            isNegative: real.Sign != 0,
            scale:    (byte)real.Scale);
    }

    private static object? ReadResult(ForeignWhole whole, CufetType result) => result switch
    {
        // ★ The C twin is cufet_dec_from_foreign in the emitted runtime, and the two branches have
        // to match it exactly. A decimal holds every 64-bit integer, signed or unsigned, so neither
        // branch rounds — an `unsigned long long` of 2^64-1 arrives as 18446744073709551615 rather
        // than as -1, which is the whole reason the flag travels with the bits.
        NumberType => whole.IsUnsigned != 0 ? (decimal)whole.Bits : (decimal)unchecked((long)whole.Bits),
        FactType   => whole.Bits != 0,
        // A text arrives as its POINTER and is COPIED here. The bytes belong to C: a static buffer
        // the next call overwrites, or something its owner will free. The compiled backend copies
        // into the arena for the same reason.
        //
        // ⚠ NULL comes back as null, and the INTERPRETER names it void. What "absent" is called
        // is the language's business; the runner's job stops at the bytes.
        _          => whole.Bits == 0 ? null : Marshal.PtrToStringUTF8((IntPtr)whole.Bits) ?? "",
    };

    /// <summary>Marshals the arguments, calls the shim, and releases what was allocated for it.</summary>
    /// <remarks>
    /// ⚠ Every allocation is freed in a `finally`, including when the foreign call throws. The C
    /// side never keeps any of it: a `text` argument is valid for the length of the call and no
    /// longer, which is the same promise the compiled backend makes by passing a pointer into the
    /// arena that outlives the statement.
    /// </remarks>
    private static object? Call(LoadedShim entry,
                                IReadOnlyList<(CufetType Type, string Name)> parameters,
                                CufetType result, IReadOnlyList<object> arguments)
    {
        if (parameters.Count == 0) return Read(entry, IntPtr.Zero, 0, result);

        var slots = new ShimArgument[parameters.Count];
        var texts = new List<IntPtr>();
        try
        {
            for (int i = 0; i < parameters.Count; i++)
            {
                // ★ The values arrive already converted — the interpreter did the range check and
                // owns the sentence it raises, because that refusal is Cufet's rather than the
                // marshaller's. What is left here is which half of the union to fill.
                if (parameters[i].Type is TextType)
                {
                    // UTF-8, which is what Cufet text already is on the compiled side.
                    var utf8 = Marshal.StringToCoTaskMemUTF8((string)arguments[i]);
                    texts.Add(utf8);
                    slots[i].Text = utf8;
                }
                else
                {
                    slots[i].Whole = (long)arguments[i];
                }
            }


            var block = Marshal.AllocCoTaskMem(slots.Length * Marshal.SizeOf<ShimArgument>());
            try
            {
                for (int i = 0; i < slots.Length; i++)
                    Marshal.StructureToPtr(slots[i], block + i * Marshal.SizeOf<ShimArgument>(), false);
                return Read(entry, block, slots.Length, result);
            }
            finally { Marshal.FreeCoTaskMem(block); }
        }
        finally { foreach (var text in texts) Marshal.ZeroFreeCoTaskMemUTF8(text); }
    }

    /// <summary>This axiom's loaded entry point, building and loading it the first time.</summary>
    private LoadedShim Entry(string language, string source,
                             IReadOnlyList<(CufetType Type, string Name)> parameters,
                             CufetType result, int line) =>
        _loaded.GetOrAdd(ForeignC.Identity(language, source, parameters, result),
                         _ => Load(language, source, parameters, result, line));

    private LoadedShim Load(string language, string source,
                            IReadOnlyList<(CufetType Type, string Name)> parameters,
                            CufetType result, int line)
    {
        string libraryPath = Build(language, source, parameters, result, line);
        try
        {
            var handle = NativeLibrary.Load(libraryPath);
            if (ForeignC.IsRealResult(result))
                return new LoadedShim(null,
                    Marshal.GetDelegateForFunctionPointer<RealEntry>(
                        NativeLibrary.GetExport(handle, RealEntryPoint)));
            return new LoadedShim(
                Marshal.GetDelegateForFunctionPointer<WholeEntry>(
                    NativeLibrary.GetExport(handle, WholeEntryPoint)), null);
        }
        catch (Exception e)
        {
            throw new RuntimeException(
                $"The compiled {language} source could not be loaded (line {line}).\n\n{e.Message}");
        }
    }

    /// <summary>The path to a shared library wrapping this axiom, building it if it is not cached.</summary>
    private string Build(string language, string source,
                         IReadOnlyList<(CufetType Type, string Name)> parameters,
                         CufetType result, int line)
    {
        string shim = ShimSource(language, source, parameters, result);
        string libraryName = "shim" + LibrarySuffix;

        // ⚠ A cache that cannot be used must not fail the run — the same rule the runtime object
        // cache follows. An unwritable home or a locked-down image falls back to a temporary
        // build, which costs a gcc invocation and nothing else.
        string directory = _cacheRoot != null
            ? Path.Combine(_cacheRoot, "axioms", Key(shim))
            : Path.Combine(Path.GetTempPath(), "cufet-axiom-" + Guid.NewGuid().ToString("N"));
        string libraryPath = Path.Combine(directory, libraryName);

        try
        {
            if (File.Exists(libraryPath)) return libraryPath;
            Directory.CreateDirectory(directory);

            // Built in a private staging directory and MOVED into place, so two runs racing cannot
            // leave a half-written library for a third to load — and so a build that FAILS leaves
            // nothing behind for the next run to trip over.
            string staging = Path.Combine(directory, "build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                string cPath = Path.Combine(staging, "shim.c");
                string staged = Path.Combine(staging, libraryName);
                File.WriteAllText(cPath, shim);
                _gcc.Compile(cPath, staged, SharedLibraryFlags);

                // Losing the race is not an error: the winner built the same library from the same
                // key, so its copy is equally valid and this one is simply dropped.
                try { File.Move(staged, libraryPath, overwrite: false); } catch (IOException) { }
                return libraryPath;
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
            }
        }
        catch (CompilerException e)
        {
            // gcc refusing the axiom is the program's problem, and its message is the useful part:
            // it is the C compiler saying exactly what is wrong with the C it was handed.
            throw new RuntimeException(
                $"The {language} source on line {line} could not be compiled.\n\n{e.Message}");
        }
    }

    /// <summary>The whole shim: shared headers, the shared guard, and one wrapped axiom.</summary>
    private static string ShimSource(string language, string source,
                                     IReadOnlyList<(CufetType Type, string Name)> parameters,
                                     CufetType result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("/* Generated by cufet — one foreign axiom, compiled so the interpreter can call it. */");
        sb.AppendLine(ForeignC.Headers);
        sb.AppendLine(ForeignC.GuardMacro);
        sb.AppendLine(ForeignC.WholeResultType);
        sb.AppendLine(ForeignC.RealResultType);
        sb.AppendLine(ForeignC.RealConversion);
        sb.AppendLine();
        sb.AppendLine("#if defined(_WIN32)");
        sb.AppendLine("#define CUFET_SHIM_EXPORT __declspec(dllexport)");
        sb.AppendLine("#else");
        sb.AppendLine("#define CUFET_SHIM_EXPORT __attribute__((visibility(\"default\")))");
        sb.AppendLine("#endif");
        sb.AppendLine();
        sb.AppendLine(ForeignC.ShimArgumentType);
        sb.AppendLine();

        // ★★ The SAME wrapper the compiled backend emits, byte for byte, from the same builder.
        // Everything that could differ — the splice, the guard, the C types, the call — is decided
        // once in ForeignC and compiled twice, rather than described twice and compiled once each.
        const string wrapped = "cufet_axiom_shimmed";
        sb.Append(ForeignC.Wrapper(wrapped, "static ", language, source, parameters, result));
        sb.AppendLine();

        // The exported entry is a thin shell around it: unpack the slots, call, hand the answer
        // back in ONE shape whatever the result type is. A text comes back as its POINTER, which
        // the managed side copies before returning.
        //
        // ★ A whole number is already the pair; a fact and a pointer are values with no signedness
        // question, so they travel in `bits` with the flag clear. One returned struct means one
        // managed delegate, which is what keeps this side to a single P/Invoke signature.
        bool real = ForeignC.IsRealResult(result);
        string entryPoint = real ? RealEntryPoint : WholeEntryPoint;
        string entryType  = real ? ForeignC.RealResultCType : ForeignC.WholeResultCType;
        sb.AppendLine($"CUFET_SHIM_EXPORT {entryType} {entryPoint}"
                    + "(const CufetShimArg* cufet_args, int cufet_count) {");
        sb.AppendLine("    (void)cufet_args; (void)cufet_count;");
        sb.Append(ForeignC.ShimUnpack(parameters));
        var handed = string.Join(", ", Enumerable.Range(0, parameters.Count).Select(ForeignC.ParameterName));
        string call = $"{wrapped}({handed})";
        string packed = result switch
        {
            NumberType => call,                                                    // already the pair
            VoidableType { Inner: NumberType } => call,                            // already converted
            VoidableType => $"({ForeignC.WholeResultCType}){{ (unsigned long long)(intptr_t){call}, 0 }}",
            _ => $"({ForeignC.WholeResultCType}){{ (unsigned long long){call}, 0 }}",
        };
        sb.AppendLine($"    return {packed};");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // Everything that could change the library: the shim source (which carries the axiom and the
    // shared wrapper), the compiler's identity, and the flags. Upgrading gcc changes the key
    // without anyone remembering to invalidate anything.
    private string Key(string shim)
    {
        var material = Encoding.UTF8.GetBytes(
            $"{shim}\0{_gcc.Identification}\0{string.Join(" ", SharedLibraryFlags)}");
        return Convert.ToHexString(SHA256.HashData(material))[..24].ToLowerInvariant();
    }

    private static string LibrarySuffix =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".dll"
      : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)     ? ".dylib"
                                                            : ".so";

    private static string[] SharedLibraryFlags =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ["-shared", "-O2"]
            : ["-shared", "-O2", "-fPIC"];
}
