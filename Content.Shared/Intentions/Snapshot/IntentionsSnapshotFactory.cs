using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Content.Shared.Intentions.Snapshot;

/// <summary>
/// Pure factory that normalizes raw round facts into one immutable Intentions snapshot.
/// </summary>
public static class IntentionsSnapshotFactory
{
    /// <summary>
    /// Builds a deterministic snapshot from already collected raw inputs.
    /// </summary>
    public static SnapshotBuildResult Build(
        IntentionsSnapshotRequest request,
        string? gameMode,
        TimeSpan stationTime,
        string? stationName,
        IEnumerable<string>? eventTags,
        IEnumerable<CandidateFacts> candidates,
        TimeSpan? builtAt = null,
        string? snapshotId = null,
        AntagSummary? antagSummary = null)
    {
        var candidateList = candidates.ToList();
        var issues = new List<SnapshotBuildIssue>();

        // Duplicate mind ids would make slot assignment and UI ownership ambiguous for the entire wave.
        foreach (var duplicate in candidateList.GroupBy(candidate => candidate.MindId).Where(group => group.Count() > 1))
        {
            issues.Add(new SnapshotBuildIssue(
                "duplicate-candidate-mind",
                $"Snapshot candidate list contains duplicate mind id {duplicate.Key}.",
                MindId: duplicate.Key,
                Path: "candidates"));
        }

        if (issues.Count > 0)
            return SnapshotBuildResult.Failure(issues);

        // Candidate order is fixed up front so every later phase observes the same deterministic pool.
        var normalizedCandidates = candidateList
            .OrderBy(candidate => candidate.UserId.ToString(), StringComparer.Ordinal)
            .ThenBy(candidate => candidate.MindId)
            .ToImmutableArray();

        var roundFacts = new RoundFacts(
            NormalizeIdentifier(gameMode, "unknown"),
            stationTime,
            NormalizeIdentifier(stationName, "station"),
            normalizedCandidates.Length,
            normalizedCandidates.Count(candidate => candidate.Department == "Security"),
            eventTags,
            antagSummary ?? BuildAntagSummary(normalizedCandidates));

        var actualBuiltAt = builtAt ?? stationTime;
        var actualSnapshotId = snapshotId ?? $"{request.Kind.ToString().ToLowerInvariant()}-{request.WaveId}-{actualBuiltAt.Ticks}";
        var snapshot = new IntentionsSnapshot(actualSnapshotId, request, actualBuiltAt, roundFacts, normalizedCandidates);

        return SnapshotBuildResult.Success(snapshot);
    }

    /// <summary>
    /// Aggregates antagonist counts from the normalized candidate list when no explicit round-wide summary is provided.
    /// </summary>
    private static AntagSummary BuildAntagSummary(ImmutableArray<CandidateFacts> candidates)
    {
        var byRole = candidates
            .SelectMany(candidate => candidate.AntagRoles)
            .GroupBy(role => role, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var byObjectiveType = candidates
            .SelectMany(candidate => candidate.AntagObjectiveTypes)
            .GroupBy(objectiveType => objectiveType, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var antags = candidates.Where(candidate => candidate.HasAntagRole).ToList();

        return new AntagSummary(
            antags.Count,
            antags.Count(candidate => !candidate.IsGhostRoleAntag),
            antags.Count(candidate => candidate.IsGhostRoleAntag),
            byRole,
            byObjectiveType);
    }

    /// <summary>
    /// Returns a fallback identifier when an optional raw value is empty.
    /// </summary>
    private static string NormalizeIdentifier(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
