using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

namespace CodeSpace.Core.Services.Workflows.Artifacts;

/// <summary>
/// The CAS ports an offloaded artifact needs when the team routes <c>workflow-artifact/v1</c> through a configured
/// storage profile. They stay two separate interfaces on purpose — a window read carries no digest guarantee where a
/// whole-object read does — and this record only carries them together so the store's constructor names one routed
/// plane instead of two ports.
/// </summary>
public sealed record ArtifactRoutedPlane(IArtifactCasRuntimeCoordinator Transfers, IArtifactCasRangeReader Ranges) : IScopedDependency;
