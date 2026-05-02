using System.Collections.Generic;
using Content.Shared.Eui;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization;
using Robust.Shared.Utility;

namespace Content.Shared.Intentions.UI;

/// <summary>
/// Client request to open the player Intentions window for one entity.
/// </summary>
[Serializable, NetSerializable]
public sealed class RequestIntentionsUiEvent : EntityEventArgs
{
    /// <summary>
    /// Target entity whose mind should be shown in the player UI.
    /// </summary>
    public readonly NetEntity Target;

    /// <summary>
    /// Creates a UI request for the provided entity.
    /// </summary>
    public RequestIntentionsUiEvent(NetEntity target)
    {
        Target = target;
    }
}

/// <summary>
/// EUI state containing the full player or admin read-model for one target mind.
/// </summary>
[Serializable, NetSerializable]
public sealed class IntentionsEuiState : EuiStateBase
{
    /// <summary>
    /// Creates a new Intentions EUI state payload.
    /// </summary>
    public IntentionsEuiState(
        IntentionsEuiMode mode,
        string targetName,
        TimeSpan roundTime,
        IntentionsCardView[] ownIntentions,
        IntentionsCardView[] linkedIntentions,
        IntentionsScenarioAdminView[] adminScenarios)
    {
        Mode = mode;
        TargetName = targetName;
        RoundTime = roundTime;
        OwnIntentions = ownIntentions;
        LinkedIntentions = linkedIntentions;
        AdminScenarios = adminScenarios;
    }

    /// <summary>
    /// UI mode that controls redaction and admin-only sections.
    /// </summary>
    public readonly IntentionsEuiMode Mode;

    /// <summary>
    /// Display name of the target whose intentions are being viewed.
    /// </summary>
    public readonly string TargetName;

    /// <summary>
    /// Current round time used by the client to display reveal information.
    /// </summary>
    public readonly TimeSpan RoundTime;

    /// <summary>
    /// Primary intentions directly owned by the viewed mind.
    /// </summary>
    public readonly IntentionsCardView[] OwnIntentions;

    /// <summary>
    /// Secondary intentions linked to the viewed mind.
    /// </summary>
    public readonly IntentionsCardView[] LinkedIntentions;

    /// <summary>
    /// Admin-only scenario metadata for cards in the current view.
    /// </summary>
    public readonly IntentionsScenarioAdminView[] AdminScenarios;
}

/// <summary>
/// Client-facing read-model for one intention card.
/// </summary>
[Serializable, NetSerializable]
public sealed class IntentionsCardView
{
    /// <summary>
    /// Creates a read-only UI card view.
    /// </summary>
    public IntentionsCardView(
        long intentionUid,
        long scenarioUid,
        string intentionTemplateId,
        string scenarioTemplateId,
        string categoryId,
        string slotId,
        string kind,
        string title,
        string author,
        string summary,
        string description,
        string? oocInfo,
        string? copyableText,
        Dictionary<string, string> resolvedTextParameters,
        bool isHidden,
        string revealText,
        TimeSpan? revealedAtRoundTime,
        string intentionStatus,
        string scenarioStatus,
        string slotStatus,
        int waveId,
        TimeSpan assignedAtRoundTime,
        SpriteSpecifier? icon,
        string color)
    {
        IntentionUid = intentionUid;
        ScenarioUid = scenarioUid;
        IntentionTemplateId = intentionTemplateId;
        ScenarioTemplateId = scenarioTemplateId;
        CategoryId = categoryId;
        SlotId = slotId;
        Kind = kind;
        Title = title;
        Author = author;
        Summary = summary;
        Description = description;
        OocInfo = oocInfo;
        CopyableText = copyableText;
        ResolvedTextParameters = resolvedTextParameters;
        IsHidden = isHidden;
        RevealText = revealText;
        RevealedAtRoundTime = revealedAtRoundTime;
        IntentionStatus = intentionStatus;
        ScenarioStatus = scenarioStatus;
        SlotStatus = slotStatus;
        WaveId = waveId;
        AssignedAtRoundTime = assignedAtRoundTime;
        Icon = icon;
        Color = color;
    }

    /// <summary>
    /// Runtime intention uid.
    /// </summary>
    public readonly long IntentionUid;

    /// <summary>
    /// Runtime scenario uid that owns this card.
    /// </summary>
    public readonly long ScenarioUid;

    /// <summary>
    /// Source intention template id, redacted for hidden player cards.
    /// </summary>
    public readonly string IntentionTemplateId;

    /// <summary>
    /// Source scenario template id, redacted for hidden player cards.
    /// </summary>
    public readonly string ScenarioTemplateId;

    /// <summary>
    /// Source category id, redacted for hidden player cards.
    /// </summary>
    public readonly string CategoryId;

    /// <summary>
    /// Slot id that produced this intention instance.
    /// </summary>
    public readonly string SlotId;

    /// <summary>
    /// Primary or secondary card grouping.
    /// </summary>
    public readonly string Kind;

    /// <summary>
    /// Title shown in the list and detail panel.
    /// </summary>
    public readonly string Title;

    /// <summary>
    /// Author label prepared for the current UI mode.
    /// </summary>
    public readonly string Author;

    /// <summary>
    /// Short summary shown in the card list.
    /// </summary>
    public readonly string Summary;

    /// <summary>
    /// Full body text shown in the detail panel.
    /// </summary>
    public readonly string Description;

    /// <summary>
    /// Optional out-of-character guidance block.
    /// </summary>
    public readonly string? OocInfo;

    /// <summary>
    /// Optional copyable materials text.
    /// </summary>
    public readonly string? CopyableText;

    /// <summary>
    /// Resolved runtime text parameters safe for the current UI mode.
    /// </summary>
    public readonly Dictionary<string, string> ResolvedTextParameters;

    /// <summary>
    /// Whether the card is currently hidden in the player view.
    /// </summary>
    public readonly bool IsHidden;

    /// <summary>
    /// Reveal status text prepared for the current viewer.
    /// </summary>
    public readonly string RevealText;

    /// <summary>
    /// Scheduled reveal time for timer-based hidden cards, if any.
    /// </summary>
    public readonly TimeSpan? RevealedAtRoundTime;

    /// <summary>
    /// Runtime intention status as a display-ready string.
    /// </summary>
    public readonly string IntentionStatus;

    /// <summary>
    /// Runtime scenario status as a display-ready string.
    /// </summary>
    public readonly string ScenarioStatus;

    /// <summary>
    /// Runtime slot status as a display-ready string.
    /// </summary>
    public readonly string SlotStatus;

    /// <summary>
    /// Wave id that created the owning scenario.
    /// </summary>
    public readonly int WaveId;

    /// <summary>
    /// Round time when the intention was assigned.
    /// </summary>
    public readonly TimeSpan AssignedAtRoundTime;

    /// <summary>
    /// UI icon selected for the current mode and content state.
    /// </summary>
    public readonly SpriteSpecifier? Icon;

    /// <summary>
    /// Accent color used by list rendering.
    /// </summary>
    public readonly string Color;
}

/// <summary>
/// Admin-only scenario metadata shown next to the card list.
/// </summary>
[Serializable, NetSerializable]
public sealed class IntentionsScenarioAdminView
{
    /// <summary>
    /// Creates a new admin scenario summary.
    /// </summary>
    public IntentionsScenarioAdminView(
        long scenarioUid,
        string scenarioTemplateId,
        string categoryId,
        string status,
        string ownerSlotId,
        int ownerMindId,
        int ownerEntityUid,
        int waveId,
        TimeSpan createdAtRoundTime)
    {
        ScenarioUid = scenarioUid;
        ScenarioTemplateId = scenarioTemplateId;
        CategoryId = categoryId;
        Status = status;
        OwnerSlotId = ownerSlotId;
        OwnerMindId = ownerMindId;
        OwnerEntityUid = ownerEntityUid;
        WaveId = waveId;
        CreatedAtRoundTime = createdAtRoundTime;
    }

    /// <summary>
    /// Runtime scenario uid.
    /// </summary>
    public readonly long ScenarioUid;

    /// <summary>
    /// Source scenario template id.
    /// </summary>
    public readonly string ScenarioTemplateId;

    /// <summary>
    /// Scenario category id.
    /// </summary>
    public readonly string CategoryId;

    /// <summary>
    /// Runtime scenario status.
    /// </summary>
    public readonly string Status;

    /// <summary>
    /// Owner slot id for the scenario.
    /// </summary>
    public readonly string OwnerSlotId;

    /// <summary>
    /// Numeric owner mind id prepared for admin display.
    /// </summary>
    public readonly int OwnerMindId;

    /// <summary>
    /// Numeric owner entity uid prepared for admin display.
    /// </summary>
    public readonly int OwnerEntityUid;

    /// <summary>
    /// Wave id that created the scenario.
    /// </summary>
    public readonly int WaveId;

    /// <summary>
    /// Round time when the scenario was committed.
    /// </summary>
    public readonly TimeSpan CreatedAtRoundTime;
}

/// <summary>
/// Client-to-server message requesting a state refresh for an already open Intentions EUI.
/// </summary>
[Serializable, NetSerializable]
public sealed class IntentionsEuiRefreshMessage : EuiMessageBase;

/// <summary>
/// Intentions UI mode that controls redaction and available panels.
/// </summary>
[Serializable, NetSerializable]
public enum IntentionsEuiMode : byte
{
    Player,
    Admin,
}
