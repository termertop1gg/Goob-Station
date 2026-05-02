using System;
using Content.Server.Intentions.Runtime;
using Content.Shared.Intentions.Runtime;
using NUnit.Framework;

#nullable enable

namespace Content.Tests.Server.Intentions;

[TestFixture]
[TestOf(typeof(IntentionsRevealService))]
/// <summary>
/// Covers timer-based reveal processing for hidden runtime intentions.
/// </summary>
public sealed class IntentionsRevealServiceTests
{
    [Test]
    public void TimerRevealChangesVisibilityAtDueTime()
    {
        var fixture = IntentionsLifecycleServiceTests.Fixture(hiddenTimer: true);
        var result = new IntentionsRevealService().EvaluateTimerReveals(fixture.Registry, TimeSpan.FromMinutes(20));
        var intention = fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid];

        Assert.That(result.RevealedIntentions, Has.Count.EqualTo(1));
        Assert.That(intention.IsHidden, Is.False);
        Assert.That(fixture.Registry.HiddenIntentionsByRevealTime, Is.Empty);
    }

    [Test]
    public void TimerRevealBeforeDueTimeDoesNothing()
    {
        var fixture = IntentionsLifecycleServiceTests.Fixture(hiddenTimer: true);
        var result = new IntentionsRevealService().EvaluateTimerReveals(fixture.Registry, TimeSpan.FromMinutes(19));
        var intention = fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid];

        Assert.That(result.RevealedIntentions, Is.Empty);
        Assert.That(intention.IsHidden, Is.True);
        Assert.That(fixture.Registry.HiddenIntentionsByRevealTime, Is.Not.Empty);
    }

    [Test]
    public void NoneRevealDoesNotRevealAutomatically()
    {
        var fixture = IntentionsLifecycleServiceTests.Fixture(hiddenTimer: false);
        var result = new IntentionsRevealService().EvaluateTimerReveals(fixture.Registry, TimeSpan.FromHours(1));

        Assert.That(result.RevealedIntentions, Is.Empty);
    }

    [Test]
    public void CancelledIntentionDoesNotRevealAndCleansIndex()
    {
        var fixture = IntentionsLifecycleServiceTests.Fixture(hiddenTimer: true);
        var intention = fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid];
        fixture.Registry.ReplaceIntention(intention.WithStatus(IntentionRuntimeStatus.Cancelled, TimeSpan.FromMinutes(10), "cancelled"));

        var result = new IntentionsRevealService().EvaluateTimerReveals(fixture.Registry, TimeSpan.FromMinutes(20));
        var cancelled = fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid];

        Assert.That(result.RevealedIntentions, Is.Empty);
        Assert.That(cancelled.Status, Is.EqualTo(IntentionRuntimeStatus.Cancelled));
        Assert.That(cancelled.IsHidden, Is.True);
        Assert.That(fixture.Registry.HiddenIntentionsByRevealTime, Is.Empty);
    }

    [Test]
    public void RepeatedRevealIsIdempotent()
    {
        var fixture = IntentionsLifecycleServiceTests.Fixture(hiddenTimer: true);
        var service = new IntentionsRevealService();

        var first = service.EvaluateTimerReveals(fixture.Registry, TimeSpan.FromMinutes(20));
        var second = service.EvaluateTimerReveals(fixture.Registry, TimeSpan.FromMinutes(21));

        Assert.That(first.RevealedIntentions, Has.Count.EqualTo(1));
        Assert.That(second.RevealedIntentions, Is.Empty);
        Assert.That(fixture.Registry.IntentionByUid[fixture.OwnerIntentionUid].IsHidden, Is.False);
    }
}
