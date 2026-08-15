using System.Globalization;
using System.IO;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using BIM.DatViet.Domain;

namespace BIM.DatViet.RevitServices;

/// <summary>Everything one reinforcement package contributes to a schedule.</summary>
public sealed record AbutmentScheduleSource(
    string AssemblyId,
    string HostName,
    IReadOnlyList<AbutmentScheduleBarRow> Rows);

/// <summary>
/// Builds the two reinforcement tables out of the bars that are actually in the model.
///
/// Everything here reads Rebar elements. Nothing re-reads the preset: a table rebuilt from the
/// configuration states what somebody intended to build, and the point of the table is what got
/// built. A bar that does not carry the scheduling parameters is not guessed at and not skipped —
/// it stops the whole table, because a schedule quietly missing a mark is indistinguishable from a
/// correct one.
/// </summary>
public static class AbutmentScheduleService
{
    /// <summary>Element ids named in a refusal before the list is cut short.</summary>
    private const int MaximumNamedElements = 10;

    /// <summary>
    /// Reads every managed bar of one abutment host into schedule rows, or refuses and says which
    /// bars are not schedulable and what to do about them.
    /// </summary>
    public static bool TryReadManagedBars(
        Document document,
        Element host,
        out AbutmentScheduleSource source,
        out string error)
    {
        source = new AbutmentScheduleSource(string.Empty, host?.Name ?? string.Empty, []);
        error = string.Empty;
        if (document is null || host is null)
        {
            error = "ABUTMENT_SCHEDULE_HOST_UNDECLARED: chưa chọn được mố để lập bảng.";
            return false;
        }

        var managed = AbutmentMetadataStore.FindOwned(document, host.UniqueId);
        if (managed.Count == 0)
        {
            error = $"ABUTMENT_SCHEDULE_NO_MANAGED_REBAR: mố '{host.Name}' chưa có thanh thép nào do " +
                    "add-in quản lý; hãy chạy Rải Thép Mố Cầu và Tạo trước.";
            return false;
        }

        var assemblyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stale = new List<string>();
        var rows = new List<AbutmentScheduleBarRow>(managed.Count);
        foreach (var rebar in managed)
        {
            var label = rebar.Id.ToString();
            var missing = AbutmentMetadataStore.MissingScheduleParameters(rebar);
            if (missing.Count > 0)
            {
                stale.Add($"{label} ({string.Join(", ", missing)})");
                continue;
            }
            if (!TryReadRow(document, rebar, out var row, out var rowError))
            {
                error = rowError;
                return false;
            }
            rows.Add(row);
            if (AbutmentMetadataStore.TryRead(rebar, out var metadata) &&
                !string.IsNullOrWhiteSpace(metadata.AssemblyId))
                assemblyIds.Add(metadata.AssemblyId);
        }

        if (stale.Count > 0)
        {
            var named = string.Join("; ", stale.Take(MaximumNamedElements));
            var rest = stale.Count > MaximumNamedElements
                ? $" và {stale.Count - MaximumNamedElements} thanh nữa"
                : string.Empty;
            error = $"ABUTMENT_SCHEDULE_LEGACY_REBAR: {stale.Count}/{managed.Count} thanh thép của mố " +
                    $"'{host.Name}' được tạo bởi bản add-in trước khi có tham số thống kê, nên bảng sẽ " +
                    $"thiếu hoặc sai khối lượng. Chạy lại Tạo lại vùng cho các khu vực đó rồi lập bảng. " +
                    $"Thanh thiếu: {named}{rest}.";
            return false;
        }
        if (assemblyIds.Count != 1)
        {
            error = $"ABUTMENT_SCHEDULE_ASSEMBLY_AMBIGUOUS: thép của mố '{host.Name}' mang " +
                    $"{assemblyIds.Count} mã bộ thép ({string.Join(", ", assemblyIds)}); bảng phải lọc " +
                    "theo đúng một mã.";
            return false;
        }

        source = new AbutmentScheduleSource(assemblyIds.First(), host.Name, rows);
        return true;
    }

    private static bool TryReadRow(
        Document document,
        Rebar rebar,
        out AbutmentScheduleBarRow row,
        out string error)
    {
        row = null!;
        error = string.Empty;
        var label = rebar.Id.ToString();
        var internalMark = rebar.LookupParameter("DVB_CanonicalMark")?.AsString() ?? string.Empty;
        var drawingMark = rebar.get_Parameter(BuiltInParameter.REBAR_ELEM_SCHEDULE_MARK)?.AsString()
                          ?? string.Empty;
        if (document.GetElement(rebar.GetTypeId()) is not RebarBarType barType)
        {
            error = $"ABUTMENT_SCHEDULE_BAR_TYPE_UNRESOLVED: thanh {label} không đọc được RebarBarType " +
                    "nên không lấy được đường kính cho bảng.";
            return false;
        }

        row = new AbutmentScheduleBarRow
        {
            ElementLabel = label,
            InternalMark = internalMark,
            DrawingMark = drawingMark,
            BarDiameterMm = UnitUtils.ConvertFromInternalUnits(
                barType.BarModelDiameter, UnitTypeId.Millimeters),
            BendRadiusMm = LengthMm(rebar, "DVB_BendRadius"),
            BarPositionIndex = Integer(rebar, "DVB_BarPosition"),
            PieceIndex = Integer(rebar, "DVB_PieceIndex"),
            SpliceCountPerPosition = Integer(rebar, "DVB_SpliceCount"),
            CutLengthMm = LengthMm(rebar, "DVB_CutLength"),
            BarLengthMm = LengthMm(rebar, "DVB_BarLength"),
            LapLengthMm = LengthMm(rebar, "DVB_LapLength"),
            UnitMassKgPerMetre = Number(rebar, "DVB_UnitMass")
        };
        return true;
    }

    /// <summary>
    /// Creates the bar bending schedule. It groups on the internal mark rather than the drawing
    /// mark, which is the whole difficulty here: the top and bottom transverse mats are both drawn
    /// as F3, and a schedule grouped on the drawing mark would report 210 bars of one mat instead
    /// of two mats of 105 at two different elevations.
    ///
    /// It is filtered to the first piece of each bar position, so one row is one finished bar. The
    /// length and mass columns are the per-position ones, which every piece of a position carries
    /// the same value of, so the totals stay right under that filter.
    /// </summary>
    public static ViewSchedule CreateFinishedBarSchedule(
        Document document,
        AbutmentScheduleSource source,
        out IReadOnlyList<string> notes)
    {
        var warnings = new List<string>();
        notes = warnings;
        var category = Category.GetCategory(document, BuiltInCategory.OST_Rebar)
                       ?? throw new InvalidOperationException(
                           "Document không có category Rebar để tạo bảng thống kê.");
        var schedule = ViewSchedule.CreateSchedule(document, category.Id);
        schedule.Name = UniqueScheduleName(document, $"DVB - Thống kê thép mố - {source.AssemblyId}");

        var definition = schedule.Definition;
        var schedulable = definition.GetSchedulableFields();

        ScheduleField? Shared(string parameterName, string heading)
        {
            var match = schedulable.FirstOrDefault(field =>
                string.Equals(field.GetName(document), parameterName, StringComparison.Ordinal));
            if (match is null)
            {
                warnings.Add($"Revit không cho đưa tham số '{parameterName}' vào bảng.");
                return null;
            }
            var field = definition.AddField(match);
            field.ColumnHeading = heading;
            return field;
        }

        ScheduleField? BuiltIn(BuiltInParameter parameter, string heading)
        {
            var parameterId = new ElementId(parameter);
            var match = schedulable.FirstOrDefault(field => field.ParameterId == parameterId);
            if (match is null)
            {
                warnings.Add($"Revit không cho đưa cột '{heading}' vào bảng ở phiên bản này.");
                return null;
            }
            var field = definition.AddField(match);
            field.ColumnHeading = heading;
            return field;
        }

        var markField = Shared("DVB_CanonicalMark", "Số hiệu")
                        ?? throw new InvalidOperationException(
                            "Không đưa được DVB_CanonicalMark vào bảng nên không nhóm theo mark nội bộ " +
                            "được; hai thảm F3 sẽ gộp thành một dòng.");
        BuiltIn(BuiltInParameter.REBAR_ELEM_SCHEDULE_MARK, "Mark bản vẽ");
        BuiltIn(BuiltInParameter.REBAR_BAR_DIAMETER, "Đường kính");
        Shared("DVB_BendRadius", "Bán kính uốn");
        var countField = schedulable.FirstOrDefault(field => field.FieldType == ScheduleFieldType.Count);
        if (countField is not null)
        {
            var field = definition.AddField(countField);
            field.ColumnHeading = "Số lượng";
        }
        else
        {
            warnings.Add("Revit không cấp trường Count nên cột Số lượng bị thiếu.");
        }
        Shared("DVB_BarLength", "Chiều dài 1 thanh");
        Shared("DVB_SpliceCount", "Số mối nối");
        Shared("DVB_LapLength", "Chiều dài mối nối");
        Total(Shared("DVB_BarTotalLength", "Tổng chiều dài"));
        Shared("DVB_UnitMass", "KL đơn vị (kg/m)");
        Total(Shared("DVB_BarMass", "Khối lượng (kg)"));

        var assemblyField = Shared("DVB_AssemblyId", "Bộ thép");
        if (assemblyField is not null) assemblyField.IsHidden = true;
        var pieceField = Shared("DVB_PieceIndex", "Đoạn");
        if (pieceField is not null) pieceField.IsHidden = true;

        definition.AddSortGroupField(new ScheduleSortGroupField(markField.FieldId, ScheduleSortOrder.Ascending));
        definition.IsItemized = false;
        definition.ShowGrandTotal = true;
        definition.ShowGrandTotalTitle = true;

        if (assemblyField is not null)
            definition.AddFilter(new ScheduleFilter(
                assemblyField.FieldId, ScheduleFilterType.Equal, source.AssemblyId));
        else
            warnings.Add("Bảng không lọc được theo mã bộ thép nên có thể gồm cả thép của mố khác.");
        if (pieceField is not null)
            definition.AddFilter(new ScheduleFilter(pieceField.FieldId, ScheduleFilterType.Equal, 1));
        else
            warnings.Add("Bảng không lọc được theo số thứ tự đoạn nên cột Số lượng đếm theo đoạn, " +
                         "không theo thanh hoàn thiện.");

        return schedule;

        static void Total(ScheduleField? field)
        {
            if (field is not null) field.DisplayType = ScheduleFieldDisplayType.Totals;
        }
    }

    /// <summary>
    /// Writes the yard cutting list. Separate from the schedule above because the two count
    /// different things: the schedule lists the 41 finished bars the drawing dimensions, this lists
    /// the 82 pieces somebody has to cut.
    ///
    /// The file is CSV in the machine's own list separator and number format, so it opens straight
    /// into Excel on a Vietnamese install instead of arriving in one column.
    /// </summary>
    public static string ExportCuttingList(
        AbutmentScheduleSource source,
        IReadOnlyList<AbutmentCuttingListLine> lines,
        IReadOnlyList<AbutmentScheduleLine> scheduleLines)
    {
        var culture = CultureInfo.CurrentCulture;
        var separator = culture.TextInfo.ListSeparator;
        var notes = new List<string>
        {
            $"Bang cat phoi thep mo - bo thep {source.AssemblyId} - host {source.HostName}",
            $"Xuat luc {DateTime.Now:yyyy-MM-dd HH:mm} tu {source.Rows.Count} doan thep trong model",
            $"Doi chieu: bang thong ke co {scheduleLines.Count} so hieu, " +
            $"{scheduleLines.Sum(line => line.Quantity)} thanh hoan thien",
            "Liet ke theo DOAN CAT that, khong theo thanh hoan thien"
        };
        var text = AbutmentRebarScheduleKernel.FormatCuttingListTable(lines, notes, culture, separator);

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DVB_ADDIN", "Exports", "Abutment");
        Directory.CreateDirectory(root);
        var path = Path.Combine(
            root, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Sanitize(source.AssemblyId)}-cat-phoi.csv");
        File.WriteAllText(path, text, new UTF8Encoding(true));
        return path;
    }

    private static string UniqueScheduleName(Document document, string preferred)
    {
        var taken = new FilteredElementCollector(document)
            .OfClass(typeof(ViewSchedule))
            .Select(view => view.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(preferred)) return preferred;
        for (var index = 2; index < 1000; index++)
        {
            var candidate = $"{preferred} ({index})";
            if (!taken.Contains(candidate)) return candidate;
        }
        return $"{preferred} ({Guid.NewGuid():N})";
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(character =>
            Path.GetInvalidFileNameChars().Contains(character) ? '-' : character));

    private static double LengthMm(Element element, string name) =>
        element.LookupParameter(name) is { StorageType: StorageType.Double } parameter
            ? UnitUtils.ConvertFromInternalUnits(parameter.AsDouble(), UnitTypeId.Millimeters)
            : 0;

    private static double Number(Element element, string name) =>
        element.LookupParameter(name) is { StorageType: StorageType.Double } parameter
            ? parameter.AsDouble()
            : 0;

    private static int Integer(Element element, string name) =>
        element.LookupParameter(name) is { StorageType: StorageType.Integer } parameter
            ? parameter.AsInteger()
            : 0;
}
