using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;

namespace Content.Shared.Intentions.Snapshot;

/// <summary>
/// Describes which kind of immutable snapshot should be built for a wave.
/// </summary>
public sealed record IntentionsSnapshotRequest(IntentionsSnapshotKind Kind, int WaveId)
{
    /// <summary>
    /// Creates a start-wave snapshot request.
    /// </summary>
    public static IntentionsSnapshotRequest Start(int waveId = 0)
    {
        return new IntentionsSnapshotRequest(IntentionsSnapshotKind.Start, waveId);
    }

    /// <summary>
    /// Creates a refill-wave snapshot request.
    /// </summary>
    public static IntentionsSnapshotRequest Refill(int waveId)
    {
        return new IntentionsSnapshotRequest(IntentionsSnapshotKind.Refill, waveId);
    }
}

/// <summary>
/// Identifies whether a snapshot belongs to the start or refill distribution flow.
/// </summary>
public enum IntentionsSnapshotKind : byte
{
    Start,
    Refill,
}

/// <summary>
/// Immutable round snapshot consumed by predicate evaluation and wave building.
/// </summary>
public sealed class IntentionsSnapshot
{
    /// <summary>
    /// Creates an immutable snapshot from normalized round and candidate facts.
    /// </summary>
    public IntentionsSnapshot(
        string snapshotId,
        IntentionsSnapshotRequest request,
        TimeSpan builtAt,
        RoundFacts roundFacts,
        ImmutableArray<CandidateFacts> candidates)
    {
        SnapshotId = snapshotId;
        Request = request;
        BuiltAt = builtAt;
        RoundFacts = roundFacts;
        Candidates = candidates.IsDefault ? ImmutableArray<CandidateFacts>.Empty : candidates;
        CandidatesByMind = Candidates.ToImmutableDictionary(candidate => candidate.MindId);
    }

    /// <summary>
    /// Stable identifier used by logs, wave contexts, and deterministic seed generation.
    /// </summary>
    public string SnapshotId { get; }

    /// <summary>
    /// Request that produced this snapshot.
    /// </summary>
    public IntentionsSnapshotRequest Request { get; }

    /// <summary>
    /// Round time when the snapshot was built.
    /// </summary>
    public TimeSpan BuiltAt { get; }

    /// <summary>
    /// Immutable round-level facts shared by all predicates in the wave.
    /// </summary>
    public RoundFacts RoundFacts { get; }

    /// <summary>
    /// Stable candidate list considered by the builder.
    /// </summary>
    public ImmutableArray<CandidateFacts> Candidates { get; }

    /// <summary>
    /// Fast lookup from mind id to candidate facts.
    /// </summary>
    public ImmutableDictionary<EntityUid, CandidateFacts> CandidatesByMind { get; }
}

/// <summary>
/// Immutable round-wide facts used by global predicates and text bindings.
/// </summary>
public sealed class RoundFacts
{
    /// <summary>
    /// Creates a normalized round-facts snapshot.
    /// </summary>
    public RoundFacts(
        string gameMode,
        TimeSpan stationTime,
        string stationName,
        int crewCount,
        int securityCount,
        IEnumerable<string>? eventTags,
        AntagSummary antagSummary)
    {
        GameMode = gameMode;
        StationTime = stationTime;
        StationName = stationName;
        CrewCount = crewCount;
        SecurityCount = securityCount;
        EventTags = NormalizeStrings(eventTags);
        AntagSummary = antagSummary;
    }

    /// <summary>
    /// Active game mode identifier captured for the wave.
    /// </summary>
    public string GameMode { get; }

    /// <summary>
    /// Round time captured for the wave.
    /// </summary>
    public TimeSpan StationTime { get; }

    /// <summary>
    /// Human-readable station name captured for text resolution.
    /// </summary>
    public string StationName { get; }

    /// <summary>
    /// Candidate count available to the wave.
    /// </summary>
    public int CrewCount { get; }

    /// <summary>
    /// Security department count used by round predicates and balancing.
    /// </summary>
    public int SecurityCount { get; }

    /// <summary>
    /// Normalized round event tags reserved for future event-aware predicates.
    /// </summary>
    public ImmutableArray<string> EventTags { get; }

    /// <summary>
    /// Aggregated antagonist facts derived from the candidate set.
    /// </summary>
    public AntagSummary AntagSummary { get; }

    /// <summary>
    /// Normalizes optional string sets into deterministic immutable arrays.
    /// </summary>
    private static ImmutableArray<string> NormalizeStrings(IEnumerable<string>? values)
    {
        return values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray() ?? ImmutableArray<string>.Empty;
    }
}

/// <summary>
/// Aggregated antagonist counts derived from one immutable candidate set.
/// </summary>
public sealed class AntagSummary
{
    /// <summary>
    /// Creates a summary of antagonist counts for the current snapshot.
    /// </summary>
    public AntagSummary(
        int totalCount,
        int gameModeAntagCount,
        int ghostRoleAntagCount,
        ImmutableDictionary<string, int>? byRole = null,
        ImmutableDictionary<string, int>? byObjectiveType = null)
    {
        TotalCount = totalCount;
        GameModeAntagCount = gameModeAntagCount;
        GhostRoleAntagCount = ghostRoleAntagCount;
        ByRole = byRole ?? ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.Ordinal);
        ByObjectiveType = byObjectiveType ?? ImmutableDictionary<string, int>.Empty.WithComparers(StringComparer.Ordinal);
    }

    /// <summary>
    /// Number of candidates that qualify as antagonists in any way.
    /// </summary>
    public int TotalCount { get; }

    /// <summary>
    /// Number of antagonists assigned by the round mode rather than by ghost roles.
    /// </summary>
    public int GameModeAntagCount { get; }

    /// <summary>
    /// Number of antagonists coming from ghost-role sources.
    /// </summary>
    public int GhostRoleAntagCount { get; }

    /// <summary>
    /// Per-antagonist-role counts keyed by role prototype id.
    /// </summary>
    public ImmutableDictionary<string, int> ByRole { get; }

    /// <summary>
    /// Per-objective-type counts keyed by objective prototype id.
    /// </summary>
    public ImmutableDictionary<string, int> ByObjectiveType { get; }
}

/// <summary>
/// Immutable candidate facts captured for one eligible player mind.
/// </summary>
public sealed class CandidateFacts
{
    /// <summary>
    /// Creates immutable candidate facts normalized for deterministic evaluation.
    /// </summary>
    public CandidateFacts(
        EntityUid mindId,
        NetUserId userId,
        EntityUid ownerEntityUid,
        string characterName,
        string? job,
        string? department,
        int? age,
        string? species,
        string? sex,
        IEnumerable<string>? traits = null,
        bool hasMindshield = false,
        IEnumerable<string>? antagRoles = null,
        IEnumerable<string>? antagObjectiveTypes = null,
        bool isGhostRoleAntag = false)
    {
        MindId = mindId;
        UserId = userId;
        OwnerEntityUid = ownerEntityUid;
        CharacterName = characterName;
        Job = NormalizeOptional(job);
        Department = NormalizeOptional(department);
        Age = age;
        Species = NormalizeOptional(species);
        Sex = NormalizeOptional(sex);
        Traits = NormalizeStrings(traits);
        HasMindshield = hasMindshield;
        AntagRoles = NormalizeStrings(antagRoles);
        AntagObjectiveTypes = NormalizeStrings(antagObjectiveTypes);
        IsGhostRoleAntag = isGhostRoleAntag;
    }

    /// <summary>
    /// Mind entity that owns this candidate entry.
    /// </summary>
    public EntityUid MindId { get; }

    /// <summary>
    /// Network user id used to deterministically order candidates.
    /// </summary>
    public NetUserId UserId { get; }

    /// <summary>
    /// Current body entity used for sound, UI, and runtime ownership.
    /// </summary>
    public EntityUid OwnerEntityUid { get; }

    /// <summary>
    /// Visible character name used by text bindings.
    /// </summary>
    public string CharacterName { get; }

    /// <summary>
    /// Job identifier when available.
    /// </summary>
    public string? Job { get; }

    /// <summary>
    /// Primary department identifier when available.
    /// </summary>
    public string? Department { get; }

    /// <summary>
    /// Character age when available.
    /// </summary>
    public int? Age { get; }

    /// <summary>
    /// Species identifier when available.
    /// </summary>
    public string? Species { get; }

    /// <summary>
    /// Character sex value when available.
    /// </summary>
    public string? Sex { get; }

    /// <summary>
    /// Normalized trait preference identifiers.
    /// </summary>
    public ImmutableArray<string> Traits { get; }

    /// <summary>
    /// Whether the current body carries a mindshield.
    /// </summary>
    public bool HasMindshield { get; }

    /// <summary>
    /// Normalized antagonist role ids attached to the mind.
    /// </summary>
    public ImmutableArray<string> AntagRoles { get; }

    /// <summary>
    /// Normalized objective-type ids attached to the mind.
    /// </summary>
    public ImmutableArray<string> AntagObjectiveTypes { get; }

    /// <summary>
    /// Whether the candidate is considered a ghost-role antagonist.
    /// </summary>
    public bool IsGhostRoleAntag { get; }

    /// <summary>
    /// Whether the candidate counts as an antagonist for summary purposes.
    /// </summary>
    public bool HasAntagRole => IsGhostRoleAntag || AntagRoles.Length > 0;

    /// <summary>
    /// Normalizes optional string fields to null when they are empty.
    /// </summary>
    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>
    /// Normalizes optional string sets into deterministic immutable arrays.
    /// </summary>
    private static ImmutableArray<string> NormalizeStrings(IEnumerable<string>? values)
    {
        return values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToImmutableArray() ?? ImmutableArray<string>.Empty;
    }
}

/// <summary>
/// Result wrapper returned by snapshot building services.
/// </summary>
public sealed class SnapshotBuildResult
{
    /// <summary>
    /// Creates a snapshot build result.
    /// </summary>
    private SnapshotBuildResult(IntentionsSnapshot? snapshot, ImmutableArray<SnapshotBuildIssue> issues)
    {
        Snapshot = snapshot;
        Issues = issues.IsDefault ? ImmutableArray<SnapshotBuildIssue>.Empty : issues;
    }

    /// <summary>
    /// Built snapshot, if snapshot construction succeeded.
    /// </summary>
    public IntentionsSnapshot? Snapshot { get; }

    /// <summary>
    /// Informational, warning, and error issues emitted while building the snapshot.
    /// </summary>
    public ImmutableArray<SnapshotBuildIssue> Issues { get; }

    /// <summary>
    /// Whether the issue list contains at least one error.
    /// </summary>
    public bool HasErrors => Issues.Any(issue => issue.Severity == SnapshotBuildIssueSeverity.Error);

    /// <summary>
    /// Whether the snapshot exists and no error issues were emitted.
    /// </summary>
    public bool IsSuccess => Snapshot is not null && !HasErrors;

    /// <summary>
    /// Creates a successful snapshot build result.
    /// </summary>
    public static SnapshotBuildResult Success(IntentionsSnapshot snapshot, IEnumerable<SnapshotBuildIssue>? issues = null)
    {
        return new SnapshotBuildResult(snapshot, issues?.ToImmutableArray() ?? ImmutableArray<SnapshotBuildIssue>.Empty);
    }

    /// <summary>
    /// Creates a failed snapshot build result without a snapshot instance.
    /// </summary>
    public static SnapshotBuildResult Failure(IEnumerable<SnapshotBuildIssue> issues)
    {
        return new SnapshotBuildResult(null, issues.ToImmutableArray());
    }
}

/// <summary>
/// Describes one issue encountered while building an immutable snapshot.
/// </summary>
public sealed record SnapshotBuildIssue(
    string Code,
    string Message,
    SnapshotBuildIssueSeverity Severity = SnapshotBuildIssueSeverity.Error,
    EntityUid? MindId = null,
    string? Path = null);

/// <summary>
/// Severity level used by snapshot build issues.
/// </summary>
public enum SnapshotBuildIssueSeverity : byte
{
    Info,
    Warning,
    Error,
}
