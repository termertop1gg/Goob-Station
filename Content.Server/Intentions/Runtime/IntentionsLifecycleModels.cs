using Content.Shared.Intentions.Runtime;
using Robust.Shared.GameObjects;

namespace Content.Server.Intentions.Runtime;

/// <summary>
/// Describes whether a scenario owner can currently support an assigned runtime scenario.
/// </summary>
public enum OwnerAvailabilityStatus : byte
{
    Available,
    TemporarilyMissing,
    PermanentlyUnavailable,
}

/// <summary>
/// Captures the resolved availability state for one owner mind during lifecycle reconciliation.
/// </summary>
public sealed record OwnerAvailabilityResult(
    EntityUid MindId,
    OwnerAvailabilityStatus Status,
    EntityUid? CurrentOwnerEntityUid,
    string Reason)
{
    /// <summary>
    /// Creates an availability result for an owner that is currently present in a usable body.
    /// </summary>
    public static OwnerAvailabilityResult Available(EntityUid mindId, EntityUid ownerEntityUid, string reason = "owner-available")
    {
        return new OwnerAvailabilityResult(mindId, OwnerAvailabilityStatus.Available, ownerEntityUid, reason);
    }

    /// <summary>
    /// Creates an availability result for an owner that may return later in the round.
    /// </summary>
    public static OwnerAvailabilityResult TemporarilyMissing(EntityUid mindId, string reason)
    {
        return new OwnerAvailabilityResult(mindId, OwnerAvailabilityStatus.TemporarilyMissing, null, reason);
    }

    /// <summary>
    /// Creates an availability result for an owner that should no longer hold the scenario.
    /// </summary>
    public static OwnerAvailabilityResult PermanentlyUnavailable(EntityUid mindId, string reason)
    {
        return new OwnerAvailabilityResult(mindId, OwnerAvailabilityStatus.PermanentlyUnavailable, null, reason);
    }
}

/// <summary>
/// Represents the outcome of one lifecycle transition attempt for a runtime scenario.
/// </summary>
public sealed class LifecycleOperationResult
{
    private LifecycleOperationResult(bool isSuccess, ScenarioInstanceUid? scenarioUid, string? failureReason)
    {
        IsSuccess = isSuccess;
        ScenarioUid = scenarioUid;
        FailureReason = failureReason;
    }

    /// <summary>
    /// Indicates whether the lifecycle action completed successfully.
    /// </summary>
    public bool IsSuccess { get; }
    /// <summary>
    /// Identifies the affected scenario when the operation reached a concrete runtime object.
    /// </summary>
    public ScenarioInstanceUid? ScenarioUid { get; }
    /// <summary>
    /// Contains the failure code when the lifecycle action could not be completed.
    /// </summary>
    public string? FailureReason { get; }

    /// <summary>
    /// Creates a successful lifecycle result for the provided scenario.
    /// </summary>
    public static LifecycleOperationResult Success(ScenarioInstanceUid scenarioUid)
    {
        return new LifecycleOperationResult(true, scenarioUid, null);
    }

    /// <summary>
    /// Creates a failed lifecycle result with an optional affected scenario id.
    /// </summary>
    public static LifecycleOperationResult Failure(string failureReason, ScenarioInstanceUid? scenarioUid = null)
    {
        return new LifecycleOperationResult(false, scenarioUid, failureReason);
    }
}
