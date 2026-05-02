using Content.Shared.Intentions.UI;
using Robust.Client.Player;

namespace Content.Client.Intentions.UI;

/// <summary>
/// Sends player requests to open the server-authored Intentions window.
/// </summary>
public sealed class IntentionsUiSystem : EntitySystem
{
    [Dependency] private readonly IPlayerManager _player = default!;

    /// <summary>
    /// Requests the Intentions window for the local player's attached entity.
    /// </summary>
    public void RequestOpenForLocalPlayer()
    {
        if (_player.LocalEntity is not { } entity)
            return;

        RaiseNetworkEvent(new RequestIntentionsUiEvent(GetNetEntity(entity)));
    }
}
