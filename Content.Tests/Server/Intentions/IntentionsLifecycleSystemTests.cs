using System;
using System.Collections.Generic;
using Content.Server.Intentions.Runtime;
using Content.Shared.Intentions.Prototypes;
using Content.Shared.Intentions.Runtime;
using NUnit.Framework;
using Robust.Shared.GameObjects;

namespace Content.Tests.Server.Intentions;

[TestFixture]
[TestOf(typeof(IntentionsLifecycleSystem))]
/// <summary>
/// Covers lifecycle-system helpers that collect affected minds after runtime-only lifecycle changes.
/// </summary>
public sealed class IntentionsLifecycleSystemTests
{
    [Test]
    public void CollectAffectedMindIdsReturnsAllParticipantsForSuccessfulScenario()
    {
        var fixture = FixtureWithSecondaryParticipant();

        var affectedMindIds = IntentionsLifecycleSystem.CollectAffectedMindIds(
            [LifecycleOperationResult.Success(fixture.ScenarioUid)],
            fixture.Registry);

        Assert.That(affectedMindIds, Is.EquivalentTo(new[] { fixture.OwnerMindId, fixture.LinkedMindId }));
    }

    [Test]
    public void CollectAffectedMindIdsSkipsFailedLifecycleResults()
    {
        var fixture = FixtureWithSecondaryParticipant();

        var affectedMindIds = IntentionsLifecycleSystem.CollectAffectedMindIds(
            [LifecycleOperationResult.Failure("owner-slot-not-found", fixture.ScenarioUid)],
            fixture.Registry);

        Assert.That(affectedMindIds, Is.Empty);
    }

    [Test]
    public void CollectAffectedMindIdsStillFindsParticipantsAfterScenarioCancellation()
    {
        var fixture = FixtureWithSecondaryParticipant();
        var lifecycle = new IntentionsLifecycleService();

        lifecycle.CancelScenario(fixture.Registry, fixture.ScenarioUid, TimeSpan.FromMinutes(20), "owner-dead");

        var affectedMindIds = IntentionsLifecycleSystem.CollectAffectedMindIds(
            [LifecycleOperationResult.Success(fixture.ScenarioUid)],
            fixture.Registry);

        Assert.That(affectedMindIds, Is.EquivalentTo(new[] { fixture.OwnerMindId, fixture.LinkedMindId }));
    }

    private static LifecycleRefreshFixture FixtureWithSecondaryParticipant()
    {
        var baseFixture = IntentionsLifecycleServiceTests.Fixture();
        var linkedMindId = new EntityUid(2);
        var linkedEntityUid = new EntityUid(2002);
        var linkedIntentionUid = baseFixture.Registry.NextIntentionUid();
        var assignedAt = TimeSpan.FromMinutes(5);

        var linkedAssignment = new ScenarioSlotAssignment(
            baseFixture.ScenarioUid,
            "assistant",
            IntentionsPrototypeConstants.Secondary,
            linkedMindId,
            linkedEntityUid,
            ScenarioSlotAssignmentStatus.Assigned,
            linkedIntentionUid,
            required: true,
            wasBound: false,
            boundToSlotId: null,
            assignedAt);

        var linkedIntention = new IntentionInstance(
            linkedIntentionUid,
            "secondary",
            baseFixture.ScenarioUid,
            "assistant",
            linkedMindId,
            linkedEntityUid,
            IntentionsPrototypeConstants.Secondary,
            IntentionRuntimeStatus.Active,
            assignedAt,
            assignedAt,
            isHidden: false,
            IntentionRevealMode.None,
            revealedAtRoundTime: null,
            new Dictionary<string, string>(),
            copyableTextResolved: null);

        baseFixture.Registry.AddSlotAssignment(linkedAssignment);
        baseFixture.Registry.ReplaceScenario(
            baseFixture.Registry.ScenarioByUid[baseFixture.ScenarioUid].WithSlotAssignments(
                [.. baseFixture.Registry.ScenarioByUid[baseFixture.ScenarioUid].SlotAssignments, linkedAssignment]));
        baseFixture.Registry.AddIntention(linkedIntention);
        baseFixture.Registry.AttachIntentionToMind(linkedMindId, linkedIntentionUid);
        baseFixture.Registry.AddScenarioBackReference(linkedIntentionUid, baseFixture.ScenarioUid);

        return new LifecycleRefreshFixture(
            baseFixture.Registry,
            baseFixture.ScenarioUid,
            baseFixture.OwnerMindId,
            linkedMindId);
    }

    private sealed record LifecycleRefreshFixture(
        IntentionsRuntimeRegistry Registry,
        ScenarioInstanceUid ScenarioUid,
        EntityUid OwnerMindId,
        EntityUid LinkedMindId);
}
