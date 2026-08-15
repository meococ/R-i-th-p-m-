namespace BIM.DatViet.Domain;

/// <summary>
/// One physical rebar object of one bar position. A position is what the drawing counts and what
/// <c>expectedBarCount</c> means; a piece is what the yard cuts and what the model holds. A position
/// that fits one stock bar is a single piece, so this record describes the unspliced layer as well
/// and the two paths never diverge.
/// </summary>
public readonly record struct AbutmentBarPiece(
    int PositionIndex,
    int PieceIndex,
    int PieceCount,
    double StartOffsetMm,
    double EndOffsetMm,
    double LapLengthMm,
    bool ReversedLayout)
{
    /// <summary>Cut length of this piece.</summary>
    public double LengthMm => EndOffsetMm - StartOffsetMm;
}

/// <summary>A piece measured back out of the model, projected onto the axis of its own position.</summary>
public readonly record struct AbutmentMeasuredBarPiece(
    int PositionIndex,
    double StartOffsetMm,
    double EndOffsetMm)
{
    public double LengthMm => EndOffsetMm - StartOffsetMm;
}

/// <summary>What one layer's piece plan is solved from.</summary>
public sealed record AbutmentBarPiecePlanRequest
{
    /// <summary>Number of bar positions the station chain produces.</summary>
    public int PositionCount { get; init; }

    /// <summary>Longest measured position; the cut pair has to reach this one.</summary>
    public double LongestBarLengthMm { get; init; }

    /// <summary>Shortest measured position; only used to prove the layer is one cut pair.</summary>
    public double ShortestBarLengthMm { get; init; }

    public double LapLengthMm { get; init; }

    public double StockLengthMm { get; init; }

    public double CuttingModuleMm { get; init; }

    public double SpliceCentreStaggerFraction { get; init; }

    /// <summary>Share of the bars the drawing allows to be spliced on one section.</summary>
    public double MaximumSplicedFractionPerSection { get; init; }
}

/// <summary>
/// The two cut lengths of one layer plus everything a bar bending schedule and a cutting list need
/// to be rebuilt from the model later.
/// </summary>
public sealed record AbutmentBarPiecePlan
{
    public int PositionCount { get; init; }

    public int PiecesPerPosition { get; init; } = 1;

    /// <summary>Number of rebar objects the layer produces; the model count, not the drawing count.</summary>
    public int TotalPieceCount => PositionCount * PiecesPerPosition;

    public double LapLengthMm { get; init; }

    public double ShortPieceMm { get; init; }

    public double LongPieceMm { get; init; }

    public double WorstSectionSplicedFraction { get; init; }

    public double ClearGapBetweenSpliceZonesMm { get; init; }

    /// <summary>Splice band of the odd positions, laid short piece first.</summary>
    public AbutmentSpliceZone OddPositionSpliceZone { get; init; }

    /// <summary>Splice band of the even positions, laid reversed end for end.</summary>
    public AbutmentSpliceZone EvenPositionSpliceZone { get; init; }

    public bool IsSpliced => PiecesPerPosition > 1;
}

/// <summary>
/// Turns one layer of bar positions into the rebar objects a model can actually hold, as a pure
/// kernel: no Revit API, no I/O, millimeters only.
///
/// A position longer than a stock bar becomes two overlapping pieces. Which two comes from
/// <see cref="AbutmentLapStaggerKernel"/>; this kernel only decides where along each position they
/// sit, alternates odd and even positions end for end so the two splice bands never share a
/// section, and hands the readback the same arithmetic so a model can be graded against the plan
/// that built it.
///
/// Every count is kept in two layers on purpose. The position count is what the drawing states and
/// what stays traceable to the source; the piece count is what the model holds. Collapsing them
/// into one number is how a schedule ends up listing bars that were never cut.
/// </summary>
public static class AbutmentBarPieceKernel
{
    /// <summary>Millimeters two lengths may differ by and still count as equal.</summary>
    public const double LengthToleranceMm = 1e-6;

    /// <summary>
    /// Spread the measured positions of one layer may cover and still be cut as a single pair. A
    /// parallelogram footing gives every longitudinal bar the same length, so anything wider than
    /// float noise means the layer is not one cut pair and the yard needs to be told.
    /// </summary>
    public const double BarLengthUniformityToleranceMm = 1.0;

    /// <summary>A layer whose positions each fit one stock bar; the shape of every rule before this wave.</summary>
    public static AbutmentBarPiecePlan SinglePiecePlan(int positionCount) =>
        new() { PositionCount = positionCount, PiecesPerPosition = 1 };

    /// <summary>Numeric ceiling of the §5.11.5.3.1 column a rule declares.</summary>
    public static bool TryResolveSplicedFractionLimit(
        AbutmentSplicedFractionBand band,
        out double limit,
        out string error)
    {
        error = string.Empty;
        limit = band switch
        {
            AbutmentSplicedFractionBand.UpToFiftyPercent => 0.5,
            AbutmentSplicedFractionBand.UpToSeventyFivePercent => 0.75,
            AbutmentSplicedFractionBand.UpToOneHundredPercent => 1.0,
            _ => 0
        };
        if (limit > 0) return true;
        error = "ABUTMENT_BAR_PIECE_SPLICED_FRACTION_UNRESOLVED: chưa khai tỷ lệ thanh nối trên một " +
                "mặt cắt nên không có ngưỡng nào để kiểm sơ đồ so le.";
        return false;
    }

    /// <summary>
    /// Solves the one cut pair a whole layer is built from. The pair is solved against the longest
    /// measured position, so every position of the layer reaches its far end and the shorter ones
    /// simply overlap by more than the required lap — never by less.
    /// </summary>
    public static bool TryPlanSplicedPositions(
        AbutmentBarPiecePlanRequest request,
        out AbutmentBarPiecePlan plan,
        out string error)
    {
        plan = SinglePiecePlan(0);
        error = string.Empty;
        if (request is null)
        {
            error = "ABUTMENT_BAR_PIECE_REQUEST_UNDECLARED: chưa có dữ liệu lớp thép để giải sơ đồ nối.";
            return false;
        }
        if (request.PositionCount <= 0)
        {
            error = $"ABUTMENT_BAR_PIECE_POSITION_COUNT_INVALID: lớp thép có {request.PositionCount} " +
                    "vị trí thanh nên không có gì để nối.";
            return false;
        }
        if (!double.IsFinite(request.ShortestBarLengthMm) || request.ShortestBarLengthMm <= 0 ||
            !double.IsFinite(request.LongestBarLengthMm) || request.LongestBarLengthMm <= 0 ||
            request.ShortestBarLengthMm > request.LongestBarLengthMm + LengthToleranceMm)
        {
            error = $"ABUTMENT_BAR_PIECE_BAR_LENGTH_INVALID: dải chiều dài đo được " +
                    $"[{request.ShortestBarLengthMm}; {request.LongestBarLengthMm}] mm không hợp lệ.";
            return false;
        }

        var spread = request.LongestBarLengthMm - request.ShortestBarLengthMm;
        if (spread > BarLengthUniformityToleranceMm)
        {
            error = $"ABUTMENT_BAR_PIECE_BAR_LENGTH_NOT_UNIFORM: các vị trí thanh của lớp dài từ " +
                    $"{request.ShortestBarLengthMm:0.#} đến {request.LongestBarLengthMm:0.#} mm, " +
                    $"chênh {spread:0.###} mm vượt dung sai {BarLengthUniformityToleranceMm:0.#} mm nên " +
                    "lớp này không phải một cặp chiều dài cắt duy nhất; bảng cắt phôi sẽ sai.";
            return false;
        }

        if (!AbutmentLapStaggerKernel.TryPlanSingleSplice(
                new AbutmentLapStaggerRequest
                {
                    BarLengthMm = request.LongestBarLengthMm,
                    LapLengthMm = request.LapLengthMm,
                    StockLengthMm = request.StockLengthMm,
                    CuttingModuleMm = request.CuttingModuleMm,
                    SpliceCentreStaggerFraction = request.SpliceCentreStaggerFraction
                },
                out var pattern,
                out error))
            return false;

        if (!double.IsFinite(request.MaximumSplicedFractionPerSection) ||
            request.MaximumSplicedFractionPerSection <= 0 ||
            request.MaximumSplicedFractionPerSection > 1)
        {
            error = $"ABUTMENT_BAR_PIECE_SPLICED_FRACTION_INVALID: ngưỡng tỷ lệ nối " +
                    $"{request.MaximumSplicedFractionPerSection} phải nằm trong (0; 1].";
            return false;
        }
        if (pattern.WorstSectionSplicedFraction >
            request.MaximumSplicedFractionPerSection + LengthToleranceMm)
        {
            error = "ABUTMENT_BAR_PIECE_SPLICED_FRACTION_EXCEEDED: sơ đồ so le cho tỷ lệ nối " +
                    $"{pattern.WorstSectionSplicedFraction:0.###} trên mặt cắt bất lợi nhất, vượt " +
                    $"ngưỡng {request.MaximumSplicedFractionPerSection:0.###} mà rule khai theo " +
                    "§5.11.5.3.1.";
            return false;
        }
        if (pattern.OddBarSpliceZone.EndMm > pattern.EvenBarSpliceZone.StartMm + LengthToleranceMm)
        {
            error = "ABUTMENT_BAR_PIECE_SPLICE_ZONES_OVERLAP: vùng nối của thanh lẻ kết thúc ở " +
                    $"{pattern.OddBarSpliceZone.EndMm:0.#} mm trong khi vùng nối của thanh chẵn bắt " +
                    $"đầu ở {pattern.EvenBarSpliceZone.StartMm:0.#} mm, nên có mặt cắt mang cả hai " +
                    "sơ đồ và tỷ lệ nối vượt ngưỡng.";
            return false;
        }

        plan = new AbutmentBarPiecePlan
        {
            PositionCount = request.PositionCount,
            PiecesPerPosition = pattern.SpliceCountPerBar + 1,
            LapLengthMm = pattern.AchievedLapMm,
            ShortPieceMm = pattern.ShortPieceMm,
            LongPieceMm = pattern.LongPieceMm,
            WorstSectionSplicedFraction = pattern.WorstSectionSplicedFraction,
            ClearGapBetweenSpliceZonesMm = pattern.ClearGapBetweenSpliceZonesMm,
            OddPositionSpliceZone = pattern.OddBarSpliceZone,
            EvenPositionSpliceZone = pattern.EvenBarSpliceZone
        };
        return true;
    }

    /// <summary>
    /// Places the solved cut pair onto one position. Odd positions run the short piece first, even
    /// positions are laid reversed end for end, which is what moves their splice into the second
    /// band and keeps any one section at half the bars.
    /// </summary>
    public static bool TryLayOutPosition(
        AbutmentBarPiecePlan plan,
        int positionIndex,
        double barLengthMm,
        out IReadOnlyList<AbutmentBarPiece> pieces,
        out string error)
    {
        pieces = [];
        error = string.Empty;
        if (plan is null)
        {
            error = "ABUTMENT_BAR_PIECE_PLAN_UNDECLARED: chưa có sơ đồ đoạn để đặt lên vị trí thanh.";
            return false;
        }
        if (positionIndex <= 0)
        {
            error = $"ABUTMENT_BAR_PIECE_POSITION_INDEX_INVALID: số thứ tự vị trí thanh {positionIndex} " +
                    "phải bắt đầu từ 1.";
            return false;
        }
        if (!double.IsFinite(barLengthMm) || barLengthMm <= 0)
        {
            error = $"ABUTMENT_BAR_PIECE_BAR_LENGTH_INVALID: vị trí thanh {positionIndex} đo được " +
                    $"{barLengthMm} mm.";
            return false;
        }

        if (!plan.IsSpliced)
        {
            pieces = [new AbutmentBarPiece(positionIndex, 1, 1, 0, barLengthMm, 0, false)];
            return true;
        }

        var reversed = positionIndex % 2 == 0;
        var firstPiece = reversed ? plan.LongPieceMm : plan.ShortPieceMm;
        var secondPiece = reversed ? plan.ShortPieceMm : plan.LongPieceMm;
        if (firstPiece > barLengthMm - LengthToleranceMm ||
            secondPiece > barLengthMm - LengthToleranceMm)
        {
            error = $"ABUTMENT_BAR_PIECE_PIECE_EXCEEDS_POSITION: vị trí thanh {positionIndex} dài " +
                    $"{barLengthMm:0.#} mm không chứa nổi đoạn {Math.Max(firstPiece, secondPiece):0.#} mm.";
            return false;
        }

        var achievedLap = firstPiece + secondPiece - barLengthMm;
        if (achievedLap < plan.LapLengthMm - LengthToleranceMm)
        {
            error = $"ABUTMENT_BAR_PIECE_LAP_SHORT: vị trí thanh {positionIndex} dài {barLengthMm:0.###} mm " +
                    $"chỉ cho hai đoạn chồng nhau {achievedLap:0.###} mm, thiếu so với nối chồng yêu cầu " +
                    $"{plan.LapLengthMm:0.###} mm.";
            return false;
        }

        pieces =
        [
            new AbutmentBarPiece(positionIndex, 1, 2, 0, firstPiece, achievedLap, reversed),
            new AbutmentBarPiece(
                positionIndex, 2, 2, barLengthMm - secondPiece, barLengthMm, achievedLap, reversed)
        ];
        return true;
    }

    /// <summary>
    /// Grades what the model actually holds against the plan, on both levels at once: the layer has
    /// to carry every bar position the drawing states, and every position has to carry exactly the
    /// pieces it was planned from, overlapping by at least the lap. A position short of a piece, one
    /// piece too many, or two pieces that do not reach each other all fail here — the count on its
    /// own would pass a layer whose pieces were built in the wrong places.
    /// </summary>
    public static bool TryVerifyMeasuredPositions(
        int expectedPositionCount,
        int piecesPerPosition,
        double lapLengthMm,
        IReadOnlyList<AbutmentMeasuredBarPiece> measured,
        double toleranceMm,
        out string error)
    {
        error = string.Empty;
        if (measured is null)
        {
            error = "ABUTMENT_BAR_PIECE_READBACK_UNDECLARED: chưa đo được đoạn thép nào để đối chiếu.";
            return false;
        }
        if (expectedPositionCount <= 0 || piecesPerPosition <= 0)
        {
            error = $"ABUTMENT_BAR_PIECE_EXPECTATION_INVALID: plan khai {expectedPositionCount} vị trí " +
                    $"thanh và {piecesPerPosition} đoạn mỗi vị trí; cả hai phải dương.";
            return false;
        }
        if (!double.IsFinite(toleranceMm) || toleranceMm < 0)
        {
            error = $"ABUTMENT_BAR_PIECE_TOLERANCE_INVALID: dung sai {toleranceMm} mm không hợp lệ.";
            return false;
        }
        if (piecesPerPosition > 1 && (!double.IsFinite(lapLengthMm) || lapLengthMm <= 0))
        {
            error = $"ABUTMENT_BAR_PIECE_LAP_LENGTH_INVALID: vị trí thanh nhiều đoạn phải khai nối " +
                    $"chồng dương, plan cho {lapLengthMm} mm.";
            return false;
        }

        var byPosition = measured
            .GroupBy(piece => piece.PositionIndex)
            .OrderBy(group => group.Key)
            .ToList();
        if (byPosition.Count != expectedPositionCount)
        {
            error = $"ABUTMENT_BAR_PIECE_POSITION_COUNT: plan yêu cầu {expectedPositionCount} vị trí " +
                    $"thanh, đọc lại thấy {byPosition.Count}.";
            return false;
        }

        foreach (var group in byPosition)
        {
            var pieces = group.OrderBy(piece => piece.StartOffsetMm).ToList();
            if (pieces.Count != piecesPerPosition)
            {
                error = $"ABUTMENT_BAR_PIECE_COUNT: vị trí thanh {group.Key} phải có " +
                        $"{piecesPerPosition} đoạn, đọc lại thấy {pieces.Count}.";
                return false;
            }
            if (piecesPerPosition == 1) continue;

            for (var index = 1; index < pieces.Count; index++)
            {
                var overlap = pieces[index - 1].EndOffsetMm - pieces[index].StartOffsetMm;
                if (overlap < lapLengthMm - toleranceMm)
                {
                    error = $"ABUTMENT_BAR_PIECE_LAP_SHORT: vị trí thanh {group.Key} có hai đoạn chỉ " +
                            $"chồng nhau {overlap:0.###} mm, thiếu {lapLengthMm - overlap:0.###} mm " +
                            $"so với nối chồng yêu cầu {lapLengthMm:0.###} mm.";
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// Identity of one piece. A position built from one piece keeps the key it had before pieces
    /// existed, so every rule that never splices keeps its metadata, its duplicate check and its
    /// bar-mark binding byte for byte.
    /// </summary>
    public static string FormatPieceKey(string ruleId, int positionIndex, int pieceIndex, int pieceCount) =>
        pieceCount <= 1
            ? $"{ruleId}-{positionIndex:D3}"
            : $"{ruleId}-{positionIndex:D3}-P{pieceIndex:D1}";
}
