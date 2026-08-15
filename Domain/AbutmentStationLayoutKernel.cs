using BIM.DatViet.Models;

namespace BIM.DatViet.Domain;

/// <summary>What one entry of a station layout contributes to the chain.</summary>
public enum AbutmentStationStepKind
{
    /// <summary>Default value: a step that never declared its kind must fail closed.</summary>
    Unresolved,
    /// <summary>An evenly spaced group of bars.</summary>
    Run,
    /// <summary>A single non-uniform offset to the next bar.</summary>
    Jump
}

/// <summary>
/// One entry of a station layout. Steps are built through the factory methods only, so a step can
/// never carry the fields of the other kind.
/// </summary>
public readonly struct AbutmentStationStep
{
    private AbutmentStationStep(
        AbutmentStationStepKind kind,
        double? startMm,
        double spacingMm,
        int spanCount,
        double offsetMm)
    {
        Kind = kind;
        StartMm = startMm;
        SpacingMm = spacingMm;
        SpanCount = spanCount;
        OffsetMm = offsetMm;
    }

    public AbutmentStationStepKind Kind { get; }

    /// <summary>
    /// Offset of the first bar of a run from the anchored edge, or null when the run continues from
    /// the last bar of the previous step.
    /// </summary>
    public double? StartMm { get; }

    /// <summary>Even spacing of a run, in millimeters.</summary>
    public double SpacingMm { get; }

    /// <summary>
    /// Number of even spaces inside a run. A run that declares its own start adds one bar more than
    /// it has spaces; a continuing run reuses the previous bar as its first station.
    /// </summary>
    public int SpanCount { get; }

    /// <summary>Non-uniform offset of a jump, in millimeters.</summary>
    public double OffsetMm { get; }

    /// <summary>An evenly spaced group starting at a declared offset from the anchored edge.</summary>
    public static AbutmentStationStep Run(double startMm, double spacingMm, int spanCount) =>
        new(AbutmentStationStepKind.Run, startMm, spacingMm, spanCount, 0);

    /// <summary>An evenly spaced group whose first station is the last station already placed.</summary>
    public static AbutmentStationStep ContinuedRun(double spacingMm, int spanCount) =>
        new(AbutmentStationStepKind.Run, null, spacingMm, spanCount, 0);

    /// <summary>A single non-uniform offset from the last station to the next one.</summary>
    public static AbutmentStationStep Jump(double offsetMm) =>
        new(AbutmentStationStepKind.Jump, null, 0, 0, offsetMm);
}

/// <summary>
/// A band that one layer deliberately leaves without bars. An unexplained gap cannot be told apart
/// from a lost bar, so the reason and the drawing address that authorise it are both mandatory.
/// </summary>
public readonly record struct AbutmentStationBlank(
    double StartMm,
    double EndMm,
    string Reason,
    AbutmentRuleSourceLocator? DrawingAddress);

/// <summary>
/// Describes one layer the way the drawing states it: the anchored concrete edge, the axis the
/// dimension chain runs along, the run/jump chain and the bands left without bars.
/// </summary>
public sealed record AbutmentStationLayout
{
    /// <summary>
    /// Semantic concrete edge carrying offset 0. <see cref="AbutmentStationDatumEdge.Unresolved"/>
    /// is accepted only for a mirror-symmetric chain, where both edges yield the same bars.
    /// </summary>
    public AbutmentStationDatumEdge Anchor { get; init; } = AbutmentStationDatumEdge.Unresolved;

    /// <summary>
    /// Axis the drawing measured the chain along. Anything other than
    /// <see cref="AbutmentStationReferenceAxis.PerpendicularToBar"/> means the solved chain still
    /// has to be projected normal to the bar before it becomes a bar station.
    /// </summary>
    public AbutmentStationReferenceAxis MeasuredAlong { get; init; } =
        AbutmentStationReferenceAxis.PerpendicularToBar;

    /// <summary>
    /// Width of the usable concrete band in millimeters, measured along <see cref="MeasuredAlong"/>
    /// so it can be compared with the chain without a projection.
    /// </summary>
    public double AvailableWidthMm { get; init; }

    public IReadOnlyList<AbutmentStationStep> Steps { get; init; } = [];

    public IReadOnlyList<AbutmentStationBlank> Blanks { get; init; } = [];
}

/// <summary>
/// A solved chain of bar-centerline offsets in millimeters, strictly increasing from the anchored
/// concrete edge, plus the stations a blank removed so the loss stays auditable.
/// </summary>
public sealed record AbutmentStationChain(
    IReadOnlyList<double> OffsetsMm,
    IReadOnlyList<double> BlankedOffsetsMm)
{
    public int BarCount => OffsetsMm.Count;
}

/// <summary>
/// Expands a drawing-shaped station description into bar-centerline offsets in millimeters. The
/// description carries only the dimensions a drawing actually states, so a layer no longer needs a
/// hand-multiplied list of one bridge's offsets to be planned. Every length is a millimeter; feet
/// never enter this kernel.
/// </summary>
public static class AbutmentStationLayoutKernel
{
    /// <summary>
    /// Numeric separation two neighbouring stations must exceed to count as strictly increasing.
    /// Physical bar clearance is a layer rule, not a property of the chain.
    /// </summary>
    public const double StationSeparationToleranceMm = 1e-6;

    /// <summary>
    /// Millimeters the solved chain may differ from the explicit chain a rule still declares and
    /// still count as the same chain. Wide enough for the six-decimal rounding the preset stores,
    /// tight enough that half a millimeter of drift is a mismatch.
    /// </summary>
    public const double DeclaredChainMatchToleranceMm = 1e-3;

    private static readonly AbutmentStationChain EmptyChain = new([], []);

    /// <summary>
    /// Cross-checks the chain a rule declares explicitly against the same chain solved from its
    /// drawing-shaped description, so the two sources keep proving each other while both are kept.
    /// <paramref name="barDirection"/>, <paramref name="measuredDirection"/> and
    /// <paramref name="concreteSpanNormalToBarMm"/> must all come from the measured footing
    /// boundary: the description states which axis the drawing used, never how steep it is.
    /// </summary>
    public static bool TryVerifyDeclaredChain(
        AbutmentStationSchedule schedule,
        IReadOnlyList<double> declaredOffsetsNormalToBarMm,
        double concreteSpanNormalToBarMm,
        PlanPoint2 barDirection,
        PlanPoint2 measuredDirection,
        double solveToleranceMm,
        string ruleLabel,
        out AbutmentStationChain chain,
        out string error)
    {
        chain = EmptyChain;
        error = string.Empty;
        if (schedule?.StationLayout is not { } spec)
        {
            error = $"ABUTMENT_STATION_LAYOUT_UNDECLARED: {ruleLabel} chưa khai stationLayout để đối chiếu.";
            return false;
        }
        // Each chain is read from the edge its own field names, so an anchor that contradicts the
        // schedule datum would compare two mirrored readings value by value and still pass.
        if (schedule.DatumEdge != AbutmentStationDatumEdge.Unresolved &&
            spec.Anchor != AbutmentStationDatumEdge.Unresolved &&
            spec.Anchor != schedule.DatumEdge)
        {
            error = $"ABUTMENT_STATION_LAYOUT_ANCHOR_CONFLICT: {ruleLabel} khai stationLayout.anchor " +
                    $"{spec.Anchor} nhưng stationSchedule.datumEdge là {schedule.DatumEdge}; " +
                    "hai chuỗi sẽ được đọc từ hai cạnh bê tông khác nhau.";
            return false;
        }

        if (!TryMeasureAvailableWidth(
                spec.MeasuredAlong, concreteSpanNormalToBarMm, barDirection, measuredDirection,
                out var availableWidthMm, out error))
            return false;
        if (!TryBuildLayout(spec, availableWidthMm, out var layout, out error)) return false;
        if (!TrySolveNormalToBar(
                layout, solveToleranceMm, barDirection, measuredDirection, out chain, out error))
            return false;

        if (chain.BarCount != declaredOffsetsNormalToBarMm.Count)
        {
            error = $"ABUTMENT_STATION_LAYOUT_CHAIN_MISMATCH: {ruleLabel} giải ra {chain.BarCount} ga " +
                    $"nhưng centerlineOffsetsMm đang khai {declaredOffsetsNormalToBarMm.Count} ga.";
            chain = EmptyChain;
            return false;
        }
        for (var index = 0; index < chain.BarCount; index++)
        {
            var deviationMm = Math.Abs(chain.OffsetsMm[index] - declaredOffsetsNormalToBarMm[index]);
            if (deviationMm <= DeclaredChainMatchToleranceMm) continue;
            error = $"ABUTMENT_STATION_LAYOUT_CHAIN_MISMATCH: {ruleLabel} ga #{index + 1} giải ra " +
                    $"{chain.OffsetsMm[index]:0.######} mm nhưng centerlineOffsetsMm khai " +
                    $"{declaredOffsetsNormalToBarMm[index]:0.######} mm, lệch {deviationMm:0.######} mm " +
                    $"vượt dung sai {DeclaredChainMatchToleranceMm:0.######} mm.";
            chain = EmptyChain;
            return false;
        }
        return true;
    }

    /// <summary>
    /// Converts the concrete band measured normal to the bar into the same band measured along the
    /// axis the drawing dimensioned. The scale is the one projection
    /// <see cref="GeometryKernel.ProjectOffsetsNormalToBar"/> applies, so the chain and the band it
    /// has to fit inside stay in one unit.
    /// </summary>
    public static bool TryMeasureAvailableWidth(
        AbutmentStationReferenceAxis measuredAlong,
        double concreteSpanNormalToBarMm,
        PlanPoint2 barDirection,
        PlanPoint2 measuredDirection,
        out double availableWidthMm,
        out string error)
    {
        availableWidthMm = concreteSpanNormalToBarMm;
        error = string.Empty;
        if (measuredAlong == AbutmentStationReferenceAxis.PerpendicularToBar) return true;

        try
        {
            var bar = barDirection.Normalize();
            var measured = measuredDirection.Normalize();
            var scale = Math.Abs(measured.Dot(new PlanPoint2(-bar.Y, bar.X)));
            if (scale <= 1e-9)
                throw new InvalidOperationException(
                    "Station reference axis is parallel to the bar; its offsets cannot define bar stations.");
            availableWidthMm = concreteSpanNormalToBarMm / scale;
            return true;
        }
        catch (InvalidOperationException exception)
        {
            availableWidthMm = 0;
            error = $"ABUTMENT_STATION_REFERENCE_INVALID: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// Maps the preset description onto the kernel layout. The band width is supplied by the
    /// caller from measured geometry and is deliberately absent from the description itself.
    /// </summary>
    public static bool TryBuildLayout(
        AbutmentStationLayoutSpec spec,
        double availableWidthMm,
        out AbutmentStationLayout layout,
        out string error)
    {
        layout = new AbutmentStationLayout();
        error = string.Empty;
        if (spec is null || spec.Steps is null || spec.Blanks is null)
        {
            error = "ABUTMENT_STATION_LAYOUT_UNDECLARED: chưa có mô tả bố trí ga đầy đủ để giải.";
            return false;
        }

        var steps = new List<AbutmentStationStep>(spec.Steps.Count);
        for (var index = 0; index < spec.Steps.Count; index++)
        {
            var step = spec.Steps[index];
            var label = $"bước #{index + 1}";
            switch (step.Kind)
            {
                case AbutmentStationStepForm.Run when step.StartMm is { } startMm:
                    steps.Add(AbutmentStationStep.Run(startMm, step.SpacingMm, step.SpanCount));
                    break;
                case AbutmentStationStepForm.Run:
                    error = $"ABUTMENT_STATION_LAYOUT_START_REQUIRED: {label} là đoạn rải đều " +
                            "nhưng thiếu startMm; đoạn nối tiếp phải khai kind ContinuedRun.";
                    return false;
                case AbutmentStationStepForm.ContinuedRun when step.StartMm is null:
                    steps.Add(AbutmentStationStep.ContinuedRun(step.SpacingMm, step.SpanCount));
                    break;
                case AbutmentStationStepForm.ContinuedRun:
                    error = $"ABUTMENT_STATION_LAYOUT_RUN_INVALID: {label} nối tiếp đoạn trước " +
                            $"nhưng vẫn khai startMm = {step.StartMm} mm.";
                    return false;
                case AbutmentStationStepForm.Jump:
                    steps.Add(AbutmentStationStep.Jump(step.OffsetMm));
                    break;
                default:
                    error = $"ABUTMENT_STATION_LAYOUT_STEP_UNRESOLVED: {label} chưa khai là đoạn rải đều hay khoảng lệch chuẩn.";
                    return false;
            }
        }

        layout = new AbutmentStationLayout
        {
            Anchor = spec.Anchor,
            MeasuredAlong = spec.MeasuredAlong,
            AvailableWidthMm = availableWidthMm,
            Steps = steps,
            Blanks = spec.Blanks
                .Select(blank => new AbutmentStationBlank(
                    blank.StartMm, blank.EndMm, blank.Reason, blank.DrawingAddress))
                .ToList()
        };
        return true;
    }

    /// <summary>
    /// Solves the chain in the unit and axis the layout declares. <paramref name="toleranceMm"/>
    /// covers the available-width fit and the mirror-symmetry test; pass the profile-versus-solid
    /// mismatch tolerance of the topology profile.
    /// </summary>
    public static bool TrySolve(
        AbutmentStationLayout layout,
        double toleranceMm,
        out AbutmentStationChain chain,
        out string error)
    {
        chain = EmptyChain;
        error = string.Empty;
        if (layout is null || layout.Steps is null || layout.Blanks is null)
        {
            error = "ABUTMENT_STATION_LAYOUT_UNDECLARED: chưa có mô tả bố trí ga đầy đủ để giải.";
            return false;
        }
        if (!double.IsFinite(toleranceMm) || toleranceMm <= 0)
        {
            error = $"ABUTMENT_STATION_LAYOUT_TOLERANCE_INVALID: dung sai {toleranceMm} mm không dương hữu hạn.";
            return false;
        }
        if (!double.IsFinite(layout.AvailableWidthMm) || layout.AvailableWidthMm <= 0)
        {
            error = "ABUTMENT_STATION_LAYOUT_AVAILABLE_WIDTH_INVALID: " +
                    $"bề rộng khả dụng {layout.AvailableWidthMm} mm không dương hữu hạn.";
            return false;
        }

        if (!TryExpandSteps(layout.Steps, out var stations, out error)) return false;
        if (!TryValidateStrictIncrease(stations, out error)) return false;
        if (!TryValidateBand(stations, layout.AvailableWidthMm, toleranceMm, out error)) return false;
        if (!TryValidateBlanks(layout.Blanks, layout.AvailableWidthMm, out error)) return false;

        var kept = new List<double>(stations.Count);
        var blanked = new List<double>();
        foreach (var station in stations)
            (IsBlanked(layout.Blanks, station) ? blanked : kept).Add(station);
        if (kept.Count == 0)
        {
            error = "ABUTMENT_STATION_LAYOUT_BLANK_REMOVES_EVERY_STATION: " +
                    $"vùng chừa nuốt trọn {stations.Count} ga nên lớp thép này rỗng.";
            return false;
        }

        if (!TryValidateAnchor(kept, layout, toleranceMm, out error)) return false;
        chain = new AbutmentStationChain(kept, blanked);
        return true;
    }

    /// <summary>
    /// Solves the chain and, when the drawing measured it along a boundary edge, projects it normal
    /// to the bar with <see cref="GeometryKernel.ProjectOffsetsNormalToBar"/>.
    /// <paramref name="measuredDirection"/> must be the boundary direction named by
    /// <see cref="AbutmentStationLayout.MeasuredAlong"/>: the kernel cannot tell the two boundary
    /// axes apart, it only decides whether a projection is owed.
    /// </summary>
    public static bool TrySolveNormalToBar(
        AbutmentStationLayout layout,
        double toleranceMm,
        PlanPoint2 barDirection,
        PlanPoint2 measuredDirection,
        out AbutmentStationChain chain,
        out string error)
    {
        if (!TrySolve(layout, toleranceMm, out chain, out error)) return false;
        if (layout.MeasuredAlong == AbutmentStationReferenceAxis.PerpendicularToBar) return true;

        try
        {
            chain = new AbutmentStationChain(
                GeometryKernel.ProjectOffsetsNormalToBar(
                    chain.OffsetsMm, barDirection, measuredDirection),
                GeometryKernel.ProjectOffsetsNormalToBar(
                    chain.BlankedOffsetsMm, barDirection, measuredDirection));
        }
        catch (InvalidOperationException exception)
        {
            chain = EmptyChain;
            error = $"ABUTMENT_STATION_REFERENCE_INVALID: {exception.Message}";
            return false;
        }

        // The projection is one positive scale, so it keeps the order and the fit; a nearly parallel
        // axis still collapses the chain until neighbouring stations stop being separable.
        if (TryValidateStrictIncrease(chain.OffsetsMm, out error)) return true;
        chain = EmptyChain;
        return false;
    }

    private static bool TryExpandSteps(
        IReadOnlyList<AbutmentStationStep> steps,
        out List<double> stations,
        out string error)
    {
        stations = [];
        error = string.Empty;
        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            var label = $"bước #{index + 1}";
            switch (step.Kind)
            {
                case AbutmentStationStepKind.Run:
                    if (!double.IsFinite(step.SpacingMm) || step.SpacingMm <= 0 || step.SpanCount <= 0)
                    {
                        error = $"ABUTMENT_STATION_LAYOUT_RUN_INVALID: {label} có bước " +
                                $"{step.SpacingMm} mm và {step.SpanCount} nhịp; cả hai phải dương hữu hạn.";
                        return false;
                    }
                    double first;
                    if (step.StartMm is { } declared)
                    {
                        if (!double.IsFinite(declared))
                        {
                            error = $"ABUTMENT_STATION_LAYOUT_RUN_INVALID: {label} khai ga đầu {declared} mm không hữu hạn.";
                            return false;
                        }
                        first = declared;
                        stations.Add(first);
                    }
                    else if (stations.Count == 0)
                    {
                        error = $"ABUTMENT_STATION_LAYOUT_START_REQUIRED: {label} tiếp nối đoạn trước " +
                                "nhưng chưa có ga nào; đoạn đầu tiên phải khai ga bắt đầu.";
                        return false;
                    }
                    else first = stations[^1];
                    for (var span = 1; span <= step.SpanCount; span++)
                        stations.Add(first + span * step.SpacingMm);
                    break;

                case AbutmentStationStepKind.Jump:
                    if (stations.Count == 0)
                    {
                        error = $"ABUTMENT_STATION_LAYOUT_START_REQUIRED: {label} là khoảng lệch chuẩn " +
                                "nhưng chưa có ga nào để lệch khỏi.";
                        return false;
                    }
                    if (!double.IsFinite(step.OffsetMm) || step.OffsetMm <= 0)
                    {
                        error = $"ABUTMENT_STATION_LAYOUT_JUMP_INVALID: {label} có khoảng lệch " +
                                $"{step.OffsetMm} mm; khoảng lệch phải dương hữu hạn.";
                        return false;
                    }
                    stations.Add(stations[^1] + step.OffsetMm);
                    break;

                default:
                    error = $"ABUTMENT_STATION_LAYOUT_STEP_UNRESOLVED: {label} chưa khai là đoạn rải đều hay khoảng lệch chuẩn.";
                    return false;
            }
        }

        if (stations.Count != 0) return true;
        error = "ABUTMENT_STATION_LAYOUT_EMPTY: mô tả bố trí không sinh được ga nào.";
        return false;
    }

    private static bool TryValidateStrictIncrease(IReadOnlyList<double> stations, out string error)
    {
        error = string.Empty;
        for (var index = 1; index < stations.Count; index++)
        {
            if (stations[index] - stations[index - 1] > StationSeparationToleranceMm) continue;
            error = "ABUTMENT_STATION_LAYOUT_NOT_STRICTLY_INCREASING: " +
                    $"ga #{index + 1} = {stations[index]:0.###} mm không lớn hơn " +
                    $"ga #{index} = {stations[index - 1]:0.###} mm.";
            return false;
        }
        return true;
    }

    private static bool TryValidateBand(
        IReadOnlyList<double> stations,
        double availableWidthMm,
        double toleranceMm,
        out string error)
    {
        error = string.Empty;
        for (var index = 0; index < stations.Count; index++)
        {
            var station = stations[index];
            if (station >= -toleranceMm && station <= availableWidthMm + toleranceMm) continue;
            error = "ABUTMENT_STATION_LAYOUT_EXCEEDS_AVAILABLE_WIDTH: " +
                    $"ga #{index + 1} = {station:0.###} mm nằm ngoài dải bê tông " +
                    $"[0; {availableWidthMm:0.###}] mm (dung sai {toleranceMm:0.###} mm).";
            return false;
        }
        return true;
    }

    private static bool TryValidateBlanks(
        IReadOnlyList<AbutmentStationBlank> blanks,
        double availableWidthMm,
        out string error)
    {
        error = string.Empty;
        for (var index = 0; index < blanks.Count; index++)
        {
            var blank = blanks[index];
            var label = $"vùng chừa #{index + 1}";
            if (string.IsNullOrWhiteSpace(blank.Reason) || !HasDrawingAddress(blank.DrawingAddress))
            {
                error = $"ABUTMENT_STATION_LAYOUT_BLANK_UNJUSTIFIED: {label} phải nêu lý do chừa " +
                        "và địa chỉ bản vẽ (drawingCode + page + detail/section/callout).";
                return false;
            }
            if (!double.IsFinite(blank.StartMm) || !double.IsFinite(blank.EndMm) ||
                blank.EndMm - blank.StartMm <= StationSeparationToleranceMm ||
                blank.EndMm <= 0 || blank.StartMm >= availableWidthMm)
            {
                error = $"ABUTMENT_STATION_LAYOUT_BLANK_RANGE_INVALID: {label} = " +
                        $"[{blank.StartMm}; {blank.EndMm}] mm không phải một khoảng dương " +
                        $"cắt dải bê tông [0; {availableWidthMm:0.###}] mm.";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// A declared edge resolves the chain by itself. An undeclared edge is only safe when reading the
    /// chain from the opposite edge reproduces it, because a mirrored chain keeps both the bar count
    /// and the plan-versus-readback comparison clean.
    /// </summary>
    private static bool TryValidateAnchor(
        IReadOnlyList<double> stations,
        AbutmentStationLayout layout,
        double toleranceMm,
        out string error)
    {
        error = string.Empty;
        if (layout.Anchor != AbutmentStationDatumEdge.Unresolved) return true;
        if (AbutmentStationDatumKernel.IsMirrorSymmetric(
                stations, layout.AvailableWidthMm, toleranceMm, out var mirrorShiftMm))
            return true;
        error = double.IsFinite(mirrorShiftMm)
            ? "ABUTMENT_STATION_DATUM_EDGE_REQUIRED: chuỗi ga không đối xứng gương " +
              $"(đọc từ cạnh bê tông đối diện làm lệch tới {mirrorShiftMm:0.###} mm, " +
              $"vượt {toleranceMm:0.###} mm) nên anchor phải khai ToeSide hoặc HeelSide theo bản vẽ."
            : "ABUTMENT_STATION_DATUM_EDGE_REQUIRED: không chứng minh được chuỗi ga đối xứng gương " +
              "trong bề rộng khả dụng nên anchor phải khai ToeSide hoặc HeelSide theo bản vẽ.";
        return false;
    }

    /// <summary>The blank band is closed: a declared void must not keep a bar on its own edge.</summary>
    private static bool IsBlanked(IReadOnlyList<AbutmentStationBlank> blanks, double station) =>
        blanks.Any(blank =>
            station >= blank.StartMm - StationSeparationToleranceMm &&
            station <= blank.EndMm + StationSeparationToleranceMm);

    private static bool HasDrawingAddress(AbutmentRuleSourceLocator? locator) =>
        locator is not null &&
        !string.IsNullOrWhiteSpace(locator.DrawingCode) &&
        locator.Page > 0 &&
        (!string.IsNullOrWhiteSpace(locator.Detail) ||
         !string.IsNullOrWhiteSpace(locator.Section) ||
         !string.IsNullOrWhiteSpace(locator.Callout));
}
