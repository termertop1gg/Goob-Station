using Content.Server.EUI;
using Content.Shared.Eui;
using Content.Shared.Intentions.UI;
using Robust.Shared.GameObjects;

namespace Content.Server.Intentions.UI;

/// <summary>
/// Server-side EUI host for the Intentions player and admin windows.
/// </summary>
public sealed class IntentionsEui : BaseEui
{
    private readonly IntentionsUiSystem _uiSystem;
    private readonly EntityUid _targetMindId;
    private readonly string _targetName;
    private readonly IntentionsEuiMode _mode;

    /// <summary>
    /// Initializes an Intentions EUI bound to one target mind and view mode.
    /// </summary>
    public IntentionsEui(
        IntentionsUiSystem uiSystem,
        EntityUid targetMindId,
        string targetName,
        IntentionsEuiMode mode)
    {
        _uiSystem = uiSystem;
        _targetMindId = targetMindId;
        _targetName = targetName;
        _mode = mode;
    }

    /// <summary>
    /// Gets the mind whose Intentions this EUI is currently presenting.
    /// </summary>
    public EntityUid TargetMindId => _targetMindId;
    /// <summary>
    /// Gets whether the EUI is a player view or an admin read-only view.
    /// </summary>
    public IntentionsEuiMode Mode => _mode;

    /// <summary>
    /// Registers the EUI with the server UI system when the client opens it.
    /// </summary>
    public override void Opened()
    {
        base.Opened();
        _uiSystem.RegisterOpenUi(this);
    }

    /// <summary>
    /// Unregisters the EUI from the server UI system when the client closes it.
    /// </summary>
    public override void Closed()
    {
        base.Closed();
        _uiSystem.UnregisterOpenUi(this);
    }

    /// <summary>
    /// Builds a fresh read-model snapshot for the current target mind.
    /// </summary>
    public override EuiStateBase GetNewState()
    {
        return _uiSystem.BuildState(_targetMindId, _targetName, _mode);
    }

    /// <summary>
    /// Handles refresh messages by marking the EUI state dirty.
    /// </summary>
    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);

        if (msg is IntentionsEuiRefreshMessage)
            StateDirty();
    }
}
