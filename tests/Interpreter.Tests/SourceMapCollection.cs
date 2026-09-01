using Xunit;

namespace Cufet.Interpreter.Tests;

/// <summary>
/// Test classes that write <see cref="SourceMap.Current"/>, kept out of each other's way.
/// </summary>
/// <remarks>
/// ⚠⚠ <c>SourceMap.Current</c> is a mutable static — the CLI checks one program per process, so the
/// product is right to have one. Tests are not one program per process: xUnit runs classes in
/// PARALLEL, so a second class writing it while the first is composing a message makes that message
/// report the raw virtual line (<c>line 100003</c>) instead of the book's own line 3.
///
/// ★ It was not a race until there were two writers. <c>BookLoadingTests</c> had it to itself until
/// <c>ModuleCarriedTypeTests</c> landed on 2026-08-31, and the flake begins there. Any new class
/// that assigns <c>SourceMap.Current</c> belongs in this collection — that is the whole rule.
/// </remarks>
[CollectionDefinition("SourceMap")]
public class SourceMapCollection { }
