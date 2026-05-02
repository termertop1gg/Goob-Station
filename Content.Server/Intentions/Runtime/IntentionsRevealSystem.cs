using System.Linq;
using Content.Server.GameTicking;
using Content.Server.Intentions.UI;
using Content.Shared.Popups;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Timing;

namespace Content.Server.Intentions.Runtime;

/// <summary>
/// Periodically evaluates timer reveals, refreshes UIs, and notifies players when hidden intentions open up.
/// </summary>
public sealed class IntentionsRevealSystem : EntitySystem
{
    private static readonly TimeSpan RevealInterval = TimeSpan.FromSeconds(1);

    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IntentionsRuntimeSystem _runtime = default!;
    [Dependency] private readonly IntentionsUiSystem _ui = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;

    private readonly IntentionsRevealService _reveal = new();
    private TimeSpan _nextRevealCheck;

    /// <summary>
    /// Runs the timer reveal pass while the round is active.
    /// </summary>
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_gameTicker.RunLevel != GameRunLevel.InRound)
            return;

        if (_timing.CurTime < _nextRevealCheck)
            return;

        _nextRevealCheck = _timing.CurTime + RevealInterval;
        var result = _reveal.EvaluateTimerReveals(_runtime.Registry, _gameTicker.RoundDuration());
        if (!result.HasReveals)
            return;

        foreach (var reveal in result.RevealedIntentions.GroupBy(reveal => reveal.MindId))
        {
            _ui.RefreshMind(reveal.Key);

            var ownerEntity = reveal.First().OwnerEntityUid;
            if (!TerminatingOrDeleted(ownerEntity))
                _popup.PopupEntity(Loc.GetString("intentions-ui-reveal-notification"), ownerEntity, ownerEntity);
        }
    }
}
