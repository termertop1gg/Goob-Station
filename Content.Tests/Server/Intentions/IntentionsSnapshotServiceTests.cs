using System;
using System.Linq;
using Content.Server.Intentions.Snapshot;
using Content.Shared.Intentions.Snapshot;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;

#nullable enable

namespace Content.Tests.Server.Intentions;

[TestFixture]
[TestOf(typeof(IntentionsSnapshotFactory))]
/// <summary>
/// Covers snapshot building, candidate ordering, and round fact aggregation.
/// </summary>
public sealed class IntentionsSnapshotServiceTests
{
    [Test]
    public void FactorySortsCandidatesByUserIdThenMindId()
    {
        var result = Build([
            Candidate(3, 2),
            Candidate(2, 1),
            Candidate(1, 1),
        ]);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Snapshot!.Candidates.Select(candidate => candidate.MindId.Id), Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void FactoryBuildsCrewAndSecurityCounts()
    {
        var result = Build([
            Candidate(1, 1, department: "Security"),
            Candidate(2, 2, department: "Medical"),
        ]);

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Snapshot!.RoundFacts.CrewCount, Is.EqualTo(2));
        Assert.That(result.Snapshot.RoundFacts.SecurityCount, Is.EqualTo(1));
    }

    [Test]
    public void FactoryAggregatesAntagSummary()
    {
        var result = Build([
            Candidate(1, 1, antagRoles: ["Traitor"], objectiveTypes: ["Kill"]),
            Candidate(2, 2, antagRoles: ["Dragon"], objectiveTypes: ["Kill", "Escape"], isGhostRoleAntag: true),
            Candidate(3, 3),
        ]);

        var summary = result.Snapshot!.RoundFacts.AntagSummary;

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(summary.TotalCount, Is.EqualTo(2));
        Assert.That(summary.GameModeAntagCount, Is.EqualTo(1));
        Assert.That(summary.GhostRoleAntagCount, Is.EqualTo(1));
        Assert.That(summary.ByRole["Traitor"], Is.EqualTo(1));
        Assert.That(summary.ByRole["Dragon"], Is.EqualTo(1));
        Assert.That(summary.ByObjectiveType["Kill"], Is.EqualTo(2));
        Assert.That(summary.ByObjectiveType["Escape"], Is.EqualTo(1));
    }

    [Test]
    public void FactoryUsesExplicitAntagSummaryInsteadOfCrewOnlyCandidates()
    {
        var explicitSummary = new AntagSummary(
            totalCount: 3,
            gameModeAntagCount: 1,
            ghostRoleAntagCount: 2,
            byRole: System.Collections.Immutable.ImmutableDictionary<string, int>.Empty
                .Add("Traitor", 1)
                .Add("Dragon", 2),
            byObjectiveType: System.Collections.Immutable.ImmutableDictionary<string, int>.Empty
                .Add("Kill", 2)
                .Add("Escape", 1));
        var result = IntentionsSnapshotFactory.Build(
            IntentionsSnapshotRequest.Start(1),
            "extended",
            TimeSpan.FromMinutes(15),
            "Test Station",
            [],
            [Candidate(1, 1)],
            TimeSpan.FromMinutes(15),
            "snapshot-explicit-antag-summary",
            explicitSummary);

        var summary = result.Snapshot!.RoundFacts.AntagSummary;

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Snapshot.RoundFacts.CrewCount, Is.EqualTo(1));
        Assert.That(summary.TotalCount, Is.EqualTo(3));
        Assert.That(summary.GameModeAntagCount, Is.EqualTo(1));
        Assert.That(summary.GhostRoleAntagCount, Is.EqualTo(2));
        Assert.That(summary.ByRole["Dragon"], Is.EqualTo(2));
        Assert.That(summary.ByObjectiveType["Kill"], Is.EqualTo(2));
    }

    [Test]
    public void FactoryUsesFallbacksForMissingRoundFacts()
    {
        var result = IntentionsSnapshotFactory.Build(
            IntentionsSnapshotRequest.Start(7),
            gameMode: null,
            TimeSpan.Zero,
            stationName: " ",
            eventTags: null,
            candidates: [],
            snapshotId: "fallback-test");

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Snapshot!.RoundFacts.GameMode, Is.EqualTo("unknown"));
        Assert.That(result.Snapshot.RoundFacts.StationName, Is.EqualTo("station"));
        Assert.That(result.Snapshot.RoundFacts.EventTags, Is.Empty);
    }

    [Test]
    public void FactoryNormalizesEventTags()
    {
        var result = IntentionsSnapshotFactory.Build(
            IntentionsSnapshotRequest.Refill(3),
            "extended",
            TimeSpan.FromMinutes(22),
            "Test Station",
            ["FestiveSeason", "NewYear", "FestiveSeason"],
            [],
            TimeSpan.FromMinutes(22),
            "snapshot-event-tags");

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(result.Snapshot!.RoundFacts.EventTags, Is.EqualTo(new[] { "FestiveSeason", "NewYear" }));
    }

    [Test]
    public void FactoryRejectsDuplicateMindIds()
    {
        var result = Build([
            Candidate(1, 1),
            Candidate(1, 2),
        ]);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.Snapshot, Is.Null);
        Assert.That(result.Issues.Single().Code, Is.EqualTo("duplicate-candidate-mind"));
    }

    [Test]
    public void GhostRoleOverrideRecognizesMajorGhostRoleAntags()
    {
        Assert.That(IntentionsSnapshotService.IsGhostRoleAntagOverride(["Dragon"]), Is.True);
        Assert.That(IntentionsSnapshotService.IsGhostRoleAntagOverride(["Wizard"]), Is.True);
        Assert.That(IntentionsSnapshotService.IsGhostRoleAntagOverride(["Nukeops"]), Is.True);
    }

    [Test]
    public void GhostRoleOverrideDoesNotMarkRegularRoundAntags()
    {
        Assert.That(IntentionsSnapshotService.IsGhostRoleAntagOverride(["Traitor"]), Is.False);
        Assert.That(IntentionsSnapshotService.IsGhostRoleAntagOverride(["Changeling"]), Is.False);
    }

    [Test]
    public void GhostRoleOverrideMatchesFactorySummarySemantics()
    {
        var result = Build([
            Candidate(1, 1, antagRoles: ["Dragon"], isGhostRoleAntag: IntentionsSnapshotService.IsGhostRoleAntagOverride(["Dragon"])),
            Candidate(2, 2, antagRoles: ["Traitor"], isGhostRoleAntag: IntentionsSnapshotService.IsGhostRoleAntagOverride(["Traitor"])),
            Candidate(3, 3, antagRoles: ["Wizard"], isGhostRoleAntag: IntentionsSnapshotService.IsGhostRoleAntagOverride(["Wizard"])),
        ]);

        var summary = result.Snapshot!.RoundFacts.AntagSummary;

        Assert.That(result.IsSuccess, Is.True);
        Assert.That(summary.TotalCount, Is.EqualTo(3));
        Assert.That(summary.GameModeAntagCount, Is.EqualTo(1));
        Assert.That(summary.GhostRoleAntagCount, Is.EqualTo(2));
        Assert.That(summary.ByRole["Dragon"], Is.EqualTo(1));
        Assert.That(summary.ByRole["Wizard"], Is.EqualTo(1));
        Assert.That(summary.ByRole["Traitor"], Is.EqualTo(1));
    }

    private static SnapshotBuildResult Build(params CandidateFacts[] candidates)
    {
        return IntentionsSnapshotFactory.Build(
            IntentionsSnapshotRequest.Start(1),
            "extended",
            TimeSpan.FromMinutes(15),
            "Test Station",
            [],
            candidates,
            TimeSpan.FromMinutes(15),
            "snapshot-test");
    }

    private static CandidateFacts Candidate(
        int mindId,
        int userId,
        string department = "Civilian",
        string[]? antagRoles = null,
        string[]? objectiveTypes = null,
        bool isGhostRoleAntag = false)
    {
        return new CandidateFacts(
            new EntityUid(mindId),
            new NetUserId(Guid.Parse($"00000000-0000-0000-0000-{userId:000000000000}")),
            new EntityUid(1000 + mindId),
            $"Candidate {mindId}",
            "Assistant",
            department,
            30,
            "Human",
            "Male",
            traits: null,
            hasMindshield: false,
            antagRoles,
            objectiveTypes,
            isGhostRoleAntag);
    }
}
