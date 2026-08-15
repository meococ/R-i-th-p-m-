$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding $false

# Models: add helper on AbutmentRebarPlan
$models = 'Models\AbutmentModels.cs'
$mt = [IO.File]::ReadAllText((Resolve-Path $models), [Text.Encoding]::UTF8)
if ($mt -notmatch 'CanCreateZone') {
    $mt = $mt.Replace(
        'public bool CanCreate => Issues.All(issue => issue.Severity != ValidationSeverity.Error);',
@'
public bool CanCreate => Issues.All(issue => issue.Severity != ValidationSeverity.Error);

    /// <summary>
    /// Zone-scoped readiness: host-level errors still block every zone, but a failed Stem rule
    /// must not prevent Create on Footing when Footing bars are valid.
    /// </summary>
    public bool CanCreateZone(AbutmentZoneKind zone)
    {
        if (Bars.All(bar => bar.Zone != zone)) return false;
        foreach (var issue in Issues.Where(item => item.Severity == ValidationSeverity.Error))
        {
            if (IsHostLevelBlocker(issue.Code)) return false;
            if (IsRuleScopedGeometryOrTypeIssue(issue.Code))
            {
                // Block only when the message references a rule of this zone or no rule id is present.
                var zoneRuleIds = Bars.Where(bar => bar.Zone == zone)
                    .Select(bar => bar.Rule.RuleId)
                    .Concat(Preset.Rules.Where(rule => rule.Zone == zone && rule.Enabled).Select(rule => rule.RuleId))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (zoneRuleIds.Count == 0) return false;
                if (zoneRuleIds.Any(id => issue.Message.Contains(id, StringComparison.OrdinalIgnoreCase)))
                    return false;
                continue;
            }
            // Unknown error: fail closed for the zone.
            return false;
        }
        return true;
    }

    private static bool IsHostLevelBlocker(string code) =>
        code is "ABUTMENT_CANONICAL_ZONE_BIND_FAILED"
            or "REBAR_BAR_TYPE_CATALOG_EMPTY"
            or "ABUTMENT_PLAN_BUILD_FAILED"
            or "ABUTMENT_FOOTING_TOP_UNRESOLVED"
            or "ABUTMENT_FOOTING_DIMENSIONS_AMBIGUOUS"
            or "ABUTMENT_LOCAL_FRAME_AMBIGUOUS"
            or "ABUTMENT_FOOTING_BOUNDARY_UNRESOLVED"
            or "ABUTMENT_ZONE_NOT_UNIQUE";

    private static bool IsRuleScopedGeometryOrTypeIssue(string code) =>
        code is "ABUTMENT_BAR_GEOMETRY_UNRESOLVED"
            or "REBAR_BAR_TYPE_NOT_FOUND"
            or "REBAR_BAR_TYPE_DIAMETER_MISMATCH"
            or "REBAR_COVER_UNRESOLVED"
            or "ABUTMENT_RULE_ZONE_MISSING"
            or "ABUTMENT_MAT_LAYER_CLASH";
'@)
    [IO.File]::WriteAllText((Resolve-Path $models), $mt, $utf8)
    Write-Host 'Models patched'
} else { Write-Host 'Models already has CanCreateZone' }

# ViewModel gate
$vm = 'ViewModels\AbutmentRebarViewModel.cs'
$vt = [IO.File]::ReadAllText((Resolve-Path $vm), [Text.Encoding]::UTF8)
$vt2 = $vt.Replace(
    'PlanCanCreate: !_coverDraftDirty && !_lastOperationFailed && plan is not null && plan.CanCreate,',
    'PlanCanCreate: !_coverDraftDirty && !_lastOperationFailed && plan is not null && plan.CanCreateZone(SelectedZoneKind),')
if ($vt2 -eq $vt) { Write-Host 'WARN: ViewModel PlanCanCreate pattern not replaced' } else {
    [IO.File]::WriteAllText((Resolve-Path $vm), $vt2, $utf8)
    Write-Host 'ViewModel gate patched'
}

# Controller snapshot uses plan.CanCreate - change to zone
$ctl = 'Controllers\AbutmentRebarController.cs'
$ct = [IO.File]::ReadAllText((Resolve-Path $ctl), [Text.Encoding]::UTF8)
$ct2 = $ct.Replace(
    'if (plan is null || !plan.CanCreate)',
    'if (plan is null || !plan.CanCreateZone(zone))')
if ($ct2 -eq $ct) { Write-Host 'WARN: controller snapshot pattern not replaced' } else {
    [IO.File]::WriteAllText((Resolve-Path $ctl), $ct2, $utf8)
    Write-Host 'Controller snapshot patched'
}

# Factory still checks plan.CanCreate - change to snapshot zone
$fac = 'RevitServices\AbutmentRebarFactory.cs'
$ft = [IO.File]::ReadAllText((Resolve-Path $fac), [Text.Encoding]::UTF8)
if ($ft -match 'if \(!plan\.CanCreate\)') {
    $ft = $ft.Replace('if (!plan.CanCreate)', 'if (!plan.CanCreateZone(snapshot.Zone))')
    # need snapshot in scope - check method signature
    [IO.File]::WriteAllText((Resolve-Path $fac), $ft, $utf8)
    Write-Host 'Factory patched (verify snapshot in scope)'
} else { Write-Host 'Factory pattern missing or already patched' }
