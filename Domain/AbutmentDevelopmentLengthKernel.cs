namespace BIM.DatViet.Domain;

/// <summary>Which term of a development or splice clause produced the reported length.</summary>
public enum AbutmentDevelopmentGovern
{
    /// <summary>Default value: a result that never recorded its governing term must fail closed.</summary>
    Unresolved,
    /// <summary>The area or diameter expression the clause states first.</summary>
    PrimaryFormula,
    /// <summary>The bound the same clause states as "but not less than".</summary>
    LowerBound,
    /// <summary>The absolute minimum length the clause never goes below.</summary>
    AbsoluteMinimum
}

/// <summary>
/// Epoxy-coating condition of a bar developed in tension, TCVN 11823-5:2017 §5.11.2.1.2. The
/// factor raises the development length, so an undeclared coating stays
/// <see cref="None"/> only because an uncoated bar is the unfactored base case.
/// </summary>
public enum AbutmentEpoxyCoating
{
    /// <summary>Uncoated bar: factor 1.0.</summary>
    None,
    /// <summary>Coated bar with cover under 3 d_b or clear spacing under 6 d_b: factor 1.5.</summary>
    CoatedWithCloseCoverOrSpacing,
    /// <summary>Coated bar in every other condition: factor 1.2.</summary>
    CoatedOther
}

/// <summary>Tension lap splice class of TCVN 11823-5:2017 §5.11.5.3.1.</summary>
public enum AbutmentSpliceClass
{
    /// <summary>Default value: an unresolved class must fail closed.</summary>
    Unresolved,
    /// <summary>1.0 ℓ_d.</summary>
    ClassA,
    /// <summary>1.3 ℓ_d.</summary>
    ClassB,
    /// <summary>1.7 ℓ_d.</summary>
    ClassC
}

/// <summary>
/// Column of the §5.11.5.3.1 table: the maximum share of the bar area spliced inside one required
/// lap length.
/// </summary>
public enum AbutmentSplicedFractionBand
{
    /// <summary>Default value: an undeclared share must fail closed.</summary>
    Unresolved,
    UpToFiftyPercent,
    UpToSeventyFivePercent,
    UpToOneHundredPercent
}

/// <summary>
/// Bar and concrete data every development clause needs. Millimeters and megapascals only; feet and
/// Revit internal units never enter this kernel.
/// </summary>
public sealed record AbutmentRebarMaterial
{
    public double BarDiameterMm { get; init; }

    /// <summary>Specified 28-day compressive strength f'c in MPa.</summary>
    public double ConcreteStrengthMPa { get; init; }

    /// <summary>Specified yield strength f_y in MPa.</summary>
    public double YieldStrengthMPa { get; init; }

    /// <summary>
    /// Catalogue nominal area A_b in mm². Left null the kernel uses π d_b² / 4, which differs from
    /// the TCVN 1651-2008 nominal areas by under 0.3% and never moves a rounded result.
    /// </summary>
    public double? NominalAreaMm2 { get; init; }

    /// <summary>Area used by §5.11.2.1: the declared nominal area, otherwise the circle area.</summary>
    public double EffectiveAreaMm2 =>
        NominalAreaMm2 ?? Math.PI * BarDiameterMm * BarDiameterMm / 4;
}

/// <summary>
/// Modification factors of TCVN 11823-5:2017 §5.11.2.1.2 and §5.11.2.1.3. Every factor defaults to
/// 1.0; a reduction has to be claimed explicitly and is only granted when the geometry that
/// authorises it is supplied with the claim.
/// </summary>
public sealed record AbutmentTensionDevelopmentModifiers
{
    public AbutmentEpoxyCoating EpoxyCoating { get; init; } = AbutmentEpoxyCoating.None;

    /// <summary>Lightweight-concrete factor in [1.2, 1.3]. Null means normal-weight concrete.</summary>
    public double? LightweightConcreteFactor { get; init; }

    /// <summary>Claims the 0.8 factor for widely spaced bars with cover in the spacing direction.</summary>
    public bool ClaimWideSpacingReduction { get; init; }

    public double? LateralSpacingMm { get; init; }

    public double? ClearCoverInSpacingDirectionMm { get; init; }

    /// <summary>Claims the 0.75 factor for bars enclosed in a spiral.</summary>
    public bool ClaimSpiralReduction { get; init; }

    public double? SpiralBarDiameterMm { get; init; }

    public double? SpiralPitchMm { get; init; }

    /// <summary>
    /// A_s required / A_s provided of §5.11.2.1.3, so a value in (0, 1]. Null means the bar is
    /// developed for its full yield strength.
    /// </summary>
    public double? ExcessReinforcementRatio { get; init; }
}

/// <summary>
/// Cover around a standard hook, TCVN 11823-5:2017 §5.11.2.4.2. The 0.7 factor is only granted when
/// both covers are supplied and both reach the clause threshold.
/// </summary>
public sealed record AbutmentHookCoverCondition
{
    public bool ClaimCoverReduction { get; init; }

    /// <summary>Side cover normal to the plane of the hook.</summary>
    public double? SideCoverNormalToHookPlaneMm { get; init; }

    /// <summary>Cover on the bar extension beyond the hook.</summary>
    public double? CoverOnBarExtensionMm { get; init; }
}

/// <summary>
/// One solved development length with every term kept, so a reviewer can see which part of the
/// clause governed instead of re-deriving it from the final number.
/// </summary>
public sealed record AbutmentDevelopmentLength
{
    /// <summary>The area or diameter expression the clause states first.</summary>
    public double PrimaryFormulaMm { get; init; }

    /// <summary>The "but not less than" bound of the same clause.</summary>
    public double LowerBoundMm { get; init; }

    /// <summary>Larger of the two clause terms, before modification factors.</summary>
    public double BasicMm { get; init; }

    /// <summary>
    /// Which clause term produced <see cref="BasicMm"/>. Stays meaningful after the absolute
    /// minimum takes over, so the shape of the clause is still auditable.
    /// </summary>
    public AbutmentDevelopmentGovern BasicGovernedBy { get; init; }

    public double CombinedFactor { get; init; } = 1;

    public double FactoredMm { get; init; }

    /// <summary>Governing length before the detailing module is applied.</summary>
    public double UnroundedMm { get; init; }

    /// <summary>Length to detail, rounded up onto the detailing module.</summary>
    public double RequiredMm { get; init; }

    /// <summary>Which term produced <see cref="UnroundedMm"/>, absolute minimum included.</summary>
    public AbutmentDevelopmentGovern GovernedBy { get; init; }
}

/// <summary>
/// A tension lap splice: the class the two-way table of §5.11.5.3.1 yields, the development length
/// it multiplies and the length to detail.
/// </summary>
public sealed record AbutmentLapSplice
{
    public AbutmentSpliceClass SpliceClass { get; init; } = AbutmentSpliceClass.Unresolved;

    public double ClassFactor { get; init; }

    public AbutmentDevelopmentLength Development { get; init; } = new();

    public double UnroundedMm { get; init; }

    public double RequiredMm { get; init; }

    /// <summary>True when the 300 mm floor of §5.11.5.3.1 produced the length.</summary>
    public bool GovernedByAbsoluteMinimum { get; init; }
}

/// <summary>
/// What the caller knows about a tension lap splice before the class is resolved. Both rows of the
/// §5.11.5.3.1 table are reachable, but the favourable row needs a calculation on file: an
/// unproven area ratio has to land on the "under 2" row.
/// </summary>
public sealed record AbutmentLapSpliceRequest
{
    /// <summary>Column of the table. Undeclared fails closed rather than guessing a share.</summary>
    public AbutmentSplicedFractionBand SplicedFraction { get; init; } =
        AbutmentSplicedFractionBand.Unresolved;

    /// <summary>
    /// Claims A_s provided / A_s required ≥ 2, the favourable row of the table. Only honoured with
    /// <see cref="AreaRatioEvidenceReference"/> naming the calculation that proves it.
    /// </summary>
    public bool AreaRatioAtLeastTwo { get; init; }

    public string AreaRatioEvidenceReference { get; init; } = string.Empty;

    /// <summary>
    /// Class the caller wants. <see cref="AbutmentSpliceClass.Unresolved"/> takes the class straight
    /// from the table; anything else must be at least as conservative as the table.
    /// </summary>
    public AbutmentSpliceClass RequestedClass { get; init; } = AbutmentSpliceClass.Unresolved;
}

/// <summary>
/// Development and lap-splice lengths of TCVN 11823-5:2017 §5.11 (equivalent to AASHTO LRFD
/// §5.11), as a pure kernel: no Revit API, no I/O, millimeters and megapascals only.
/// </summary>
public static class AbutmentDevelopmentLengthKernel
{
    /// <summary>Absolute minimum tension development length, §5.11.2.1.1.</summary>
    public const double MinimumTensionDevelopmentMm = 300;

    /// <summary>Absolute minimum compression development length, §5.11.2.2.1.</summary>
    public const double MinimumCompressionDevelopmentMm = 200;

    /// <summary>Absolute minimum hook development length, §5.11.2.4.1.</summary>
    public const double MinimumHookDevelopmentMm = 150;

    /// <summary>Absolute minimum tension lap splice length, §5.11.5.3.1.</summary>
    public const double MinimumLapSpliceMm = 300;

    /// <summary>
    /// Detailing module for development and lap lengths. Every length is rounded up: these are
    /// minimum lengths of the standard, so rounding down details a non-compliant bar.
    /// </summary>
    public const double DevelopmentModuleMm = 10;

    /// <summary>Detailing module for the shorter hook development length.</summary>
    public const double HookModuleMm = 5;

    /// <summary>Lowest f'c the standard covers, §5.4.2.1.</summary>
    public const double MinimumConcreteStrengthMPa = 16;

    /// <summary>Highest f'c the standard covers without special provisions, §5.4.2.1.</summary>
    public const double MaximumConcreteStrengthMPa = 70;

    /// <summary>Lowest f_y a reinforcing steel grade of the standard can carry.</summary>
    public const double MinimumYieldStrengthMPa = 200;

    /// <summary>Highest f_y a reinforcing steel grade of the standard can carry.</summary>
    public const double MaximumYieldStrengthMPa = 700;

    /// <summary>Largest bar diameter the detailing clauses of §5.10 and §5.11 cover.</summary>
    public const double MaximumBarDiameterMm = 60;

    /// <summary>Lateral spacing the 0.8 factor of §5.11.2.1.3 requires.</summary>
    public const double WideSpacingMinimumSpacingMm = 150;

    /// <summary>Cover in the spacing direction the 0.8 factor of §5.11.2.1.3 requires.</summary>
    public const double WideSpacingMinimumCoverMm = 75;

    /// <summary>Largest bar the 0.8 factor of §5.11.2.1.3 covers.</summary>
    public const double WideSpacingMaximumBarDiameterMm = 36;

    /// <summary>Spiral bar diameter the 0.75 factor of §5.11.2.1.3 requires.</summary>
    public const double SpiralMinimumBarDiameterMm = 6;

    /// <summary>Spiral pitch the 0.75 factor of §5.11.2.1.3 allows at most.</summary>
    public const double SpiralMaximumPitchMm = 100;

    /// <summary>Side cover the 0.7 hook factor of §5.11.2.4.2 requires.</summary>
    public const double HookSideCoverMm = 64;

    /// <summary>Cover on the bar extension the 0.7 hook factor of §5.11.2.4.2 requires.</summary>
    public const double HookExtensionCoverMm = 50;

    /// <summary>
    /// Millimeters two lengths may differ by and still count as equal. Never compare a computed
    /// length with <c>==</c>: every value here went through a square root.
    /// </summary>
    public const double LengthToleranceMm = 1e-6;

    private static readonly AbutmentDevelopmentLength EmptyLength = new();

    private static readonly AbutmentLapSplice EmptySplice = new();

    /// <summary>
    /// Tension development length of TCVN 11823-5:2017 §5.11.2.1:
    /// ℓ_db = max(0.02 A_b f_y / √f'c ; 0.06 d_b f_y), then ℓ_d = ℓ_db × ∏(factors) ≥ 300 mm.
    /// </summary>
    public static bool TryTensionDevelopmentLength(
        AbutmentRebarMaterial material,
        AbutmentTensionDevelopmentModifiers modifiers,
        out AbutmentDevelopmentLength length,
        out string error)
    {
        length = EmptyLength;
        if (!TryValidateMaterial(material, out error)) return false;
        modifiers ??= new AbutmentTensionDevelopmentModifiers();
        if (!TryCombineTensionFactors(material, modifiers, out var factor, out error)) return false;

        var areaTerm = 0.02 * material.EffectiveAreaMm2 * material.YieldStrengthMPa /
                       Math.Sqrt(material.ConcreteStrengthMPa);
        var diameterTerm = 0.06 * material.BarDiameterMm * material.YieldStrengthMPa;
        length = Solve(
            areaTerm, diameterTerm, factor, MinimumTensionDevelopmentMm, DevelopmentModuleMm);
        return true;
    }

    /// <summary>
    /// Compression development length of TCVN 11823-5:2017 §5.11.2.2:
    /// ℓ_d = max(0.24 d_b f_y / √f'c ; 0.044 d_b f_y) ≥ 200 mm. The 0.044 term governs for every
    /// f'c at or above (0.24 / 0.044)² ≈ 29.8 MPa, so a compression anchorage does not move between
    /// grade 30 and grade 35 concrete.
    /// </summary>
    public static bool TryCompressionDevelopmentLength(
        AbutmentRebarMaterial material,
        out AbutmentDevelopmentLength length,
        out string error)
    {
        length = EmptyLength;
        if (!TryValidateMaterial(material, out error)) return false;

        var strengthTerm = 0.24 * material.BarDiameterMm * material.YieldStrengthMPa /
                           Math.Sqrt(material.ConcreteStrengthMPa);
        var diameterTerm = 0.044 * material.BarDiameterMm * material.YieldStrengthMPa;
        length = Solve(
            strengthTerm, diameterTerm, 1, MinimumCompressionDevelopmentMm, DevelopmentModuleMm);
        return true;
    }

    /// <summary>
    /// Development length of a standard hook in tension, TCVN 11823-5:2017 §5.11.2.4:
    /// ℓ_hb = 100 d_b / √f'c × (f_y / 420), times 0.7 when the hook covers of §5.11.2.4.2 are met,
    /// then not less than 8 d_b and not less than 150 mm.
    /// </summary>
    public static bool TryHookDevelopmentLength(
        AbutmentRebarMaterial material,
        AbutmentHookCoverCondition cover,
        out AbutmentDevelopmentLength length,
        out string error)
    {
        length = EmptyLength;
        if (!TryValidateMaterial(material, out error)) return false;
        cover ??= new AbutmentHookCoverCondition();
        if (!TryHookCoverFactor(cover, out var factor, out error)) return false;

        var basicTerm = 100 * material.BarDiameterMm / Math.Sqrt(material.ConcreteStrengthMPa) *
                        (material.YieldStrengthMPa / 420);
        var diameterTerm = 8 * material.BarDiameterMm;
        // Both bounds of §5.11.2.4.1 sit on the factored length, unlike §5.11.2.1 where the diameter
        // bound sits on the basic one, so the 0.7 factor may not push a hook under 8 d_b.
        var factored = basicTerm * factor;
        var bounded = Math.Max(factored, diameterTerm);
        var govern = bounded < MinimumHookDevelopmentMm - LengthToleranceMm
            ? AbutmentDevelopmentGovern.AbsoluteMinimum
            : factored >= diameterTerm - LengthToleranceMm
                ? AbutmentDevelopmentGovern.PrimaryFormula
                : AbutmentDevelopmentGovern.LowerBound;
        var unrounded = Math.Max(bounded, MinimumHookDevelopmentMm);

        length = new AbutmentDevelopmentLength
        {
            PrimaryFormulaMm = basicTerm,
            LowerBoundMm = diameterTerm,
            BasicMm = basicTerm,
            BasicGovernedBy = AbutmentDevelopmentGovern.PrimaryFormula,
            CombinedFactor = factor,
            FactoredMm = factored,
            UnroundedMm = unrounded,
            RequiredMm = RoundUpToModule(unrounded, HookModuleMm),
            GovernedBy = govern
        };
        return true;
    }

    /// <summary>
    /// Tension lap splice class from the two-way table of TCVN 11823-5:2017 §5.11.5.3.1. The
    /// favourable row needs A_s provided / A_s required ≥ 2 proven by a calculation on file; without
    /// that evidence a splice of at most 50% of the bars is Class B, never Class A.
    /// </summary>
    public static bool TryResolveSpliceClass(
        AbutmentLapSpliceRequest request,
        out AbutmentSpliceClass spliceClass,
        out string error)
    {
        spliceClass = AbutmentSpliceClass.Unresolved;
        error = string.Empty;
        if (request is null)
        {
            error = "ABUTMENT_LAP_SPLICE_REQUEST_UNDECLARED: chưa có mô tả mối nối để tra cấp nối.";
            return false;
        }
        if (request.SplicedFraction == AbutmentSplicedFractionBand.Unresolved)
        {
            error = "ABUTMENT_LAP_SPLICED_FRACTION_UNRESOLVED: chưa khai tỷ lệ thanh nối trong một " +
                    "chiều dài nối nên không tra được cột của bảng §5.11.5.3.1.";
            return false;
        }
        if (request.AreaRatioAtLeastTwo &&
            string.IsNullOrWhiteSpace(request.AreaRatioEvidenceReference))
        {
            error = "ABUTMENT_LAP_AREA_RATIO_EVIDENCE_REQUIRED: khai tỷ lệ thép cung cấp trên thép " +
                    "yêu cầu ít nhất 2 thì phải dẫn hồ sơ tính chứng minh.";
            return false;
        }

        var fromTable = request.AreaRatioAtLeastTwo
            ? request.SplicedFraction switch
            {
                AbutmentSplicedFractionBand.UpToFiftyPercent => AbutmentSpliceClass.ClassA,
                AbutmentSplicedFractionBand.UpToSeventyFivePercent => AbutmentSpliceClass.ClassA,
                _ => AbutmentSpliceClass.ClassB
            }
            : request.SplicedFraction switch
            {
                AbutmentSplicedFractionBand.UpToFiftyPercent => AbutmentSpliceClass.ClassB,
                _ => AbutmentSpliceClass.ClassC
            };

        if (request.RequestedClass == AbutmentSpliceClass.Unresolved)
        {
            spliceClass = fromTable;
            return true;
        }
        // AbutmentSpliceClass is declared in order of increasing lap length, so the comparison below
        // reads as "at least as conservative as the table": asking for a longer lap than the table
        // demands is always allowed, asking for a shorter one never is.
        if (request.RequestedClass >= fromTable)
        {
            spliceClass = request.RequestedClass;
            return true;
        }
        error = request.RequestedClass == AbutmentSpliceClass.ClassA
            ? "ABUTMENT_LAP_CLASS_A_UNPROVEN: xin Cấp A nhưng bảng §5.11.5.3.1 cho " +
              $"{Describe(fromTable)} với dữ liệu đã khai; chỉ hạ về Cấp A khi có hồ sơ tính chứng " +
              "minh tỷ lệ thép cung cấp trên thép yêu cầu ít nhất 2."
            : $"ABUTMENT_LAP_CLASS_UNDERSTATED: xin {Describe(request.RequestedClass)} nhưng bảng " +
              $"§5.11.5.3.1 cho {Describe(fromTable)} với dữ liệu đã khai.";
        return false;
    }

    /// <summary>
    /// Tension lap splice length of TCVN 11823-5:2017 §5.11.5.3.1: 1.0, 1.3 or 1.7 times the
    /// detailed tension development length by class, never under 300 mm. The class factor multiplies
    /// the development length as detailed, so the lap of a bar is a whole multiple of the module the
    /// same bar's anchorage was detailed on.
    /// </summary>
    public static bool TryLapSpliceLength(
        AbutmentRebarMaterial material,
        AbutmentTensionDevelopmentModifiers modifiers,
        AbutmentLapSpliceRequest request,
        out AbutmentLapSplice splice,
        out string error)
    {
        splice = EmptySplice;
        if (!TryResolveSpliceClass(request, out var spliceClass, out error)) return false;
        if (!TryTensionDevelopmentLength(material, modifiers, out var development, out error))
            return false;

        var factor = ClassFactor(spliceClass);
        var factored = factor * development.RequiredMm;
        var unrounded = Math.Max(factored, MinimumLapSpliceMm);
        splice = new AbutmentLapSplice
        {
            SpliceClass = spliceClass,
            ClassFactor = factor,
            Development = development,
            UnroundedMm = unrounded,
            RequiredMm = RoundUpToModule(unrounded, DevelopmentModuleMm),
            GovernedByAbsoluteMinimum = factored < MinimumLapSpliceMm - LengthToleranceMm
        };
        return true;
    }

    /// <summary>
    /// Lap splice length of §5.11.5.3.1 rounded onto a detailing module coarser than the 10 mm the
    /// clause itself is detailed on. A yard that cuts and bends on a coarser module has to lengthen
    /// the lap, never shorten it, so the module may only be at least
    /// <see cref="DevelopmentModuleMm"/> and the result only rounds up.
    /// <see cref="AbutmentLapSplice.UnroundedMm"/> keeps the clause value the module was applied to,
    /// so the extra length a project's detailing rule adds stays visible.
    /// </summary>
    public static bool TryDetailedLapSpliceLength(
        AbutmentRebarMaterial material,
        AbutmentTensionDevelopmentModifiers modifiers,
        AbutmentLapSpliceRequest request,
        double detailingModuleMm,
        out AbutmentLapSplice splice,
        out string error)
    {
        splice = EmptySplice;
        if (!double.IsFinite(detailingModuleMm) ||
            detailingModuleMm < DevelopmentModuleMm - LengthToleranceMm)
        {
            error = $"ABUTMENT_LAP_DETAILING_MODULE_INVALID: bước làm tròn chi tiết " +
                    $"{detailingModuleMm} mm phải hữu hạn và không nhỏ hơn bước " +
                    $"{DevelopmentModuleMm:0.#} mm mà chính điều khoản đã dùng.";
            return false;
        }
        if (!TryLapSpliceLength(material, modifiers, request, out var clause, out error)) return false;

        splice = clause with
        {
            UnroundedMm = clause.RequiredMm,
            RequiredMm = RoundUpToModule(clause.RequiredMm, detailingModuleMm)
        };
        return true;
    }

    /// <summary>Class factor of §5.11.5.3.1. An unresolved class has no factor.</summary>
    public static double ClassFactor(AbutmentSpliceClass spliceClass) => spliceClass switch
    {
        AbutmentSpliceClass.ClassA => 1.0,
        AbutmentSpliceClass.ClassB => 1.3,
        AbutmentSpliceClass.ClassC => 1.7,
        _ => 0
    };

    /// <summary>
    /// Rounds a minimum length of the standard up onto a detailing module. Rounding down would
    /// detail a bar shorter than the clause allows, so every module in this kernel rounds up. A
    /// length already on the module stays there: <see cref="LengthToleranceMm"/> absorbs the square
    /// roots the clause formulas go through, without reaching any real fabrication tolerance.
    /// </summary>
    public static double RoundUpToModule(double value, double moduleMm) =>
        moduleMm <= 0 || !double.IsFinite(value)
            ? value
            : Math.Ceiling((value - LengthToleranceMm) / moduleMm) * moduleMm;

    /// <summary>
    /// Rejects material data no clause of §5.11 can be evaluated on. A non-positive diameter,
    /// strength or yield stress and a concrete grade outside the range the standard covers all stop
    /// here instead of producing an approximate length.
    /// </summary>
    public static bool TryValidateMaterial(AbutmentRebarMaterial material, out string error)
    {
        error = string.Empty;
        if (material is null)
        {
            error = "ABUTMENT_REBAR_MATERIAL_UNDECLARED: chưa khai đường kính, mác bê tông và giới hạn chảy.";
            return false;
        }
        if (!IsPositiveFinite(material.BarDiameterMm) ||
            material.BarDiameterMm > MaximumBarDiameterMm)
        {
            error = $"ABUTMENT_REBAR_BAR_DIAMETER_INVALID: đường kính {material.BarDiameterMm} mm " +
                    $"phải dương hữu hạn và không vượt {MaximumBarDiameterMm:0.#} mm.";
            return false;
        }
        if (!IsPositiveFinite(material.ConcreteStrengthMPa))
        {
            error = $"ABUTMENT_REBAR_CONCRETE_STRENGTH_INVALID: mác bê tông " +
                    $"{material.ConcreteStrengthMPa} MPa không dương hữu hạn.";
            return false;
        }
        if (material.ConcreteStrengthMPa < MinimumConcreteStrengthMPa ||
            material.ConcreteStrengthMPa > MaximumConcreteStrengthMPa)
        {
            error = $"ABUTMENT_REBAR_CONCRETE_STRENGTH_OUT_OF_RANGE: mác bê tông " +
                    $"{material.ConcreteStrengthMPa:0.#} MPa nằm ngoài dải " +
                    $"[{MinimumConcreteStrengthMPa:0.#}; {MaximumConcreteStrengthMPa:0.#}] MPa " +
                    "mà TCVN 11823-5:2017 §5.4.2.1 phủ.";
            return false;
        }
        if (!IsPositiveFinite(material.YieldStrengthMPa))
        {
            error = $"ABUTMENT_REBAR_YIELD_STRENGTH_INVALID: giới hạn chảy " +
                    $"{material.YieldStrengthMPa} MPa không dương hữu hạn.";
            return false;
        }
        if (material.YieldStrengthMPa < MinimumYieldStrengthMPa ||
            material.YieldStrengthMPa > MaximumYieldStrengthMPa)
        {
            error = $"ABUTMENT_REBAR_YIELD_STRENGTH_OUT_OF_RANGE: giới hạn chảy " +
                    $"{material.YieldStrengthMPa:0.#} MPa nằm ngoài dải " +
                    $"[{MinimumYieldStrengthMPa:0.#}; {MaximumYieldStrengthMPa:0.#}] MPa của thép thanh.";
            return false;
        }
        if (material.NominalAreaMm2 is { } area && !IsPositiveFinite(area))
        {
            error = $"ABUTMENT_REBAR_BAR_AREA_INVALID: diện tích danh nghĩa {area} mm² không dương hữu hạn.";
            return false;
        }
        return true;
    }

    private static AbutmentDevelopmentLength Solve(
        double primaryMm,
        double lowerBoundMm,
        double factor,
        double absoluteMinimumMm,
        double moduleMm)
    {
        var basic = Math.Max(primaryMm, lowerBoundMm);
        var basicGovern = primaryMm >= lowerBoundMm - LengthToleranceMm
            ? AbutmentDevelopmentGovern.PrimaryFormula
            : AbutmentDevelopmentGovern.LowerBound;
        var factored = basic * factor;
        var govern = factored >= absoluteMinimumMm - LengthToleranceMm
            ? basicGovern
            : AbutmentDevelopmentGovern.AbsoluteMinimum;
        var unrounded = Math.Max(factored, absoluteMinimumMm);
        return new AbutmentDevelopmentLength
        {
            PrimaryFormulaMm = primaryMm,
            LowerBoundMm = lowerBoundMm,
            BasicMm = basic,
            BasicGovernedBy = basicGovern,
            CombinedFactor = factor,
            FactoredMm = factored,
            UnroundedMm = unrounded,
            RequiredMm = RoundUpToModule(unrounded, moduleMm),
            GovernedBy = govern
        };
    }

    private static bool TryCombineTensionFactors(
        AbutmentRebarMaterial material,
        AbutmentTensionDevelopmentModifiers modifiers,
        out double factor,
        out string error)
    {
        factor = 1;
        error = string.Empty;

        factor *= modifiers.EpoxyCoating switch
        {
            AbutmentEpoxyCoating.CoatedWithCloseCoverOrSpacing => 1.5,
            AbutmentEpoxyCoating.CoatedOther => 1.2,
            _ => 1.0
        };

        if (modifiers.LightweightConcreteFactor is { } lightweight)
        {
            if (!double.IsFinite(lightweight) || lightweight < 1.2 - 1e-9 || lightweight > 1.3 + 1e-9)
            {
                error = $"ABUTMENT_DEVELOPMENT_LIGHTWEIGHT_FACTOR_INVALID: hệ số bê tông nhẹ " +
                        $"{lightweight} nằm ngoài dải [1,2; 1,3] của §5.11.2.1.2.";
                return false;
            }
            factor *= lightweight;
        }

        if (modifiers.ClaimWideSpacingReduction)
        {
            if (material.BarDiameterMm > WideSpacingMaximumBarDiameterMm + LengthToleranceMm ||
                modifiers.LateralSpacingMm is not { } spacing ||
                modifiers.ClearCoverInSpacingDirectionMm is not { } cover ||
                !double.IsFinite(spacing) || !double.IsFinite(cover) ||
                spacing < WideSpacingMinimumSpacingMm - LengthToleranceMm ||
                cover < WideSpacingMinimumCoverMm - LengthToleranceMm)
            {
                error = "ABUTMENT_DEVELOPMENT_WIDE_SPACING_UNPROVEN: hệ số giảm 0,8 của §5.11.2.1.3 " +
                        $"đòi đường kính không quá {WideSpacingMaximumBarDiameterMm:0.#} mm, bước ít " +
                        $"nhất {WideSpacingMinimumSpacingMm:0.#} mm và cover theo hướng bước ít nhất " +
                        $"{WideSpacingMinimumCoverMm:0.#} mm; dữ liệu đã khai chưa chứng minh đủ.";
                return false;
            }
            factor *= 0.8;
        }

        if (modifiers.ClaimSpiralReduction)
        {
            if (modifiers.SpiralBarDiameterMm is not { } spiralBar ||
                modifiers.SpiralPitchMm is not { } pitch ||
                !double.IsFinite(spiralBar) || !double.IsFinite(pitch) ||
                spiralBar < SpiralMinimumBarDiameterMm - LengthToleranceMm ||
                pitch <= 0 || pitch > SpiralMaximumPitchMm + LengthToleranceMm)
            {
                error = "ABUTMENT_DEVELOPMENT_SPIRAL_UNPROVEN: hệ số giảm 0,75 của §5.11.2.1.3 đòi " +
                        $"đai xoắn đường kính ít nhất {SpiralMinimumBarDiameterMm:0.#} mm và bước " +
                        $"không quá {SpiralMaximumPitchMm:0.#} mm; dữ liệu đã khai chưa chứng minh đủ.";
                return false;
            }
            factor *= 0.75;
        }

        if (modifiers.ExcessReinforcementRatio is { } ratio)
        {
            if (!double.IsFinite(ratio) || ratio <= 0 || ratio > 1 + 1e-9)
            {
                error = $"ABUTMENT_DEVELOPMENT_EXCESS_RATIO_INVALID: tỷ lệ thép yêu cầu trên thép " +
                        $"cung cấp {ratio} phải nằm trong (0; 1] theo §5.11.2.1.3.";
                return false;
            }
            factor *= ratio;
        }

        return true;
    }

    private static bool TryHookCoverFactor(
        AbutmentHookCoverCondition cover,
        out double factor,
        out string error)
    {
        factor = 1;
        error = string.Empty;
        if (!cover.ClaimCoverReduction) return true;

        if (cover.SideCoverNormalToHookPlaneMm is not { } sideCover ||
            cover.CoverOnBarExtensionMm is not { } extensionCover ||
            !double.IsFinite(sideCover) || !double.IsFinite(extensionCover) ||
            sideCover < HookSideCoverMm - LengthToleranceMm ||
            extensionCover < HookExtensionCoverMm - LengthToleranceMm)
        {
            error = "ABUTMENT_DEVELOPMENT_HOOK_COVER_UNPROVEN: hệ số giảm 0,7 của §5.11.2.4.2 đòi " +
                    $"cover ngang mặt phẳng móc ít nhất {HookSideCoverMm:0.#} mm và cover trên đoạn " +
                    $"kéo dài ít nhất {HookExtensionCoverMm:0.#} mm; dữ liệu đã khai chưa chứng minh đủ.";
            return false;
        }
        factor = 0.7;
        return true;
    }

    private static string Describe(AbutmentSpliceClass spliceClass) => spliceClass switch
    {
        AbutmentSpliceClass.ClassA => "Cấp A",
        AbutmentSpliceClass.ClassB => "Cấp B",
        AbutmentSpliceClass.ClassC => "Cấp C",
        _ => "cấp chưa xác định"
    };

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;
}
