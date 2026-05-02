using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Server.GameTicking;
using Content.Server.Ghost.Roles;
using Content.Server.Holiday;
using Content.Server.Station.Systems;
using Content.Shared.Humanoid;
using Content.Shared.Intentions.Snapshot;
using Content.Shared.Mind;
using Content.Shared.Mindshield.Components;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Content.Shared.Silicons.Borgs.Components;
using Content.Shared.Silicons.StationAi;
using Robust.Server.Player;
using Robust.Shared.Enums;
using Robust.Shared.GameObjects;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server.Intentions.Snapshot;

/// <summary>
/// Collects live round data and normalizes it into immutable Intentions snapshots.
/// </summary>
public sealed class IntentionsSnapshotService : EntitySystem
{
    // Intentions treat several major ghost-role antags as ghost-role participants even though
    // their shared mind roles do not carry GhostRoleMarkerRoleComponent in base SS14 content.
    private static readonly FrozenSet<string> GhostRoleAntagOverrides = new[]
    {
        "Dragon",
        "SpaceNinja",
        "ParadoxClone",
        "Nukeops",
        "NukeopsCommander",
        "NukeopsMedic",
        "Wizard",
        "SubvertedSilicon",
        "MothershipCore",
        "Xenoborg",
    }.ToFrozenSet(EqualityComparer<string>.Default);

    [Dependency] private readonly GameTicker _gameTicker = default!;
    [Dependency] private readonly HolidaySystem _holidays = default!;
    [Dependency] private readonly IPlayerManager _player = default!;
    [Dependency] private readonly SharedJobSystem _jobs = default!;
    [Dependency] private readonly StationJobsSystem _stationJobs = default!;
    [Dependency] private readonly StationSystem _station = default!;

    /// <summary>
    /// Builds a fresh immutable snapshot for the requested wave kind.
    /// </summary>
    public SnapshotBuildResult BuildSnapshot(IntentionsSnapshotRequest request)
    {
        var gameMode = _gameTicker.CurrentPreset?.ID ?? _gameTicker.Preset?.ID ?? "unknown";
        var stationTime = _gameTicker.RoundDuration();
        var stationName = GetStationName();
        var antagSummary = BuildAntagSummary();

        var candidates = new List<CandidateFacts>();
        var query = EntityQueryEnumerator<MindComponent>();
        while (query.MoveNext(out var mindId, out var mind))
        {
            if (TryBuildCandidateFacts(mindId, mind, out var facts))
                candidates.Add(facts);
        }

        return IntentionsSnapshotFactory.Build(
            request,
            gameMode,
            stationTime,
            stationName,
            eventTags: GetEventTags(),
            candidates,
            antagSummary: antagSummary);
    }

    private bool TryBuildCandidateFacts(EntityUid mindId, MindComponent mind, [NotNullWhen(true)] out CandidateFacts? facts)
    {
        facts = null;

        if (mind.UserId is not { } userId)
            return false;

        if (!_player.TryGetSessionById(userId, out var session) || session.Status != SessionStatus.InGame)
            return false;

        if (mind.TimeOfDeath is not null)
            return false;

        if (mind.OwnedEntity is not { } owned || TerminatingOrDeleted(owned))
            return false;

        if (!TryGetEligibleCrewJob(mindId, userId, owned, out var job))
            return false;

        var department = GetDepartment(job);
        var traits = GetTraits(session);
        var (antagRoles, isGhostRoleAntag) = GetAntagRoles(mind);
        var objectiveTypes = GetObjectiveTypes(mind);

        TryComp<HumanoidAppearanceComponent>(owned, out var profile);

        facts = new CandidateFacts(
            mindId,
            userId,
            owned,
            mind.CharacterName ?? Name(owned),
            job,
            department,
            profile?.Age,
            profile?.Species.Id,
            GetSex(profile),
            traits,
            HasComp<MindShieldComponent>(owned),
            antagRoles,
            objectiveTypes,
            isGhostRoleAntag);

        return true;
    }

    /// <summary>
    /// Returns the current crew job id when the owner still qualifies as an active crew participant for Intentions.
    /// </summary>
    private bool TryGetEligibleCrewJob(
        EntityUid mindId,
        NetUserId userId,
        EntityUid ownedEntity,
        [NotNullWhen(true)] out string? jobId)
    {
        jobId = null;

        // Intentions currently distribute only to active crew who still occupy a live crew job slot.
        // Silicon bodies are excluded, and a player stops being snapshot-eligible as soon as they no longer
        // hold the matching crew job counter even if the mind itself is still active elsewhere.
        if (HasComp<BorgChassisComponent>(ownedEntity) || HasComp<StationAiHeldComponent>(ownedEntity))
            return false;

        if (!_jobs.MindTryGetJobId(mindId, out var currentJob) || currentJob is not { } actualJob)
            return false;

        if (!_stationJobs.PlayerHoldsJobOnAnyStation(userId, actualJob))
            return false;

        jobId = actualJob.Id;
        return true;
    }

    /// <summary>
    /// Builds a round-wide antagonist summary without applying the crew-only snapshot candidate filter.
    /// </summary>
    private AntagSummary BuildAntagSummary()
    {
        var totalCount = 0;
        var gameModeAntagCount = 0;
        var ghostRoleAntagCount = 0;
        var byRole = new Dictionary<string, int>(EqualityComparer<string>.Default);
        var byObjectiveType = new Dictionary<string, int>(EqualityComparer<string>.Default);

        var query = EntityQueryEnumerator<MindComponent>();
        while (query.MoveNext(out _, out var mind))
        {
            if (!TryBuildAntagSummaryEntry(mind, out var roles, out var objectiveTypes, out var isGhostRoleAntag))
                continue;

            totalCount++;
            if (isGhostRoleAntag)
                ghostRoleAntagCount++;
            else
                gameModeAntagCount++;

            foreach (var role in roles)
            {
                byRole[role] = byRole.TryGetValue(role, out var current) ? current + 1 : 1;
            }

            foreach (var objectiveType in objectiveTypes)
            {
                byObjectiveType[objectiveType] = byObjectiveType.TryGetValue(objectiveType, out var current) ? current + 1 : 1;
            }
        }

        return new AntagSummary(
            totalCount,
            gameModeAntagCount,
            ghostRoleAntagCount,
            byRole.ToImmutableDictionary(EqualityComparer<string>.Default),
            byObjectiveType.ToImmutableDictionary(EqualityComparer<string>.Default));
    }

    /// <summary>
    /// Resolves one active antagonist participant for round predicates independently from crew snapshot eligibility.
    /// </summary>
    private bool TryBuildAntagSummaryEntry(
        MindComponent mind,
        out ImmutableArray<string> antagRoles,
        out ImmutableArray<string> objectiveTypes,
        out bool isGhostRoleAntag)
    {
        antagRoles = ImmutableArray<string>.Empty;
        objectiveTypes = ImmutableArray<string>.Empty;
        isGhostRoleAntag = false;

        if (mind.UserId is not { } userId)
            return false;

        if (!_player.TryGetSessionById(userId, out var session) || session.Status != SessionStatus.InGame)
            return false;

        if (mind.TimeOfDeath is not null)
            return false;

        if (mind.OwnedEntity is not { } ownedEntity || TerminatingOrDeleted(ownedEntity))
            return false;

        (antagRoles, isGhostRoleAntag) = GetAntagRoles(mind);
        if (antagRoles.Length == 0 && !isGhostRoleAntag)
            return false;

        objectiveTypes = GetObjectiveTypes(mind);
        return true;
    }

    private string? GetDepartment(string? job)
    {
        if (job is null)
            return null;

        return _jobs.TryGetPrimaryDepartment(job, out var department)
            ? department.ID
            : null;
    }

    private ImmutableArray<string> GetTraits(ICommonSession session)
    {
        try
        {
            return _gameTicker.GetPlayerProfile(session)
                .TraitPreferences
                .Select(trait => trait.Id)
                .OrderBy(trait => trait, Comparer<string>.Default)
                .ToImmutableArray();
        }
        catch
        {
            return ImmutableArray<string>.Empty;
        }
    }

    private static string? GetSex(HumanoidAppearanceComponent? profile)
    {
        if (profile is null)
            return null;

        return profile.Sex switch
        {
            Sex.Male => nameof(Sex.Male),
            Sex.Female => nameof(Sex.Female),
            Sex.Unsexed => nameof(Sex.Unsexed),
            _ => null,
        };
    }

    private (ImmutableArray<string> Roles, bool IsGhostRoleAntag) GetAntagRoles(MindComponent mind)
    {
        var roles = new List<string>();
        var hasGhostRoleMarker = false;

        foreach (var roleUid in mind.MindRoles)
        {
            if (!TryComp<MindRoleComponent>(roleUid, out var role))
                continue;

            if (!role.Antag && !role.ExclusiveAntag)
                continue;

            if (role.AntagPrototype is { } antagPrototype)
                roles.Add(antagPrototype.Id);

            if (HasComp<GhostRoleMarkerRoleComponent>(roleUid))
                hasGhostRoleMarker = true;
        }

        var orderedRoles = roles
            .OrderBy(role => role, Comparer<string>.Default)
            .ToImmutableArray();
        var isGhostRoleAntag = hasGhostRoleMarker || IsGhostRoleAntagOverride(orderedRoles);
        return (orderedRoles, isGhostRoleAntag);
    }

    /// <summary>
    /// Applies Intentions-local overrides for major ghost-role antagonists whose antag roles should
    /// count as ghost-role antags even when the shared mind role lacks the ghost-role marker.
    /// </summary>
    internal static bool IsGhostRoleAntagOverride(IEnumerable<string> antagRoles)
    {
        foreach (var antagRole in antagRoles)
        {
            if (GhostRoleAntagOverrides.Contains(antagRole))
                return true;
        }

        return false;
    }

    private ImmutableArray<string> GetObjectiveTypes(MindComponent mind)
    {
        var objectiveTypes = new List<string>();

        foreach (var objective in mind.Objectives)
        {
            if (TerminatingOrDeleted(objective))
                continue;

            if (MetaData(objective).EntityPrototype?.ID is { } prototypeId)
                objectiveTypes.Add(prototypeId);
        }

        return objectiveTypes.ToImmutableArray();
    }

    private string GetStationName()
    {
        return _station.GetStationNames()
            .Select(station => station.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderBy(name => name, Comparer<string>.Default)
            .FirstOrDefault() ?? "station";
    }

    /// <summary>
    /// Returns the currently active holiday ids used as event tags for snapshot predicates.
    /// </summary>
    private ImmutableArray<string> GetEventTags()
    {
        return _holidays.GetCurrentHolidays()
            .Select(holiday => holiday.ID)
            .Distinct(EqualityComparer<string>.Default)
            .OrderBy(holiday => holiday, Comparer<string>.Default)
            .ToImmutableArray();
    }
}
