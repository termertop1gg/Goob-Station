using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Server.Intentions.Waves;
using Content.Shared.Intentions.Predicates;
using Content.Shared.Intentions.Prototypes;
using Content.Shared.Intentions.Snapshot;
using Content.Shared.Intentions.Validation;
using Content.Shared.Intentions.Waves;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;

#nullable enable

namespace Content.Tests.Server.Intentions;

[TestFixture]
[TestOf(typeof(IntentionsScenarioBuilder))]
/// <summary>
/// Covers pure scenario-building behavior including slot ordering, binding, and candidate reuse rules.
/// </summary>
public sealed class IntentionsScenarioBuilderTests
{
    [Test]
    public void BuildsOwnerFirst()
    {
        var scenario = Scenario("ownerOnly", [Owner()]);
        var result = Build(scenario, ["owner"], Snapshot([Candidate(1, 1)]));

        Assert.That(result.IsSuccess, Is.True, Reasons(result));
        Assert.That(result.BuiltSlots.Select(slot => slot.SlotId), Is.EqualTo(new[] { "owner" }));
        Assert.That(result.BuiltSlots.Single().State, Is.EqualTo(ScenarioSlotBuildState.Assigned));
    }

    [Test]
    public void OptionalSlotWithoutCandidateIsSkipped()
    {
        var scenario = Scenario("optionalSkip", [
            Owner(),
            Secondary("helper", required: false),
        ]);

        var result = Build(scenario, ["owner", "helper"], Snapshot([Candidate(1, 1)]));

        Assert.That(result.IsSuccess, Is.True, Reasons(result));
        Assert.That(result.SkippedOptionalSlots, Is.EqualTo(new[] { "helper" }));
    }

    [Test]
    public void RequiredSlotWithoutCandidateRejectsScenario()
    {
        var scenario = Scenario("requiredReject", [
            Owner(),
            Secondary("helper", required: true),
        ]);

        var result = Build(scenario, ["owner", "helper"], Snapshot([Candidate(1, 1)]));

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.FailureReason, Is.EqualTo("no-candidate-for-required-slot"));
        Assert.That(result.SlotRejectReasons.Any(reason => reason.SlotId == "helper" && reason.Code == "candidate-pool-exhausted"), Is.True);
    }

    [Test]
    public void AllowSameActorAsCanReuseSelectedActor()
    {
        var scenario = Scenario("allowSame", [
            Owner(),
            Secondary("helper", allowSameActorAs: ["owner"]),
        ]);

        var result = Build(scenario, ["owner", "helper"], Snapshot([Candidate(1, 1)]));

        Assert.That(result.IsSuccess, Is.True, Reasons(result));
        Assert.That(result.BuiltSlotsBySlotId["helper"].MindId, Is.EqualTo(result.BuiltSlotsBySlotId["owner"].MindId));
    }

    [Test]
    public void BindToSlotUsesSelectedCandidate()
    {
        var scenario = Scenario("bind", [
            Owner(),
            Secondary("reminder", bindToSlot: "owner"),
        ]);

        var result = Build(scenario, ["owner", "reminder"], Snapshot([Candidate(1, 1)]));
        var reminder = result.BuiltSlotsBySlotId["reminder"];

        Assert.That(result.IsSuccess, Is.True, Reasons(result));
        Assert.That(reminder.MindId, Is.EqualTo(result.BuiltSlotsBySlotId["owner"].MindId));
        Assert.That(reminder.State, Is.EqualTo(ScenarioSlotBuildState.Bound));
        Assert.That(reminder.WasBound, Is.True);
        Assert.That(reminder.BoundToSlotId, Is.EqualTo("owner"));
    }

    [Test]
    public void CompareToFiltersCandidatesBySelectedSlotFacts()
    {
        var scenario = Scenario("compare", [
            Owner(candidatePredicates: [CandidatePredicate("job", "equals", value: "Captain")]),
            Secondary("helper", candidatePredicates: [CandidateComparePredicate("department", "sameAs", "owner")]),
        ]);
        var snapshot = Snapshot([
            Candidate(1, 1, job: "Captain", department: "Security"),
            Candidate(2, 2, job: "Assistant", department: "Medical"),
            Candidate(3, 3, job: "Assistant", department: "Security"),
        ]);

        var result = Build(scenario, ["owner", "helper"], snapshot, seed: 1);

        Assert.That(result.IsSuccess, Is.True, Reasons(result));
        Assert.That(result.BuiltSlotsBySlotId["owner"].MindId, Is.EqualTo(new EntityUid(1)));
        Assert.That(result.BuiltSlotsBySlotId["helper"].MindId, Is.EqualTo(new EntityUid(3)));
    }

    [Test]
    public void BuilderDoesNotMutateWaveContextOrCategoryState()
    {
        var scenario = Scenario("pure", [Owner()]);
        var snapshot = Snapshot([Candidate(1, 1)]);
        var context = Context(snapshot);
        var state = new CategoryWaveState("social", targetQuota: 1, effectiveMaxPrimaryPerMind: 1);
        state.CandidateScenarioIds.Add("pure");

        var result = new IntentionsScenarioBuilder().TryBuildScenario(
            new ValidatedScenarioTemplate(scenario, ["owner"]),
            snapshot,
            context,
            state,
            new IntentionsDeterministicRandom(1));

        Assert.That(result.IsSuccess, Is.True, Reasons(result));
        Assert.That(context.AssignedScenarioIds, Is.Empty);
        Assert.That(context.AssignedPrimaryByMind, Is.Empty);
        Assert.That(state.FilledQuota, Is.EqualTo(0));
        Assert.That(state.CandidateScenarioIds, Is.EqualTo(new[] { "pure" }));
    }

    private static ScenarioBuildResult Build(
        ScenarioTemplatePrototype scenario,
        IReadOnlyList<string> slotBuildOrder,
        IntentionsSnapshot snapshot,
        int seed = 1,
        int maxPrimaryPerMind = 1)
    {
        return new IntentionsScenarioBuilder().TryBuildScenario(
            new ValidatedScenarioTemplate(scenario, slotBuildOrder),
            snapshot,
            Context(snapshot),
            new CategoryWaveState("social", targetQuota: 1, effectiveMaxPrimaryPerMind: maxPrimaryPerMind),
            new IntentionsDeterministicRandom(seed));
    }

    private static DistributionWaveContext Context(IntentionsSnapshot snapshot)
    {
        return new DistributionWaveContext(
            1,
            snapshot.SnapshotId,
            1,
            snapshot.RoundFacts.StationTime,
            snapshot.RoundFacts.CrewCount);
    }

    private static ScenarioTemplatePrototype Scenario(string id, List<ScenarioEntry> entries)
    {
        var scenario = new ScenarioTemplatePrototype
        {
            Name = id,
            Category = "social",
            Enabled = true,
            Weight = 1,
            Entries = entries,
        };

        SetId(scenario, id);
        return scenario;
    }

    private static ScenarioEntry Owner(List<PredicateDefinition>? candidatePredicates = null)
    {
        return new ScenarioEntry
        {
            SlotId = "owner",
            Kind = IntentionsPrototypeConstants.Primary,
            IntentionId = "primary",
            Required = true,
            CandidatePredicates = candidatePredicates ?? [],
        };
    }

    private static ScenarioEntry Secondary(
        string slotId,
        bool required = true,
        List<PredicateDefinition>? candidatePredicates = null,
        string? bindToSlot = null,
        List<string>? allowSameActorAs = null)
    {
        return new ScenarioEntry
        {
            SlotId = slotId,
            Kind = IntentionsPrototypeConstants.Secondary,
            IntentionId = "secondary",
            Required = required,
            CandidatePredicates = candidatePredicates ?? [],
            BindToSlot = bindToSlot,
            AllowSameActorAs = allowSameActorAs ?? [],
        };
    }

    private static PredicateDefinition CandidatePredicate(string field, string op, string? value = null)
    {
        return new PredicateDefinition
        {
            Scope = IntentionsPredicateSchema.CandidateScope,
            Field = field,
            Operator = op,
            Value = value,
        };
    }

    private static PredicateDefinition CandidateComparePredicate(string field, string op, string slotId)
    {
        return new PredicateDefinition
        {
            Scope = IntentionsPredicateSchema.CandidateScope,
            Field = field,
            Operator = op,
            CompareTo = new CompareToDefinition
            {
                Scope = IntentionsPredicateSchema.SlotScope,
                SlotId = slotId,
                Field = field,
            },
        };
    }

    private static IntentionsSnapshot Snapshot(IReadOnlyList<CandidateFacts> candidates)
    {
        var result = IntentionsSnapshotFactory.Build(
            IntentionsSnapshotRequest.Start(1),
            "extended",
            TimeSpan.FromMinutes(15),
            "Test Station",
            [],
            candidates,
            TimeSpan.FromMinutes(15),
            "builder-test-snapshot");

        Assert.That(result.IsSuccess, Is.True);
        return result.Snapshot!;
    }

    private static CandidateFacts Candidate(
        int mindId,
        int userId,
        string job = "Assistant",
        string department = "Civilian")
    {
        return new CandidateFacts(
            new EntityUid(mindId),
            new NetUserId(Guid.Parse($"00000000-0000-0000-0000-{userId:000000000000}")),
            new EntityUid(1000 + mindId),
            $"Candidate {mindId}",
            job,
            department,
            30,
            "Human",
            "Male");
    }

    private static string Reasons(ScenarioBuildResult result)
    {
        return string.Join(", ", result.SlotRejectReasons.Select(reason => $"{reason.SlotId}:{reason.Code}"));
    }

    private static void SetId<T>(T prototype, string id)
    {
        typeof(T).GetProperty("ID", BindingFlags.Instance | BindingFlags.Public)!.SetValue(prototype, id);
    }
}
