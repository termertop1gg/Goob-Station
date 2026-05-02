using System;
using Content.Server.GameTicking;
using Content.Server.Intentions.Waves;
using NUnit.Framework;

namespace Content.Tests.Server.Intentions;

[TestFixture]
[TestOf(typeof(IntentionsDistributionSystem))]
/// <summary>
/// Covers scheduler-aware manual start/refill wave rules in the automatic distribution system.
/// </summary>
public sealed class IntentionsDistributionSystemTests
{
    [Test]
    public void ManualStartRejectsOutsideInRound()
    {
        var status = new IntentionsDistributionScheduleStatus(
            StartWaveFinished: false,
            StartWavePending: false,
            NextWaveId: 1,
            CurrentTime: TimeSpan.Zero,
            NextStartWaveAttempt: null,
            NextRefillWaveAttempt: null);

        var allowed = IntentionsDistributionSystem.CanRunStartManually(status, GameRunLevel.PreRoundLobby, out var message);

        Assert.That(allowed, Is.False);
        Assert.That(message, Does.Contain("InRound"));
    }

    [Test]
    public void ManualStartRejectsAfterStartAlreadyFinished()
    {
        var status = new IntentionsDistributionScheduleStatus(
            StartWaveFinished: true,
            StartWavePending: false,
            NextWaveId: 2,
            CurrentTime: TimeSpan.FromMinutes(5),
            NextStartWaveAttempt: null,
            NextRefillWaveAttempt: TimeSpan.FromMinutes(12));

        var allowed = IntentionsDistributionSystem.CanRunStartManually(status, GameRunLevel.InRound, out var message);

        Assert.That(allowed, Is.False);
        Assert.That(message, Does.Contain("already finished"));
    }

    [Test]
    public void ManualStartAllowsPendingStart()
    {
        var status = new IntentionsDistributionScheduleStatus(
            StartWaveFinished: false,
            StartWavePending: true,
            NextWaveId: 1,
            CurrentTime: TimeSpan.FromSeconds(5),
            NextStartWaveAttempt: TimeSpan.FromSeconds(7),
            NextRefillWaveAttempt: null);

        var allowed = IntentionsDistributionSystem.CanRunStartManually(status, GameRunLevel.InRound, out var message);

        Assert.That(allowed, Is.True);
        Assert.That(message, Does.Contain("pending"));
    }

    [Test]
    public void ManualRefillRejectsBeforeStart()
    {
        var status = new IntentionsDistributionScheduleStatus(
            StartWaveFinished: false,
            StartWavePending: false,
            NextWaveId: 1,
            CurrentTime: TimeSpan.FromMinutes(1),
            NextStartWaveAttempt: null,
            NextRefillWaveAttempt: null);

        var allowed = IntentionsDistributionSystem.CanRunRefillManually(status, GameRunLevel.InRound, out var message);

        Assert.That(allowed, Is.False);
        Assert.That(message, Does.Contain("start wave finishes"));
    }

    [Test]
    public void ManualRefillAllowsWhenScheduled()
    {
        var status = new IntentionsDistributionScheduleStatus(
            StartWaveFinished: true,
            StartWavePending: false,
            NextWaveId: 2,
            CurrentTime: TimeSpan.FromMinutes(10),
            NextStartWaveAttempt: null,
            NextRefillWaveAttempt: TimeSpan.FromMinutes(18));

        var allowed = IntentionsDistributionSystem.CanRunRefillManually(status, GameRunLevel.InRound, out var message);

        Assert.That(allowed, Is.True);
        Assert.That(message, Does.Contain("previous refill timer"));
    }
}
