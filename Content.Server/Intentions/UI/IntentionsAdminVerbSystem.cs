using Content.Server.Administration.Managers;
using Content.Server.Mind;
using Content.Shared.Administration;
using Content.Shared.Intentions.UI;
using Content.Shared.Verbs;
using Robust.Shared.GameObjects;
using Robust.Shared.Player;

namespace Content.Server.Intentions.UI;

/// <summary>
/// Adds the admin verb that opens a read-only Intentions view for another player's mind.
/// </summary>
public sealed class IntentionsAdminVerbSystem : EntitySystem
{
    [Dependency] private readonly IAdminManager _admin = default!;
    [Dependency] private readonly IntentionsUiSystem _ui = default!;
    [Dependency] private readonly MindSystem _mind = default!;

    /// <summary>
    /// Subscribes to verb collection so the Intentions admin verb can be injected when allowed.
    /// </summary>
    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<GetVerbsEvent<Verb>>(OnGetVerbs);
    }

    /// <summary>
    /// Adds the Intentions admin verb when the acting user has admin privileges and the target has a mind.
    /// </summary>
    private void OnGetVerbs(GetVerbsEvent<Verb> args)
    {
        if (!TryComp(args.User, out ActorComponent? actor))
            return;

        var session = actor.PlayerSession;
        if (!_admin.HasAdminFlag(session, AdminFlags.Admin))
            return;

        if (!_mind.TryGetMind(args.Target, out var mindId, out _))
            return;

        args.Verbs.Add(new Verb
        {
            Text = Loc.GetString("intentions-admin-verb-text"),
            Category = VerbCategory.Admin,
            Act = () => _ui.OpenAdminUi(session, mindId, Name(args.Target)),
        });
    }
}
