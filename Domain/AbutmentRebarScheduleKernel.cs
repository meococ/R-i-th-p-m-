using System.Globalization;
using System.Text;

namespace BIM.DatViet.Domain;

/// <summary>
/// One rebar object exactly as it was read back out of the model. Every value is a parameter on the
/// element, never a value re-read from the preset: a schedule that consults the configuration is a
/// schedule of what somebody intended, and the whole point of the table is what the model holds.
/// </summary>
public sealed record AbutmentScheduleBarRow
{
    /// <summary>How this object is named in an error message; normally its element id.</summary>
    public required string ElementLabel { get; init; }

    /// <summary>
    /// Grouping key. This is the internal mark, not the drawing mark: the top and bottom mat of one
    /// drawing mark are two different mats of two different elevations, and grouping them by the
    /// drawing mark collapses them into a single row of twice the bars.
    /// </summary>
    public required string InternalMark { get; init; }

    /// <summary>Mark printed on the drawing, carried through so the row traces back to the sheet.</summary>
    public required string DrawingMark { get; init; }

    public required double BarDiameterMm { get; init; }

    public required double BendRadiusMm { get; init; }

    /// <summary>Bar position this object belongs to; the drawing counts positions, not objects.</summary>
    public required int BarPositionIndex { get; init; }

    public required int PieceIndex { get; init; }

    /// <summary>Splices on the whole bar position, so the piece count is this plus one.</summary>
    public required int SpliceCountPerPosition { get; init; }

    /// <summary>Length this object was cut to.</summary>
    public required double CutLengthMm { get; init; }

    /// <summary>Finished length of the whole bar position, lap overlap removed.</summary>
    public required double BarLengthMm { get; init; }

    public required double LapLengthMm { get; init; }

    public required double UnitMassKgPerMetre { get; init; }
}

/// <summary>One row of the bar bending schedule: one finished bar mark.</summary>
public sealed record AbutmentScheduleLine
{
    public required string InternalMark { get; init; }
    public required string DrawingMark { get; init; }
    public required double BarDiameterMm { get; init; }
    public required double BendRadiusMm { get; init; }

    /// <summary>Finished bars, which is what the drawing dimensions — 41, never the 82 pieces.</summary>
    public required int Quantity { get; init; }

    public required double BarLengthMm { get; init; }
    public required int SpliceCountPerBar { get; init; }
    public required double LapLengthMm { get; init; }

    /// <summary>
    /// Steel actually cut for this mark: the finished length plus the overlap each splice consumes,
    /// times the number of bars. This is what the mass column has to be weighed from, and it
    /// reconciles exactly with the sum of the cutting list for the same mark.
    /// </summary>
    public required double TotalLengthMm { get; init; }

    public required double UnitMassKgPerMetre { get; init; }
    public required double MassKg { get; init; }
}

/// <summary>One row of the yard cutting list: one cut length of one mark.</summary>
public sealed record AbutmentCuttingListLine
{
    public required string InternalMark { get; init; }
    public required string DrawingMark { get; init; }
    public required double BarDiameterMm { get; init; }
    public required double CutLengthMm { get; init; }

    /// <summary>Pieces to cut at this length.</summary>
    public required int PieceCount { get; init; }

    public required double TotalLengthMm { get; init; }
    public required double UnitMassKgPerMetre { get; init; }
    public required double MassKg { get; init; }
}

/// <summary>
/// Turns the rebar objects of one abutment into the two tables a Vietnamese reinforcement package
/// needs, as a pure kernel: no Revit API, no I/O, millimeters and kilograms only.
///
/// The two tables count different things on purpose. The bar bending schedule lists <b>finished
/// bars</b> with a splice column, because that is the quantity the drawing dimensions and the
/// quantity surveyor checks — a spliced longitudinal layer is 41 bars of 17250 mm carrying one
/// 1550 mm lap, not 82 bars. The cutting list lists <b>cut pieces</b>, because the yard cuts
/// pieces. Both are built from the same rows, so their steel totals reconcile to the millimetre.
/// </summary>
public static class AbutmentRebarScheduleKernel
{
    /// <summary>
    /// Millimetres two values of one group may differ by and still count as the same value. Wide
    /// enough to absorb a round trip through Revit's internal feet, far below the cutting module
    /// any two genuinely different cut lengths are separated by.
    /// </summary>
    public const double ValueToleranceMm = 1e-3;

    /// <summary>
    /// Groups the objects into finished bars. Fails closed on anything that would make a column
    /// wrong rather than emitting a table with a plausible number in it.
    /// </summary>
    public static bool TryBuildFinishedBarSchedule(
        IReadOnlyList<AbutmentScheduleBarRow> rows,
        out IReadOnlyList<AbutmentScheduleLine> lines,
        out string error)
    {
        lines = [];
        if (!TryValidateRows(rows, out error)) return false;

        var built = new List<AbutmentScheduleLine>();
        foreach (var group in rows
                     .GroupBy(row => row.InternalMark.Trim(), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var sample = group.First();
            if (!TryCheckGroupIsOneMark(group.Key, group.ToList(), sample, out error)) return false;

            var positions = group
                .GroupBy(row => row.BarPositionIndex)
                .OrderBy(position => position.Key)
                .ToList();
            var piecesPerPosition = sample.SpliceCountPerPosition + 1;
            foreach (var position in positions)
            {
                var pieceIndexes = position.Select(row => row.PieceIndex).OrderBy(index => index).ToList();
                if (pieceIndexes.Count != piecesPerPosition ||
                    pieceIndexes.Distinct().Count() != piecesPerPosition ||
                    pieceIndexes[0] != 1 || pieceIndexes[^1] != piecesPerPosition)
                {
                    error = $"ABUTMENT_SCHEDULE_PIECE_SET_INCOMPLETE: số hiệu '{group.Key}' vị trí thanh " +
                            $"{position.Key} có các đoạn [{string.Join(", ", pieceIndexes)}] trong khi " +
                            $"một thanh phải đủ {piecesPerPosition} đoạn đánh số 1..{piecesPerPosition}; " +
                            "bảng sẽ đếm sai số thanh hoặc sai tổng chiều dài.";
                    return false;
                }
            }

            var quantity = positions.Count;
            var totalLengthMm = group.Sum(row => row.CutLengthMm);
            var expectedTotalMm =
                quantity * (sample.BarLengthMm + sample.SpliceCountPerPosition * sample.LapLengthMm);
            if (Math.Abs(totalLengthMm - expectedTotalMm) > ValueToleranceMm * Math.Max(1, quantity))
            {
                error = $"ABUTMENT_SCHEDULE_LENGTH_NOT_RECONCILED: số hiệu '{group.Key}' có tổng chiều " +
                        $"dài đoạn cắt {totalLengthMm:0.###} mm, còn {quantity} thanh × " +
                        $"({sample.BarLengthMm:0.###} + {sample.SpliceCountPerPosition} × " +
                        $"{sample.LapLengthMm:0.###}) mm cho {expectedTotalMm:0.###} mm; hai con số phải " +
                        "trùng thì bảng thống kê và bảng cắt phôi mới nói cùng một khối lượng.";
                return false;
            }

            if (!AbutmentBarMassKernel.TryMassKg(
                    sample.UnitMassKgPerMetre, totalLengthMm, out var massKg, out error))
            {
                error = $"ABUTMENT_SCHEDULE_MASS_UNRESOLVED: số hiệu '{group.Key}' — {error}";
                return false;
            }

            built.Add(new AbutmentScheduleLine
            {
                InternalMark = group.Key,
                DrawingMark = sample.DrawingMark.Trim(),
                BarDiameterMm = sample.BarDiameterMm,
                BendRadiusMm = sample.BendRadiusMm,
                Quantity = quantity,
                BarLengthMm = sample.BarLengthMm,
                SpliceCountPerBar = sample.SpliceCountPerPosition,
                LapLengthMm = sample.LapLengthMm,
                TotalLengthMm = totalLengthMm,
                UnitMassKgPerMetre = sample.UnitMassKgPerMetre,
                MassKg = massKg
            });
        }

        lines = built;
        return true;
    }

    /// <summary>
    /// Groups the same objects by what the yard actually cuts. A spliced layer therefore appears as
    /// two rows of one mark — its two cut lengths — while the bar bending schedule shows it as one.
    /// </summary>
    public static bool TryBuildCuttingList(
        IReadOnlyList<AbutmentScheduleBarRow> rows,
        out IReadOnlyList<AbutmentCuttingListLine> lines,
        out string error)
    {
        lines = [];
        if (!TryValidateRows(rows, out error)) return false;

        var built = new List<AbutmentCuttingListLine>();
        foreach (var group in rows
                     .GroupBy(row => row.InternalMark.Trim(), StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var sample = group.First();
            if (!TryCheckGroupIsOneMark(group.Key, group.ToList(), sample, out error)) return false;

            foreach (var cut in group
                         .GroupBy(row => Math.Round(row.CutLengthMm / ValueToleranceMm))
                         .OrderBy(cut => cut.First().CutLengthMm))
            {
                var cutLengthMm = cut.First().CutLengthMm;
                var totalLengthMm = cut.Sum(row => row.CutLengthMm);
                if (!AbutmentBarMassKernel.TryMassKg(
                        sample.UnitMassKgPerMetre, totalLengthMm, out var massKg, out error))
                {
                    error = $"ABUTMENT_CUTTING_LIST_MASS_UNRESOLVED: số hiệu '{group.Key}' đoạn " +
                            $"{cutLengthMm:0.###} mm — {error}";
                    return false;
                }

                built.Add(new AbutmentCuttingListLine
                {
                    InternalMark = group.Key,
                    DrawingMark = sample.DrawingMark.Trim(),
                    BarDiameterMm = sample.BarDiameterMm,
                    CutLengthMm = cutLengthMm,
                    PieceCount = cut.Count(),
                    TotalLengthMm = totalLengthMm,
                    UnitMassKgPerMetre = sample.UnitMassKgPerMetre,
                    MassKg = massKg
                });
            }
        }

        lines = built;
        return true;
    }

    /// <summary>
    /// Renders the cutting list as a tab-separated table. Tabs rather than commas because the list
    /// is opened in Excel on machines whose list separator is a semicolon, where a comma file
    /// arrives in one column; a tab is a column break in every locale.
    /// </summary>
    public static string FormatCuttingListTable(
        IReadOnlyList<AbutmentCuttingListLine> lines,
        IReadOnlyList<string> headerNotes,
        IFormatProvider? formatProvider = null,
        string columnSeparator = "\t")
    {
        var culture = formatProvider ?? CultureInfo.InvariantCulture;
        var rows = lines ?? [];
        var text = new StringBuilder();
        foreach (var note in headerNotes ?? [])
            text.Append("# ").AppendLine(note);
        text.AppendLine(string.Join(columnSeparator,
            "So hieu", "Mark ban ve", "Duong kinh (mm)", "Chieu dai cat (mm)", "So doan",
            "Tong chieu dai (mm)", "Khoi luong don vi (kg/m)", "Khoi luong (kg)"));
        foreach (var line in rows)
            text.AppendLine(string.Join(columnSeparator,
                line.InternalMark,
                line.DrawingMark,
                line.BarDiameterMm.ToString("0.###", culture),
                line.CutLengthMm.ToString("0.###", culture),
                line.PieceCount.ToString(culture),
                line.TotalLengthMm.ToString("0.###", culture),
                line.UnitMassKgPerMetre.ToString("0.###", culture),
                line.MassKg.ToString("0.###", culture)));
        text.AppendLine(string.Join(columnSeparator,
            "TONG", string.Empty, string.Empty, string.Empty,
            rows.Sum(line => line.PieceCount).ToString(culture),
            rows.Sum(line => line.TotalLengthMm).ToString("0.###", culture),
            string.Empty,
            rows.Sum(line => line.MassKg).ToString("0.###", culture)));
        return text.ToString();
    }

    private static bool TryValidateRows(IReadOnlyList<AbutmentScheduleBarRow> rows, out string error)
    {
        error = string.Empty;
        if (rows is null || rows.Count == 0)
        {
            error = "ABUTMENT_SCHEDULE_ROWS_EMPTY: không có thanh thép nào do add-in quản lý để lập bảng.";
            return false;
        }

        foreach (var row in rows)
        {
            var label = string.IsNullOrWhiteSpace(row.ElementLabel) ? "<không rõ>" : row.ElementLabel;
            if (string.IsNullOrWhiteSpace(row.InternalMark))
            {
                error = $"ABUTMENT_SCHEDULE_INTERNAL_MARK_REQUIRED: thanh {label} không có số hiệu nội " +
                        "bộ nên không nhóm được; hai thảm cùng mark bản vẽ sẽ gộp thành một dòng.";
                return false;
            }
            if (!IsPositiveFinite(row.BarDiameterMm) || !IsPositiveFinite(row.CutLengthMm) ||
                !IsPositiveFinite(row.BarLengthMm) || !IsPositiveFinite(row.UnitMassKgPerMetre))
            {
                error = $"ABUTMENT_SCHEDULE_ROW_INVALID: thanh {label} (số hiệu '{row.InternalMark}') có " +
                        $"đường kính {row.BarDiameterMm} mm, chiều dài cắt {row.CutLengthMm} mm, chiều " +
                        $"dài thanh {row.BarLengthMm} mm, khối lượng đơn vị {row.UnitMassKgPerMetre} kg/m; " +
                        "cả bốn phải dương hữu hạn.";
                return false;
            }
            if (!double.IsFinite(row.BendRadiusMm) || row.BendRadiusMm < 0 ||
                !double.IsFinite(row.LapLengthMm) || row.LapLengthMm < 0)
            {
                error = $"ABUTMENT_SCHEDULE_ROW_INVALID: thanh {label} (số hiệu '{row.InternalMark}') có " +
                        $"bán kính uốn {row.BendRadiusMm} mm hoặc chiều dài mối nối {row.LapLengthMm} mm âm.";
                return false;
            }
            if (row.BarPositionIndex <= 0 || row.PieceIndex <= 0 || row.SpliceCountPerPosition < 0 ||
                row.PieceIndex > row.SpliceCountPerPosition + 1)
            {
                error = $"ABUTMENT_SCHEDULE_ROW_INVALID: thanh {label} (số hiệu '{row.InternalMark}') khai " +
                        $"vị trí thanh {row.BarPositionIndex}, đoạn {row.PieceIndex}, " +
                        $"{row.SpliceCountPerPosition} mối nối; số thứ tự đoạn phải nằm trong " +
                        "1..(số mối nối + 1).";
                return false;
            }
            if (row.SpliceCountPerPosition > 0 && row.LapLengthMm <= 0)
            {
                error = $"ABUTMENT_SCHEDULE_LAP_REQUIRED: thanh {label} (số hiệu '{row.InternalMark}') có " +
                        $"{row.SpliceCountPerPosition} mối nối nhưng chiều dài mối nối là " +
                        $"{row.LapLengthMm} mm.";
                return false;
            }
        }
        return true;
    }

    private static bool TryCheckGroupIsOneMark(
        string internalMark,
        IReadOnlyList<AbutmentScheduleBarRow> group,
        AbutmentScheduleBarRow sample,
        out string error)
    {
        error = string.Empty;
        foreach (var row in group)
        {
            if (!row.DrawingMark.Trim().Equals(sample.DrawingMark.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                error = $"ABUTMENT_SCHEDULE_GROUP_INCONSISTENT: số hiệu '{internalMark}' mang hai mark " +
                        $"bản vẽ khác nhau ('{sample.DrawingMark}' và '{row.DrawingMark}').";
                return false;
            }
            if (row.SpliceCountPerPosition != sample.SpliceCountPerPosition)
            {
                error = $"ABUTMENT_SCHEDULE_GROUP_INCONSISTENT: số hiệu '{internalMark}' có thanh khai " +
                        $"{sample.SpliceCountPerPosition} mối nối và thanh khác khai " +
                        $"{row.SpliceCountPerPosition}.";
                return false;
            }
            if (!IsSame(row.BarDiameterMm, sample.BarDiameterMm) ||
                !IsSame(row.BendRadiusMm, sample.BendRadiusMm) ||
                !IsSame(row.BarLengthMm, sample.BarLengthMm) ||
                !IsSame(row.LapLengthMm, sample.LapLengthMm) ||
                !IsSame(row.UnitMassKgPerMetre, sample.UnitMassKgPerMetre))
            {
                error = $"ABUTMENT_SCHEDULE_GROUP_INCONSISTENT: số hiệu '{internalMark}' không đồng nhất " +
                        $"— thanh {row.ElementLabel} khai Ø{row.BarDiameterMm}, bán kính uốn " +
                        $"{row.BendRadiusMm}, chiều dài thanh {row.BarLengthMm}, mối nối " +
                        $"{row.LapLengthMm}, khối lượng đơn vị {row.UnitMassKgPerMetre}; thanh " +
                        $"{sample.ElementLabel} khai Ø{sample.BarDiameterMm}, {sample.BendRadiusMm}, " +
                        $"{sample.BarLengthMm}, {sample.LapLengthMm}, {sample.UnitMassKgPerMetre}. " +
                        "Một số hiệu phải là một loại thanh.";
                return false;
            }
        }
        return true;
    }

    private static bool IsSame(double left, double right) => Math.Abs(left - right) <= ValueToleranceMm;

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;
}
