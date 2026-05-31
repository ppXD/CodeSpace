using CodeSpace.Core.Services.Workflows.Engine;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The iteration-key helpers are the backbone of nested loops + durable resume: a body node runs under
/// a key like "outer#0/inner#1", and on resume the engine PARSES that key to re-enter the exact pass
/// (RehydrateLoopState reads the index up to the first '/'; the nesting-depth guard counts segments).
/// These pin the pure math directly — a regression here silently corrupts which iteration a resumed
/// run re-enters, which integration tests catch only obliquely. Mirrors the real key shapes the engine
/// builds: top-level "&lt;loop&gt;#&lt;i&gt;" and nested "&lt;outer&gt;#&lt;i&gt;/&lt;inner&gt;#&lt;j&gt;".
/// </summary>
[Trait("Category", "Unit")]
public class WorkflowEngineIterationKeyTests
{
    [Theory]
    [InlineData("", "loop#0", "loop#0")]                          // top level: no prefix → just the segment
    [InlineData("loop#2", "inner#1", "loop#2/inner#1")]           // one level of nesting
    [InlineData("a#0/b#1", "c#2", "a#0/b#1/c#2")]                 // two levels deep
    public void CombineIterationKey_appends_with_a_slash_only_when_nested(string prefix, string segment, string expected)
    {
        WorkflowEngine.CombineIterationKey(prefix, segment).ShouldBe(expected);
    }

    [Theory]
    [InlineData("loop#0", "loop#", 0)]                            // direct body of this loop, pass 0
    [InlineData("loop#7", "loop#", 7)]                            // multi-digit index
    [InlineData("outer#3/inner#1", "outer#", 3)]                 // a nested descendant attributes to the OUTER pass (read up to '/')
    [InlineData("outer#3/inner#1", "outer#3/inner#", 1)]         // …and to the INNER pass under the inner loop's own prefix
    [InlineData("other#0", "loop#", -1)]                          // not in this loop's subtree
    [InlineData("loop#x", "loop#", -1)]                           // malformed index → -1, never throws
    [InlineData("", "loop#", -1)]                                 // top-level key isn't in any loop subtree
    public void LoopIterationIndex_reads_the_pass_for_a_given_loop_prefix(string key, string bodyKeyPrefix, int expected)
    {
        WorkflowEngine.LoopIterationIndex(key, bodyKeyPrefix).ShouldBe(expected);
    }

    [Theory]
    [InlineData("", 0)]                                           // top level
    [InlineData("loop#0", 1)]                                     // inside one loop
    [InlineData("outer#0/inner#1", 2)]                           // inside two
    [InlineData("a#0/b#1/c#2", 3)]                               // inside three (the nesting-depth guard counts these)
    public void LoopNestingDepth_counts_the_enclosing_loops(string key, int expected)
    {
        WorkflowEngine.LoopNestingDepth(key).ShouldBe(expected);
    }

    [Fact]
    public void Combine_then_index_round_trips_a_nested_key_back_to_its_outer_pass()
    {
        // The exact compose→parse cycle the engine performs: build an inner pass key under an outer
        // pass, then resume must attribute it back to outer pass 4 (not the inner index).
        var outerPass = WorkflowEngine.CombineIterationKey("", "outer#4");
        var innerPass = WorkflowEngine.CombineIterationKey(outerPass, "inner#2");

        innerPass.ShouldBe("outer#4/inner#2");
        WorkflowEngine.LoopIterationIndex(innerPass, "outer#").ShouldBe(4, "a resumed outer loop re-enters pass 4, not the inner index");
        WorkflowEngine.LoopNestingDepth(innerPass).ShouldBe(2);
    }
}
