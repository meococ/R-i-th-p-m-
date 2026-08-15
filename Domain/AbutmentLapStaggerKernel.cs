namespace BIM.DatViet.Domain;

/// <summary>A band along the bar where one cut pattern carries its lap splice.</summary>
public readonly record struct AbutmentSpliceZone(double StartMm, double EndMm)
{
    public double LengthMm => EndMm - StartMm;

    public double CentreMm => (StartMm + EndMm) / 2;
}

/// <summary>
/// What a single-splice stagger pattern is solved from. The stock length and the cutting module are
/// yard data, the lap length comes from
/// <see cref="AbutmentDevelopmentLengthKernel.TryLapSpliceLength"/>, and the stagger fraction is the
/// detailing policy the two splice zones are separated by.
/// </summary>
public sealed record AbutmentLapStaggerRequest
{
    /// <summary>Length one finished bar has to reach, cover to cover.</summary>
    public double BarLengthMm { get; init; }

    /// <summary>Lap splice length the splice class of §5.11.5.3.1 requires.</summary>
    public double LapLengthMm { get; init; }

    /// <summary>Length of the stock bar the yard delivers.</summary>
    public double StockLengthMm { get; init; } = AbutmentLapStaggerKernel.DefaultStockLengthMm;

    /// <summary>Module every cut length is rounded onto.</summary>
    public double CuttingModuleMm { get; init; } = AbutmentLapStaggerKernel.DefaultCuttingModuleMm;

    /// <summary>
    /// Share of the bar length the two splice-zone centres are separated by, so each zone sits half
    /// that far from mid-length. Raised to twice the lap length whenever the policy value would not
    /// keep the two zones apart.
    /// </summary>
    public double SpliceCentreStaggerFraction { get; init; } =
        AbutmentLapStaggerKernel.DefaultSpliceCentreStaggerFraction;

    /// <summary>
    /// Shorter cut length the caller wants checked instead of solved, so a bar bending schedule
    /// already on paper can be graded against the same rules. Null solves from the policy.
    /// </summary>
    public double? ShortPieceOverrideMm { get; init; }
}

/// <summary>
/// One staggered single-splice pattern: two cut lengths, where the even bars are laid reversed end
/// for end so their splice falls in the other zone.
/// </summary>
public sealed record AbutmentLapStaggerPattern
{
    public double BarLengthMm { get; init; }

    public double LapLengthMm { get; init; }

    /// <summary>What the two pieces actually add up to.</summary>
    public double TotalBlankLengthMm { get; init; }

    /// <summary>
    /// Overlap the two pieces actually produce, <c>short + long − bar</c>. Equals
    /// <see cref="LapLengthMm"/> for a solved pattern and exceeds it for a declared pair whose
    /// pieces were rounded up.
    /// </summary>
    public double AchievedLapMm { get; init; }

    /// <summary>First piece of an odd bar, second piece of an even bar.</summary>
    public double ShortPieceMm { get; init; }

    /// <summary>Second piece of an odd bar, first piece of an even bar.</summary>
    public double LongPieceMm { get; init; }

    /// <summary>Splice band of the bars laid short piece first.</summary>
    public AbutmentSpliceZone OddBarSpliceZone { get; init; }

    /// <summary>Splice band of the bars laid reversed, long piece first.</summary>
    public AbutmentSpliceZone EvenBarSpliceZone { get; init; }

    /// <summary>Clear distance between the two splice bands; equals bar length minus twice the short piece.</summary>
    public double ClearGapBetweenSpliceZonesMm { get; init; }

    /// <summary>Share of the bars spliced at the worst section: 0.5 once the two bands are apart.</summary>
    public double WorstSectionSplicedFraction { get; init; }

    public int SpliceCountPerBar { get; init; }

    /// <summary>True when the short piece was supplied by the caller instead of solved.</summary>
    public bool ShortPieceFromOverride { get; init; }
}

/// <summary>
/// Turns one bar longer than a stock bar into a staggered pair of cut lengths, as a pure kernel: no
/// Revit API, no I/O, millimeters only.
///
/// A bar longer than the stock length has to be spliced, while the drawing asks for reversed
/// staggered splices with at most half the bars spliced on any one section. Two cut lengths solve
/// both at once: lay the even bars reversed end for end and their splice moves into a second band,
/// so no section ever carries more than one of the two patterns.
///
/// Only the acceptance rules are the standard's — both pieces inside the stock length, the two
/// pieces differing by at least twice the lap so the bands keep a clear gap, and at most half the
/// bars spliced per section. Where inside the resulting window the split point sits is a detailing
/// policy, carried by <see cref="AbutmentLapStaggerRequest.SpliceCentreStaggerFraction"/> and
/// overridable, never a hidden constant.
/// </summary>
public static class AbutmentLapStaggerKernel
{
    /// <summary>Stock bar length a yard delivers unless the project states another one.</summary>
    public const double DefaultStockLengthMm = 11700;

    /// <summary>Module cut lengths are rounded onto unless the project states another one.</summary>
    public const double DefaultCuttingModuleMm = 100;

    /// <summary>
    /// Default separation of the two splice-zone centres, as a share of the bar length: the zones
    /// sit at 40% and 60% of the bar, keeping both clear of mid-length.
    /// </summary>
    public const double DefaultSpliceCentreStaggerFraction = 0.20;

    /// <summary>Millimeters two lengths may differ by and still count as equal.</summary>
    public const double LengthToleranceMm = 1e-6;

    private static readonly AbutmentLapStaggerPattern EmptyPattern = new();

    /// <summary>
    /// Number of laps one bar needs: with n pieces a bar reaches n × stock − (n − 1) × lap, so the
    /// piece count is ⌈(L − lap) / (stock − lap)⌉ and the lap count is one less.
    /// </summary>
    public static bool TryRequiredSpliceCount(
        double barLengthMm,
        double lapLengthMm,
        double stockLengthMm,
        out int spliceCount,
        out string error)
    {
        spliceCount = 0;
        if (!TryValidateLengths(barLengthMm, lapLengthMm, stockLengthMm, out error)) return false;

        if (barLengthMm <= stockLengthMm + LengthToleranceMm) return true;
        var pieces = (int)Math.Ceiling(
            (barLengthMm - lapLengthMm) / (stockLengthMm - lapLengthMm) - 1e-9);
        spliceCount = Math.Max(pieces - 1, 1);
        return true;
    }

    /// <summary>
    /// Solves the two cut lengths of a bar that needs exactly one lap. Fails closed when the bar
    /// needs no lap, when it needs more than one, or when no split point satisfies the stock length
    /// and the stagger requirement at once.
    /// </summary>
    public static bool TryPlanSingleSplice(
        AbutmentLapStaggerRequest request,
        out AbutmentLapStaggerPattern pattern,
        out string error)
    {
        pattern = EmptyPattern;
        error = string.Empty;
        if (request is null)
        {
            error = "ABUTMENT_LAP_STAGGER_REQUEST_UNDECLARED: chưa có dữ liệu thanh, nối chồng và phôi kho.";
            return false;
        }
        if (!TryRequiredSpliceCount(
                request.BarLengthMm, request.LapLengthMm, request.StockLengthMm,
                out var spliceCount, out error))
            return false;
        if (spliceCount == 0)
        {
            error = $"ABUTMENT_LAP_STAGGER_NOT_REQUIRED: thanh {request.BarLengthMm:0.#} mm nằm trong " +
                    $"phôi kho {request.StockLengthMm:0.#} mm nên không cần mối nối nào.";
            return false;
        }
        if (spliceCount > 1)
        {
            error = $"ABUTMENT_LAP_STAGGER_MULTIPLE_SPLICES_REQUIRED: thanh {request.BarLengthMm:0.#} mm " +
                    $"với nối chồng {request.LapLengthMm:0.#} mm và phôi kho {request.StockLengthMm:0.#} mm " +
                    $"cần {spliceCount} mối nối; bộ sinh này chỉ giải sơ đồ một mối nối.";
            return false;
        }
        if (!double.IsFinite(request.CuttingModuleMm) || request.CuttingModuleMm <= 0)
        {
            error = $"ABUTMENT_LAP_STAGGER_CUTTING_MODULE_INVALID: bước làm tròn chiều dài cắt " +
                    $"{request.CuttingModuleMm} mm không dương hữu hạn.";
            return false;
        }
        if (!double.IsFinite(request.SpliceCentreStaggerFraction) ||
            request.SpliceCentreStaggerFraction <= 0 ||
            request.SpliceCentreStaggerFraction >= 1)
        {
            error = $"ABUTMENT_LAP_STAGGER_FRACTION_INVALID: tỷ lệ so le tim mối nối " +
                    $"{request.SpliceCentreStaggerFraction} phải nằm trong khoảng mở (0; 1).";
            return false;
        }

        var totalBlank = request.BarLengthMm + request.LapLengthMm;
        var shortestAllowed = totalBlank - request.StockLengthMm;
        var longestAllowed = (request.BarLengthMm - request.LapLengthMm) / 2;
        if (shortestAllowed > longestAllowed + LengthToleranceMm)
        {
            error = "ABUTMENT_LAP_STAGGER_NO_VALID_SPLIT: đoạn ngắn phải ít nhất " +
                    $"{shortestAllowed:0.#} mm để đoạn dài nằm trong phôi kho, nhưng không được vượt " +
                    $"{longestAllowed:0.#} mm để hai vùng nối còn hở; hai điều kiện loại trừ nhau.";
            return false;
        }

        double shortPiece;
        bool fromOverride;
        if (request.ShortPieceOverrideMm is { } declared)
        {
            if (!double.IsFinite(declared) || declared <= 0 ||
                2 * declared > totalBlank + LengthToleranceMm)
            {
                error = $"ABUTMENT_LAP_STAGGER_SHORT_PIECE_INVALID: đoạn ngắn khai {declared} mm phải " +
                        $"dương hữu hạn và không vượt nửa tổng phôi {totalBlank / 2:0.#} mm.";
                return false;
            }
            shortPiece = declared;
            fromOverride = true;
        }
        else
        {
            var centreToCentre = Math.Max(
                request.SpliceCentreStaggerFraction * request.BarLengthMm, 2 * request.LapLengthMm);
            shortPiece = RoundToNearestModule(
                (totalBlank - centreToCentre) / 2, request.CuttingModuleMm);
            if (shortPiece > longestAllowed)
                shortPiece = RoundDownToModule(longestAllowed, request.CuttingModuleMm);
            if (shortPiece < shortestAllowed)
                shortPiece = RoundUpToModule(shortestAllowed, request.CuttingModuleMm);
            fromOverride = false;
            if (shortPiece < shortestAllowed - LengthToleranceMm ||
                shortPiece > longestAllowed + LengthToleranceMm)
            {
                error = "ABUTMENT_LAP_STAGGER_NO_VALID_SPLIT: bước làm tròn chiều dài cắt " +
                        $"{request.CuttingModuleMm:0.#} mm không có bội số nào trong dải hợp lệ " +
                        $"[{shortestAllowed:0.#}; {longestAllowed:0.#}] mm.";
                return false;
            }
        }

        return TryBuildPattern(
            request.BarLengthMm, request.LapLengthMm, request.StockLengthMm,
            shortPiece, totalBlank - shortPiece, fromOverride, out pattern, out error);
    }

    /// <summary>
    /// Grades a cut-length pair a bar bending schedule already states. Both pieces have to add up to
    /// the bar length plus one full lap: a pair that adds up to less silently shortens the lap, which
    /// is the failure mode a schedule rounded piece by piece runs into.
    /// </summary>
    public static bool TryVerifyDeclaredPieces(
        double barLengthMm,
        double lapLengthMm,
        double stockLengthMm,
        double firstPieceMm,
        double secondPieceMm,
        out AbutmentLapStaggerPattern pattern,
        out string error)
    {
        pattern = EmptyPattern;
        if (!TryValidateLengths(barLengthMm, lapLengthMm, stockLengthMm, out error)) return false;
        if (!double.IsFinite(firstPieceMm) || firstPieceMm <= 0 ||
            !double.IsFinite(secondPieceMm) || secondPieceMm <= 0)
        {
            error = $"ABUTMENT_LAP_STAGGER_SHORT_PIECE_INVALID: hai chiều dài cắt {firstPieceMm} mm và " +
                    $"{secondPieceMm} mm phải dương hữu hạn.";
            return false;
        }

        var achievedLap = firstPieceMm + secondPieceMm - barLengthMm;
        if (achievedLap < lapLengthMm - LengthToleranceMm)
        {
            error = $"ABUTMENT_LAP_STAGGER_TOTAL_BLANK_SHORT: hai chiều dài cắt {firstPieceMm:0.#} mm và " +
                    $"{secondPieceMm:0.#} mm chỉ chồng nhau {achievedLap:0.#} mm trên thanh " +
                    $"{barLengthMm:0.#} mm, thiếu {lapLengthMm - achievedLap:0.#} mm so với nối chồng " +
                    $"yêu cầu {lapLengthMm:0.#} mm.";
            return false;
        }

        return TryBuildPattern(
            barLengthMm, lapLengthMm, stockLengthMm,
            Math.Min(firstPieceMm, secondPieceMm), Math.Max(firstPieceMm, secondPieceMm),
            true, out pattern, out error);
    }

    /// <summary>Rounds onto the nearest module; ties go up, so the split never drifts down silently.</summary>
    public static double RoundToNearestModule(double value, double moduleMm) =>
        moduleMm <= 0 || !double.IsFinite(value)
            ? value
            : Math.Round(value / moduleMm, MidpointRounding.AwayFromZero) * moduleMm;

    private static double RoundDownToModule(double value, double moduleMm) =>
        moduleMm <= 0 || !double.IsFinite(value)
            ? value
            : Math.Floor(value / moduleMm + 1e-9) * moduleMm;

    private static double RoundUpToModule(double value, double moduleMm) =>
        AbutmentDevelopmentLengthKernel.RoundUpToModule(value, moduleMm);

    private static bool TryBuildPattern(
        double barLengthMm,
        double lapLengthMm,
        double stockLengthMm,
        double shortPieceMm,
        double longPieceMm,
        bool fromOverride,
        out AbutmentLapStaggerPattern pattern,
        out string error)
    {
        pattern = EmptyPattern;
        error = string.Empty;
        if (shortPieceMm <= lapLengthMm + LengthToleranceMm)
        {
            error = $"ABUTMENT_LAP_STAGGER_SHORT_PIECE_BELOW_LAP: đoạn ngắn {shortPieceMm:0.#} mm không " +
                    $"dài hơn nối chồng {lapLengthMm:0.#} mm nên không chứa được vùng nối.";
            return false;
        }
        if (longPieceMm > stockLengthMm + LengthToleranceMm)
        {
            error = $"ABUTMENT_LAP_STAGGER_PIECE_EXCEEDS_STOCK: đoạn dài {longPieceMm:0.#} mm vượt phôi " +
                    $"kho {stockLengthMm:0.#} mm.";
            return false;
        }

        // Odd bars run short piece first, so their overlap sits just inside the short piece. Even bars
        // are laid reversed end for end, which puts the same overlap at the mirrored station. Both
        // bands are built from the overlap the pieces really produce, not from the required lap, so a
        // declared pair that adds up to more than one lap still reports its true bands.
        var achievedLap = shortPieceMm + longPieceMm - barLengthMm;
        var oddZone = new AbutmentSpliceZone(shortPieceMm - achievedLap, shortPieceMm);
        var evenZone = new AbutmentSpliceZone(
            barLengthMm - shortPieceMm, barLengthMm - shortPieceMm + achievedLap);
        var clearGap = evenZone.StartMm - oddZone.EndMm;
        if (longPieceMm - shortPieceMm < 2 * lapLengthMm - LengthToleranceMm)
        {
            error = $"ABUTMENT_LAP_STAGGER_ZONES_TOO_CLOSE: hai đoạn {shortPieceMm:0.#} mm và " +
                    $"{longPieceMm:0.#} mm chỉ chênh nhau {longPieceMm - shortPieceMm:0.#} mm, chưa đạt " +
                    $"hai lần nối chồng {2 * lapLengthMm:0.#} mm nên hai vùng nối chỉ hở " +
                    $"{clearGap:0.#} mm.";
            return false;
        }

        pattern = new AbutmentLapStaggerPattern
        {
            BarLengthMm = barLengthMm,
            LapLengthMm = lapLengthMm,
            TotalBlankLengthMm = shortPieceMm + longPieceMm,
            AchievedLapMm = achievedLap,
            ShortPieceMm = shortPieceMm,
            LongPieceMm = longPieceMm,
            OddBarSpliceZone = oddZone,
            EvenBarSpliceZone = evenZone,
            ClearGapBetweenSpliceZonesMm = clearGap,
            WorstSectionSplicedFraction = 0.5,
            SpliceCountPerBar = 1,
            ShortPieceFromOverride = fromOverride
        };
        return true;
    }

    private static bool TryValidateLengths(
        double barLengthMm,
        double lapLengthMm,
        double stockLengthMm,
        out string error)
    {
        error = string.Empty;
        if (!double.IsFinite(barLengthMm) || barLengthMm <= 0)
        {
            error = $"ABUTMENT_LAP_STAGGER_BAR_LENGTH_INVALID: chiều dài thanh {barLengthMm} mm " +
                    "không dương hữu hạn.";
            return false;
        }
        if (!double.IsFinite(lapLengthMm) || lapLengthMm <= 0)
        {
            error = $"ABUTMENT_LAP_STAGGER_LAP_LENGTH_INVALID: chiều dài nối chồng {lapLengthMm} mm " +
                    "không dương hữu hạn.";
            return false;
        }
        if (lapLengthMm >= barLengthMm - LengthToleranceMm)
        {
            error = $"ABUTMENT_LAP_STAGGER_LAP_LENGTH_INVALID: nối chồng {lapLengthMm:0.#} mm không nhỏ " +
                    $"hơn chiều dài thanh {barLengthMm:0.#} mm.";
            return false;
        }
        if (!double.IsFinite(stockLengthMm) || stockLengthMm <= 0)
        {
            error = $"ABUTMENT_LAP_STAGGER_STOCK_LENGTH_INVALID: chiều dài phôi kho {stockLengthMm} mm " +
                    "không dương hữu hạn.";
            return false;
        }
        if (stockLengthMm <= lapLengthMm + LengthToleranceMm)
        {
            error = $"ABUTMENT_LAP_STAGGER_STOCK_LENGTH_INVALID: phôi kho {stockLengthMm:0.#} mm không " +
                    $"dài hơn nối chồng {lapLengthMm:0.#} mm nên nối bao nhiêu đoạn cũng không dài thêm.";
            return false;
        }
        return true;
    }
}
