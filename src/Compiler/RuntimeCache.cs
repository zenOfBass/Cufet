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
/// The key is a hash of the runtime source, the gcc identification string, and the flags. Anything
/// that could change the object changes the key, so upgrading gcc or editing the runtime invalidates
/// it without anyone remembering to — the failure mode of a stale cache is a mystery bug in
/// generated code, which is the worst kind to debug and the easiest to prevent.
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

        try
        {
            if (File.Exists(objPath)) return objPath;

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

                // Another build may have finished first — its object is equally valid, so losing
                // the race is not an error.
                if (!File.Exists(objPath)) File.Move(staged, objPath, overwrite: false);
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
