using BIM.DatViet.Models;

namespace BIM.DatViet.Domain;

public enum AbutmentActionGateState
{
    SelectZone,
    /// <summary>Canonical geometry zone is missing or stale.</summary>
    BindCanonicalZone,
    NoApprovedRule,
    ResolveBlockingIssues,
    MissingPlannedBars,
    ReadyCreate,
    ReadyRebuild
}

public readonly record struct AbutmentActionGateInput(
    bool HasZoneChoice,
    bool HasCanonicalZone,
    bool HasEnabledRule,
    bool PlanCanCreate,
    bool HasPlannedBars,
    bool HasManagedRebars);

public readonly record struct AbutmentActionGateResult(
    AbutmentActionGateState State,
    bool CanCreate,
    bool CanRebuild);

public static class AbutmentActionGate
{
    public static AbutmentActionGateResult Evaluate(AbutmentActionGateInput input)
    {
        if (!input.HasZoneChoice)
            return Blocked(AbutmentActionGateState.SelectZone);
        if (!input.HasEnabledRule)
            return Blocked(AbutmentActionGateState.NoApprovedRule);
        if (!input.HasCanonicalZone)
            return Blocked(AbutmentActionGateState.BindCanonicalZone);
        if (!input.PlanCanCreate)
            return Blocked(AbutmentActionGateState.ResolveBlockingIssues);
        if (!input.HasPlannedBars)
            return Blocked(AbutmentActionGateState.MissingPlannedBars);

        // The immutable snapshot is still mandatory, but it is captured internally
        // at the start of Create/Rebuild instead of requiring a redundant user step.
        return input.HasManagedRebars
            ? new AbutmentActionGateResult(
                AbutmentActionGateState.ReadyRebuild,
                CanCreate: false,
                CanRebuild: true)
            : new AbutmentActionGateResult(
                AbutmentActionGateState.ReadyCreate,
                CanCreate: true,
                CanRebuild: false);
    }

    public static IReadOnlyList<AbutmentZoneKind> SelectableZones(
        IEnumerable<AbutmentZoneKind> recognizedZones,
        IEnumerable<AbutmentZoneKind> enabledRuleZones)
    {
        // Callers must pass only zones that currently have enabled rules.
        var enabled = enabledRuleZones.ToHashSet();
        var recognized = recognizedZones.ToHashSet();
        return enabled
            .Where(kind => kind != AbutmentZoneKind.Footing || recognized.Contains(kind))
            .Distinct()
            .OrderBy(kind => kind)
            .ToList();
    }

    public static IReadOnlyList<AbutmentZoneRule> VisibleRulesForZone(
        IEnumerable<AbutmentZoneRule> rules,
        AbutmentZoneKind zone) =>
        rules
            .Where(rule =>
                rule.Zone == zone &&
                !string.IsNullOrWhiteSpace(rule.RuleId) &&
                !string.IsNullOrWhiteSpace(rule.CanonicalMark) &&
                !string.IsNullOrWhiteSpace(rule.SourceDrawingMark))
            .OrderByDescending(rule => rule.Enabled)
            .ThenBy(rule => rule.CanonicalMark, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static AbutmentActionGateResult Blocked(AbutmentActionGateState state) =>
        new(state, CanCreate: false, CanRebuild: false);
}
