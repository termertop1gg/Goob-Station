using System;
using Content.Server.Intentions.Runtime;
using Content.Shared.Intentions.Prototypes;
using Content.Shared.Intentions.Runtime;
using Content.Shared.Intentions.Waves;
using NUnit.Framework;
using Robust.Shared.GameObjects;

#nullable enable

namespace Content.Tests.Server.Intentions;

[TestFixture]
[TestOf(typeof(IntentionsRuntimeRegistry))]
/// <summary>
/// Covers runtime registry ids, indexes, and rollback-sensitive bookkeeping.
/// </summary>
public sealed class IntentionsRuntimeRegistryTests
{
    [Test]
    public void UidCountersCreateUniqueIds()
    {
        var registry = new IntentionsRuntimeRegistry();
        var firstScenarioUid = registry.NextScenarioUid();
        var secondScenarioUid = registry.NextScenarioUid();
        var firstIntentionUid = registry.NextIntentionUid();
        var secondIntentionUid = registry.NextIntentionUid();

        Assert.That(firstScenarioUid, Is.Not.EqualTo(secondScenarioUid));
        Assert.That(firstIntentionUid, Is.Not.EqualTo(secondIntentionUid));
    }

    [Test]
    public void MindBackReferenceAndSlotIndexesStayConsistent()
    {
        var registry = new IntentionsRuntimeRegistry();
        var scenarioUid = registry.NextScenarioUid();
        var intentionUid = registry.NextIntentionUid();
        var mindId = new EntityUid(1);
        var assignment = new ScenarioSlotAssignment(
            scenarioUid,
            "owner",
            IntentionsPrototypeConstants.Primary,
            mindId,
            new EntityUid(1001),
            ScenarioSlotAssignmentStatus.Assigned,
            intentionUid,
            required: true,
            wasBound: false,
            boundToSlotId: null,
            TimeSpan.FromMinutes(5));

        registry.AttachIntentionToMind(mindId, intentionUid);
        registry.AddScenarioBackReference(intentionUid, scenarioUid);
        registry.AddSlotAssignment(assignment);

        Assert.That(registry.IntentionIdsByMind[mindId], Does.Contain(intentionUid));
        Assert.That(registry.ScenarioUidByIntentionUid[intentionUid], Is.EqualTo(scenarioUid));
        Assert.That(registry.SlotAssignmentByScenarioAndSlot[(scenarioUid, "owner")], Is.EqualTo(assignment));

        registry.RemoveSlotAssignment(scenarioUid, "owner");
        registry.RemoveScenarioBackReference(intentionUid);
        registry.DetachIntentionFromMind(mindId, intentionUid);

        Assert.That(registry.IntentionIdsByMind, Is.Empty);
        Assert.That(registry.ScenarioUidByIntentionUid, Is.Empty);
        Assert.That(registry.SlotAssignmentByScenarioAndSlot, Is.Empty);
    }

    [Test]
    public void AssignedScenarioAndPrimaryCountersCanRollback()
    {
        var registry = new IntentionsRuntimeRegistry();
        var mindId = new EntityUid(1);

        registry.AddAssignedScenarioId("scenario");
        registry.IncrementAssignedPrimary(mindId, "social");
        registry.RemoveAssignedScenarioId("scenario");
        registry.DecrementAssignedPrimary(mindId, "social");

        Assert.That(registry.AssignedScenarioIds, Is.Empty);
        Assert.That(registry.AssignedPrimaryByMind, Is.Empty);
    }

    [Test]
    public void WaveContextAndRevealIndexesCanRollback()
    {
        var registry = new IntentionsRuntimeRegistry();
        var context = new DistributionWaveContext(1, "snapshot", 10, TimeSpan.FromMinutes(1), 2);
        var intentionUid = registry.NextIntentionUid();
        var revealTime = TimeSpan.FromMinutes(6);

        registry.SetWaveContext(context, out var previous);
        registry.AddHiddenReveal(revealTime, intentionUid);
        registry.RemoveHiddenReveal(revealTime, intentionUid);
        registry.RestoreWaveContext(context.WaveId, previous);

        Assert.That(registry.WaveContextByWaveId, Is.Empty);
        Assert.That(registry.HiddenIntentionsByRevealTime, Is.Empty);
    }
}
