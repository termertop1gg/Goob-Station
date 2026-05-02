using System.Numerics;
using System.Linq;
using Content.Client.Stylesheets;
using Content.Client.Eui;
using Robust.Client.Graphics;
using Content.Shared.Eui;
using Content.Shared.Intentions.UI;
using JetBrains.Annotations;
using Robust.Client.UserInterface;
using Robust.Client.UserInterface.Controls;
using Robust.Client.UserInterface.CustomControls;
using Robust.Shared.Maths;
using Robust.Shared.Timing;
using Robust.Shared.Utility;
using static Robust.Client.UserInterface.Controls.BoxContainer;

namespace Content.Client.Intentions.UI;

[UsedImplicitly]
/// <summary>
/// Client-side EUI entry point for the Intentions window.
/// </summary>
public sealed class IntentionsEui : BaseEui
{
    [Dependency] private readonly IUserInterfaceManager _uiManager = default!;

    private readonly IntentionsWindow _window;
    private IntentionsEuiMode? _mode;

    /// <summary>
    /// Initializes the top-level client EUI and wires its close button back to the server EUI host.
    /// </summary>
    public IntentionsEui()
    {
        _window = new IntentionsWindow();
        _window.OnClose += () => SendMessage(new CloseEuiMessage());
    }

    /// <summary>
    /// Opens the main Intentions window.
    /// </summary>
    public override void Opened()
    {
        _window.OpenCentered();
    }

    /// <summary>
    /// Closes the main window and unregisters it from the player button controller when needed.
    /// </summary>
    public override void Closed()
    {
        if (_mode == IntentionsEuiMode.Player)
            _uiManager.GetUIController<IntentionsUIController>().UnregisterPlayerEui(this);

        _window.Close();
    }

    /// <summary>
    /// Applies the latest server-authored read-model to the window without recomputing any runtime text locally.
    /// </summary>
    public override void HandleState(EuiStateBase state)
    {
        if (state is not IntentionsEuiState intentionsState)
            return;

        if (_mode != intentionsState.Mode)
        {
            if (_mode == IntentionsEuiMode.Player)
                _uiManager.GetUIController<IntentionsUIController>().UnregisterPlayerEui(this);

            _mode = intentionsState.Mode;

            if (_mode == IntentionsEuiMode.Player)
                _uiManager.GetUIController<IntentionsUIController>().RegisterPlayerEui(this);
        }

        _window.SetState(intentionsState);
    }

    /// <summary>
    /// Requests the server-side EUI host to close this window.
    /// </summary>
    public void RequestClose()
    {
        _window.Close();
    }
}

/// <summary>
/// Renders the full Intentions window from the server-provided read-model.
/// </summary>
public sealed class IntentionsWindow : DefaultWindow
{
    [Dependency] private readonly IGameTiming _timing = default!;

    private readonly BoxContainer _primaryList;
    private readonly BoxContainer _secondaryList;
    private readonly BoxContainer _adminList;
    private readonly Control _adminSection;
    private readonly PanelContainer _detailHeaderPanel;
    private readonly RichTextLabel _detailTitle;
    private readonly Label _detailAuthor;
    private readonly PanelContainer _detailStatusBadge;
    private readonly Label _detailStatus;
    private readonly PanelContainer _detailRevealPanel;
    private readonly RichTextLabel _detailReveal;
    private readonly PanelContainer _detailDescriptionPanel;
    private readonly PanelContainer _detailOocPanel;
    private readonly RichTextLabel _detailDescription;
    private readonly RichTextLabel _detailOoc;
    private readonly PanelContainer _detailFooterPanel;
    private readonly Button _materialsButton;
    private readonly List<IntentionsListCardControl> _cardControls = new();
    private IntentionsMaterialsWindow? _materialsWindow;
    private IntentionsCardView? _selected;
    private TimeSpan _stateRoundTime;
    private TimeSpan _stateRealTime;
    private string? _lastRevealText;

    /// <summary>
    /// Initializes the main Intentions window layout.
    /// </summary>
    public IntentionsWindow()
    {
        IoCManager.InjectDependencies(this);

        Title = Loc.GetString("intentions-ui-title");
        MinSize = SetSize = new Vector2(920, 620);

        var root = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            SeparationOverride = 10,
            Margin = new Thickness(8),
        };
        ContentsContainer.AddChild(root);

        var left = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            MinSize = new Vector2(330, 0),
            VerticalExpand = true,
            SeparationOverride = 8,
        };
        root.AddChild(left);

        left.AddChild(SectionLabel("intentions-ui-own-section"));
        var primaryPanel = CreateSectionPanel(out _primaryList);
        primaryPanel.SizeFlagsStretchRatio = 1f;
        left.AddChild(primaryPanel);

        left.AddChild(SectionLabel("intentions-ui-linked-section"));
        var secondaryPanel = CreateSectionPanel(out _secondaryList);
        secondaryPanel.SizeFlagsStretchRatio = 1f;
        left.AddChild(secondaryPanel);

        _adminSection = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            SeparationOverride = 4,
            VerticalExpand = true,
            Visible = false,
            SizeFlagsStretchRatio = 0.8f,
        };
        _adminSection.AddChild(SectionLabel("intentions-ui-admin-scenarios-section"));
        var adminPanel = CreateSectionPanel(out _adminList);
        adminPanel.SizeFlagsStretchRatio = 0.8f;
        _adminSection.AddChild(adminPanel);
        left.AddChild(_adminSection);

        var detail = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            HorizontalExpand = true,
            VerticalExpand = true,
            SeparationOverride = 8,
        };
        root.AddChild(detail);

        _detailHeaderPanel = CreateFramedDetailPanel("#6C7690", 10);
        detail.AddChild(_detailHeaderPanel);

        var headerBox = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            SeparationOverride = 6,
            HorizontalExpand = true,
        };
        _detailHeaderPanel.AddChild(headerBox);

        var headerRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
            SeparationOverride = 12,
        };
        headerBox.AddChild(headerRow);

        _detailTitle = new RichTextLabel
        {
            HorizontalExpand = true,
        };
        headerRow.AddChild(_detailTitle);

        _detailStatusBadge = new PanelContainer
        {
            HorizontalAlignment = HAlignment.Right,
            VerticalAlignment = VAlignment.Center,
        };
        headerRow.AddChild(_detailStatusBadge);

        _detailStatus = new Label
        {
            HorizontalAlignment = HAlignment.Center,
        };
        _detailStatusBadge.AddChild(_detailStatus);

        _detailAuthor = new Label
        {
            ClipText = true,
            HorizontalExpand = true,
        };
        _detailAuthor.StyleClasses.Add(StyleBase.StyleClassLabelSubText);
        headerBox.AddChild(_detailAuthor);

        _detailRevealPanel = CreateFramedDetailPanel("#6C7690", 10);
        detail.AddChild(_detailRevealPanel);

        var revealBox = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            SeparationOverride = 4,
            HorizontalExpand = true,
        };
        _detailRevealPanel.AddChild(revealBox);

        revealBox.AddChild(new Label
        {
            Text = Loc.GetString("intentions-ui-reveal-header"),
            StyleClasses = { StyleBase.StyleClassLabelSubText },
        });

        _detailReveal = new RichTextLabel
        {
            HorizontalExpand = true,
        };
        revealBox.AddChild(_detailReveal);

        var detailScroll = new ScrollContainer
        {
            VerticalExpand = true,
            HorizontalExpand = true,
            HScrollEnabled = false,
        };
        detail.AddChild(detailScroll);

        var body = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            SeparationOverride = 8,
            HorizontalExpand = true,
        };
        detailScroll.AddChild(body);

        _detailDescriptionPanel = CreateFramedDetailPanel("#6C7690", 10);
        body.AddChild(_detailDescriptionPanel);

        var descriptionBox = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            SeparationOverride = 4,
            HorizontalExpand = true,
        };
        _detailDescriptionPanel.AddChild(descriptionBox);

        descriptionBox.AddChild(new Label
        {
            Text = Loc.GetString("intentions-ui-intention-header"),
            StyleClasses = { StyleBase.StyleClassLabelSubText },
        });

        _detailDescription = new RichTextLabel
        {
            HorizontalExpand = true,
        };
        descriptionBox.AddChild(_detailDescription);

        _detailOocPanel = CreateFilledDetailPanel("#1B2434", "#677B99", 10);
        body.AddChild(_detailOocPanel);

        var oocBox = new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            SeparationOverride = 4,
            HorizontalExpand = true,
        };
        _detailOocPanel.AddChild(oocBox);

        oocBox.AddChild(new Label
        {
            Text = Loc.GetString("intentions-ui-ooc-header"),
            StyleClasses = { StyleBase.StyleClassLabelSubText },
        });

        _detailOoc = new RichTextLabel
        {
            HorizontalExpand = true,
        };
        _detailOoc.StyleClasses.Add(StyleBase.StyleClassLabelSubText);
        oocBox.AddChild(_detailOoc);

        _detailFooterPanel = CreateFramedDetailPanel("#6C7690", 10);
        detail.AddChild(_detailFooterPanel);

        var footerRow = new BoxContainer
        {
            Orientation = LayoutOrientation.Horizontal,
            HorizontalExpand = true,
        };
        _detailFooterPanel.AddChild(footerRow);

        _materialsButton = new Button
        {
            Text = Loc.GetString("intentions-ui-open-materials"),
            HorizontalAlignment = Control.HAlignment.Right,
        };
        _materialsButton.OnPressed += _ => OpenMaterials();
        footerRow.AddChild(new Control { HorizontalExpand = true });
        footerRow.AddChild(_materialsButton);

        OnClose += () => _materialsWindow?.Close();

        SetEmptyDetail();
    }

    /// <summary>
    /// Applies a fresh server read-model to the window.
    /// </summary>
    public void SetState(IntentionsEuiState state)
    {
        _stateRoundTime = state.RoundTime;
        _stateRealTime = _timing.RealTime;
        Title = state.Mode == IntentionsEuiMode.Admin
            ? Loc.GetString("intentions-ui-title-admin")
            : Loc.GetString("intentions-ui-title");
        _primaryList.RemoveAllChildren();
        _secondaryList.RemoveAllChildren();
        _adminList.RemoveAllChildren();
        _cardControls.Clear();
        _adminSection.Visible = state.Mode == IntentionsEuiMode.Admin;

        foreach (var card in state.OwnIntentions)
            _primaryList.AddChild(CardControl(card));

        foreach (var card in state.LinkedIntentions)
            _secondaryList.AddChild(CardControl(card));

        if (state.Mode == IntentionsEuiMode.Admin)
        {
            foreach (var scenario in state.AdminScenarios)
            {
                _adminList.AddChild(new Label
                {
                    Text = Loc.GetString(
                        "intentions-ui-admin-scenario-line",
                        ("uid", scenario.ScenarioUid),
                        ("template", scenario.ScenarioTemplateId),
                        ("status", scenario.Status),
                        ("wave", scenario.WaveId)),
                    HorizontalExpand = true,
                });
            }
        }

        var allCards = state.OwnIntentions.Concat(state.LinkedIntentions).ToArray();
        _selected = _selected is { } selected
            ? allCards.FirstOrDefault(card => card.IntentionUid == selected.IntentionUid) ?? allCards.FirstOrDefault()
            : allCards.FirstOrDefault();

        if (_selected is { } current)
            SetDetail(current);
        else
            SetEmptyDetail();

        UpdateSelection();
    }

    /// <summary>
    /// Creates one selectable list-card control for the provided read-model card.
    /// </summary>
    private IntentionsListCardControl CardControl(IntentionsCardView card)
    {
        var control = new IntentionsListCardControl(card);
        _cardControls.Add(control);
        control.OnPressed += _ =>
        {
            _selected = card;
            SetDetail(card);
            UpdateSelection();
        };
        return control;
    }

    /// <summary>
    /// Updates the right-hand detail panel for the selected card.
    /// </summary>
    private void SetDetail(IntentionsCardView card)
    {
        _detailTitle.SetMessage(IntentionsTextHighlighting.FormatHeading(card.Title, card.ResolvedTextParameters));
        _detailAuthor.Text = Loc.GetString("intentions-ui-created-by-line", ("author", card.Author));
        _detailStatus.Text = card.IntentionStatus.ToUpperInvariant();
        _detailStatus.FontColorOverride = StatusTextColor(card.IntentionStatus);
        _detailStatusBadge.PanelOverride = CreateStatusBadge(card.IntentionStatus);
        _detailHeaderPanel.Visible = true;

        UpdateRevealPanel(card);

        var oocInfo = !card.IsHidden
            ? string.IsNullOrWhiteSpace(card.OocInfo)
                ? Loc.GetString("default-ooc-info")
                : card.OocInfo
            : null;

        if (!string.IsNullOrWhiteSpace(oocInfo))
        {
            _detailOocPanel.Visible = true;
            _detailOoc.SetMessage(IntentionsTextHighlighting.Format(oocInfo!, card.ResolvedTextParameters));
        }
        else
        {
            _detailOocPanel.Visible = false;
            _detailOoc.SetMessage(string.Empty);
        }

        _detailDescription.SetMessage(IntentionsTextHighlighting.Format(card.Description, card.ResolvedTextParameters));

        _materialsButton.Visible = !string.IsNullOrWhiteSpace(card.CopyableText);
        _detailFooterPanel.Visible = _materialsButton.Visible;
    }

    /// <summary>
    /// Resets the detail panel to the empty selection state.
    /// </summary>
    private void SetEmptyDetail()
    {
        _detailTitle.SetMessage(IntentionsTextHighlighting.FormatHeading(Loc.GetString("intentions-ui-empty-title"), null));
        _detailAuthor.Text = string.Empty;
        _detailStatus.Text = string.Empty;
        _detailStatus.FontColorOverride = Color.White;
        _detailStatusBadge.PanelOverride = CreateStatusBadge(string.Empty);
        _detailHeaderPanel.Visible = false;
        _detailRevealPanel.Visible = false;
        _detailReveal.SetMessage(string.Empty);
        _lastRevealText = null;
        _detailDescription.SetMessage(Loc.GetString("intentions-ui-empty-description"));
        _detailOocPanel.Visible = false;
        _detailOoc.SetMessage(string.Empty);
        _detailFooterPanel.Visible = false;
    }

    protected override void FrameUpdate(FrameEventArgs args)
    {
        base.FrameUpdate(args);

        var currentRoundTime = GetCurrentRoundTime();

        foreach (var control in _cardControls)
            control.UpdateHiddenRevealText(currentRoundTime);

        if (_selected is { IsHidden: true } selected)
            UpdateRevealPanel(selected, currentRoundTime);
    }

    /// <summary>
    /// Opens the materials window or focuses the existing one without creating duplicates.
    /// </summary>
    private void OpenMaterials()
    {
        if (_selected?.CopyableText is not { } copyableText)
            return;

        if (_materialsWindow is null)
        {
            _materialsWindow = new IntentionsMaterialsWindow(copyableText);
            _materialsWindow.OnClose += () => _materialsWindow = null;
            _materialsWindow.OpenCentered();
            return;
        }

        _materialsWindow.SetText(copyableText);
        if (_materialsWindow.IsOpen)
        {
            _materialsWindow.MoveToFront();
            _materialsWindow.FocusInput();
            return;
        }

        _materialsWindow.OpenCentered();
    }

    /// <summary>
    /// Updates the selected state across all list cards.
    /// </summary>
    private void UpdateSelection()
    {
        foreach (var control in _cardControls)
            control.SetSelected(_selected?.IntentionUid == control.Card.IntentionUid);
    }

    /// <summary>
    /// Updates the hidden-intention reveal block, including the locally ticking countdown for timer reveals.
    /// </summary>
    private void UpdateRevealPanel(IntentionsCardView card)
    {
        UpdateRevealPanel(card, GetCurrentRoundTime());
    }

    /// <summary>
    /// Updates the hidden-intention reveal block, including the locally ticking countdown for timer reveals.
    /// </summary>
    private void UpdateRevealPanel(IntentionsCardView card, TimeSpan currentRoundTime)
    {
        if (!card.IsHidden)
        {
            _detailRevealPanel.Visible = false;
            _detailReveal.SetMessage(string.Empty);
            _lastRevealText = null;
            return;
        }

        var revealText = BuildCurrentRevealText(card, currentRoundTime);
        _detailRevealPanel.Visible = true;

        if (revealText == _lastRevealText)
            return;

        _detailReveal.SetMessage(IntentionsTextHighlighting.Format(revealText, card.ResolvedTextParameters));
        _lastRevealText = revealText;
    }

    /// <summary>
    /// Recomputes the current reveal text from the last server round-time snapshot and local elapsed real time.
    /// </summary>
    private string BuildCurrentRevealText(IntentionsCardView card, TimeSpan currentRoundTime)
    {
        if (!card.IsHidden)
            return card.RevealText;

        if (card.RevealedAtRoundTime is not { } revealTime)
            return card.RevealText;

        var remaining = revealTime > currentRoundTime ? revealTime - currentRoundTime : TimeSpan.Zero;

        return Loc.GetString(
            "intentions-ui-hidden-reveal-timer",
            ("time", remaining.ToString(@"hh\:mm\:ss")));
    }

    private TimeSpan GetCurrentRoundTime()
    {
        return _stateRoundTime + (_timing.RealTime - _stateRealTime);
    }

    /// <summary>
    /// Creates the standard vertical list container used by the card sections.
    /// </summary>
    private static BoxContainer ListBox()
    {
        return new BoxContainer
        {
            Orientation = LayoutOrientation.Vertical,
            SeparationOverride = 6,
            HorizontalExpand = true,
        };
    }

    /// <summary>
    /// Creates one scrollable list section panel.
    /// </summary>
    private static PanelContainer CreateSectionPanel(out BoxContainer list)
    {
        list = ListBox();

        var scroll = new ScrollContainer
        {
            VerticalExpand = true,
            HorizontalExpand = true,
            HScrollEnabled = false,
        };
        scroll.AddChild(list);

        var panel = new PanelContainer
        {
            VerticalExpand = true,
            HorizontalExpand = true,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex("#181A20"),
                BorderColor = Color.FromHex("#2E3443"),
                BorderThickness = new Thickness(1),
                ContentMarginLeftOverride = 4,
                ContentMarginTopOverride = 4,
                ContentMarginRightOverride = 4,
                ContentMarginBottomOverride = 4,
            },
        };
        panel.AddChild(scroll);
        return panel;
    }

    /// <summary>
    /// Creates one framed detail panel with custom background, border, and padding.
    /// </summary>
    private static PanelContainer CreateFramedDetailPanel(string borderHex, int padding)
    {
        return new PanelContainer
        {
            HorizontalExpand = true,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.Transparent,
                BorderColor = Color.FromHex(borderHex),
                BorderThickness = new Thickness(1),
                ContentMarginLeftOverride = padding,
                ContentMarginTopOverride = padding,
                ContentMarginRightOverride = padding,
                ContentMarginBottomOverride = padding,
            },
        };
    }

    /// <summary>
    /// Creates a detail panel that keeps the framed geometry but uses a filled background.
    /// </summary>
    private static PanelContainer CreateFilledDetailPanel(string backgroundHex, string borderHex, int padding)
    {
        return new PanelContainer
        {
            HorizontalExpand = true,
            PanelOverride = new StyleBoxFlat
            {
                BackgroundColor = Color.FromHex(backgroundHex),
                BorderColor = Color.FromHex(borderHex),
                BorderThickness = new Thickness(1),
                ContentMarginLeftOverride = padding,
                ContentMarginTopOverride = padding,
                ContentMarginRightOverride = padding,
                ContentMarginBottomOverride = padding,
            },
        };
    }

    /// <summary>
    /// Creates a section heading label for the left-side lists.
    /// </summary>
    private static Label SectionLabel(string locId)
    {
        return new Label
        {
            Text = Loc.GetString(locId),
            HorizontalExpand = true,
        };
    }

    /// <summary>
    /// Returns the UI color used for the displayed intention status.
    /// </summary>
    private static Color StatusTextColor(string status)
    {
        return status switch
        {
            "Active" => Color.FromHex("#EAF6E7"),
            "Cancelled" => Color.FromHex("#FAECEC"),
            "Frozen" => Color.FromHex("#FCF1D8"),
            _ => Color.FromHex("#ECEFF7"),
        };
    }

    /// <summary>
    /// Returns the compact filled badge style used for the displayed intention status.
    /// </summary>
    private static StyleBoxFlat CreateStatusBadge(string status)
    {
        var background = status switch
        {
            "Active" => Color.FromHex("#3A6A44"),
            "Cancelled" => Color.FromHex("#7A4545"),
            "Frozen" => Color.FromHex("#7D6432"),
            _ => Color.FromHex("#4A5162"),
        };

        return new StyleBoxFlat
        {
            BackgroundColor = background,
            BorderColor = background,
            BorderThickness = new Thickness(1),
            ContentMarginLeftOverride = 8,
            ContentMarginTopOverride = 3,
            ContentMarginRightOverride = 8,
            ContentMarginBottomOverride = 3,
        };
    }
}

/// <summary>
/// Displays copyable materials text in a read-only editor-style window.
/// </summary>
public sealed class IntentionsMaterialsWindow : DefaultWindow
{
    private readonly TextEdit _input;

    /// <summary>
    /// Initializes the materials window with read-only text content.
    /// </summary>
    public IntentionsMaterialsWindow(string text)
    {
        Title = Loc.GetString("intentions-ui-materials-title");
        MinSize = SetSize = new Vector2(420, 520);
        Resizable = true;

        var scroll = new ScrollContainer
        {
            VerticalExpand = true,
            HorizontalExpand = true,
            HScrollEnabled = false,
        };
        ContentsContainer.AddChild(scroll);

        var editorInset = new Control
        {
            HorizontalExpand = true,
            VerticalExpand = true,
            Margin = new Thickness(0, 0, 6, 0),
        };
        scroll.AddChild(editorInset);

        _input = new TextEdit
        {
            Editable = false,
            HorizontalExpand = true,
            VerticalExpand = true,
            MinHeight = 100,
        };
        _input.StyleClasses.Add("PaperLineEdit");
        editorInset.AddChild(_input);

        SetText(text);
    }

    /// <summary>
    /// Focuses the text editor when the window opens.
    /// </summary>
    protected override void Opened()
    {
        base.Opened();
        FocusInput();
    }

    /// <summary>
    /// Replaces the displayed materials text.
    /// </summary>
    public void SetText(string text)
    {
        _input.TextRope = new Rope.Leaf(text);
        _input.CursorPosition = new TextEdit.CursorPos(0, TextEdit.LineBreakBias.Top);
    }

    /// <summary>
    /// Moves keyboard focus into the read-only text editor.
    /// </summary>
    public void FocusInput()
    {
        _input.GrabKeyboardFocus();
    }
}
