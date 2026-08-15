namespace BIM.DatViet.Domain;

/// <summary>Which requirement produced a reported minimum clear spacing.</summary>
public enum AbutmentSpacingGovern
{
    /// <summary>Default value: a spacing that never recorded its governing term must fail closed.</summary>
    Unresolved,
    /// <summary>The bar-diameter multiple.</summary>
    BarDiameter,
    /// <summary>The maximum-aggregate-size multiple.</summary>
    AggregateSize,
    /// <summary>The absolute minimum of the clause.</summary>
    AbsoluteMinimum
}

/// <summary>Shrinkage and temperature reinforcement of one face, TCVN 11823-5:2017 §5.10.8.</summary>
public sealed record AbutmentShrinkageTemperatureSteel
{
    /// <summary>Area per unit length straight from the clause formula.</summary>
    public double FormulaAreaMm2PerMm { get; init; }

    /// <summary>Area per unit length after the clause bounds are applied.</summary>
    public double RequiredAreaMm2PerMm { get; init; }

    public bool RaisedToClauseMinimum { get; init; }

    public bool CappedAtClauseMaximum { get; init; }
}

/// <summary>
/// Clear opening of a crossing pair of bar families, seen by an immersion vibrator. This is a
/// constructability limit rather than a clause of TCVN 11823-5:2017: the poker has to pass through
/// the mesh of the mat, and a skew mat closes the mesh further than the plan spacing suggests.
/// </summary>
public sealed record AbutmentMatOpening
{
    /// <summary>Clear spacing of the first family, measured normal to its own bars.</summary>
    public double FirstClearSpacingMm { get; init; }

    /// <summary>Clear spacing of the second family, measured normal to its own bars.</summary>
    public double SecondClearSpacingMm { get; init; }

    public double CrossingAngleDegrees { get; init; }

    /// <summary>Smaller clear spacing, projected onto the crossing angle.</summary>
    public double ClearOpeningMm { get; init; }

    /// <summary>Vibrator diameter plus the working clearance around it.</summary>
    public double RequiredOpeningMm { get; init; }

    public bool IsAdequate { get; init; }

    /// <summary>Clear opening minus what the vibrator needs; negative means the mesh is too tight.</summary>
    public double MarginMm { get; init; }
}

/// <summary>
/// Bar spacing of TCVN 11823-5:2017 §5.10.3, shrinkage and temperature steel of §5.10.8, and the
/// vibrator clearance a skew mat has to keep, as a pure kernel: no Revit API, no I/O, millimeters
/// and megapascals only.
/// </summary>
public static class AbutmentBarSpacingKernel
{
    /// <summary>Multiple of the bar diameter the clear spacing in a layer must reach, §5.10.3.1.1.</summary>
    public const double InLayerBarDiameterMultiple = 1.5;

    /// <summary>Multiple of the maximum aggregate size the clear spacing must reach, §5.10.3.1.1.</summary>
    public const double InLayerAggregateMultiple = 1.5;

    /// <summary>Absolute minimum clear spacing inside one layer, §5.10.3.1.1.</summary>
    public const double InLayerMinimumClearSpacingMm = 38;

    /// <summary>Absolute minimum clear distance between two layers, §5.10.3.1.3.</summary>
    public const double BetweenLayersMinimumClearSpacingMm = 25;

    /// <summary>Thickness above which shrinkage and temperature steel goes to 300 mm, §5.10.8.</summary>
    public const double ThickComponentThresholdMm = 450;

    /// <summary>Maximum spacing of shrinkage and temperature steel in a thick component, §5.10.8.</summary>
    public const double ThickComponentMaximumSpacingMm = 300;

    /// <summary>Maximum spacing of shrinkage and temperature steel in a thin component, §5.10.8.</summary>
    public const double ThinComponentMaximumSpacingMm = 450;

    /// <summary>Multiple of the component thickness the spacing must also stay under, §5.10.8.</summary>
    public const double ThicknessSpacingMultiple = 3;

    /// <summary>Lower bound of the §5.10.8 area, in mm² per mm.</summary>
    public const double MinimumShrinkageAreaMm2PerMm = 0.233;

    /// <summary>Upper bound of the §5.10.8 area, in mm² per mm.</summary>
    public const double MaximumShrinkageAreaMm2PerMm = 1.27;

    /// <summary>Diameter of the immersion vibrator assumed when the site has not named one.</summary>
    public const double DefaultVibratorDiameterMm = 50;

    /// <summary>Working clearance the vibrator needs on top of its own diameter.</summary>
    public const double VibratorWorkingClearanceMm = 25;

    /// <summary>Millimeters two lengths may differ by and still count as equal.</summary>
    public const double LengthToleranceMm = 1e-6;

    private static readonly AbutmentShrinkageTemperatureSteel EmptyShrinkageSteel = new();

    private static readonly AbutmentMatOpening EmptyOpening = new();

    /// <summary>
    /// Minimum clear distance between parallel bars in one layer, TCVN 11823-5:2017 §5.10.3.1.1:
    /// the largest of 1.5 d_b, 1.5 times the maximum aggregate size and 38 mm.
    /// </summary>
    public static bool TryMinimumClearSpacingInLayer(
        double barDiameterMm,
        double maximumAggregateSizeMm,
        out double clearSpacingMm,
        out AbutmentSpacingGovern governedBy,
        out string error)
    {
        clearSpacingMm = 0;
        governedBy = AbutmentSpacingGovern.Unresolved;
        error = string.Empty;
        if (!double.IsFinite(barDiameterMm) || barDiameterMm <= 0)
        {
            error = $"ABUTMENT_SPACING_BAR_DIAMETER_INVALID: đường kính {barDiameterMm} mm không dương hữu hạn.";
            return false;
        }
        if (!double.IsFinite(maximumAggregateSizeMm) || maximumAggregateSizeMm <= 0)
        {
            error = $"ABUTMENT_SPACING_AGGREGATE_SIZE_INVALID: cỡ hạt cốt liệu lớn nhất " +
                    $"{maximumAggregateSizeMm} mm không dương hữu hạn.";
            return false;
        }

        var fromDiameter = InLayerBarDiameterMultiple * barDiameterMm;
        var fromAggregate = InLayerAggregateMultiple * maximumAggregateSizeMm;
        clearSpacingMm = Math.Max(
            InLayerMinimumClearSpacingMm, Math.Max(fromDiameter, fromAggregate));
        governedBy = clearSpacingMm <= InLayerMinimumClearSpacingMm + LengthToleranceMm
            ? AbutmentSpacingGovern.AbsoluteMinimum
            : clearSpacingMm <= fromDiameter + LengthToleranceMm
                ? AbutmentSpacingGovern.BarDiameter
                : AbutmentSpacingGovern.AggregateSize;
        return true;
    }

    /// <summary>
    /// Minimum clear distance between two layers of parallel bars, TCVN 11823-5:2017 §5.10.3.1.3:
    /// the larger of 25 mm and the bar diameter.
    /// </summary>
    public static bool TryMinimumClearSpacingBetweenLayers(
        double barDiameterMm,
        out double clearSpacingMm,
        out AbutmentSpacingGovern governedBy,
        out string error)
    {
        clearSpacingMm = 0;
        governedBy = AbutmentSpacingGovern.Unresolved;
        error = string.Empty;
        if (!double.IsFinite(barDiameterMm) || barDiameterMm <= 0)
        {
            error = $"ABUTMENT_SPACING_BAR_DIAMETER_INVALID: đường kính {barDiameterMm} mm không dương hữu hạn.";
            return false;
        }

        clearSpacingMm = Math.Max(BetweenLayersMinimumClearSpacingMm, barDiameterMm);
        governedBy = barDiameterMm > BetweenLayersMinimumClearSpacingMm + LengthToleranceMm
            ? AbutmentSpacingGovern.BarDiameter
            : AbutmentSpacingGovern.AbsoluteMinimum;
        return true;
    }

    /// <summary>
    /// Maximum spacing of shrinkage and temperature reinforcement, TCVN 11823-5:2017 §5.10.8:
    /// 300 mm once the component is thicker than 450 mm, otherwise three times the thickness capped
    /// at 450 mm.
    /// </summary>
    public static bool TryMaximumShrinkageTemperatureSpacing(
        double componentThicknessMm,
        out double maximumSpacingMm,
        out string error)
    {
        maximumSpacingMm = 0;
        error = string.Empty;
        if (!double.IsFinite(componentThicknessMm) || componentThicknessMm <= 0)
        {
            error = $"ABUTMENT_SPACING_COMPONENT_THICKNESS_INVALID: chiều dày cấu kiện " +
                    $"{componentThicknessMm} mm không dương hữu hạn.";
            return false;
        }

        maximumSpacingMm = componentThicknessMm > ThickComponentThresholdMm + LengthToleranceMm
            ? ThickComponentMaximumSpacingMm
            : Math.Min(ThicknessSpacingMultiple * componentThicknessMm, ThinComponentMaximumSpacingMm);
        return true;
    }

    /// <summary>
    /// Shrinkage and temperature reinforcement per unit length, TCVN 11823-5:2017 §5.10.8:
    /// A_s ≥ 0.75 b h / (2 (b + h) f_y), then bounded into [0.233; 1.27] mm² per mm.
    /// </summary>
    public static bool TryShrinkageTemperatureSteelArea(
        double leastWidthMm,
        double leastThicknessMm,
        double yieldStrengthMPa,
        out AbutmentShrinkageTemperatureSteel steel,
        out string error)
    {
        steel = EmptyShrinkageSteel;
        error = string.Empty;
        if (!double.IsFinite(leastWidthMm) || leastWidthMm <= 0 ||
            !double.IsFinite(leastThicknessMm) || leastThicknessMm <= 0)
        {
            error = $"ABUTMENT_SHRINKAGE_SECTION_INVALID: tiết diện {leastWidthMm} × " +
                    $"{leastThicknessMm} mm không dương hữu hạn.";
            return false;
        }
        if (!double.IsFinite(yieldStrengthMPa) || yieldStrengthMPa <= 0)
        {
            error = $"ABUTMENT_SHRINKAGE_YIELD_STRENGTH_INVALID: giới hạn chảy {yieldStrengthMPa} MPa " +
                    "không dương hữu hạn.";
            return false;
        }
        if (yieldStrengthMPa < AbutmentDevelopmentLengthKernel.MinimumYieldStrengthMPa ||
            yieldStrengthMPa > AbutmentDevelopmentLengthKernel.MaximumYieldStrengthMPa)
        {
            error = $"ABUTMENT_SHRINKAGE_YIELD_STRENGTH_OUT_OF_RANGE: giới hạn chảy " +
                    $"{yieldStrengthMPa:0.#} MPa nằm ngoài dải " +
                    $"[{AbutmentDevelopmentLengthKernel.MinimumYieldStrengthMPa:0.#}; " +
                    $"{AbutmentDevelopmentLengthKernel.MaximumYieldStrengthMPa:0.#}] MPa của thép thanh.";
            return false;
        }

        var formula = 0.75 * leastWidthMm * leastThicknessMm /
                      (2 * (leastWidthMm + leastThicknessMm) * yieldStrengthMPa);
        var bounded = Math.Min(
            Math.Max(formula, MinimumShrinkageAreaMm2PerMm), MaximumShrinkageAreaMm2PerMm);
        steel = new AbutmentShrinkageTemperatureSteel
        {
            FormulaAreaMm2PerMm = formula,
            RequiredAreaMm2PerMm = bounded,
            RaisedToClauseMinimum = formula < MinimumShrinkageAreaMm2PerMm - 1e-12,
            CappedAtClauseMaximum = formula > MaximumShrinkageAreaMm2PerMm + 1e-12
        };
        return true;
    }

    /// <summary>
    /// Converts a pitch the drawing dimensioned along some axis into the pitch measured normal to
    /// the bar, reusing <see cref="GeometryKernel.ProjectOffsetsNormalToBar"/>. A skew mat is the
    /// whole point: a 150 mm pitch dimensioned along the bridge axis with bars crossing it at 65° is
    /// only 136 mm normal to the bar, and grading the clearance on 150 mm overstates it by 10% on
    /// the unsafe side.
    /// </summary>
    public static bool TryProjectPitchNormalToBar(
        double dimensionedPitchMm,
        PlanPoint2 barDirection,
        PlanPoint2 measuredDirection,
        out double normalPitchMm,
        out string error)
    {
        normalPitchMm = 0;
        error = string.Empty;
        if (!double.IsFinite(dimensionedPitchMm) || dimensionedPitchMm <= 0)
        {
            error = $"ABUTMENT_SPACING_PITCH_INVALID: bước ghi trên bản vẽ {dimensionedPitchMm} mm " +
                    "không dương hữu hạn.";
            return false;
        }

        try
        {
            normalPitchMm = GeometryKernel.ProjectOffsetsNormalToBar(
                [dimensionedPitchMm], barDirection, measuredDirection)[0];
            return true;
        }
        catch (InvalidOperationException exception)
        {
            normalPitchMm = 0;
            error = $"ABUTMENT_STATION_REFERENCE_INVALID: {exception.Message}";
            return false;
        }
    }

    /// <summary>
    /// Same projection as
    /// <see cref="TryProjectPitchNormalToBar(double, PlanPoint2, PlanPoint2, out double, out string)"/>,
    /// stated as the angle between the bar and the axis the pitch was dimensioned along.
    /// </summary>
    public static bool TryProjectPitchNormalToBar(
        double dimensionedPitchMm,
        double barToMeasuredAxisAngleDegrees,
        out double normalPitchMm,
        out string error)
    {
        normalPitchMm = 0;
        error = string.Empty;
        if (!TryValidateAngle(
                barToMeasuredAxisAngleDegrees, "ABUTMENT_SPACING_ANGLE_INVALID", out var radians,
                out error))
            return false;

        return TryProjectPitchNormalToBar(
            dimensionedPitchMm,
            new PlanPoint2(Math.Cos(radians), Math.Sin(radians)),
            new PlanPoint2(1, 0),
            out normalPitchMm,
            out error);
    }

    /// <summary>
    /// Clear spacing of one family from the pitch its drawing dimensioned: project the pitch normal
    /// to the bar first, then take the bar diameter off. Taking the diameter off the dimensioned
    /// pitch of a skew mat is the mistake this overload exists to stop.
    /// </summary>
    public static bool TryClearSpacingFromDimensionedPitch(
        double dimensionedPitchMm,
        double barToMeasuredAxisAngleDegrees,
        double barDiameterMm,
        out double clearSpacingMm,
        out string error)
    {
        clearSpacingMm = 0;
        if (!double.IsFinite(barDiameterMm) || barDiameterMm <= 0)
        {
            error = $"ABUTMENT_SPACING_BAR_DIAMETER_INVALID: đường kính {barDiameterMm} mm không dương hữu hạn.";
            return false;
        }
        if (!TryProjectPitchNormalToBar(
                dimensionedPitchMm, barToMeasuredAxisAngleDegrees, out var normalPitchMm, out error))
            return false;
        if (normalPitchMm <= barDiameterMm + LengthToleranceMm)
        {
            error = $"ABUTMENT_SPACING_PITCH_BELOW_BAR: bước vuông góc thanh {normalPitchMm:0.###} mm " +
                    $"không lớn hơn đường kính {barDiameterMm:0.#} mm nên không còn hở trần.";
            return false;
        }

        clearSpacingMm = normalPitchMm - barDiameterMm;
        return true;
    }

    /// <summary>
    /// Clear opening an immersion vibrator sees through a crossing pair of bar families. The opening
    /// is the smaller clear spacing projected onto the crossing angle, and it has to reach the
    /// vibrator diameter plus a 25 mm working clearance. Both spacings must already be measured
    /// normal to their own bars: use
    /// <see cref="TryClearSpacingFromDimensionedPitch"/> when the drawing dimensioned them along a
    /// different axis.
    /// </summary>
    public static bool TryDiagonalMatClearOpening(
        double firstClearSpacingMm,
        double secondClearSpacingMm,
        double crossingAngleDegrees,
        double vibratorDiameterMm,
        out AbutmentMatOpening opening,
        out string error)
    {
        opening = EmptyOpening;
        error = string.Empty;
        if (!double.IsFinite(firstClearSpacingMm) || firstClearSpacingMm <= 0 ||
            !double.IsFinite(secondClearSpacingMm) || secondClearSpacingMm <= 0)
        {
            error = $"ABUTMENT_MAT_CLEAR_SPACING_INVALID: hở trần {firstClearSpacingMm} mm và " +
                    $"{secondClearSpacingMm} mm phải dương hữu hạn.";
            return false;
        }
        if (!double.IsFinite(vibratorDiameterMm) || vibratorDiameterMm <= 0)
        {
            error = $"ABUTMENT_MAT_VIBRATOR_DIAMETER_INVALID: đường kính đầm dùi {vibratorDiameterMm} mm " +
                    "không dương hữu hạn.";
            return false;
        }
        if (!TryValidateAngle(
                crossingAngleDegrees, "ABUTMENT_MAT_CROSSING_ANGLE_INVALID", out var radians, out error))
            return false;

        var required = vibratorDiameterMm + VibratorWorkingClearanceMm;
        var clear = Math.Min(firstClearSpacingMm, secondClearSpacingMm) * Math.Sin(radians);
        opening = new AbutmentMatOpening
        {
            FirstClearSpacingMm = firstClearSpacingMm,
            SecondClearSpacingMm = secondClearSpacingMm,
            CrossingAngleDegrees = crossingAngleDegrees,
            ClearOpeningMm = clear,
            RequiredOpeningMm = required,
            IsAdequate = clear >= required - LengthToleranceMm,
            MarginMm = clear - required
        };
        return true;
    }

    /// <summary>
    /// A crossing angle of 0° or 180° means the two families are parallel, so they never form a mesh
    /// opening at all; the sine would silently report a zero opening instead.
    /// </summary>
    private static bool TryValidateAngle(
        double angleDegrees,
        string errorCode,
        out double radians,
        out string error)
    {
        radians = 0;
        error = string.Empty;
        if (double.IsFinite(angleDegrees) && angleDegrees > 0 && angleDegrees < 180)
        {
            radians = angleDegrees * Math.PI / 180;
            return true;
        }
        error = $"{errorCode}: góc {angleDegrees}° phải nằm trong khoảng mở (0; 180).";
        return false;
    }
}
