namespace BIM.DatViet.Domain;

/// <summary>
/// Exposure condition of one concrete face, TCVN 11823-5:2017 §5.12.3. The base cover of the
/// clause is read from this class, so an undeclared class cannot fall back on a number typed for
/// one bridge.
/// </summary>
public enum AbutmentExposureClass
{
    /// <summary>Default value: an undeclared exposure class must fail closed.</summary>
    Unresolved,
    /// <summary>Direct exposure to salt water: 100 mm.</summary>
    DirectSeawater,
    /// <summary>Coastal exposure, or concrete cast against earth: 75 mm.</summary>
    CoastalOrCastAgainstEarth,
    /// <summary>Any other exterior exposure: 50 mm.</summary>
    ExteriorOther,
    /// <summary>Interior exposure: 40 mm up to D36 and 50 mm above it.</summary>
    Interior
}

/// <summary>Which requirement produced the reported cover.</summary>
public enum AbutmentCoverGovern
{
    /// <summary>Default value: a cover that never recorded its governing term must fail closed.</summary>
    Unresolved,
    /// <summary>The exposure base cover after the water-cement factor.</summary>
    ExposureBase,
    /// <summary>The bar diameter.</summary>
    BarDiameter,
    /// <summary>1.5 times the maximum aggregate size.</summary>
    AggregateSize
}

/// <summary>What one face needs before its clear cover can be resolved.</summary>
public sealed record AbutmentCoverRequest
{
    public AbutmentExposureClass Exposure { get; init; } = AbutmentExposureClass.Unresolved;

    public double BarDiameterMm { get; init; }

    public double MaximumAggregateSizeMm { get; init; }

    /// <summary>
    /// Water-cement ratio of the mix. Mandatory: the factor of §5.12.3 raises cover by 20% at a
    /// ratio of 0.50 and above, so assuming 1.0 for an undeclared mix understates cover.
    /// </summary>
    public double? WaterCementRatio { get; init; }
}

/// <summary>One resolved clear cover with every requirement that competed for it.</summary>
public sealed record AbutmentConcreteCover
{
    /// <summary>Base cover of the exposure class before the water-cement factor.</summary>
    public double ExposureBaseMm { get; init; }

    public double WaterCementFactor { get; init; } = 1;

    /// <summary>Exposure base cover after the water-cement factor.</summary>
    public double FactoredExposureMm { get; init; }

    public double BarDiameterRequirementMm { get; init; }

    public double AggregateRequirementMm { get; init; }

    /// <summary>Largest of the three requirements, before the detailing module.</summary>
    public double UnroundedMm { get; init; }

    /// <summary>Cover to detail, rounded up onto the detailing module.</summary>
    public double RequiredMm { get; init; }

    public AbutmentCoverGovern GovernedBy { get; init; } = AbutmentCoverGovern.Unresolved;
}

/// <summary>
/// Clear cover of TCVN 11823-5:2017 §5.12.3 (equivalent to AASHTO LRFD §5.12.3), as a pure kernel:
/// no Revit API, no I/O, millimeters only.
/// </summary>
public static class AbutmentConcreteCoverKernel
{
    /// <summary>Base cover for direct salt-water exposure, §5.12.3.</summary>
    public const double DirectSeawaterCoverMm = 100;

    /// <summary>Base cover for coastal exposure and for concrete cast against earth, §5.12.3.</summary>
    public const double CoastalOrCastAgainstEarthCoverMm = 75;

    /// <summary>Base cover for any other exterior exposure, §5.12.3.</summary>
    public const double ExteriorOtherCoverMm = 50;

    /// <summary>Base cover for interior exposure up to D36, §5.12.3.</summary>
    public const double InteriorCoverUpToD36Mm = 40;

    /// <summary>Base cover for interior exposure above D36, §5.12.3.</summary>
    public const double InteriorCoverAboveD36Mm = 50;

    /// <summary>Bar diameter at which the interior base cover steps up, §5.12.3.</summary>
    public const double InteriorCoverBarDiameterStepMm = 36;

    /// <summary>Water-cement ratio at or below which the 0.8 factor applies, §5.12.3.</summary>
    public const double LowWaterCementRatio = 0.40;

    /// <summary>Water-cement ratio at or above which the 1.2 factor applies, §5.12.3.</summary>
    public const double HighWaterCementRatio = 0.50;

    /// <summary>Multiple of the maximum aggregate size the cover must also reach.</summary>
    public const double AggregateMultiple = 1.5;

    /// <summary>
    /// Detailing module for cover. Cover is a minimum, so the module rounds up; the caller keeps
    /// <see cref="AbutmentConcreteCover.UnroundedMm"/> when it needs the exact clause threshold.
    /// </summary>
    public const double CoverModuleMm = 5;

    /// <summary>Lowest water-cement ratio a structural concrete mix can be declared with.</summary>
    public const double MinimumWaterCementRatio = 0.25;

    /// <summary>Highest water-cement ratio a structural concrete mix can be declared with.</summary>
    public const double MaximumWaterCementRatio = 0.90;

    /// <summary>Millimeters two lengths may differ by and still count as equal.</summary>
    public const double LengthToleranceMm = 1e-6;

    private static readonly AbutmentConcreteCover EmptyCover = new();

    /// <summary>
    /// Clear cover of TCVN 11823-5:2017 §5.12.3: the base cover of the exposure class, times 0.8
    /// for a water-cement ratio of 0.40 or below and 1.2 for 0.50 or above, then not less than the
    /// bar diameter and not less than 1.5 times the maximum aggregate size.
    /// </summary>
    public static bool TryRequiredCover(
        AbutmentCoverRequest request,
        out AbutmentConcreteCover cover,
        out string error)
    {
        cover = EmptyCover;
        error = string.Empty;
        if (request is null)
        {
            error = "ABUTMENT_COVER_REQUEST_UNDECLARED: chưa có dữ liệu mặt bê tông để tra lớp bảo vệ.";
            return false;
        }
        if (request.Exposure == AbutmentExposureClass.Unresolved)
        {
            error = "ABUTMENT_COVER_EXPOSURE_UNRESOLVED: chưa khai cấp phơi nhiễm của mặt bê tông nên " +
                    "không tra được lớp bảo vệ cơ bản của §5.12.3.";
            return false;
        }
        if (!double.IsFinite(request.BarDiameterMm) || request.BarDiameterMm <= 0)
        {
            error = $"ABUTMENT_COVER_BAR_DIAMETER_INVALID: đường kính {request.BarDiameterMm} mm " +
                    "không dương hữu hạn.";
            return false;
        }
        if (!double.IsFinite(request.MaximumAggregateSizeMm) || request.MaximumAggregateSizeMm <= 0)
        {
            error = $"ABUTMENT_COVER_AGGREGATE_SIZE_INVALID: cỡ hạt cốt liệu lớn nhất " +
                    $"{request.MaximumAggregateSizeMm} mm không dương hữu hạn.";
            return false;
        }
        if (request.WaterCementRatio is not { } ratio || !double.IsFinite(ratio) || ratio <= 0)
        {
            error = "ABUTMENT_COVER_WATER_CEMENT_RATIO_REQUIRED: chưa khai tỷ lệ nước–xi măng; " +
                    $"§5.12.3 nhân cover lên 1,2 khi tỷ lệ từ {HighWaterCementRatio:0.00} trở lên nên " +
                    "không được mặc định hệ số 1,0.";
            return false;
        }
        if (ratio < MinimumWaterCementRatio - 1e-9 || ratio > MaximumWaterCementRatio + 1e-9)
        {
            error = $"ABUTMENT_COVER_WATER_CEMENT_RATIO_OUT_OF_RANGE: tỷ lệ nước–xi măng {ratio:0.###} " +
                    $"nằm ngoài dải [{MinimumWaterCementRatio:0.00}; {MaximumWaterCementRatio:0.00}] " +
                    "của bê tông kết cấu.";
            return false;
        }

        var baseCover = request.Exposure switch
        {
            AbutmentExposureClass.DirectSeawater => DirectSeawaterCoverMm,
            AbutmentExposureClass.CoastalOrCastAgainstEarth => CoastalOrCastAgainstEarthCoverMm,
            AbutmentExposureClass.ExteriorOther => ExteriorOtherCoverMm,
            _ => request.BarDiameterMm <= InteriorCoverBarDiameterStepMm + LengthToleranceMm
                ? InteriorCoverUpToD36Mm
                : InteriorCoverAboveD36Mm
        };
        var factor = ratio <= LowWaterCementRatio + 1e-9
            ? 0.8
            : ratio >= HighWaterCementRatio - 1e-9
                ? 1.2
                : 1.0;

        var factored = baseCover * factor;
        var aggregate = AggregateMultiple * request.MaximumAggregateSizeMm;
        var unrounded = Math.Max(factored, Math.Max(request.BarDiameterMm, aggregate));
        var govern = unrounded <= factored + LengthToleranceMm
            ? AbutmentCoverGovern.ExposureBase
            : unrounded <= request.BarDiameterMm + LengthToleranceMm
                ? AbutmentCoverGovern.BarDiameter
                : AbutmentCoverGovern.AggregateSize;

        cover = new AbutmentConcreteCover
        {
            ExposureBaseMm = baseCover,
            WaterCementFactor = factor,
            FactoredExposureMm = factored,
            BarDiameterRequirementMm = request.BarDiameterMm,
            AggregateRequirementMm = aggregate,
            UnroundedMm = unrounded,
            RequiredMm = AbutmentDevelopmentLengthKernel.RoundUpToModule(unrounded, CoverModuleMm),
            GovernedBy = govern
        };
        return true;
    }
}
