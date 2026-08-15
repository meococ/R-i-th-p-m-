using System.Globalization;

namespace BIM.DatViet.Domain;

/// <summary>
/// Dựng đường tâm thép chờ nối bệ mố với thân mố.
/// <para>
/// Mặt cắt tới hạn số một của mố: toàn bộ mô men do áp lực đất, lực hãm, co ngót–từ biến và động
/// đất truyền qua đây. Trước đợt này <c>StarterLongitudinal</c> chỉ tồn tại dưới dạng enum member,
/// không có một dòng code path nào.
/// </para>
/// <para>
/// Mặt bằng chân thân lấy từ cạnh thấp nhất của <c>StemPolygonLocal</c> ở ba trạm PROFILE_TM rồi
/// nội suy tuyến tính theo toạ độ trạm. Với mố xéo, chân thân là hình bình hành nên phép nội suy
/// này <b>tự đúng mà không cần biết góc xéo</b> — khác hẳn việc đọc AABB, vốn phình theo góc xoay.
/// </para>
/// <para>
/// Chiều sâu neo xuống bệ bị kẹp bởi chiều dày bệ <b>đo được</b>, không phải chiều dày khai trong
/// preset: bản vẽ và model đang lệch nhau 200 mm, và thanh chờ đâm thủng đáy bệ là lỗi không được
/// phép tự làm tròn cho qua.
/// </para>
/// </summary>
public static class AbutmentStarterBarKernel
{
    public const string EmbedmentExceedsFootingCode = "ABUTMENT_STARTER_EMBEDMENT_EXCEEDS_FOOTING";
    public const string RowDegenerateCode = "ABUTMENT_STARTER_ROW_DEGENERATE";
    public const string SectionsInvalidCode = "ABUTMENT_STARTER_SECTIONS_INVALID";
    public const string SpacingInvalidCode = "ABUTMENT_STARTER_SPACING_INVALID";

    public enum FacePosition
    {
        /// <summary>Mép chân thân phía mũi bệ.</summary>
        Front,
        /// <summary>Mép chân thân phía gót bệ.</summary>
        Back
    }

    /// <summary>Một trạm PROFILE_TM, quy về hệ toạ độ local của mố.</summary>
    public readonly record struct StemBaseSection(
        double Station,
        double TransverseFront,
        double TransverseBack,
        double FootingTopElevation);

    public readonly record struct BarPoint(double Longitudinal, double Transverse, double Vertical);

    public readonly record struct Request(
        IReadOnlyList<StemBaseSection> Sections,
        FacePosition Face,
        double CoverPlusHalfDiameter,
        double MaximumSpacing,
        double StartClearance,
        double EndClearance,
        bool IncludeFirst,
        bool IncludeLast,
        double EmbedmentDepth,
        double Projection,
        double FootingDepth,
        double BottomLayerDepth);

    public readonly record struct Resolution(
        IReadOnlyList<IReadOnlyList<BarPoint>> Bars,
        double ActualSpacing,
        double RowLength,
        double AvailableEmbedment);

    public static bool TryBuild(Request request, out Resolution resolution, out string error)
    {
        resolution = default;
        error = string.Empty;

        if (request.Sections is null || request.Sections.Count < 2)
        {
            error = $"{SectionsInvalidCode}: cần ít nhất hai trạm PROFILE_TM để nội suy chân thân; " +
                    $"nhận được {request.Sections?.Count ?? 0}.";
            return false;
        }

        var sections = request.Sections.OrderBy(section => section.Station).ToList();
        for (var index = 1; index < sections.Count; index++)
        {
            if (sections[index].Station > sections[index - 1].Station) continue;
            error = $"{SectionsInvalidCode}: hai trạm trùng toạ độ {F(sections[index].Station)}; " +
                    $"không nội suy được mặt bằng chân thân.";
            return false;
        }
        if (sections.Any(section =>
                !double.IsFinite(section.TransverseFront) ||
                !double.IsFinite(section.TransverseBack) ||
                !double.IsFinite(section.FootingTopElevation) ||
                section.TransverseBack <= section.TransverseFront))
        {
            error = $"{SectionsInvalidCode}: có trạm không hữu hạn hoặc mép sau không nằm sau mép " +
                    $"trước; chân thân suy biến.";
            return false;
        }
        if (!(request.MaximumSpacing > 0) || !double.IsFinite(request.MaximumSpacing))
        {
            error = $"{SpacingInvalidCode}: khoảng cách tối đa là {F(request.MaximumSpacing)}; " +
                    $"phải là số dương hữu hạn.";
            return false;
        }

        // Chiều sâu neo phải nằm gọn trong bê tông bệ ĐO ĐƯỢC, trừ đi lớp bảo vệ đáy và chiều sâu
        // lớp thép đáy đã đặt. Không tự cắt ngắn: thanh chờ ngắn hơn chiều dài neo là mất liên kết.
        var availableEmbedment = request.FootingDepth - request.BottomLayerDepth;
        if (request.EmbedmentDepth > availableEmbedment + 1e-9)
        {
            error = $"{EmbedmentExceedsFootingCode}: thép chờ cần neo {F(request.EmbedmentDepth)} mm " +
                    $"xuống bệ nhưng bệ đo được chỉ dày {F(request.FootingDepth)} mm và lớp thép đáy " +
                    $"cùng lớp bảo vệ đã chiếm {F(request.BottomLayerDepth)} mm, còn lại " +
                    $"{F(availableEmbedment)} mm. Không tự cắt ngắn vì thanh chờ hụt neo là mất liên " +
                    $"kết bệ–thân. Tăng chiều dày bệ, giảm đường kính thanh chờ, hoặc dùng móc chân.";
            return false;
        }

        var first = sections[0];
        var last = sections[^1];
        var rowStart = first.Station + request.StartClearance;
        var rowEnd = last.Station - request.EndClearance;
        var rowLength = rowEnd - rowStart;
        if (rowLength <= 0)
        {
            error = $"{RowDegenerateCode}: sau khi lùi {F(request.StartClearance)} mm đầu và " +
                    $"{F(request.EndClearance)} mm cuối, dải đặt thép chờ dài {F(rowLength)} mm; " +
                    $"không còn chỗ đặt thanh nào.";
            return false;
        }

        // Lùi hai đầu TRƯỚC khi chia bước: chia trước rồi lùi sẽ đổi bước thực và làm sai số thanh.
        var stations = GeometryKernel.EvenPositions(rowStart, rowEnd, request.MaximumSpacing).ToList();
        if (stations.Count == 0)
        {
            error = $"{RowDegenerateCode}: dải dài {F(rowLength)} mm với bước tối đa " +
                    $"{F(request.MaximumSpacing)} mm không sinh được vị trí thanh nào.";
            return false;
        }
        if (!request.IncludeLast && stations.Count > 0) stations.RemoveAt(stations.Count - 1);
        if (!request.IncludeFirst && stations.Count > 0) stations.RemoveAt(0);
        if (stations.Count == 0)
        {
            error = $"{RowDegenerateCode}: bỏ thanh đầu/cuối làm dải không còn thanh nào.";
            return false;
        }

        var bars = new List<IReadOnlyList<BarPoint>>();
        foreach (var station in stations)
        {
            var footingTop = Interpolate(sections, station, section => section.FootingTopElevation);
            var faceTransverse = request.Face == FacePosition.Front
                ? Interpolate(sections, station, section => section.TransverseFront) +
                  request.CoverPlusHalfDiameter
                : Interpolate(sections, station, section => section.TransverseBack) -
                  request.CoverPlusHalfDiameter;

            bars.Add(
            [
                new BarPoint(station, faceTransverse, footingTop - request.EmbedmentDepth),
                new BarPoint(station, faceTransverse, footingTop + request.Projection)
            ]);
        }

        var actualSpacing = stations.Count > 1
            ? (stations[^1] - stations[0]) / (stations.Count - 1)
            : 0;

        resolution = new Resolution(bars, actualSpacing, rowLength, availableEmbedment);
        return true;
    }

    /// <summary>Nội suy tuyến tính theo trạm, kẹp ngoài biên về trạm gần nhất.</summary>
    private static double Interpolate(
        IReadOnlyList<StemBaseSection> sections,
        double station,
        Func<StemBaseSection, double> select)
    {
        if (station <= sections[0].Station) return select(sections[0]);
        if (station >= sections[^1].Station) return select(sections[^1]);
        for (var index = 1; index < sections.Count; index++)
        {
            var lower = sections[index - 1];
            var upper = sections[index];
            if (station > upper.Station) continue;
            var span = upper.Station - lower.Station;
            var fraction = span <= 0 ? 0 : (station - lower.Station) / span;
            return select(lower) + (select(upper) - select(lower)) * fraction;
        }
        return select(sections[^1]);
    }

    private static string F(double value) =>
        value.ToString("0.#", CultureInfo.InvariantCulture);
}
