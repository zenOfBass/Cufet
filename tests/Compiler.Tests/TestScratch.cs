namespace Cufet.Compiler.Tests;

/// <summary>
/// One directory for everything the compiled-test suite writes.
/// </summary>
/// <remarks>
/// ★★ This exists for a MEASURED reason, not for tidiness. Every compiled test emits C, links a
/// fresh executable and runs it exactly once. Measured on Windows with Defender real-time
/// protection on: a binary whose contents are novel takes <b>482 ms</b> to start, the same binary
/// again takes 41 ms, and a copy of already-scanned content takes 119 ms. Nothing here is ever run
/// twice, so every test pays the novel-content price and no test ever collects the discount —
/// roughly 480 ms of a ~1.6 s test, across 900-odd tests.
///
/// <para>That is fixable only by excluding the directory from real-time scanning, and the tests
/// used to scatter their artifacts across bare <c>%TEMP%</c> from 30-odd call sites — so the only
/// exclusion that would have helped was the whole user temp directory, which is far too broad to
/// ask anyone for. Everything lands here instead, so the exclusion can name one folder.</para>
///
/// <para>⚠ Names inside are still unique per test. This changes WHERE the artifacts live and
/// nothing else: tests still create their own uniquely-named file or directory underneath, so
/// parallel tests cannot collide.</para>
///
/// <para>⚠ Nothing empties this directory. Leftovers from a killed run accumulate, which is the
/// same as before except that they are now in one findable place rather than mixed into
/// <c>%TEMP%</c> — deleting it while no suite is running is always safe.</para>
/// </remarks>
internal static class TestScratch
{
    /// <summary>The scratch root, created on first use.</summary>
    internal static string Root { get; } = Make();

    private static string Make()
    {
        var root = Path.Combine(Path.GetTempPath(), "cufet-tests");
        Directory.CreateDirectory(root);
        return root;
    }
}
