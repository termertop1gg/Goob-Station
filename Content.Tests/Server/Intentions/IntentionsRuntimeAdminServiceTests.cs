using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Content.Server.Intentions.Runtime;
using Content.Shared.Intentions.Predicates;
using Content.Shared.Intentions.Prototypes;
using Content.Shared.Intentions.Runtime;
using Content.Shared.Intentions.Snapshot;
using Content.Shared.Intentions.Validation;
using NUnit.Framework;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;

#nullable enable

namespace Content.Tests.Server.Intentions;

[TestFixture]
[TestOf(typeof(IntentionsRuntimeAdminService))]
/// <summary>
/// Covers debug/admin runtime mutations such as full reset, targeted removal, and forced scenario assignment.
/// </summary>
public sealed class IntentionsRuntimeAdminServiceTests
{
    [Test]
    public void ClearAllScenariosResetsRegistryAndReturnsAffectedMinds()
    {
        var runtime = new IntentionsRuntimeSystem();
        var first = IntentionsLifecycleServiceTests.Fixture("scenario-a", ownerMindId: new EntityUid(1), registry: runtime.Registry);
        var second = IntentionsLifecycleServiceTests.Fixture("scenario-b", ownerMindId: new EntityUid(2), ownerEntityUid: new EntityUid(1002), registry: runtime.Registry);

        var result = new IntentionsRuntimeAdminService().ClearAllScenarios(runtime);

        Assert.That(result.RemovedScenarioCount, Is.EqualTo(2));
        Assert.That(result.RemovedIntentionCount, Is.EqualTo(2));
        Assert.That(result.AffectedMindIds, Is.EquivalentTo(new[] { first.OwnerMindId, second.OwnerMindId }));
        AssertRegistryEmpty(runtime.Registry);
    }

    [Test]
    public void RemoveScenarioPurgesIndexesAndUniqueMarker()
    {
        var fixture = IntentionsLifecycleServiceTests.Fixture(hiddenTimer: true);

        var result = new IntentionsRuntimeAdminService().RemoveScenario(
            fixture.Registry,
            fixture.ScenarioUid,
            fixture.OwnerMindId);

        Assert.That(result.IsSuccess, Is.True, result.Message);
        Assert.That(fixture.Registry.ScenarioByUid, Is.Empty);
        Assert.That(fixture.Registry.IntentionByUid, Is.Empty);
        Assert.That(fixture.Registry.IntentionIdsByMind, Is.Empty);
        Assert.That(fixture.Registry.ScenarioUidByIntentionUid, Is.Empty);
        Assert.That(fixture.Registry.SlotAssignmentByScenarioAndSlot, Is.Empty);
        Assert.That(fixture.Registry.AssignedScenarioIds, Is.Empty);
        Assert.That(fixture.Registry.AssignedPrimaryByMind, Is.Empty);
        Assert.That(fixture.Registry.HiddenIntentionsByRevealTime, Is.Empty);
    }

    [Test]
    public void RemoveScenarioKeepsUniqueMarkerWhenAnotherRuntimeScenarioUsesSameTemplate()
    {
        var registry = new IntentionsRuntimeRegistry();
        var first = IntentionsLifecycleServiceTests.Fixture("shared-template", ownerMindId: new EntityUid(1), registry: registry);
        var second = IntentionsLifecycleServiceTests.Fixture("shared-template", ownerMindId: new EntityUid(2), ownerEntityUid: new EntityUid(1002), registry: registry);

        var result = new IntentionsRuntimeAdminService().RemoveScenario(
            registry,
            first.ScenarioUid,
            first.OwnerMindId);

        Assert.That(result.IsSuccess, Is.True, result.Message);
        Assert.That(registry.AssignedScenarioIds, Does.Contain("shared-template"));
        Assert.That(registry.ScenarioByUid, Has.Count.EqualTo(1));
        Assert.That(registry.ScenarioByUid, Contains.Key(second.ScenarioUid));
    }

    [Test]
    public void RemoveScenarioDoesNotDoubleDecrementCancelledPrimary()
    {
        var registry = new IntentionsRuntimeRegistry();
        var active = IntentionsLifecycleServiceTests.Fixture("active-template", ownerMindId: new EntityUid(1), registry: registry);
        var cancelled = IntentionsLifecycleServiceTests.Fixture(
            "cancelled-template",
            ownerMindId: active.OwnerMindId,
            ownerEntityUid: new EntityUid(1003),
            status: ScenarioRuntimeStatus.Cancelled,
            registry: registry);

        var result = new IntentionsRuntimeAdminService().RemoveScenario(
            registry,
            cancelled.ScenarioUid,
            cancelled.OwnerMindId);

        Assert.That(result.IsSuccess, Is.True, result.Message);
        Assert.That(registry.AssignedPrimaryByMind[active.OwnerMindId]["social"], Is.EqualTo(1));
    }

    [Test]
    public void RevealHiddenIntentionRevealsHiddenNoneIntent()
    {
        var fixture = IntentionsLifecycleServiceTests.Fixture();
        SetHiddenState(fixture.Registry, fixture.OwnerIntentionUid, isHidden: true, IntentionRevealMode.None, null);

        var result = new IntentionsRuntimeAdminService().RevealHiddenIntention(
            fixture.Registry,
            fixture.OwnerIntentionUid,
            fixture.OwnerMindId,
            TimeSpan.FromMinutes(12));

        Assert.That(result.IsSuccess, Is.True, result.Message);
        Assert.That(result.Scope, Is.EqualTo(IntentionsRuntimeRevealScope.One));
        Assert.That(result.RevealedIntentions, Has.Length.EqualTo(1));
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].IsHidden, Is.False);
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].RevealMode, Is.EqualTo(IntentionRevealMode.None));
        Assert.That(fixture.Registry.HiddenIntentionsByRevealTime, Is.Empty);
    }

    [Test]
    public void RevealHiddenIntentionRevealsTimerIntentAndRemovesRevealIndex()
    {
        var fixture = IntentionsLifecycleServiceTests.Fixture(hiddenTimer: true);

        var result = new IntentionsRuntimeAdminService().RevealHiddenIntention(
            fixture.Registry,
            fixture.OwnerIntentionUid,
            fixture.OwnerMindId,
            TimeSpan.FromMinutes(12));

        Assert.That(result.IsSuccess, Is.True, result.Message);
        Assert.That(result.RevealedIntentions[0].PreviousRevealMode, Is.EqualTo(IntentionRevealMode.Timer));
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].IsHidden, Is.False);
        Assert.That(fixture.Registry.HiddenIntentionsByRevealTime, Is.Empty);
    }

    [Test]
    public void RevealHiddenIntentionRejectsOwnerMismatch()
    {
        var fixture = IntentionsLifecycleServiceTests.Fixture(hiddenTimer: true);

        var result = new IntentionsRuntimeAdminService().RevealHiddenIntention(
            fixture.Registry,
            fixture.OwnerIntentionUid,
            new EntityUid(999),
            TimeSpan.FromMinutes(12));

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo("owner-mind-mismatch"));
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].IsHidden, Is.True);
    }

    [Test]
    public void RevealHiddenIntentionRejectsAlreadyVisibleIntent()
    {
        var fixture = IntentionsLifecycleServiceTests.Fixture();

        var result = new IntentionsRuntimeAdminService().RevealHiddenIntention(
            fixture.Registry,
            fixture.OwnerIntentionUid,
            fixture.OwnerMindId,
            TimeSpan.FromMinutes(12));

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo("intention-not-hidden"));
    }

    [Test]
    public void RevealHiddenIntentionRejectsNonActiveIntent()
    {
        var fixture = IntentionsLifecycleServiceTests.Fixture(hiddenTimer: true);
        SetHiddenState(fixture.Registry, fixture.OwnerIntentionUid, isHidden: true, IntentionRevealMode.Timer, TimeSpan.FromMinutes(20), IntentionRuntimeStatus.Cancelled);

        var result = new IntentionsRuntimeAdminService().RevealHiddenIntention(
            fixture.Registry,
            fixture.OwnerIntentionUid,
            fixture.OwnerMindId,
            TimeSpan.FromMinutes(12));

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo("intention-not-active"));
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].IsHidden, Is.True);
    }

    [Test]
    public void RevealAllHiddenIntentionsForMindRevealsOnlyActiveHiddenOnes()
    {
        var registry = new IntentionsRuntimeRegistry();
        var ownerMindId = new EntityUid(1);
        var hiddenTimer = IntentionsLifecycleServiceTests.Fixture("hidden-timer", ownerMindId: ownerMindId, hiddenTimer: true, registry: registry);
        var hiddenNone = IntentionsLifecycleServiceTests.Fixture("hidden-none", ownerMindId: ownerMindId, ownerEntityUid: new EntityUid(1002), registry: registry);
        var visible = IntentionsLifecycleServiceTests.Fixture("visible", ownerMindId: ownerMindId, ownerEntityUid: new EntityUid(1003), registry: registry);

        SetHiddenState(hiddenNone.Registry, hiddenNone.OwnerIntentionUid, isHidden: true, IntentionRevealMode.None, null);

        var result = new IntentionsRuntimeAdminService().RevealAllHiddenIntentionsForMind(
            registry,
            ownerMindId,
            TimeSpan.FromMinutes(22));

        Assert.That(result.IsSuccess, Is.True, result.Message);
        Assert.That(result.Scope, Is.EqualTo(IntentionsRuntimeRevealScope.AllForMind));
        Assert.That(result.RevealedIntentions.Select(item => item.IntentionUid), Is.EquivalentTo([hiddenTimer.OwnerIntentionUid, hiddenNone.OwnerIntentionUid]));
        Assert.That(registry.IntentionByUid[hiddenTimer.OwnerIntentionUid].IsHidden, Is.False);
        Assert.That(registry.IntentionByUid[hiddenNone.OwnerIntentionUid].IsHidden, Is.False);
        Assert.That(registry.IntentionByUid[visible.OwnerIntentionUid].IsHidden, Is.False);
        Assert.That(registry.HiddenIntentionsByRevealTime, Is.Empty);
    }

    [Test]
    public void RevealAllHiddenIntentionsForMindSkipsCancelledHiddenIntentions()
    {
        var registry = new IntentionsRuntimeRegistry();
        var ownerMindId = new EntityUid(1);
        var active = IntentionsLifecycleServiceTests.Fixture("active-hidden", ownerMindId: ownerMindId, registry: registry);
        var cancelled = IntentionsLifecycleServiceTests.Fixture("cancelled-hidden", ownerMindId: ownerMindId, ownerEntityUid: new EntityUid(1002), registry: registry);
        SetHiddenState(active.Registry, active.OwnerIntentionUid, isHidden: true, IntentionRevealMode.None, null);
        SetHiddenState(cancelled.Registry, cancelled.OwnerIntentionUid, isHidden: true, IntentionRevealMode.Timer, TimeSpan.FromMinutes(20), IntentionRuntimeStatus.Cancelled);

        var result = new IntentionsRuntimeAdminService().RevealAllHiddenIntentionsForMind(
            registry,
            ownerMindId,
            TimeSpan.FromMinutes(22));

        Assert.That(result.IsSuccess, Is.True, result.Message);
        Assert.That(result.RevealedIntentions, Has.Length.EqualTo(1));
        Assert.That(result.RevealedIntentions[0].IntentionUid, Is.EqualTo(active.OwnerIntentionUid));
        Assert.That(registry.IntentionByUid[cancelled.OwnerIntentionUid].IsHidden, Is.True);
    }

    [Test]
    public void RevealAllHiddenIntentionsForMindFailsWhenMindHasNoHiddenIntentions()
    {
        var fixture = IntentionsLifecycleServiceTests.Fixture();

        var result = new IntentionsRuntimeAdminService().RevealAllHiddenIntentionsForMind(
            fixture.Registry,
            fixture.OwnerMindId,
            TimeSpan.FromMinutes(22));

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo("no-hidden-intentions"));
    }

    [Test]
    public void ForceAssignCommitsScenarioForExplicitMindOrder()
    {
        var scenario = ValidatedScenario(
            "manual-social",
            ["owner", "helper"],
            OwnerEntry(),
            SecondaryEntry("helper"));
        var catalog = Catalog(scenario, Intention("primary", IntentionsPrototypeConstants.Primary), Intention("secondary", IntentionsPrototypeConstants.Secondary));
        var snapshot = Snapshot(
            Candidate(1, 1, "Owner Person"),
            Candidate(2, 2, "Helper Person"));
        var registry = new IntentionsRuntimeRegistry();

        var result = new IntentionsRuntimeAdminService().TryForceAssignScenario(
            catalog,
            snapshot,
            registry,
            "manual-social",
            ["1", "2"],
            waveId: -1);

        Assert.That(result.IsSuccess, Is.True, result.Message);
        Assert.That(result.BuildResult, Is.Not.Null);
        Assert.That(result.CommitResult?.IsSuccess, Is.True);
        Assert.That(registry.ScenarioByUid, Has.Count.EqualTo(1));
        Assert.That(registry.IntentionByUid, Has.Count.EqualTo(2));
        Assert.That(registry.AssignedScenarioIds, Does.Contain("manual-social"));
        Assert.That(registry.WaveContextByWaveId, Contains.Key(-1));
    }

    [Test]
    public void ForceAssignAllowsDashForOptionalSlot()
    {
        var scenario = ValidatedScenario(
            "optional-social",
            ["owner", "helper"],
            OwnerEntry(),
            SecondaryEntry("helper", required: false));
        var catalog = Catalog(scenario, Intention("primary", IntentionsPrototypeConstants.Primary), Intention("secondary", IntentionsPrototypeConstants.Secondary));
        var snapshot = Snapshot(Candidate(1, 1, "Owner Person"));

        var result = new IntentionsRuntimeAdminService().TryForceAssignScenario(
            catalog,
            snapshot,
            new IntentionsRuntimeRegistry(),
            "optional-social",
            ["1", "-"],
            waveId: -2);

        Assert.That(result.IsSuccess, Is.True, result.Message);
        Assert.That(result.BuildResult?.SkippedOptionalSlots, Is.EqualTo(new[] { "helper" }));
        Assert.That(result.CommitResult?.IntentionInstances.Length, Is.EqualTo(1));
    }

    [Test]
    public void ForceAssignRejectsDashForRequiredSlot()
    {
        var scenario = ValidatedScenario(
            "required-social",
            ["owner", "helper"],
            OwnerEntry(),
            SecondaryEntry("helper", required: true));
        var catalog = Catalog(scenario, Intention("primary", IntentionsPrototypeConstants.Primary), Intention("secondary", IntentionsPrototypeConstants.Secondary));
        var snapshot = Snapshot(Candidate(1, 1, "Owner Person"));

        var result = new IntentionsRuntimeAdminService().TryForceAssignScenario(
            catalog,
            snapshot,
            new IntentionsRuntimeRegistry(),
            "required-social",
            ["1", "-"],
            waveId: -3);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo("required-slot-cannot-be-skipped"));
        Assert.That(result.ExpectedArgumentLayout, Is.EqualTo("owner=<mindId> helper=<mindId>"));
    }

    [Test]
    public void ForceAssignAutoResolvesBindToSlot()
    {
        var scenario = ValidatedScenario(
            "bind-social",
            ["owner", "echo"],
            OwnerEntry(),
            SecondaryEntry("echo", bindToSlot: "owner"));
        var catalog = Catalog(scenario, Intention("primary", IntentionsPrototypeConstants.Primary), Intention("secondary", IntentionsPrototypeConstants.Secondary));
        var snapshot = Snapshot(Candidate(1, 1, "Owner Person"));

        var result = new IntentionsRuntimeAdminService().TryForceAssignScenario(
            catalog,
            snapshot,
            new IntentionsRuntimeRegistry(),
            "bind-social",
            ["1"],
            waveId: -4);

        Assert.That(result.IsSuccess, Is.True, result.Message);
        Assert.That(result.BuildResult?.BuiltSlotsBySlotId["echo"].WasBound, Is.True);
        Assert.That(result.BuildResult?.BuiltSlotsBySlotId["echo"].BoundToSlotId, Is.EqualTo("owner"));
    }

    [Test]
    public void ForceAssignRejectsDuplicateScenarioTemplateId()
    {
        var scenario = ValidatedScenario(
            "unique-social",
            ["owner"],
            OwnerEntry());
        var catalog = Catalog(scenario, Intention("primary", IntentionsPrototypeConstants.Primary));
        var snapshot = Snapshot(Candidate(1, 1, "Owner Person"));
        var registry = new IntentionsRuntimeRegistry();
        registry.AddAssignedScenarioId("unique-social");

        var result = new IntentionsRuntimeAdminService().TryForceAssignScenario(
            catalog,
            snapshot,
            registry,
            "unique-social",
            ["1"],
            waveId: -5);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo("scenario-already-assigned"));
    }

    [Test]
    public void ForceAssignRejectsGlobalPredicateFailure()
    {
        var scenario = ValidatedScenario(
            "global-social",
            ["owner"],
            OwnerEntry());
        scenario.Template.GlobalPredicates =
        [
            new PredicateDefinition
            {
                Scope = IntentionsPredicateSchema.RoundScope,
                Field = "crewCount",
                Operator = ">=",
                Value = "5",
            },
        ];

        var catalog = Catalog(scenario, Intention("primary", IntentionsPrototypeConstants.Primary));
        var snapshot = Snapshot(Candidate(1, 1, "Owner Person"));

        var result = new IntentionsRuntimeAdminService().TryForceAssignScenario(
            catalog,
            snapshot,
            new IntentionsRuntimeRegistry(),
            "global-social",
            ["1"],
            waveId: -6);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo("global-predicates-failed"));
        Assert.That(result.GlobalPredicateResult?.RejectReasons.Length, Is.GreaterThan(0));
    }

    [Test]
    public void ForceAssignCanIgnoreGlobalPredicateFailure()
    {
        var scenario = ValidatedScenario(
            "global-ignore-social",
            ["owner"],
            OwnerEntry());
        scenario.Template.GlobalPredicates =
        [
            new PredicateDefinition
            {
                Scope = IntentionsPredicateSchema.RoundScope,
                Field = "crewCount",
                Operator = ">=",
                Value = "5",
            },
        ];

        var catalog = Catalog(scenario, Intention("primary", IntentionsPrototypeConstants.Primary));
        var snapshot = Snapshot(Candidate(1, 1, "Owner Person"));

        var result = new IntentionsRuntimeAdminService().TryForceAssignScenario(
            catalog,
            snapshot,
            new IntentionsRuntimeRegistry(),
            "global-ignore-social",
            ["1"],
            waveId: -61,
            ignorePredicates: true);

        Assert.That(result.IsSuccess, Is.True, result.Message);
        Assert.That(result.IgnoredPredicates, Is.True);
        Assert.That(result.GlobalPredicateResult, Is.Null);
        Assert.That(result.CommitResult?.IsSuccess, Is.True);
    }

    [Test]
    public void ForceAssignRejectsCandidatePredicateFailure()
    {
        var scenario = ValidatedScenario(
            "candidate-social",
            ["owner"],
            OwnerEntry([
                new PredicateDefinition
                {
                    Scope = IntentionsPredicateSchema.CandidateScope,
                    Field = "job",
                    Operator = "equals",
                    Value = "Captain",
                },
            ]));
        var catalog = Catalog(scenario, Intention("primary", IntentionsPrototypeConstants.Primary));
        var snapshot = Snapshot(Candidate(1, 1, "Owner Person", job: "Assistant"));

        var result = new IntentionsRuntimeAdminService().TryForceAssignScenario(
            catalog,
            snapshot,
            new IntentionsRuntimeRegistry(),
            "candidate-social",
            ["1"],
            waveId: -7);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.FailureCode, Is.EqualTo("candidate-predicates-failed"));
        Assert.That(result.BuildResult?.SlotRejectReasons.Any(reason => reason.Code == "candidate-predicates-failed"), Is.True);
    }

    [Test]
    public void ForceAssignCanIgnoreCandidatePredicateFailure()
    {
        var scenario = ValidatedScenario(
            "candidate-ignore-social",
            ["owner"],
            OwnerEntry([
                new PredicateDefinition
                {
                    Scope = IntentionsPredicateSchema.CandidateScope,
                    Field = "job",
                    Operator = "equals",
                    Value = "Captain",
                },
            ]));
        var catalog = Catalog(scenario, Intention("primary", IntentionsPrototypeConstants.Primary));
        var snapshot = Snapshot(Candidate(1, 1, "Owner Person", job: "Assistant"));

        var result = new IntentionsRuntimeAdminService().TryForceAssignScenario(
            catalog,
            snapshot,
            new IntentionsRuntimeRegistry(),
            "candidate-ignore-social",
            ["1"],
            waveId: -71,
            ignorePredicates: true);

        Assert.That(result.IsSuccess, Is.True, result.Message);
        Assert.That(result.IgnoredPredicates, Is.True);
        Assert.That(result.CommitResult?.IsSuccess, Is.True);
    }

    [Test]
    public void ForceAssignStillRejectsMindOutsideSnapshotWhenIgnoringPredicates()
    {
        var scenario = ValidatedScenario(
            "snapshot-social",
            ["owner"],
            OwnerEntry([
                new PredicateDefinition
                {
                    Scope = IntentionsPredicateSchema.CandidateScope,
                    Field = "job",
                    Operator = "equals",
                    Value = "Captain",
                },
            ]));
        var catalog = Catalog(scenario, Intention("primary", IntentionsPrototypeConstants.Primary));
        var snapshot = Snapshot(Candidate(1, 1, "Owner Person", job: "Assistant"));

        var result = new IntentionsRuntimeAdminService().TryForceAssignScenario(
            catalog,
            snapshot,
            new IntentionsRuntimeRegistry(),
            "snapshot-social",
            ["999"],
            waveId: -72,
            ignorePredicates: true);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.IgnoredPredicates, Is.True);
        Assert.That(result.FailureCode, Is.EqualTo("mind-not-in-snapshot"));
    }

    [Test]
    public void ForceAssignStillRejectsForbiddenSameActorReuseWhenIgnoringPredicates()
    {
        var scenario = ValidatedScenario(
            "same-actor-social",
            ["owner", "helper"],
            OwnerEntry(),
            SecondaryEntry("helper"));
        var catalog = Catalog(scenario, Intention("primary", IntentionsPrototypeConstants.Primary), Intention("secondary", IntentionsPrototypeConstants.Secondary));
        var snapshot = Snapshot(
            Candidate(1, 1, "Owner Person"),
            Candidate(2, 2, "Helper Person"));

        var result = new IntentionsRuntimeAdminService().TryForceAssignScenario(
            catalog,
            snapshot,
            new IntentionsRuntimeRegistry(),
            "same-actor-social",
            ["1", "1"],
            waveId: -73,
            ignorePredicates: true);

        Assert.That(result.IsSuccess, Is.False);
        Assert.That(result.IgnoredPredicates, Is.True);
        Assert.That(result.FailureCode, Is.EqualTo("same-actor-not-allowed"));
    }

    [Test]
    public void ForceAssignBypassesPrimaryQuotaCounters()
    {
        var scenario = ValidatedScenario(
            "quota-social",
            ["owner"],
            OwnerEntry());
        var catalog = Catalog(scenario, Intention("primary", IntentionsPrototypeConstants.Primary));
        var snapshot = Snapshot(Candidate(1, 1, "Owner Person"));
        var registry = new IntentionsRuntimeRegistry();
        registry.IncrementAssignedPrimary(new EntityUid(1), "social");
        registry.IncrementAssignedPrimary(new EntityUid(1), "social");
        registry.IncrementAssignedPrimary(new EntityUid(1), "social");

        var result = new IntentionsRuntimeAdminService().TryForceAssignScenario(
            catalog,
            snapshot,
            registry,
            "quota-social",
            ["1"],
            waveId: -8);

        Assert.That(result.IsSuccess, Is.True, result.Message);
        Assert.That(result.CommitResult?.IsSuccess, Is.True);
    }

    private static void AssertRegistryEmpty(IntentionsRuntimeRegistry registry)
    {
        Assert.That(registry.ScenarioByUid, Is.Empty);
        Assert.That(registry.IntentionByUid, Is.Empty);
        Assert.That(registry.IntentionIdsByMind, Is.Empty);
        Assert.That(registry.ScenarioUidByIntentionUid, Is.Empty);
        Assert.That(registry.SlotAssignmentByScenarioAndSlot, Is.Empty);
        Assert.That(registry.AssignedScenarioIds, Is.Empty);
        Assert.That(registry.AssignedPrimaryByMind, Is.Empty);
        Assert.That(registry.WaveContextByWaveId, Is.Empty);
        Assert.That(registry.HiddenIntentionsByRevealTime, Is.Empty);
        Assert.That(registry.MissingOwnerScenarioIds, Is.Empty);
    }

    private static void SetHiddenState(
        IntentionsRuntimeRegistry registry,
        IntentionInstanceUid intentionUid,
        bool isHidden,
        IntentionRevealMode revealMode,
        TimeSpan? revealAt,
        IntentionRuntimeStatus? status = null)
    {
        var current = registry.IntentionByUid[intentionUid];
        registry.RemoveHiddenRevealForIntention(intentionUid);

        var replacement = new IntentionInstance(
            current.Uid,
            current.IntentionTemplateId,
            current.ScenarioUid,
            current.SlotId,
            current.OwnerMindId,
            current.OwnerEntityUid,
            current.Kind,
            status ?? current.Status,
            current.AssignedAtRoundTime,
            current.StatusChangedAtRoundTime,
            isHidden,
            revealMode,
            revealAt,
            current.ResolvedTextParameters,
            current.CopyableTextResolved,
            current.FailureReason);

        registry.ReplaceIntention(replacement);
        if (isHidden && revealMode == IntentionRevealMode.Timer && revealAt is { } revealTime)
            registry.AddHiddenReveal(revealTime, intentionUid);
    }

    private static ValidationCatalog Catalog(ValidatedScenarioTemplate scenario, params IntentionTemplatePrototype[] intentions)
    {
        var catalog = new ValidationCatalog();
        foreach (var intention in intentions)
        {
            catalog.ValidIntentions[intention.ID] = intention;
        }

        catalog.ValidScenarios[scenario.Template.ID] = scenario;
        catalog.ValidScenarioOrder.Add(scenario.Template.ID);
        return catalog;
    }

    private static ValidatedScenarioTemplate ValidatedScenario(string id, IReadOnlyList<string> slotBuildOrder, params ScenarioEntry[] entries)
    {
        var scenario = new ScenarioTemplatePrototype
        {
            Name = id,
            Category = "social",
            Enabled = true,
            Weight = 1,
            Entries = entries.ToList(),
        };
        SetId(scenario, id);
        return new ValidatedScenarioTemplate(scenario, slotBuildOrder);
    }

    private static IntentionTemplatePrototype Intention(string id, string kind)
    {
        var intention = new IntentionTemplatePrototype
        {
            Kind = kind,
            NameLoc = $"{id}-name",
            DescriptionLoc = $"{id}-description",
            DefaultVisibility = IntentionsPrototypeConstants.Visible,
        };
        SetId(intention, id);
        return intention;
    }

    private static ScenarioEntry OwnerEntry(List<PredicateDefinition>? predicates = null)
    {
        return new ScenarioEntry
        {
            SlotId = "owner",
            Kind = IntentionsPrototypeConstants.Primary,
            IntentionId = "primary",
            Required = true,
            CandidatePredicates = predicates ?? [],
        };
    }

    private static ScenarioEntry SecondaryEntry(
        string slotId,
        bool required = true,
        string? bindToSlot = null)
    {
        return new ScenarioEntry
        {
            SlotId = slotId,
            Kind = IntentionsPrototypeConstants.Secondary,
            IntentionId = "secondary",
            Required = required,
            BindToSlot = bindToSlot,
        };
    }

    private static IntentionsSnapshot Snapshot(params CandidateFacts[] candidates)
    {
        var result = IntentionsSnapshotFactory.Build(
            IntentionsSnapshotRequest.Refill(7),
            "extended",
            TimeSpan.FromMinutes(17),
            "Test Station",
            [],
            candidates,
            TimeSpan.FromMinutes(17),
            "admin-runtime-snapshot");

        Assert.That(result.IsSuccess, Is.True);
        return result.Snapshot!;
    }

    private static CandidateFacts Candidate(
        int mindId,
        int userId,
        string name,
        string job = "Assistant",
        string department = "Civilian")
    {
        return new CandidateFacts(
            new EntityUid(mindId),
            new NetUserId(Guid.Parse($"00000000-0000-0000-0000-{userId:000000000000}")),
            new EntityUid(1000 + mindId),
            name,
            job,
            department,
            30,
            "Human",
            "Male");
    }

    private static void SetId<T>(T prototype, string id)
    {
        typeof(T).GetProperty("ID", BindingFlags.Instance | BindingFlags.Public)!.SetValue(prototype, id);
    }
}
