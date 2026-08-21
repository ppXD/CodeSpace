using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.Core.Services.Workflows.Llm;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions;

[Trait("Category", "Unit")]
public sealed class SessionConcreteApiShapeTests
{
    [Fact]
    public void Narrow_reader_cutover_preserves_the_existing_public_concrete_constructor_shapes()
    {
        typeof(SessionContextBuilder).IsPublic.ShouldBeTrue();
        typeof(SessionContextBuilder).GetConstructor([typeof(CodeSpaceDbContext), typeof(IPublishManifestStore)]).ShouldNotBeNull();

        typeof(SessionSummarizer).IsPublic.ShouldBeTrue();
        typeof(SessionSummarizer).GetConstructor(
        [
            typeof(CodeSpaceDbContext), typeof(IPublishManifestStore), typeof(ILLMClientRegistry),
            typeof(IModelPoolSelector), typeof(ILogger<SessionSummarizer>),
        ]).ShouldNotBeNull();
    }
}
