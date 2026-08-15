namespace BIM.DatViet.Domain;

/// <summary>
/// Everything one rebar object knows about the bar position it is a piece of: which position, which
/// piece, what it was cut to, what the finished bar measures, and which lap it takes part in.
///
/// This is the block written into the hidden <c>Piece</c> evidence of the bar <b>and</b> the block
/// written into the create receipt. One record, resolved once per bar and handed to both sinks, so
/// the two can never state different lengths for the same object — a receipt that disagrees with
/// the model it describes is worse than a receipt that stays silent.
///
/// Property order is the order the <c>Piece</c> block has been written in since it was introduced;
/// keep it, because existing evidence is read positionally by eye.
/// </summary>
public sealed record AbutmentBarPieceFacts
{
    /// <summary>1-based bar position of the drawing this object belongs to.</summary>
    public required int PositionIndex { get; init; }

    /// <summary>1-based piece inside the position; always 1 when the position is one bar.</summary>
    public required int PieceIndex { get; init; }

    /// <summary>Pieces the whole position is built from.</summary>
    public required int PieceCount { get; init; }

    /// <summary>Length this object was cut to.</summary>
    public required double PieceLengthMm { get; init; }

    /// <summary>Finished length of the whole position, lap overlap removed.</summary>
    public required double PositionLengthMm { get; init; }

    /// <summary>Lap this piece's splice achieves; 0 when the position carries none.</summary>
    public required double LapLengthMm { get; init; }

    /// <summary>True when the position is laid reversed end for end so its splice falls in the second band.</summary>
    public required bool ReversedLayout { get; init; }

    public required int SpliceCountPerPosition { get; init; }

    /// <summary>Bar positions the rule states; the number the drawing counts.</summary>
    public required int BarPositionCount { get; init; }

    /// <summary>Rebar objects the rule produces; the number the model holds.</summary>
    public required int RebarObjectCount { get; init; }

    /// <summary>Shorter of the layer's two cut lengths, repeated on every piece of the layer.</summary>
    public required double CutPairShortMm { get; init; }

    /// <summary>Longer of the layer's two cut lengths; equal to the short one on an unspliced layer.</summary>
    public required double CutPairLongMm { get; init; }

    public required double DiameterMm { get; init; }

    /// <summary>Bar type the rule asks for.</summary>
    public required string BarTypeName { get; init; }

    /// <summary>Bar type actually resolved in the model, which is what was cut.</summary>
    public required string BarTypeInModel { get; init; }
}

/// <summary>The planned figures one piece's facts are resolved from.</summary>
public sealed record AbutmentBarPieceFactsRequest
{
    public int PositionIndex { get; init; }
    public int PieceIndex { get; init; }
    public int PieceCount { get; init; }
    public double PieceLengthMm { get; init; }
    public double PositionLengthMm { get; init; }
    public double LapLengthMm { get; init; }
    public bool ReversedLayout { get; init; }
    public int BarPositionCount { get; init; }
    public int RebarObjectCount { get; init; }
    public double DiameterMm { get; init; }
    public string BarTypeName { get; init; } = string.Empty;
    public string BarTypeInModel { get; init; } = string.Empty;
}

/// <summary>
/// Resolves the piece block, as a pure kernel: no Revit API, no I/O, millimeters only.
///
/// The only arithmetic here is the cut pair. A spliced position is two pieces whose lengths add up
/// to the finished bar plus one lap, so knowing this piece is enough to state the other one — which
/// is what lets a single rebar object answer what the yard has to cut for the whole layer.
/// </summary>
public static class AbutmentBarPieceFactsKernel
{
    public static bool TryResolve(
        AbutmentBarPieceFactsRequest request,
        out AbutmentBarPieceFacts facts,
        out string error)
    {
        facts = null!;
        error = string.Empty;
        if (request is null)
        {
            error = "ABUTMENT_BAR_PIECE_FACTS_REQUEST_UNDECLARED: không có dữ liệu đoạn thép nào để " +
                    "lập khối số đo.";
            return false;
        }
        if (request.PositionIndex <= 0 || request.PieceCount <= 0 ||
            request.PieceIndex <= 0 || request.PieceIndex > request.PieceCount)
        {
            error = $"ABUTMENT_BAR_PIECE_FACTS_INDEX_INVALID: đoạn khai vị trí thanh " +
                    $"{request.PositionIndex}, đoạn {request.PieceIndex}/{request.PieceCount}; số thứ " +
                    "tự đoạn phải nằm trong 1..số đoạn và vị trí thanh phải dương.";
            return false;
        }
        if (!IsPositiveFinite(request.PieceLengthMm) || !IsPositiveFinite(request.PositionLengthMm))
        {
            error = $"ABUTMENT_BAR_PIECE_FACTS_LENGTH_INVALID: đoạn dài {request.PieceLengthMm} mm trên " +
                    $"thanh dài {request.PositionLengthMm} mm; cả hai phải dương hữu hạn thì biên lai " +
                    "mới ghi được số đo.";
            return false;
        }
        if (!double.IsFinite(request.LapLengthMm) || request.LapLengthMm < 0 ||
            (request.PieceCount > 1 && request.LapLengthMm <= 0))
        {
            error = $"ABUTMENT_BAR_PIECE_FACTS_LAP_LENGTH_INVALID: thanh {request.PieceCount} đoạn khai " +
                    $"chiều dài nối chồng {request.LapLengthMm} mm.";
            return false;
        }
        if (request.BarPositionCount <= 0 || request.RebarObjectCount <= 0 ||
            !IsPositiveFinite(request.DiameterMm))
        {
            error = $"ABUTMENT_BAR_PIECE_FACTS_COUNT_INVALID: lớp khai {request.BarPositionCount} vị trí " +
                    $"thanh, {request.RebarObjectCount} đối tượng, đường kính {request.DiameterMm} mm; " +
                    "cả ba phải dương.";
            return false;
        }

        // The layer is cut as one pair, so the piece that is not this one follows from the finished
        // bar and the lap it overlaps by. An unspliced position is its own pair.
        var otherPieceMm = request.PieceCount > 1
            ? request.PositionLengthMm + request.LapLengthMm - request.PieceLengthMm
            : 0;
        if (request.PieceCount > 1 && !IsPositiveFinite(otherPieceMm))
        {
            error = $"ABUTMENT_BAR_PIECE_FACTS_CUT_PAIR_UNRESOLVED: đoạn dài {request.PieceLengthMm} mm " +
                    $"trên thanh {request.PositionLengthMm} mm nối chồng {request.LapLengthMm} mm cho " +
                    $"đoạn còn lại {otherPieceMm} mm; cặp chiều dài cắt không dựng được.";
            return false;
        }

        facts = new AbutmentBarPieceFacts
        {
            PositionIndex = request.PositionIndex,
            PieceIndex = request.PieceIndex,
            PieceCount = request.PieceCount,
            PieceLengthMm = request.PieceLengthMm,
            PositionLengthMm = request.PositionLengthMm,
            LapLengthMm = request.LapLengthMm,
            ReversedLayout = request.ReversedLayout,
            SpliceCountPerPosition = request.PieceCount - 1,
            BarPositionCount = request.BarPositionCount,
            RebarObjectCount = request.RebarObjectCount,
            CutPairShortMm = request.PieceCount > 1
                ? Math.Min(request.PieceLengthMm, otherPieceMm)
                : request.PieceLengthMm,
            CutPairLongMm = request.PieceCount > 1
                ? Math.Max(request.PieceLengthMm, otherPieceMm)
                : request.PieceLengthMm,
            DiameterMm = request.DiameterMm,
            BarTypeName = request.BarTypeName ?? string.Empty,
            BarTypeInModel = request.BarTypeInModel ?? string.Empty
        };
        return true;
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;
}
