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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate long WholeEntry(IntPtr arguments, int count);

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
    private readonly ConcurrentDictionary<string, WholeEntry> _loaded = new(StringComparer.Ordinal);

    public GccForeignRunner(GccInvoker? gcc = null, string? cacheRoot = null)
    {
        _gcc = gcc ?? new GccInvoker();
        _cacheRoot = cacheRoot ?? new RuntimeCache().Root;
    }

    public void Prepare(string language, string source,
                        IReadOnlyList<(CufetType Type, string Name)> parameters, int line)
        => Entry(language, source, parameters, line);

    public decimal RunForWholeNumber(string language, string source,
                                     IReadOnlyList<(CufetType Type, string Name)> parameters,
                                     IReadOnlyList<object> arguments, int line)
    {
        var entry = Entry(language, source, parameters, line);
        // ★ No conversion decision here. The C side has already taken the value through the same
        // `(long long)` the compiled backend uses, after the same guard, so both backends convert
        // the identical integer — and a decimal holds every 64-bit integer exactly, so neither
        // rounds. This is the whole reason the wrapper is shared rather than written twice.
        return Call(entry, parameters, arguments, line);
    }

    /// <summary>Marshals the arguments, calls the shim, and releases what was allocated for it.</summary>
    /// <remarks>
    /// ⚠ Every allocation is freed in a `finally`, including when the foreign call throws. The C
    /// side never keeps any of it: a `text` argument is valid for the length of the call and no
    /// longer, which is the same promise the compiled backend makes by passing a pointer into the
    /// arena that outlives the statement.
    /// </remarks>
    private static decimal Call(WholeEntry entry,
                                IReadOnlyList<(CufetType Type, string Name)> parameters,
                                IReadOnlyList<object> arguments, int line)
    {
        if (parameters.Count == 0) return entry(IntPtr.Zero, 0);

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
                return entry(block, slots.Length);
            }
            finally { Marshal.FreeCoTaskMem(block); }
        }
        finally { foreach (var text in texts) Marshal.ZeroFreeCoTaskMemUTF8(text); }
    }

    /// <summary>This axiom's loaded entry point, building and loading it the first time.</summary>
    private WholeEntry Entry(string language, string source,
                             IReadOnlyList<(CufetType Type, string Name)> parameters, int line) =>
        _loaded.GetOrAdd(ForeignC.Identity(language, source, parameters),
                         _ => Load(language, source, parameters, line));

    private WholeEntry Load(string language, string source,
                            IReadOnlyList<(CufetType Type, string Name)> parameters, int line)
    {
        string libraryPath = Build(language, source, parameters, line);
        try
        {
            var handle = NativeLibrary.Load(libraryPath);
            var address = NativeLibrary.GetExport(handle, WholeEntryPoint);
            return Marshal.GetDelegateForFunctionPointer<WholeEntry>(address);
        }
        catch (Exception e)
        {
            throw new RuntimeException(
                $"The compiled {language} source could not be loaded (line {line}).\n\n{e.Message}");
        }
    }

    /// <summary>The path to a shared library wrapping this axiom, building it if it is not cached.</summary>
    private string Build(string language, string source,
                         IReadOnlyList<(CufetType Type, string Name)> parameters, int line)
    {
        string shim = ShimSource(language, source, parameters);
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
                                     IReadOnlyList<(CufetType Type, string Name)> parameters)
    {
        var sb = new StringBuilder();
        sb.AppendLine("/* Generated by cufet — one foreign axiom, compiled so the interpreter can call it. */");
        sb.AppendLine(ForeignC.Headers);
        sb.AppendLine(ForeignC.GuardMacro);
        sb.AppendLine();
        sb.AppendLine("#if defined(_WIN32)");
        sb.AppendLine("#define CUFET_SHIM_EXPORT __declspec(dllexport)");
        sb.AppendLine("#else");
        sb.AppendLine("#define CUFET_SHIM_EXPORT __attribute__((visibility(\"default\")))");
        sb.AppendLine("#endif");
        sb.AppendLine();
        sb.AppendLine(ForeignC.ShimArgumentType);
        sb.AppendLine();

        // The foreign text with `the path` already replaced by the C name it becomes — done ONCE
        // and used for both the guard and the body, exactly as the compiled backend does it.
        string spliced = ForeignC.Splice(source, parameters);

        sb.AppendLine(ForeignC.Banner(language, source));
        sb.AppendLine($"CUFET_SHIM_EXPORT long long {WholeEntryPoint}"
                    + "(const CufetShimArg* cufet_args, int cufet_count) {");
        // Unused when the axiom takes nothing, and the interpreter passes NULL there.
        sb.AppendLine("    (void)cufet_args; (void)cufet_count;");
        sb.Append(ForeignC.ShimUnpack(parameters));
        sb.AppendLine(ForeignC.GuardStatement(spliced));
        sb.AppendLine($"    return {ForeignC.WholeExpression(spliced)};");
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
