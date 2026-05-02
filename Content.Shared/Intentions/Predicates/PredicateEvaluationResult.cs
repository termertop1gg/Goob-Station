using System.Collections.Immutable;
using System.Linq;
using Robust.Shared.GameObjects;

namespace Content.Shared.Intentions.Predicates;

/// <summary>
/// Captures the result of evaluating one predicate list against a snapshot or candidate.
/// </summary>
public sealed class PredicateEvaluationResult
{
    /// <summary>
    /// Creates a predicate evaluation result.
    /// </summary>
    public PredicateEvaluationResult(bool isMatch, bool hasError, ImmutableArray<PredicateRejectReason> rejectReasons)
    {
        IsMatch = isMatch;
        HasError = hasError;
        RejectReasons = rejectReasons.IsDefault ? ImmutableArray<PredicateRejectReason>.Empty : rejectReasons;
    }

    /// <summary>
    /// Whether all predicates matched successfully.
    /// </summary>
    public bool IsMatch { get; }

    /// <summary>
    /// Whether the evaluation encountered at least one runtime-shape error.
    /// </summary>
    public bool HasError { get; }

    /// <summary>
    /// Reject reasons explaining false predicates and runtime evaluation problems.
    /// </summary>
    public ImmutableArray<PredicateRejectReason> RejectReasons { get; }

    /// <summary>
    /// Builds a successful result without reject reasons.
    /// </summary>
    public static PredicateEvaluationResult Pass()
    {
        return new PredicateEvaluationResult(true, false, ImmutableArray<PredicateRejectReason>.Empty);
    }

    /// <summary>
    /// Builds a result from the collected reject reasons.
    /// </summary>
    public static PredicateEvaluationResult FromReasons(ImmutableArray<PredicateRejectReason> reasons)
    {
        if (reasons.IsDefaultOrEmpty)
            return Pass();

        return new PredicateEvaluationResult(false, reasons.Any(reason => reason.IsError), reasons);
    }
}

/// <summary>
/// Explains why a single predicate failed or could not be evaluated safely at runtime.
/// </summary>
public sealed record PredicateRejectReason(
    string Code,
    string Scope,
    string Field,
    string Operator,
    int PredicateIndex,
    string Message,
    EntityUid? CandidateMindId = null,
    string? ComparedSlotId = null,
    string? Expected = null,
    string? Actual = null,
    bool IsError = false);
