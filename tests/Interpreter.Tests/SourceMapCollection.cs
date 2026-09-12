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
/// that assigns <c>SourceMap.Current</c> belongs in this collection.
///
/// ⚠⚠ THAT IS NOT THE WHOLE RULE, and this remark said it was until 2026-09-12. Serialising the
/// WRITERS leaves every other class in the assembly a concurrent READER: a checker error goes
/// through <c>TypeChecker.TypeError</c> to <c>SourceMap.Rewrite</c>, so any class that provokes one
/// is reading this static while these two write it. <c>Rewrite</c> tested <c>Current</c> for null
/// and then read it AGAIN inside its replacement lambda — a check-then-use — and a
/// <c>ChaseTests</c> row that assigns nothing at all died with a NullReferenceException in a
/// full-solution run while passing every time it ran alone.
///
/// ★ The reader side is fixed where it belongs, in <c>Rewrite</c>, which now captures the map once.
/// Expanding this collection could not have fixed it: "every class that can produce a checker
/// error" is every class, which is the same as no parallelism at all.
///
/// ⚠ MEASURED, and worth knowing for the next one: only a message containing SIX OR MORE
/// consecutive digits enters that lambda, so the crash looked random and was not. The row that
/// failed was the one asserting <c>1114112</c> is not a code point.
/// </remarks>
[CollectionDefinition("SourceMap")]
public class SourceMapCollection { }
