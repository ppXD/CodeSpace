using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Tasks;

namespace CodeSpace.Core.Services.Tasks.SpecPreview;

/// <summary>Checks evidence identity and interprets the independent model assessment. It deliberately does not treat string matching as semantic verification or produce an execution receipt.</summary>
internal static class TaskSpecAcceptanceAssessment
{
    public static TaskSpecAcceptanceProposal? Assess(TaskSpecCompilation compilation, TaskSpecEvidenceContext? context, TaskSpecReview? review)
    {
        var argv = compilation.AcceptanceChecks?.ToArray() ?? [];
        if (argv.Length == 0 || argv.All(string.IsNullOrWhiteSpace)) return null;
        var evidence = new List<TaskSpecEvidenceCitation>();
        var reason = "Source support is unknown; this proposal is not prefilled as a mandatory check.";
        var status = TaskSpecEvidenceStatus.Unknown;
        var source = TaskSpecCheckSource.ProposedUnverified;
        var validCommand = argv.Length <= 256 && !string.IsNullOrWhiteSpace(argv[0]) && argv.All(t => t is not null && !t.Contains('\0'));
        var citations = review?.Citations ?? [];
        var validCitations = context is not null && citations.Count is > 0 and <= 8;
        foreach (var citation in citations.Take(8))
        {
            if (citation is null) { validCitations = false; continue; }
            var original = context?.Sources.SingleOrDefault(s => s.Id == citation.SourceId);
            if (original is null || string.IsNullOrWhiteSpace(citation.Quote) || citation.Quote.Length > 1000 || !original.Content.Contains(citation.Quote, StringComparison.Ordinal)) { validCitations = false; continue; }
            evidence.Add(new TaskSpecEvidenceCitation { SourceId = original.Id, Kind = original.Kind, Path = original.Path, Reference = original.Reference, ContentDigest = original.ContentDigest, Quote = citation.Quote });
        }

        if (!validCommand) reason = "The proposed argv is malformed; it cannot be adopted as an executable check.";
        else if (review is not null && !validCitations) reason = "The semantic review did not provide intact citations to available sources; source support remains unknown.";
        else if (review is not null && validCitations && !string.IsNullOrWhiteSpace(review.Reason))
        {
            reason = review.Reason;
            if (review.Support == "contradicted") status = TaskSpecEvidenceStatus.Contradicted;
            else if (review.Support == "supported")
            {
                if (review.Source == "user-explicit" && evidence.Any(e => e.Kind == "user-goal")) source = TaskSpecCheckSource.UserExplicit;
                else if (review.Source == "repository-evidence" && context!.ReadFailures.Count == 0 && context.Repository.Reference is not null && review.DependenciesSupported && evidence.Any(e => e.Kind == "repository-file")) source = TaskSpecCheckSource.RepositoryEvidence;
                if (source != TaskSpecCheckSource.ProposedUnverified) status = TaskSpecEvidenceStatus.Supported;
                else reason = $"The review's proposed support is incomplete: source or dependency evidence remains unknown. {review.Reason}";
            }
        }

        return new TaskSpecAcceptanceProposal
        {
            Argv = argv.Select(t => t ?? "").ToArray(), Source = source, Status = status, Reason = reason, Evidence = evidence,
            Dependencies = compilation.Dependencies?.Where(d => d is not null && !string.IsNullOrWhiteSpace(d.Requirement) && !string.IsNullOrWhiteSpace(d.ValidationStrategy)).ToArray() ?? [],
            CommandDigest = ContractHashing.Hash(argv, TaskSpecCompilerSchema.Options),
            SourceDigest = ContractHashing.Hash(context?.Sources.Select(s => new { s.Id, s.ContentDigest, s.Reference }).ToArray(), TaskSpecCompilerSchema.Options),
        };
    }
}
