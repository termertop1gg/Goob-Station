using Content.Client.Gameplay;
using Content.Client.UserInterface.Controls;
using Content.Client.UserInterface.Systems.MenuBar.Widgets;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controllers;
using static Robust.Client.UserInterface.Controls.BaseButton;

namespace Content.Client.Intentions.UI;

[UsedImplicitly]
/// <summary>
/// Owns the top-menu Intentions button and keeps it in sync with the player-owned Intentions EUI.
/// </summary>
public sealed class IntentionsUIController : UIController, IOnStateExited<GameplayState>
{
    [UISystemDependency] private readonly IntentionsUiSystem _intentions = default!;

    private IntentionsEui? _playerEui;

    private MenuButton? IntentionsButton => UIManager.GetActiveUIWidgetOrNull<GameTopMenuBar>()?.IntentionsButton;

    /// <summary>
    /// Clears the tracked player EUI when gameplay exits.
    /// </summary>
    public void OnStateExited(GameplayState state)
    {
        _playerEui = null;
        DeactivateButton();
    }

    /// <summary>
    /// Hooks the Intentions top-menu button into this controller.
    /// </summary>
    public void LoadButton()
    {
        if (IntentionsButton is not { } button)
            return;

        button.OnPressed += OnIntentionsButtonPressed;
        button.SetClickPressed(_playerEui is not null);
    }

    /// <summary>
    /// Unhooks the Intentions top-menu button from this controller.
    /// </summary>
    public void UnloadButton()
    {
        if (IntentionsButton is not { } button)
            return;

        button.OnPressed -= OnIntentionsButtonPressed;
        button.SetClickPressed(false);
    }

    /// <summary>
    /// Registers the currently open player Intentions EUI.
    /// </summary>
    public void RegisterPlayerEui(IntentionsEui eui)
    {
        _playerEui = eui;
        ActivateButton();
    }

    /// <summary>
    /// Unregisters the tracked player Intentions EUI when it closes.
    /// </summary>
    public void UnregisterPlayerEui(IntentionsEui eui)
    {
        if (!ReferenceEquals(_playerEui, eui))
            return;

        _playerEui = null;
        DeactivateButton();
    }

    /// <summary>
    /// Handles presses on the Intentions top-menu button.
    /// </summary>
    private void OnIntentionsButtonPressed(ButtonEventArgs args)
    {
        ToggleWindow();
    }

    /// <summary>
    /// Toggles the current player Intentions window.
    /// </summary>
    private void ToggleWindow()
    {
        if (_playerEui is not null)
        {
            _playerEui.RequestClose();
            return;
        }

        _intentions.RequestOpenForLocalPlayer();
    }

    /// <summary>
    /// Marks the top-menu button as pressed.
    /// </summary>
    private void ActivateButton()
    {
        IntentionsButton?.SetClickPressed(true);
    }

    /// <summary>
    /// Marks the top-menu button as not pressed.
    /// </summary>
    private void DeactivateButton()
    {
        IntentionsButton?.SetClickPressed(false);
    }
}
