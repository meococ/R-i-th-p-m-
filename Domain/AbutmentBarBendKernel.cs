namespace BIM.DatViet.Domain;

/// <summary>
/// Role a bent bar plays, because TCVN 11823-5:2017 §5.10.2.3 tabulates a different minimum bend
/// diameter for a bar carrying flexure and for the same bar used as a stirrup or tie.
/// </summary>
public enum AbutmentBarBendRole
{
    /// <summary>Default value: a bend that never declared its role must fail closed.</summary>
    Unresolved,
    /// <summary>Main or distribution bar.</summary>
    PrimaryBar,
    /// <summary>Stirrup, tie or hoop.</summary>
    StirrupOrTie
}

/// <summary>Standard hook forms of TCVN 11823-5:2017 §5.10.2.1 and §5.10.2.2.</summary>
public enum AbutmentHookForm
{
    /// <summary>Default value: a hook that never declared its form must fail closed.</summary>
    Unresolved,
    /// <summary>90° bend on a main bar, §5.10.2.1.</summary>
    Bend90,
    /// <summary>180° bend on a main bar, §5.10.2.1.</summary>
    Bend180,
    /// <summary>135° bend on a stirrup or tie, §5.10.2.2.</summary>
    StirrupBend135
}

/// <summary>
/// Minimum bend of one bar. Both the exact clause value and the value to detail are kept: the
/// clause value is the acceptance threshold, the detailed value is what a bender is told.
/// </summary>
public sealed record AbutmentBarBend
{
    public double BarDiameterMm { get; init; }

    public AbutmentBarBendRole Role { get; init; } = AbutmentBarBendRole.Unresolved;

    /// <summary>Multiple of d_b the clause tabulates for the inside bend diameter.</summary>
    public double DiameterMultiple { get; init; }

    /// <summary>Inside bend diameter exactly as the clause states it.</summary>
    public double InsideDiameterMm { get; init; }

    /// <summary>Half the clause diameter: the exact acceptance threshold for the radius.</summary>
    public double InsideRadiusMm { get; init; }

    /// <summary>
    /// Radius to detail, rounded up onto the bending module. The clause radius is a hard threshold
    /// with no slack, so this value is never below <see cref="InsideRadiusMm"/>.
    /// </summary>
    public double RequiredRadiusMm { get; init; }
}

/// <summary>A standard hook: its bend and the straight tail beyond the bend.</summary>
public sealed record AbutmentHookGeometry
{
    public AbutmentHookForm Form { get; init; } = AbutmentHookForm.Unresolved;

    public AbutmentBarBend Bend { get; init; } = new();

    /// <summary>Straight extension beyond the bend, at the free end of the bar.</summary>
    public double TailExtensionMm { get; init; }

    /// <summary>True when the absolute minimum tail of the clause produced the extension.</summary>
    public bool TailGovernedByAbsoluteMinimum { get; init; }
}

/// <summary>
/// Minimum bend diameters and standard hook tails of TCVN 11823-5:2017 §5.10.2 (equivalent to
/// AASHTO LRFD §5.10.2), as a pure kernel: no Revit API, no I/O, millimeters only.
/// </summary>
public static class AbutmentBarBendKernel
{
    /// <summary>
    /// Bending module for a detailed radius. Rounding is always up, because the minimum bend
    /// diameter of §5.10.2.3 is an exact threshold with no slack: rounding the 112 mm radius of a
    /// D28 main bar down onto 110 mm details a bend the standard does not allow.
    /// </summary>
    public const double BendRadiusModuleMm = 5;

    /// <summary>Smallest bar diameter the §5.10.2.3 table covers.</summary>
    public const double SmallestTabulatedBarDiameterMm = 10;

    /// <summary>Largest bar a stirrup or tie band of §5.10.2.3 covers.</summary>
    public const double LargestStirrupBarDiameterMm = 25;

    /// <summary>Largest bar the primary-bar bands of §5.10.2.3 cover.</summary>
    public const double LargestPrimaryBarDiameterMm = 36;

    /// <summary>Upper bound of the 4 d_b stirrup band of §5.10.2.3.</summary>
    public const double StirrupSmallBandMaximumMm = 16;

    /// <summary>Upper bound of the 6 d_b primary band of §5.10.2.3.</summary>
    public const double PrimarySmallBandMaximumMm = 25;

    /// <summary>Absolute minimum tail of a 180° hook, §5.10.2.1.</summary>
    public const double Hook180MinimumTailMm = 65;

    /// <summary>Absolute minimum tail of a 135° stirrup hook, §5.10.2.2.</summary>
    public const double Hook135MinimumTailMm = 75;

    /// <summary>Millimeters two lengths may differ by and still count as equal.</summary>
    public const double LengthToleranceMm = 1e-6;

    private static readonly AbutmentBarBend EmptyBend = new();

    private static readonly AbutmentHookGeometry EmptyHook = new();

    /// <summary>
    /// Minimum inside bend diameter and radius of TCVN 11823-5:2017 §5.10.2.3: a stirrup or tie of
    /// D10 to D16 bends on 4 d_b and of D19 to D25 on 6 d_b; a main bar of D10 to D25 bends on
    /// 6 d_b and of D28 to D36 on 8 d_b. A bar above D25 has no stirrup band, so it cannot be bent
    /// as a stirrup at all.
    /// </summary>
    public static bool TryMinimumBendRadius(
        double barDiameterMm,
        AbutmentBarBendRole role,
        out AbutmentBarBend bend,
        out string error)
    {
        bend = EmptyBend;
        error = string.Empty;
        if (!double.IsFinite(barDiameterMm) || barDiameterMm <= 0)
        {
            error = $"ABUTMENT_BEND_BAR_DIAMETER_INVALID: đường kính {barDiameterMm} mm không dương hữu hạn.";
            return false;
        }
        if (role == AbutmentBarBendRole.Unresolved)
        {
            error = "ABUTMENT_BEND_ROLE_UNRESOLVED: chưa khai thanh uốn là thép chủ hay đai/cốt ngang " +
                    "nên không tra được bảng §5.10.2.3.";
            return false;
        }
        if (barDiameterMm < SmallestTabulatedBarDiameterMm - LengthToleranceMm)
        {
            error = $"ABUTMENT_BEND_DIAMETER_OUT_OF_TABLE: đường kính {barDiameterMm:0.#} mm nhỏ hơn " +
                    $"D{SmallestTabulatedBarDiameterMm:0} là hàng đầu tiên của bảng §5.10.2.3.";
            return false;
        }

        double multiple;
        if (role == AbutmentBarBendRole.StirrupOrTie)
        {
            if (barDiameterMm > LargestStirrupBarDiameterMm + LengthToleranceMm)
            {
                error = $"ABUTMENT_BEND_STIRRUP_BAR_TOO_LARGE: §5.10.2.3 chỉ có băng đai tới " +
                        $"D{LargestStirrupBarDiameterMm:0}; thanh D{barDiameterMm:0.#} không được làm đai.";
                return false;
            }
            multiple = barDiameterMm <= StirrupSmallBandMaximumMm + LengthToleranceMm ? 4 : 6;
        }
        else
        {
            if (barDiameterMm > LargestPrimaryBarDiameterMm + LengthToleranceMm)
            {
                error = $"ABUTMENT_BEND_DIAMETER_OUT_OF_TABLE: §5.10.2.3 chỉ tra tới " +
                        $"D{LargestPrimaryBarDiameterMm:0}; thanh D{barDiameterMm:0.#} cần quy định riêng.";
                return false;
            }
            multiple = barDiameterMm <= PrimarySmallBandMaximumMm + LengthToleranceMm ? 6 : 8;
        }

        var insideDiameter = multiple * barDiameterMm;
        var insideRadius = insideDiameter / 2;
        bend = new AbutmentBarBend
        {
            BarDiameterMm = barDiameterMm,
            Role = role,
            DiameterMultiple = multiple,
            InsideDiameterMm = insideDiameter,
            InsideRadiusMm = insideRadius,
            RequiredRadiusMm = AbutmentDevelopmentLengthKernel.RoundUpToModule(
                insideRadius, BendRadiusModuleMm)
        };
        return true;
    }

    /// <summary>
    /// Straight tail beyond a standard hook: 12 d_b for a 90° bend and the larger of 4 d_b and
    /// 65 mm for a 180° bend, both TCVN 11823-5:2017 §5.10.2.1; the larger of 6 d_b and 75 mm for a
    /// 135° stirrup hook, §5.10.2.2. The 90° tail is the main-bar value of §5.10.2.1 for every
    /// diameter; the shorter stirrup tails of §5.10.2.2 are a separate detail and are not returned
    /// here.
    /// </summary>
    public static bool TryHookTailExtension(
        double barDiameterMm,
        AbutmentHookForm form,
        out double tailExtensionMm,
        out bool governedByAbsoluteMinimum,
        out string error)
    {
        tailExtensionMm = 0;
        governedByAbsoluteMinimum = false;
        error = string.Empty;
        if (!double.IsFinite(barDiameterMm) || barDiameterMm <= 0)
        {
            error = $"ABUTMENT_BEND_BAR_DIAMETER_INVALID: đường kính {barDiameterMm} mm không dương hữu hạn.";
            return false;
        }

        switch (form)
        {
            case AbutmentHookForm.Bend90:
                tailExtensionMm = 12 * barDiameterMm;
                return true;
            case AbutmentHookForm.Bend180:
                tailExtensionMm = Math.Max(4 * barDiameterMm, Hook180MinimumTailMm);
                governedByAbsoluteMinimum =
                    4 * barDiameterMm < Hook180MinimumTailMm - LengthToleranceMm;
                return true;
            case AbutmentHookForm.StirrupBend135:
                tailExtensionMm = Math.Max(6 * barDiameterMm, Hook135MinimumTailMm);
                governedByAbsoluteMinimum =
                    6 * barDiameterMm < Hook135MinimumTailMm - LengthToleranceMm;
                return true;
            default:
                error = "ABUTMENT_HOOK_FORM_UNRESOLVED: chưa khai móc 90°, 180° hay móc đai 135° nên " +
                        "không tra được đuôi móc của §5.10.2.1/§5.10.2.2.";
                return false;
        }
    }

    /// <summary>
    /// Bend and tail of one standard hook. A 135° hook is a stirrup detail by definition, so it
    /// carries the stirrup band of §5.10.2.3 and a bar above D25 is rejected instead of silently
    /// borrowing the primary-bar bend diameter.
    /// </summary>
    public static bool TryHookGeometry(
        double barDiameterMm,
        AbutmentHookForm form,
        AbutmentBarBendRole role,
        out AbutmentHookGeometry hook,
        out string error)
    {
        hook = EmptyHook;
        if (form == AbutmentHookForm.StirrupBend135 && role == AbutmentBarBendRole.PrimaryBar)
        {
            error = "ABUTMENT_HOOK_ROLE_CONFLICT: móc 135° là cấu tạo đai theo §5.10.2.2 nhưng thanh " +
                    "đang khai là thép chủ.";
            return false;
        }
        var resolvedRole = form == AbutmentHookForm.StirrupBend135
            ? AbutmentBarBendRole.StirrupOrTie
            : role;
        if (!TryHookTailExtension(
                barDiameterMm, form, out var tail, out var tailGovernedByMinimum, out error))
            return false;
        if (!TryMinimumBendRadius(barDiameterMm, resolvedRole, out var bend, out error)) return false;

        hook = new AbutmentHookGeometry
        {
            Form = form,
            Bend = bend,
            TailExtensionMm = tail,
            TailGovernedByAbsoluteMinimum = tailGovernedByMinimum
        };
        return true;
    }
}
