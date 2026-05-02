using System.Linq;
using Content.Server.EUI;
using Content.Server.GameTicking;
using Content.Server.Intentions.Runtime;
using Content.Server.Intentions.Validation;
using Content.Server.Mind;
using Content.Shared.Intentions.UI;
using Robust.Server.Player;
using Robust.Shared.GameObjects;
using Robust.Shared.Prototypes;
using Robust.Shared.Player;

namespace Content.Server.Intentions.UI;

/// <summary>
/// Opens, tracks, and refreshes server-side Intentions EUIs for both player and admin views.
/// </summary>
public sealed class IntentionsUiSystem : EntitySystem
{
    [Dependency] private readonly EuiManager _eui = default!;
    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly IPrototypeManager _prototypes = default!;
    [Dependency] private readonly IntentionsRuntimeSystem _runtime = default!;
    [Dependency] private readonly MindSystem _mind = default!;

    private readonly IntentionsQueryService _query = new();
    private readonly Dictionary<EntityUid, List<IntentionsEui>> _openByMind = new();

    /// <summary>
    /// Subscribes to the network event used by players to request their own Intentions window.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<RequestIntentionsUiEvent>(OnRequestIntentionsUi);
    }

    /// <summary>
    /// Opens the player-mode Intentions window for the given target mind.
    /// </summary>
    public void OpenPlayerUi(ICommonSession session, EntityUid targetMindId, string targetName)
    {
        OpenUi(session, targetMindId, targetName, IntentionsEuiMode.Player);
    }

    /// <summary>
    /// Opens the admin-mode Intentions window for the given target mind.
    /// </summary>
    public void OpenAdminUi(ICommonSession session, EntityUid targetMindId, string targetName)
    {
        OpenUi(session, targetMindId, targetName, IntentionsEuiMode.Admin);
    }

    /// <summary>
    /// Builds the current read-model state for one Intentions window.
    /// </summary>
    public IntentionsEuiState BuildState(EntityUid targetMindId, string targetName, IntentionsEuiMode mode)
    {
        var catalog = new IntentionsValidationService(_prototypes).ValidateAll();
        return _query.GetIntentionsForMind(
            catalog,
            _runtime.Registry,
            targetMindId,
            _gameTicker.RoundDuration(),
            mode,
            targetName);
    }

    /// <summary>
    /// Refreshes every open Intentions window registered for one mind.
    /// </summary>
    public void RefreshMind(EntityUid mindId)
    {
        if (!_openByMind.TryGetValue(mindId, out var openUis))
            return;

        foreach (var ui in openUis.ToArray())
        {
            if (!ui.IsShutDown)
                ui.StateDirty();
        }
    }

    /// <summary>
    /// Registers a newly opened server EUI instance under its target mind.
    /// </summary>
    internal void RegisterOpenUi(IntentionsEui ui)
    {
        if (!_openByMind.TryGetValue(ui.TargetMindId, out var openUis))
        {
            openUis = [];
            _openByMind[ui.TargetMindId] = openUis;
        }

        openUis.Add(ui);
    }

    /// <summary>
    /// Unregisters a closing server EUI instance.
    /// </summary>
    internal void UnregisterOpenUi(IntentionsEui ui)
    {
        if (!_openByMind.TryGetValue(ui.TargetMindId, out var openUis))
            return;

        openUis.Remove(ui);
        if (openUis.Count == 0)
            _openByMind.Remove(ui.TargetMindId);
    }

    /// <summary>
    /// Opens or reuses an existing EUI instance for the same session, target mind, and mode.
    /// </summary>
    private void OpenUi(ICommonSession session, EntityUid targetMindId, string targetName, IntentionsEuiMode mode)
    {
        if (_openByMind.TryGetValue(targetMindId, out var openUis))
        {
            var existing = openUis.FirstOrDefault(ui =>
                !ui.IsShutDown &&
                ui.Player == session &&
                ui.Mode == mode);

            if (existing is not null)
            {
                existing.StateDirty();
                return;
            }
        }

        var ui = new IntentionsEui(this, targetMindId, targetName, mode);
        _eui.OpenEui(ui, session);
        ui.StateDirty();
    }

    /// <summary>
    /// Handles a player request to open their own Intentions window.
    /// </summary>
    private void OnRequestIntentionsUi(RequestIntentionsUiEvent msg, EntitySessionEventArgs args)
    {
        var target = GetEntity(msg.Target);
        if (args.SenderSession.AttachedEntity != target)
            return;

        if (!_mind.TryGetMind(target, out var mindId, out _))
            return;

        OpenPlayerUi(args.SenderSession, mindId, Name(target));
    }
}
