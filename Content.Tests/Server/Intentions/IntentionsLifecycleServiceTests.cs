using System;
using System.Collections.Generic;
using System.Linq;
using Content.Server.Intentions.Runtime;
using Content.Shared.Intentions.Prototypes;
using Content.Shared.Intentions.Runtime;
using NUnit.Framework;
using Robust.Shared.GameObjects;

#nullable enable

namespace Content.Tests.Server.Intentions;

[TestFixture]
[TestOf(typeof(IntentionsLifecycleService))]
/// <summary>
/// Covers direct lifecycle service transitions such as missing, frozen, restored, and cancelled scenarios.
/// </summary>
public sealed class IntentionsLifecycleServiceTests
{
    [Test]
    public void OwnerMissingMarksOwnerSlotMissingWithoutFreezingScenario()
    {
        var fixture = Fixture();
        var result = new IntentionsLifecycleService().MarkOwnerMissing(
            fixture.Registry,
            fixture.ScenarioUid,
            TimeSpan.FromMinutes(10),
            "owner-left");

        var scenario = fixture.Registry.ScenarioByUid[fixture.ScenarioUid];
        var ownerSlot = fixture.Registry.SlotAssignmentByScenarioAndSlot[(fixture.ScenarioUid, "owner")];

        Assert.That(result.IsSuccess, Is.True, result.FailureReason);
        Assert.That(scenario.Status, Is.EqualTo(ScenarioRuntimeStatus.Active));
        Assert.That(ownerSlot.Status, Is.EqualTo(ScenarioSlotAssignmentStatus.Missing));
        Assert.That(scenario.SlotAssignments.Single().Status, Is.EqualTo(ScenarioSlotAssignmentStatus.Missing));
        Assert.That(fixture.Registry.MissingOwnerScenarioIds, Does.Contain(fixture.ScenarioUid));
    }

    [Test]
    public void FreezeScenarioWithMissingOwnerFreezesScenarioAndMarksOwnerSlotMissing()
    {
        var fixture = Fixture();
        var result = new IntentionsLifecycleService().FreezeScenarioWithMissingOwner(
            fixture.Registry,
            fixture.ScenarioUid,
            TimeSpan.FromMinutes(10),
            "owner-left-at-refill");

        var scenario = fixture.Registry.ScenarioByUid[fixture.ScenarioUid];
        var ownerSlot = fixture.Registry.SlotAssignmentByScenarioAndSlot[(fixture.ScenarioUid, "owner")];

        Assert.That(result.IsSuccess, Is.True, result.FailureReason);
        Assert.That(scenario.Status, Is.EqualTo(ScenarioRuntimeStatus.Frozen));
        Assert.That(ownerSlot.Status, Is.EqualTo(ScenarioSlotAssignmentStatus.Missing));
        Assert.That(scenario.SlotAssignments.Single().Status, Is.EqualTo(ScenarioSlotAssignmentStatus.Missing));
        Assert.That(fixture.Registry.MissingOwnerScenarioIds, Does.Contain(fixture.ScenarioUid));
    }

    [Test]
    public void OwnerReturnsRestoresScenarioAndUpdatesOwnerEntity()
    {
        var fixture = Fixture();
        var service = new IntentionsLifecycleService();
        var newOwnerEntity = new EntityUid(2001);

        service.MarkOwnerMissing(fixture.Registry, fixture.ScenarioUid, TimeSpan.FromMinutes(10), "owner-left");
        var result = service.RestoreOwner(
            fixture.Registry,
            fixture.ScenarioUid,
            newOwnerEntity,
            TimeSpan.FromMinutes(12),
            "owner-returned");

        var scenario = fixture.Registry.ScenarioByUid[fixture.ScenarioUid];
        var ownerSlot = fixture.Registry.SlotAssignmentByScenarioAndSlot[(fixture.ScenarioUid, "owner")];
        var ownerIntention = fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid];

        Assert.That(result.IsSuccess, Is.True, result.FailureReason);
        Assert.That(scenario.Status, Is.EqualTo(ScenarioRuntimeStatus.Active));
        Assert.That(scenario.OwnerEntityUid, Is.EqualTo(newOwnerEntity));
        Assert.That(ownerSlot.Status, Is.EqualTo(ScenarioSlotAssignmentStatus.Assigned));
        Assert.That(ownerSlot.OwnerEntityUid, Is.EqualTo(newOwnerEntity));
        Assert.That(ownerIntention.OwnerEntityUid, Is.EqualTo(newOwnerEntity));
        Assert.That(fixture.Registry.MissingOwnerScenarioIds, Does.Not.Contain(fixture.ScenarioUid));
    }

    [Test]
    public void PermanentMissingCancelsScenarioAndIntentions()
    {
        var fixture = Fixture(hiddenTimer: true);
        var result = new IntentionsLifecycleService().CancelScenario(
            fixture.Registry,
            fixture.ScenarioUid,
            TimeSpan.FromMinutes(20),
            "owner-dead");

        Assert.That(result.IsSuccess, Is.True, result.FailureReason);
        Assert.That(fixture.Registry.ScenarioByUid[fixture.ScenarioUid].Status, Is.EqualTo(ScenarioRuntimeStatus.Cancelled));
        Assert.That(fixture.Registry.SlotAssignmentByScenarioAndSlot[(fixture.ScenarioUid, "owner")].Status, Is.EqualTo(ScenarioSlotAssignmentStatus.Invalidated));
        Assert.That(fixture.Registry.ScenarioByUid[fixture.ScenarioUid].SlotAssignments.Single().Status, Is.EqualTo(ScenarioSlotAssignmentStatus.Invalidated));
        Assert.That(fixture.Registry.IntentionByUid.Values.All(intention => intention.Status == IntentionRuntimeStatus.Cancelled), Is.True);
        Assert.That(fixture.Registry.IntentionByUid.Values.All(intention => intention.StatusChangedAtRoundTime == TimeSpan.FromMinutes(20)), Is.True);
        Assert.That(fixture.Registry.HiddenIntentionsByRevealTime, Is.Empty);
    }

    [Test]
    public void CancelFreesPrimaryCounterButKeepsAssignedScenarioId()
    {
        var fixture = Fixture();

        new IntentionsLifecycleService().CancelScenario(
            fixture.Registry,
            fixture.ScenarioUid,
            TimeSpan.FromMinutes(20),
            "owner-dead");

        Assert.That(fixture.Registry.AssignedPrimaryByMind, Is.Empty);
        Assert.That(fixture.Registry.AssignedScenarioIds, Does.Contain("scenario"));
    }

    [Test]
    public void RepeatedMissingRestoreAndCancelAreIdempotent()
    {
        var fixture = Fixture();
        var service = new IntentionsLifecycleService();
        var newOwnerEntity = new EntityUid(2001);

        service.MarkOwnerMissing(fixture.Registry, fixture.ScenarioUid, TimeSpan.FromMinutes(10), "owner-left");
        service.MarkOwnerMissing(fixture.Registry, fixture.ScenarioUid, TimeSpan.FromMinutes(11), "owner-left-again");
        service.RestoreOwner(fixture.Registry, fixture.ScenarioUid, newOwnerEntity, TimeSpan.FromMinutes(12), "owner-returned");
        service.RestoreOwner(fixture.Registry, fixture.ScenarioUid, newOwnerEntity, TimeSpan.FromMinutes(13), "owner-returned-again");
        service.CancelScenario(fixture.Registry, fixture.ScenarioUid, TimeSpan.FromMinutes(20), "owner-dead");
        var secondCancel = service.CancelScenario(fixture.Registry, fixture.ScenarioUid, TimeSpan.FromMinutes(21), "owner-dead-again");

        Assert.That(secondCancel.IsSuccess, Is.True, secondCancel.FailureReason);
        Assert.That(fixture.Registry.ScenarioByUid[fixture.ScenarioUid].Status, Is.EqualTo(ScenarioRuntimeStatus.Cancelled));
        Assert.That(fixture.Registry.AssignedPrimaryByMind, Is.Empty);
        Assert.That(fixture.Registry.MissingOwnerScenarioIds, Is.Empty);
    }

    [Test]
    public void ReconcileMindMarksOwnerSlotMissingWhenOwnerBecomesPermanentlyUnavailable()
    {
        var fixture = Fixture();
        var service = new IntentionsLifecycleService();

        service.ReconcileMind(
            fixture.Registry,
            fixture.OwnerMindId,
            mind => OwnerAvailabilityResult.TemporarilyMissing(mind, "owner-left"),
            TimeSpan.FromMinutes(10));
        service.ReconcileMind(
            fixture.Registry,
            fixture.OwnerMindId,
            mind => OwnerAvailabilityResult.PermanentlyUnavailable(mind, "owner-dead"),
            TimeSpan.FromMinutes(20));

        Assert.That(fixture.Registry.ScenarioByUid[fixture.ScenarioUid].Status, Is.EqualTo(ScenarioRuntimeStatus.Active));
        Assert.That(fixture.Registry.SlotAssignmentByScenarioAndSlot[(fixture.ScenarioUid, "owner")].Status, Is.EqualTo(ScenarioSlotAssignmentStatus.Missing));
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].Status, Is.EqualTo(IntentionRuntimeStatus.Active));
    }

    [Test]
    public void ReconcileRuntimeStateMarksOwnerSlotMissingWithoutFreezingWhenOwnerStillTemporarilyMissing()
    {
        var fixture = Fixture();
        var service = new IntentionsLifecycleService();

        service.ReconcileRuntimeState(
            fixture.Registry,
            mind => OwnerAvailabilityResult.TemporarilyMissing(mind, "owner-still-away"),
            TimeSpan.FromMinutes(20));

        Assert.That(fixture.Registry.ScenarioByUid[fixture.ScenarioUid].Status, Is.EqualTo(ScenarioRuntimeStatus.Active));
        Assert.That(fixture.Registry.SlotAssignmentByScenarioAndSlot[(fixture.ScenarioUid, "owner")].Status, Is.EqualTo(ScenarioSlotAssignmentStatus.Missing));
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].Status, Is.EqualTo(IntentionRuntimeStatus.Active));
    }

    [Test]
    public void ReconcileRuntimeStateMarksOwnerSlotMissingWithoutFreezingWhenOwnerIsPermanentlyUnavailable()
    {
        var fixture = Fixture();
        var service = new IntentionsLifecycleService();

        service.ReconcileRuntimeState(
            fixture.Registry,
            mind => OwnerAvailabilityResult.PermanentlyUnavailable(mind, "owner-dead"),
            TimeSpan.FromMinutes(20));

        Assert.That(fixture.Registry.ScenarioByUid[fixture.ScenarioUid].Status, Is.EqualTo(ScenarioRuntimeStatus.Active));
        Assert.That(fixture.Registry.SlotAssignmentByScenarioAndSlot[(fixture.ScenarioUid, "owner")].Status, Is.EqualTo(ScenarioSlotAssignmentStatus.Missing));
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].Status, Is.EqualTo(IntentionRuntimeStatus.Active));
    }

    [Test]
    public void ReconcileBeforeRefillCancelsFrozenScenarioWhenOwnerStillTemporarilyMissing()
    {
        var fixture = Fixture();
        var service = new IntentionsLifecycleService();

        service.FreezeScenarioWithMissingOwner(fixture.Registry, fixture.ScenarioUid, TimeSpan.FromMinutes(10), "owner-left-at-refill");
        service.ReconcileBeforeRefill(
            fixture.Registry,
            mind => OwnerAvailabilityResult.TemporarilyMissing(mind, "owner-still-away-at-refill"),
            TimeSpan.FromMinutes(20));

        Assert.That(fixture.Registry.ScenarioByUid[fixture.ScenarioUid].Status, Is.EqualTo(ScenarioRuntimeStatus.Cancelled));
        Assert.That(fixture.Registry.SlotAssignmentByScenarioAndSlot[(fixture.ScenarioUid, "owner")].Status, Is.EqualTo(ScenarioSlotAssignmentStatus.Invalidated));
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].Status, Is.EqualTo(IntentionRuntimeStatus.Cancelled));
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].StatusChangedAtRoundTime, Is.EqualTo(TimeSpan.FromMinutes(20)));
    }

    [Test]
    public void ReconcileBeforeRefillCancelsFrozenScenarioWhenOwnerIsPermanentlyUnavailable()
    {
        var fixture = Fixture();
        var service = new IntentionsLifecycleService();

        service.FreezeScenarioWithMissingOwner(fixture.Registry, fixture.ScenarioUid, TimeSpan.FromMinutes(10), "owner-left-at-refill");
        service.ReconcileBeforeRefill(
            fixture.Registry,
            mind => OwnerAvailabilityResult.PermanentlyUnavailable(mind, "owner-dead-at-refill"),
            TimeSpan.FromMinutes(20));

        Assert.That(fixture.Registry.ScenarioByUid[fixture.ScenarioUid].Status, Is.EqualTo(ScenarioRuntimeStatus.Cancelled));
        Assert.That(fixture.Registry.SlotAssignmentByScenarioAndSlot[(fixture.ScenarioUid, "owner")].Status, Is.EqualTo(ScenarioSlotAssignmentStatus.Invalidated));
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].Status, Is.EqualTo(IntentionRuntimeStatus.Cancelled));
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].StatusChangedAtRoundTime, Is.EqualTo(TimeSpan.FromMinutes(20)));
    }

    [Test]
    public void ReconcileBeforeRefillFreezesNonFrozenScenarioWithMissingOwnerSlot()
    {
        var fixture = Fixture();
        var service = new IntentionsLifecycleService();
        service.MarkOwnerMissing(fixture.Registry, fixture.ScenarioUid, TimeSpan.FromMinutes(10), "owner-left");

        service.ReconcileBeforeRefill(
            fixture.Registry,
            mind => OwnerAvailabilityResult.TemporarilyMissing(mind, "owner-still-away-at-refill"),
            TimeSpan.FromMinutes(20));

        Assert.That(fixture.Registry.ScenarioByUid[fixture.ScenarioUid].Status, Is.EqualTo(ScenarioRuntimeStatus.Frozen));
        Assert.That(fixture.Registry.SlotAssignmentByScenarioAndSlot[(fixture.ScenarioUid, "owner")].Status, Is.EqualTo(ScenarioSlotAssignmentStatus.Missing));
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].Status, Is.EqualTo(IntentionRuntimeStatus.Active));
    }

    internal static RuntimeFixture Fixture(
        string scenarioTemplateId = "scenario",
        string categoryId = "social",
        EntityUid? ownerMindId = null,
        EntityUid? ownerEntityUid = null,
        ScenarioRuntimeStatus status = ScenarioRuntimeStatus.Active,
        bool hiddenTimer = false,
        IntentionsRuntimeRegistry? registry = null)
    {
        registry ??= new IntentionsRuntimeRegistry();
        var scenarioUid = registry.NextScenarioUid();
        var intentionUid = registry.NextIntentionUid();
        var mindId = ownerMindId ?? new EntityUid(1);
        var entityUid = ownerEntityUid ?? new EntityUid(1001);
        var now = TimeSpan.FromMinutes(5);
        var revealTime = hiddenTimer ? now + TimeSpan.FromMinutes(15) : (TimeSpan?) null;

        var assignment = new ScenarioSlotAssignment(
            scenarioUid,
            "owner",
            IntentionsPrototypeConstants.Primary,
            mindId,
            entityUid,
            ScenarioSlotAssignmentStatus.Assigned,
            intentionUid,
            required: true,
            wasBound: false,
            boundToSlotId: null,
            now);
        var scenario = new ScenarioInstance(
            scenarioUid,
            scenarioTemplateId,
            categoryId,
            status,
            "owner",
            mindId,
            entityUid,
            waveId: 1,
            now,
            [assignment]);
        var intention = new IntentionInstance(
            intentionUid,
            "primary",
            scenarioUid,
            "owner",
            mindId,
            entityUid,
            IntentionsPrototypeConstants.Primary,
            IntentionRuntimeStatus.Active,
            now,
            now,
            isHidden: hiddenTimer,
            hiddenTimer ? IntentionRevealMode.Timer : IntentionRevealMode.None,
            revealTime,
            new Dictionary<string, string>(),
            copyableTextResolved: null);

        registry.AddScenario(scenario);
        registry.AddIntention(intention);
        registry.AttachIntentionToMind(mindId, intentionUid);
        registry.AddScenarioBackReference(intentionUid, scenarioUid);
        registry.AddSlotAssignment(assignment);
        registry.AddAssignedScenarioId(scenarioTemplateId);

        if (status != ScenarioRuntimeStatus.Cancelled)
            registry.IncrementAssignedPrimary(mindId, categoryId);

        if (hiddenTimer && revealTime is { } actualRevealTime)
            registry.AddHiddenReveal(actualRevealTime, intentionUid);

        return new RuntimeFixture(registry, scenarioUid, intentionUid, mindId, entityUid);
    }

    internal sealed record RuntimeFixture(
        IntentionsRuntimeRegistry Registry,
        ScenarioInstanceUid ScenarioUid,
        IntentionInstanceUid OwnerIntentionUid,
        EntityUid OwnerMindId,
        EntityUid OwnerEntityUid);
}
