using System.Linq;
using Content.Shared.Intentions.Runtime;
using Robust.Shared.GameObjects;

namespace Content.Server.Intentions.Runtime;

/// <summary>
/// Reveals hidden intention instances whose timer-based reveal moment has arrived.
/// </summary>
public sealed class IntentionsRevealService
{
    /// <summary>
    /// Evaluates due timer reveals, mutates the registry in place, and returns the reveal events that fired.
    /// </summary>
    public IntentionsRevealResult EvaluateTimerReveals(IntentionsRuntimeRegistry registry, TimeSpan now)
    {
        var events = new List<IntentionRevealEvent>();
        var dueTimes = registry.HiddenIntentionsByRevealTime
            .Keys
            .Where(revealTime => revealTime <= now)
            .ToArray();

        foreach (var revealTime in dueTimes)
        {
            if (!registry.HiddenIntentionsByRevealTime.TryGetValue(revealTime, out var indexedIntentions))
                continue;

            foreach (var intentionUid in indexedIntentions.ToArray())
            {
                registry.RemoveHiddenReveal(revealTime, intentionUid);

                if (!registry.IntentionByUid.TryGetValue(intentionUid, out var intention))
                    continue;

                if (intention.RevealMode != IntentionRevealMode.Timer || intention.RevealedAtRoundTime is not { } scheduledReveal)
                    continue;

                if (scheduledReveal > now)
                {
                    registry.AddHiddenReveal(scheduledReveal, intentionUid);
                    continue;
                }

                if (intention.Status != IntentionRuntimeStatus.Active || !intention.IsHidden)
                    continue;

                var revealed = intention.WithRevealed();
                registry.ReplaceIntention(revealed);
                events.Add(new IntentionRevealEvent(intention.Uid, intention.ScenarioUid, intention.OwnerMindId, intention.OwnerEntityUid, scheduledReveal));
            }
        }

        return new IntentionsRevealResult(events);
    }
}

/// <summary>
/// Contains the timer reveals that became visible during one reveal evaluation pass.
/// </summary>
public sealed class IntentionsRevealResult
{
    /// <summary>
    /// Initializes a result wrapper for the reveals emitted during one pass.
    /// </summary>
    public IntentionsRevealResult(IEnumerable<IntentionRevealEvent> revealedIntentions)
    {
        RevealedIntentions = revealedIntentions.ToArray();
    }

    /// <summary>
    /// Lists the intention reveal events produced during the pass.
    /// </summary>
    public IReadOnlyList<IntentionRevealEvent> RevealedIntentions { get; }
    /// <summary>
    /// Indicates whether the pass actually revealed at least one hidden intention.
    /// </summary>
    public bool HasReveals => RevealedIntentions.Count > 0;
}

/// <summary>
/// Describes one runtime intention that became visible because of timer reveal processing.
/// </summary>
public readonly record struct IntentionRevealEvent(
    IntentionInstanceUid IntentionUid,
    ScenarioInstanceUid ScenarioUid,
    EntityUid MindId,
    EntityUid OwnerEntityUid,
    TimeSpan RevealedAtRoundTime);
