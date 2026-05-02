using System;
using System.Linq;
using Content.Server.Intentions.Runtime;
using Content.Server.Intentions.Waves;
using Content.Shared.Intentions.Runtime;
using Content.Shared.Intentions.Snapshot;
using Content.Shared.Intentions.Waves;
using NUnit.Framework;
using Robust.Shared.GameObjects;

#nullable enable

namespace Content.Tests.Server.Intentions;

[TestFixture]
[TestOf(typeof(IntentionsLifecycleService))]
[TestOf(typeof(IntentionsWaveOrchestrator))]
/// <summary>
/// Covers end-to-end lifecycle and refill interactions across multiple waves.
/// </summary>
public sealed class IntentionsLifecycleRefillIntegrationTests
{
    [Test]
    public void MissingFrozenCancelledFlow()
    {
        var fixture = IntentionsLifecycleServiceTests.Fixture();
        var service = new IntentionsLifecycleService();

        service.ReconcileMind(
            fixture.Registry,
            fixture.OwnerMindId,
            mind => OwnerAvailabilityResult.TemporarilyMissing(mind, "owner-left"),
            TimeSpan.FromMinutes(10));

        Assert.That(fixture.Registry.ScenarioByUid[fixture.ScenarioUid].Status, Is.EqualTo(ScenarioRuntimeStatus.Active));
        Assert.That(fixture.Registry.SlotAssignmentByScenarioAndSlot[(fixture.ScenarioUid, "owner")].Status, Is.EqualTo(ScenarioSlotAssignmentStatus.Missing));

        service.ReconcileBeforeRefill(
            fixture.Registry,
            mind => OwnerAvailabilityResult.TemporarilyMissing(mind, "owner-still-away-at-first-refill"),
            TimeSpan.FromMinutes(15));

        Assert.That(fixture.Registry.ScenarioByUid[fixture.ScenarioUid].Status, Is.EqualTo(ScenarioRuntimeStatus.Frozen));
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].Status, Is.EqualTo(IntentionRuntimeStatus.Active));

        service.ReconcileMind(
            fixture.Registry,
            fixture.OwnerMindId,
            mind => OwnerAvailabilityResult.PermanentlyUnavailable(mind, "owner-dead"),
            TimeSpan.FromMinutes(20));

        Assert.That(fixture.Registry.ScenarioByUid[fixture.ScenarioUid].Status, Is.EqualTo(ScenarioRuntimeStatus.Frozen));
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].Status, Is.EqualTo(IntentionRuntimeStatus.Active));

        service.ReconcileBeforeRefill(
            fixture.Registry,
            mind => OwnerAvailabilityResult.PermanentlyUnavailable(mind, "owner-dead-at-refill"),
            TimeSpan.FromMinutes(30));

        Assert.That(fixture.Registry.ScenarioByUid[fixture.ScenarioUid].Status, Is.EqualTo(ScenarioRuntimeStatus.Cancelled));
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].Status, Is.EqualTo(IntentionRuntimeStatus.Cancelled));
    }

    [Test]
    public void FrozenScenarioReturnsActive()
    {
        var fixture = IntentionsLifecycleServiceTests.Fixture();
        var service = new IntentionsLifecycleService();
        var newOwnerEntity = new EntityUid(2001);

        service.ReconcileMind(
            fixture.Registry,
            fixture.OwnerMindId,
            mind => OwnerAvailabilityResult.TemporarilyMissing(mind, "owner-left"),
            TimeSpan.FromMinutes(10));
        service.ReconcileMind(
            fixture.Registry,
            fixture.OwnerMindId,
            mind => OwnerAvailabilityResult.Available(mind, newOwnerEntity, "owner-returned"),
            TimeSpan.FromMinutes(12));

        Assert.That(fixture.Registry.ScenarioByUid[fixture.ScenarioUid].Status, Is.EqualTo(ScenarioRuntimeStatus.Active));
        Assert.That(fixture.Registry.ScenarioByUid[fixture.ScenarioUid].OwnerEntityUid, Is.EqualTo(newOwnerEntity));
        Assert.That(fixture.Registry.MissingOwnerScenarioIds, Is.Empty);
    }

    [Test]
    public void RefillCommitsNewScenarioOnlyForDeficit()
    {
        var registry = new IntentionsRuntimeRegistry();
        IntentionsLifecycleServiceTests.Fixture("existing", registry: registry, ownerMindId: new EntityUid(1));
        var catalog = IntentionsRefillWaveTests.Catalog(
            [IntentionsRefillWaveTests.Category("social", quota: 2, maxPrimary: 5)],
            [IntentionsRefillWaveTests.Scenario("existing", "social"), IntentionsRefillWaveTests.Scenario("new", "social")],
            [IntentionsRefillWaveTests.Intention("primary", "primary")]);

        var result = RunRefillCommitted(catalog, registry, [IntentionsRefillWaveTests.Candidate(2, 2)]);

        Assert.That(result.IsSuccess, Is.True, result.FailureReason);
        Assert.That(result.Context.SuccessfulBuilds, Has.Count.EqualTo(1));
        Assert.That(result.Context.SuccessfulBuilds.Single().ScenarioTemplateId, Is.EqualTo("new"));
        Assert.That(registry.ScenarioByUid, Has.Count.EqualTo(2));
    }

    [Test]
    public void RefillDoesNotDuplicateScenarioTemplates()
    {
        var registry = new IntentionsRuntimeRegistry();
        IntentionsLifecycleServiceTests.Fixture("existing", registry: registry, ownerMindId: new EntityUid(1));
        var catalog = IntentionsRefillWaveTests.Catalog(
            [IntentionsRefillWaveTests.Category("social", quota: 2, maxPrimary: 5)],
            [IntentionsRefillWaveTests.Scenario("existing", "social")],
            [IntentionsRefillWaveTests.Intention("primary", "primary")]);

        var result = RunRefillCommitted(catalog, registry, [IntentionsRefillWaveTests.Candidate(2, 2)]);

        Assert.That(result.IsSuccess, Is.True, result.FailureReason);
        Assert.That(result.Context.SuccessfulBuilds, Is.Empty);
        Assert.That(registry.ScenarioByUid, Has.Count.EqualTo(1));
    }

    [Test]
    public void CancelledScenarioFreesQuotaButNotScenarioTemplateId()
    {
        var registry = new IntentionsRuntimeRegistry();
        var existing = IntentionsLifecycleServiceTests.Fixture("existing", registry: registry, ownerMindId: new EntityUid(1));
        new IntentionsLifecycleService().CancelScenario(registry, existing.ScenarioUid, TimeSpan.FromMinutes(10), "owner-dead");
        var catalog = IntentionsRefillWaveTests.Catalog(
            [IntentionsRefillWaveTests.Category("social", quota: 1, maxPrimary: 5)],
            [IntentionsRefillWaveTests.Scenario("existing", "social"), IntentionsRefillWaveTests.Scenario("replacement", "social")],
            [IntentionsRefillWaveTests.Intention("primary", "primary")]);

        var result = RunRefillCommitted(catalog, registry, [IntentionsRefillWaveTests.Candidate(2, 2)]);

        Assert.That(result.IsSuccess, Is.True, result.FailureReason);
        Assert.That(result.Context.SuccessfulBuilds.Single().ScenarioTemplateId, Is.EqualTo("replacement"));
        Assert.That(registry.AssignedScenarioIds, Does.Contain("existing"));
        Assert.That(registry.AssignedScenarioIds, Does.Contain("replacement"));
    }

    [Test]
    public void MissingOwnerScenarioSurvivesFirstRefillThenCancelledFrozenFreesQuotaOnNextRefill()
    {
        var registry = new IntentionsRuntimeRegistry();
        var existing = IntentionsLifecycleServiceTests.Fixture("existing", registry: registry, ownerMindId: new EntityUid(1));
        var lifecycle = new IntentionsLifecycleService();
        lifecycle.MarkOwnerMissing(registry, existing.ScenarioUid, TimeSpan.FromMinutes(10), "owner-left");
        var catalog = IntentionsRefillWaveTests.Catalog(
            [IntentionsRefillWaveTests.Category("social", quota: 1, maxPrimary: 5)],
            [IntentionsRefillWaveTests.Scenario("existing", "social"), IntentionsRefillWaveTests.Scenario("replacement", "social")],
            [IntentionsRefillWaveTests.Intention("primary", "primary")]);

        lifecycle.ReconcileBeforeRefill(
            registry,
            mind => OwnerAvailabilityResult.TemporarilyMissing(mind, "owner-still-away-at-first-refill"),
            TimeSpan.FromMinutes(20));
        var firstRefill = RunRefillCommitted(catalog, registry, [IntentionsRefillWaveTests.Candidate(2, 2)], waveId: 2);

        Assert.That(registry.ScenarioByUid[existing.ScenarioUid].Status, Is.EqualTo(ScenarioRuntimeStatus.Frozen));
        Assert.That(firstRefill.IsSuccess, Is.True, firstRefill.FailureReason);
        Assert.That(firstRefill.Context.SuccessfulBuilds, Is.Empty);

        lifecycle.ReconcileBeforeRefill(
            registry,
            mind => OwnerAvailabilityResult.TemporarilyMissing(mind, "owner-still-away-at-second-refill"),
            TimeSpan.FromMinutes(40));
        var secondRefill = RunRefillCommitted(catalog, registry, [IntentionsRefillWaveTests.Candidate(2, 2)], waveId: 3);

        Assert.That(registry.ScenarioByUid[existing.ScenarioUid].Status, Is.EqualTo(ScenarioRuntimeStatus.Cancelled));
        Assert.That(registry.SlotAssignmentByScenarioAndSlot[(existing.ScenarioUid, "owner")].Status, Is.EqualTo(ScenarioSlotAssignmentStatus.Invalidated));
        Assert.That(secondRefill.IsSuccess, Is.True, secondRefill.FailureReason);
        Assert.That(secondRefill.Context.SuccessfulBuilds.Single().ScenarioTemplateId, Is.EqualTo("replacement"));
    }

    [Test]
    public void FrozenPrimaryBlocksCategoryLimitButCancelledPrimaryDoesNot()
    {
        var catalog = IntentionsRefillWaveTests.Catalog(
            [IntentionsRefillWaveTests.Category("social", quota: 2, maxPrimary: 1)],
            [IntentionsRefillWaveTests.Scenario("first", "social"), IntentionsRefillWaveTests.Scenario("second", "social")],
            [IntentionsRefillWaveTests.Intention("primary", "primary")]);
        var registry = new IntentionsRuntimeRegistry();
        var fixture = IntentionsLifecycleServiceTests.Fixture("first", registry: registry, ownerMindId: new EntityUid(1));
        var lifecycle = new IntentionsLifecycleService();

        lifecycle.FreezeScenarioWithMissingOwner(registry, fixture.ScenarioUid, TimeSpan.FromMinutes(10), "owner-left-at-refill");

        var blocked = RunRefillCommitted(catalog, registry, [IntentionsRefillWaveTests.Candidate(1, 1)], waveId: 2);
        Assert.That(blocked.Context.SuccessfulBuilds, Is.Empty);
        Assert.That(blocked.Context.RejectReasons.Any(reason => reason.Code == "no-candidate-for-required-slot"), Is.True);

        lifecycle.CancelScenario(registry, fixture.ScenarioUid, TimeSpan.FromMinutes(20), "owner-dead");
        var unblocked = RunRefillCommitted(catalog, registry, [IntentionsRefillWaveTests.Candidate(1, 1)], waveId: 3);

        Assert.That(unblocked.Context.SuccessfulBuilds.Single().ScenarioTemplateId, Is.EqualTo("second"));
    }

    private static StartWaveResult RunRefillCommitted(
        Content.Shared.Intentions.Validation.ValidationCatalog catalog,
        IntentionsRuntimeRegistry registry,
        System.Collections.Generic.IReadOnlyList<CandidateFacts> candidates,
        int waveId = 2)
    {
        return new IntentionsWaveOrchestrator().RunRefillWaveAndCommit(
            catalog,
            IntentionsRefillWaveTests.Snapshot(IntentionsSnapshotRequest.Refill(waveId), candidates),
            new IntentionsRefillWaveRequest(waveId, seed: 1),
            registry,
            new IntentionsCommitService());
    }
}
