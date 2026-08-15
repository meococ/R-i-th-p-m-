namespace BIM.DatViet.Domain;

/// <summary>One cut length of one layer, as the yard would receive it.</summary>
public sealed record AbutmentReceiptCutLine
{
    public required double CutLengthMm { get; init; }
    public required int PieceCount { get; init; }
    public required double TotalLengthMm { get; init; }
    public required double MassKg { get; init; }
}

/// <summary>
/// One layer of the create receipt: the whole picture of one rule in a single row, so a reader sees
/// what was built before deciding whether to look at individual bars.
/// </summary>
public sealed record AbutmentReceiptLayerSummary
{
    public required string RuleId { get; init; }

    /// <summary>Internal mark, which is the grouping key; two mats of one drawing mark stay apart.</summary>
    public required string InternalMark { get; init; }

    public required string DrawingMark { get; init; }
    public required double DiameterMm { get; init; }
    public required string BarTypeInModel { get; init; }

    /// <summary>Bar positions built; the number the drawing counts.</summary>
    public required int BarPositionCount { get; init; }

    public required int PiecesPerBarPosition { get; init; }

    /// <summary>Rebar objects built; the number the model holds.</summary>
    public required int RebarObjectCount { get; init; }

    /// <summary>Finished bar length, lap overlap removed.</summary>
    public required double BarLengthMm { get; init; }

    public required int SpliceCountPerBar { get; init; }
    public required double LapLengthMm { get; init; }

    /// <summary>The layer's cut lengths in ascending order: one row unspliced, two rows lapped.</summary>
    public required IReadOnlyList<AbutmentReceiptCutLine> CutLengths { get; init; }

    /// <summary>Steel actually cut for this layer, laps included.</summary>
    public required double TotalLengthMm { get; init; }

    public required double UnitMassKgPerMetre { get; init; }
    public required double MassKg { get; init; }
}

/// <summary>The four figures an acceptance run checks against the quantity sheet.</summary>
public sealed record AbutmentReceiptTotals
{
    public required int LayerCount { get; init; }
    public required int BarPositionCount { get; init; }
    public required int RebarObjectCount { get; init; }
    public required double TotalLengthMm { get; init; }
    public required double MassKg { get; init; }
}

/// <summary>One created rebar object as the summary consumes it.</summary>
public sealed record AbutmentReceiptSummaryRow
{
    public required string RuleId { get; init; }
    public required string BarTypeInModel { get; init; }
    public required AbutmentScheduleBarRow Bar { get; init; }
}

/// <summary>
/// Rolls the bars of one create run up into one row per layer, as a pure kernel: no Revit API, no
/// I/O, millimeters and kilograms only.
///
/// It computes nothing of its own. The rows come from
/// <see cref="AbutmentRebarScheduleKernel.TryBuildFinishedBarSchedule"/> and
/// <see cref="AbutmentRebarScheduleKernel.TryBuildCuttingList"/> — the same two kernels that build
/// the bar bending schedule and the yard cutting list — so a receipt cannot state a quantity the
/// two tables would later contradict, and the reconciliation gate between them fires here first,
/// before the transaction opens, instead of at the end of an acceptance run.
/// </summary>
public static class AbutmentReceiptSummaryKernel
{
    public static bool TryBuild(
        IReadOnlyList<AbutmentReceiptSummaryRow> rows,
        out IReadOnlyList<AbutmentReceiptLayerSummary> layers,
        out AbutmentReceiptTotals totals,
        out string error)
    {
        layers = [];
        totals = EmptyTotals;
        error = string.Empty;
        if (rows is null || rows.Count == 0)
        {
            error = "ABUTMENT_RECEIPT_SUMMARY_ROWS_EMPTY: không có thanh thép nào để tổng hợp.";
            return false;
        }
        foreach (var row in rows)
        {
            if (row?.Bar is null)
            {
                error = "ABUTMENT_RECEIPT_SUMMARY_ROW_UNDECLARED: có dòng tổng hợp rỗng.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(row.RuleId))
            {
                error = $"ABUTMENT_RECEIPT_SUMMARY_RULE_ID_REQUIRED: thanh {row.Bar.ElementLabel} không " +
                        "có mã rule nên không truy được về dòng nào của preset.";
                return false;
            }
        }

        var bars = rows.Select(row => row.Bar).ToList();
        if (!AbutmentRebarScheduleKernel.TryBuildFinishedBarSchedule(bars, out var schedule, out error))
            return false;
        if (!AbutmentRebarScheduleKernel.TryBuildCuttingList(bars, out var cutting, out error))
            return false;

        var identityByMark = new Dictionary<string, (string RuleId, string BarTypeInModel)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var mark = row.Bar.InternalMark.Trim();
            var barTypeInModel = (row.BarTypeInModel ?? string.Empty).Trim();
            if (!identityByMark.TryGetValue(mark, out var known))
            {
                identityByMark[mark] = (row.RuleId.Trim(), barTypeInModel);
                continue;
            }
            if (!known.RuleId.Equals(row.RuleId.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                error = $"ABUTMENT_RECEIPT_SUMMARY_RULE_AMBIGUOUS: số hiệu '{mark}' do hai rule cùng " +
                        $"sinh ('{known.RuleId}' và '{row.RuleId}'); một dòng tổng hợp phải truy về " +
                        "đúng một rule.";
                return false;
            }
            if (!known.BarTypeInModel.Equals(barTypeInModel, StringComparison.OrdinalIgnoreCase))
            {
                error = $"ABUTMENT_RECEIPT_SUMMARY_BAR_TYPE_AMBIGUOUS: số hiệu '{mark}' dùng hai loại " +
                        $"thanh ('{known.BarTypeInModel}' và '{barTypeInModel}').";
                return false;
            }
        }

        var built = new List<AbutmentReceiptLayerSummary>();
        foreach (var line in schedule)
        {
            var identity = identityByMark[line.InternalMark];
            var cuts = cutting
                .Where(cut => cut.InternalMark.Equals(line.InternalMark, StringComparison.OrdinalIgnoreCase))
                .Select(cut => new AbutmentReceiptCutLine
                {
                    CutLengthMm = cut.CutLengthMm,
                    PieceCount = cut.PieceCount,
                    TotalLengthMm = cut.TotalLengthMm,
                    MassKg = cut.MassKg
                })
                .ToList();
            if (cuts.Count == 0)
            {
                error = $"ABUTMENT_RECEIPT_SUMMARY_CUT_LENGTHS_MISSING: số hiệu '{line.InternalMark}' có " +
                        "dòng thanh hoàn thiện nhưng không có dòng cắt phôi nào.";
                return false;
            }

            var piecesPerPosition = line.SpliceCountPerBar + 1;
            var objectCount = cuts.Sum(cut => cut.PieceCount);
            if (objectCount != line.Quantity * piecesPerPosition)
            {
                error = $"ABUTMENT_RECEIPT_SUMMARY_COUNT_NOT_RECONCILED: số hiệu '{line.InternalMark}' có " +
                        $"{line.Quantity} thanh × {piecesPerPosition} đoạn nhưng bảng cắt phôi đếm " +
                        $"{objectCount} đoạn.";
                return false;
            }

            built.Add(new AbutmentReceiptLayerSummary
            {
                RuleId = identity.RuleId,
                InternalMark = line.InternalMark,
                DrawingMark = line.DrawingMark,
                DiameterMm = line.BarDiameterMm,
                BarTypeInModel = identity.BarTypeInModel,
                BarPositionCount = line.Quantity,
                PiecesPerBarPosition = piecesPerPosition,
                RebarObjectCount = objectCount,
                BarLengthMm = line.BarLengthMm,
                SpliceCountPerBar = line.SpliceCountPerBar,
                LapLengthMm = line.LapLengthMm,
                CutLengths = cuts,
                TotalLengthMm = line.TotalLengthMm,
                UnitMassKgPerMetre = line.UnitMassKgPerMetre,
                MassKg = line.MassKg
            });
        }

        layers = built;
        totals = new AbutmentReceiptTotals
        {
            LayerCount = built.Count,
            BarPositionCount = built.Sum(layer => layer.BarPositionCount),
            RebarObjectCount = built.Sum(layer => layer.RebarObjectCount),
            TotalLengthMm = built.Sum(layer => layer.TotalLengthMm),
            MassKg = built.Sum(layer => layer.MassKg)
        };
        return true;
    }

    private static readonly AbutmentReceiptTotals EmptyTotals = new()
    {
        LayerCount = 0,
        BarPositionCount = 0,
        RebarObjectCount = 0,
        TotalLengthMm = 0,
        MassKg = 0
    };
}
