using System.Globalization;

namespace BIM.DatViet.Domain;

/// <summary>
/// Where a nominal dimension came from. This decides what a mismatch against measured geometry is
/// allowed to mean, and it is the difference between a check that catches a broken model and a
/// check that only catches "this abutment is not the one the profile was written for".
/// </summary>
public enum AbutmentDeclaredValueSource
{
    /// <summary>Nobody stated the value; measured geometry is the only truth and nothing is gated.</summary>
    Undeclared,

    /// <summary>
    /// The family instance or its type states the value. Both sides then come from the same model,
    /// so a disagreement is a defective family and blocks.
    /// </summary>
    FamilyParameter,

    /// <summary>
    /// Only the profile JSON states the value. It describes the abutment the profile was authored
    /// for, so a disagreement means a different abutment — a warning, never a block, because the
    /// engine builds rebar from measured geometry either way.
    /// </summary>
    PresetConstant
}

/// <summary>
/// Compares a nominal dimension against the measured one. Nominal dimensions are cross-checks, not
/// inputs: nothing here may ever produce a length the engine then builds with.
/// </summary>
public static class AbutmentDeclaredDimensionKernel
{
    /// <summary>
    /// Floor for a family-declared value. A family whose own parameter disagrees with its own solid
    /// by more than this is defective, whatever its size.
    /// </summary>
    public const double ModelConsistencyToleranceMm = 25;

    /// <summary>Relative part of the family gate, so long members are not judged by an absolute millimetre count.</summary>
    public const double ModelConsistencyToleranceShare = 0.005;

    /// <summary>
    /// Floor for a profile-declared value. Wide on purpose: this only answers "is this roughly the
    /// abutment the profile describes", and a narrow band here is exactly the overfit that stops a
    /// differently sized abutment from ever being analysed.
    /// </summary>
    public const double ProfileIdentityToleranceMm = 150;

    /// <summary>Relative part of the profile gate.</summary>
    public const double ProfileIdentityToleranceShare = 0.05;

    /// <summary>Accepted difference between a declared skew and the measured boundary angle.</summary>
    public const double SkewToleranceDeg = 3;

    public readonly record struct DimensionCheck(
        string Label,
        double MeasuredMm,
        double DeclaredMm,
        AbutmentDeclaredValueSource Source,
        double ToleranceMm,
        bool Matches)
    {
        /// <summary>Only a self-contradicting model blocks; a profile written for another abutment warns.</summary>
        public bool Blocks => !Matches && Source == AbutmentDeclaredValueSource.FamilyParameter;

        public double DeviationMm => MeasuredMm - DeclaredMm;
    }

    public static double ToleranceFor(double declaredMm, AbutmentDeclaredValueSource source) => source switch
    {
        AbutmentDeclaredValueSource.FamilyParameter => Math.Max(
            ModelConsistencyToleranceMm, Math.Abs(declaredMm) * ModelConsistencyToleranceShare),
        AbutmentDeclaredValueSource.PresetConstant => Math.Max(
            ProfileIdentityToleranceMm, Math.Abs(declaredMm) * ProfileIdentityToleranceShare),
        _ => double.PositiveInfinity
    };

    public static DimensionCheck Compare(
        string label, double measuredMm, double declaredMm, AbutmentDeclaredValueSource source)
    {
        var tolerance = ToleranceFor(declaredMm, source);
        var measurable = double.IsFinite(measuredMm) && double.IsFinite(declaredMm) && declaredMm > 1e-9;
        var matches = source == AbutmentDeclaredValueSource.Undeclared ||
                      !measurable ||
                      Math.Abs(measuredMm - declaredMm) <= tolerance;
        return new DimensionCheck(label, measuredMm, declaredMm, source, tolerance, matches);
    }

    /// <summary>Picks the source that <c>PreferMm</c>-style resolution actually used.</summary>
    public static AbutmentDeclaredValueSource ResolveSource(double familyValueMm, double presetValueMm)
    {
        if (familyValueMm > 1e-6) return AbutmentDeclaredValueSource.FamilyParameter;
        return presetValueMm > 1e-6
            ? AbutmentDeclaredValueSource.PresetConstant
            : AbutmentDeclaredValueSource.Undeclared;
    }

    public static string Describe(DimensionCheck check)
    {
        if (check.Source == AbutmentDeclaredValueSource.Undeclared)
            return $"{check.Label}: đo được {Mm(check.MeasuredMm)} mm; không nguồn nào khai giá trị " +
                   "dự kiến nên chỉ ghi nhận số đo.";
        var origin = check.Source == AbutmentDeclaredValueSource.FamilyParameter
            ? "tham số family"
            : "preset";
        var verdict = check.Matches ? "khớp" : "LỆCH";
        var tail = check.Matches
            ? string.Empty
            : check.Source == AbutmentDeclaredValueSource.FamilyParameter
                ? " Family tự mâu thuẫn với hình học của chính nó — phải sửa family trước khi tạo thép."
                : " Preset được viết cho mố khác kích thước. Không chặn: biên tạo thép lấy từ hình học " +
                  "đo được, số khai chỉ để đối chiếu. Hãy cập nhật preset cho đúng mố đang mở.";
        return $"{check.Label}: đo được {Mm(check.MeasuredMm)} mm, {origin} khai " +
               $"{Mm(check.DeclaredMm)} mm, lệch {MmSigned(check.DeviationMm)} mm so với dung sai " +
               $"{Mm(check.ToleranceMm)} mm — {verdict}.{tail}";
    }

    private static string Mm(double value) =>
        double.IsFinite(value)
            ? Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture)
            : "không đo được";

    private static string MmSigned(double value) =>
        double.IsFinite(value)
            ? Math.Round(value, 1).ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture)
            : "không đo được";
}
