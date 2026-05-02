using System;
using Content.Shared.Intentions.Prototypes;

namespace Content.Server.Intentions.Waves;

/// <summary>
/// Calculates category quotas and per-mind primary limits for one wave.
/// </summary>
public static class IntentionsQuotaCalculator
{
    /// <summary>
    /// Calculates the target quota for one category using the configured game-mode override or default rule.
    /// </summary>
    public static int CalculateTargetQuota(ScenarioCategoryPrototype category, string gameMode, int distributionCrewBaseline)
    {
        if (!TryGetQuotaRule(category, gameMode, out var rule))
            return 0;

        var target = rule.Mode switch
        {
            "fixed" => rule.Value ?? 0,
            "ratio" => (int) Math.Floor(distributionCrewBaseline * (rule.Ratio ?? 0f)),
            "clamp" => CalculateClampedQuota(rule, distributionCrewBaseline),
            _ => 0,
        };

        return Math.Max(0, target);
    }

    /// <summary>
    /// Calculates the effective per-mind primary cap for one category.
    /// </summary>
    public static int CalculateEffectiveMaxPrimaryPerMind(ScenarioCategoryPrototype category, string gameMode)
    {
        if (!category.MaxPrimaryPerMindByGameMode.TryGetValue(gameMode, out var value)
            && !category.MaxPrimaryPerMindByGameMode.TryGetValue("default", out value))
        {
            return 0;
        }

        return Math.Max(0, value);
    }

    /// <summary>
    /// Tries to resolve the quota rule for the active game mode with fallback to the default rule.
    /// </summary>
    private static bool TryGetQuotaRule(ScenarioCategoryPrototype category, string gameMode, out QuotaRule rule)
    {
        if (category.QuotaByGameMode.TryGetValue(gameMode, out rule!))
            return true;

        return category.QuotaByGameMode.TryGetValue("default", out rule!);
    }

    /// <summary>
    /// Calculates a clamped quota defensively so debug-created invalid data cannot throw in runtime helpers.
    /// </summary>
    private static int CalculateClampedQuota(QuotaRule rule, int distributionCrewBaseline)
    {
        var baseQuota = (int) Math.Floor(distributionCrewBaseline * (rule.Ratio ?? 0f));
        var min = Math.Max(0, rule.Min ?? 0);
        var max = Math.Max(min, rule.Max ?? min);
        return Math.Clamp(baseQuota, min, max);
    }
}
