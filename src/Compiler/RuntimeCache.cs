using System.Security.Cryptography;
using System.Text;

namespace Cufet.Compiler;

/// <summary>
/// Compiles the fixed runtime once and keeps the object file, so every later build links it instead
/// of recompiling it.
/// </summary>
/// <remarks>
/// <para>
/// ★ Measured before building this: gcc spent ~415 ms on the runtime and ~0 ms on `fibonacci`'s
/// actual 578 bytes of program — the empty program and the real one compiled in the same time,
/// because the runtime IS the compile. End to end a build was 648–804 ms, so caching the object
/// removes over half the wall clock and nearly all of gcc's share.
/// </para>
/// <para>
/// ★★ It is a CACHE, never a requirement. A build that cannot read or write the cache directory —
/// a locked-down CI image, a read-only home, a sandbox — falls back to compiling the runtime source
/// alongside the program, which is exactly what every build did before this existed. The property
/// worth protecting is that gcc remains the only thing a person needs installed; a cache that could
/// fail a build would trade that away for a few hundred milliseconds.
/// </para>
/// <para>
/// The key is a hash of the runtime source, the header, the gcc identification string, and the
/// flags. Editing the runtime or moving to a gcc that reports a different version therefore
/// invalidates the entry without anyone remembering to.
/// </para>
/// <para>
/// ⚠⚠ A key proves what an object was BUILT FROM and says nothing about what it turned out to
/// HOLD. MEASURED, 2026-09-02: a suite run under WSL on Arch produced 32 failures, every one an
/// `undefined reference to cufet_dec_lit` and friends — which reads as a code-generator regression
/// and is not one. `rm -rf ~/.cache/cufet` cured all 32; the rerun was 861/861. **The mechanism
/// was never identified**, and two guards below close the class without needing it.
/// </para>
/// <para>
/// ★★ What the failure WAS is known, measured 2026-09-05 by building the shapes and linking them:
/// a symbol-poor object. An object compiled from empty source is a well-formed 936-byte ELF with
/// zero defined symbols that links without a word, and every runtime call then comes back
/// undefined — that is the reported wording exactly. A merely truncated object cannot be it: `ld`
/// says `file too short` for those. So the entry was a real object built from nothing much.
/// </para>
/// <para>
/// ⚠ An earlier version of this note blamed the rest of the toolchain — glibc, binutils, the
/// system headers — for being outside the key. That has been checked and does not hold: every
/// conditional in the runtime is `_WIN32`, `__unix__` or `__APPLE__`, so nothing a library upgrade
/// touches can change which functions the object defines.
/// </para>
/// <para>
/// ★ So the object is verified rather than assumed, twice, and neither check needs to know the
/// cause. <see cref="DefinesTheRuntime"/> asks the LINKER, when the object is built, whether it
/// really defines the runtime. <see cref="IsIntact"/> asks, every time one is reused, whether it
/// is still the bytes that passed. Between them, an object that does not define what a program is
/// about to call cannot be handed out — however it got that way.
/// </para>
/// <para>
/// ⚠ Neither catches the quiet failure: an object that still links but carries an older
/// implementation of a function whose signature never changed, which is what every bug fix to the
/// C runtime looks like. The key does close that one, since a changed runtime source is a changed
/// key; the 861 compiled tests linking this object every run are the backstop.
/// </para>
/// <para>
/// ★ Deleting the cache is still always safe — this is a cache, so an empty one only costs the
/// runtime compile back. `CUFET_CACHE_DIR` overrides the location; otherwise see DefaultRoot below.
/// </para>
/// </remarks>
public sealed class RuntimeCache
{
    private readonly string? _root;

    public RuntimeCache(string? root = null)
    {
        _root = root ?? DefaultRoot();
    }

    /// <summary>Where cached objects live, or null when no usable location was found.</summary>
    public string? Root => _root;

    // ★ A user-level cache directory, never the project folder. Build output belongs to the machine,
    // not to the source tree — dropping a .o next to someone's .cufe files would show up in their
    // version control and in their editor, for a file they did not ask for and cannot read.
    private static string? DefaultRoot()
    {
        try
        {
            string? baseDir = Environment.GetEnvironmentVariable("CUFET_CACHE_DIR");
            if (string.IsNullOrWhiteSpace(baseDir))
            {
                // XDG on Unix, LocalApplicationData on Windows. SpecialFolder.LocalApplicationData
                // resolves to ~/.local/share on Unix, so XDG_CACHE_HOME is preferred where set.
                string? xdg = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
                baseDir = !string.IsNullOrWhiteSpace(xdg) && !OperatingSystem.IsWindows()
                    ? Path.Combine(xdg, "cufet")
                    : OperatingSystem.IsWindows()
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cufet")
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "cufet");
            }
            return Path.Combine(baseDir!, "runtime");
        }
        catch
        {
            return null;   // no home, no environment — fall back to compiling in place
        }
    }

    /// <summary>
    /// The path to an object file for this runtime source, compiling it if it is not already
    /// cached. Returns null when the cache is unusable, which means "compile it inline instead".
    /// </summary>
    public string? ObjectFor(string runtimeSource, string header, GccInvoker gcc, IReadOnlyList<string> flags)
    {
        if (_root == null) return null;

        string key = Key(runtimeSource, header, gcc.Identification, flags);
        string dir = Path.Combine(_root, key);
        string objPath = Path.Combine(dir, "cufet-runtime.o");

        string stampPath = objPath + ".sha256";

        try
        {
            if (IsIntact(objPath, stampPath)) return objPath;

            // Build into a private temporary directory and MOVE the finished object into place, so
            // two builds racing cannot leave a half-written .o that later builds would link.
            Directory.CreateDirectory(dir);
            string staging = Path.Combine(dir, "build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                string cPath = Path.Combine(staging, RuntimeSplit.SourceFileName);
                File.WriteAllText(Path.Combine(staging, RuntimeSplit.HeaderFileName), header);
                File.WriteAllText(cPath, runtimeSource);
                string staged = Path.Combine(staging, "cufet-runtime.o");
                gcc.CompileObject(cPath, staged, flags);

                // ⚠ gcc exiting 0 is not the same as gcc having produced a runtime. Ask the linker.
                if (!DefinesTheRuntime(staged, staging, runtimeSource, gcc, flags)) return null;

                // ★ overwrite: true, where this used to step aside for whoever finished first. Two
                // builds of one key produce byte-identical objects — MEASURED: gcc is deterministic
                // for this source, which uses no __DATE__, __TIME__ or __FILE__ — so there is no
                // race to lose. What overwriting buys is that a verified-good object always
                // replaces one that failed the check above, which is how a poisoned entry heals
                // instead of being stepped around forever.
                File.Move(staged, objPath, overwrite: true);
                File.WriteAllText(stampPath, HashOfFile(objPath));
                return File.Exists(objPath) ? objPath : null;
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); } catch { /* best effort */ }
            }
        }
        catch (CompilerException)
        {
            throw;      // gcc rejecting the runtime is a real failure, not a cache miss
        }
        catch
        {
            return null;   // unwritable, full, racing — none of which should fail a build
        }
    }

    /// <summary>
    /// Whether a cached object is the one that was built here — present, and still byte-for-byte
    /// what it was when it passed its checks.
    /// </summary>
    /// <remarks>
    /// ★ The key proves what an object was BUILT FROM. This proves what it still CONTAINS, which
    /// is the half a content-addressed path cannot reach: nothing about hashing the source notices
    /// a file that was damaged, replaced or half-written afterwards. An entry that fails is not
    /// deleted — it is simply not trusted, and the build below overwrites it with a good one.
    ///
    /// ⚠ An entry written before this existed has no stamp and so never matches, which costs one
    /// runtime compile each and then heals itself. That is the right trade for a cache.
    /// </remarks>
    private static bool IsIntact(string objPath, string stampPath)
    {
        if (!File.Exists(objPath) || !File.Exists(stampPath)) return false;
        return string.Equals(File.ReadAllText(stampPath).Trim(), HashOfFile(objPath),
                             StringComparison.OrdinalIgnoreCase);
    }

    private static string HashOfFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// Asks the linker whether a freshly built object really defines the runtime, by linking a
    /// generated file that takes the address of every function the runtime source defines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠⚠ This is the check the cache key cannot make. MEASURED 2026-09-05: an object compiled
    /// from empty source is a well-formed 936-byte ELF with zero defined symbols, and `ld` links
    /// it without a word — the program then fails with `undefined reference to cufet_dec_lit` and
    /// friends, which is exactly the failure that once cost most of an hour reading it as a
    /// code-generator regression. Taking an address forces the linker to resolve the symbol, so
    /// an object that defines nothing fails here, at the moment it is built, instead of in every
    /// build that reuses it afterwards.
    /// </para>
    /// <para>
    /// ★ It costs one gcc invocation per cache MISS and none on a hit — sixteen across a whole
    /// suite run, against the ~415 ms the cache saves each time it hits.
    /// </para>
    /// <para>
    /// ★★ It uses gcc and nothing else, which is the point. `nm` would be a second tool a person
    /// has to have installed, and the property this cache exists to protect is that gcc remains
    /// the only one.
    /// </para>
    /// <para>
    /// ⚠ A failure returns false rather than throwing, and the caller then compiles the runtime
    /// alongside the program as if there were no cache at all. A cache that cannot vouch for
    /// itself must step aside, never fail a build — the same rule the rest of this class follows.
    /// </para>
    /// </remarks>
    private static bool DefinesTheRuntime(string objectPath, string staging, string runtimeSource,
                                          GccInvoker gcc, IReadOnlyList<string> flags)
    {
        var functions = RuntimeSplit.DefinedFunctions(runtimeSource);
        if (functions.Count == 0) return false;   // a runtime that defines nothing is not a runtime

        var probe = new StringBuilder();
        probe.AppendLine("/* Generated by cufet — asks the linker whether the cached runtime object is real. */");
        probe.AppendLine($"#include \"{RuntimeSplit.HeaderFileName}\"");
        // ⚠ External linkage, not static. A static table nothing reads is one the optimiser is
        // entitled to delete, taking every relocation — and the whole point of this file — with it.
        probe.AppendLine("typedef void (*cufet_probe_fn)(void);");
        probe.AppendLine("cufet_probe_fn cufet_probe_table[] = {");
        foreach (var name in functions)
            probe.AppendLine($"    (cufet_probe_fn)&{name},");
        probe.AppendLine("};");
        probe.AppendLine("int main(void) { return cufet_probe_table[0] != 0; }");

        string probePath = Path.Combine(staging, "cufet-cache-probe.c");
        File.WriteAllText(probePath, probe.ToString());
        string probeBinary = Path.Combine(staging, "cufet-cache-probe" + (OperatingSystem.IsWindows() ? ".exe" : ""));

        try
        {
            gcc.Compile([probePath, objectPath], probeBinary, flags);
            return true;
        }
        catch (CompilerException)
        {
            return false;
        }
    }

    // Everything that could change the object's contents, so nothing else has to be remembered.
    private static string Key(string runtimeSource, string header, string gccId, IReadOnlyList<string> flags)
    {
        var material = new StringBuilder();
        material.Append(runtimeSource).Append('\0')
                .Append(header).Append('\0')
                .Append(gccId).Append('\0')
                .Append(string.Join(" ", flags));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }
}
