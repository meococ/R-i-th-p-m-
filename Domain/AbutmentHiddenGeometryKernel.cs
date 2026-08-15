using System.Globalization;

namespace BIM.DatViet.Domain;

/// <summary>
/// Báo cáo phần hình học bị ẩn đã bị loại khỏi tập solid dùng để tính toán.
/// <para>
/// Trích xuất bật <c>IncludeNonVisibleObjects</c> vì đó là cách duy nhất nhặt được ModelLine của
/// nested PROFILE_TM — chúng vô hình trong 3D. Cái giá là mọi khối bị tắt hiển thị cũng theo vào:
/// host mố hiện bật <c>ẨN TAI_L</c>, <c>ẨN_TAI_R</c>, <c>ẨN LỀ BỘ HÀNH R</c>, và nếu tai với lề bộ
/// hành lọt vào tập solid thì hull đáy, khung trục, hộp bao và band scan đều tính trên một khối bê
/// tông không tồn tại.
/// </para>
/// <para>
/// Loại chúng ra là đúng, nhưng loại im lặng thì không: kỹ sư phải biết công cụ đang làm việc trên
/// ít hơn toàn bộ family, và biết ít hơn bao nhiêu.
/// </para>
/// </summary>
public static class AbutmentHiddenGeometryKernel
{
    public const string ExcludedCode = "ABUTMENT_HIDDEN_GEOMETRY_EXCLUDED";

    public readonly record struct Report(
        bool HasExclusions,
        int ExcludedSolidCount,
        double ExcludedVolumeM3,
        double KeptVolumeM3,
        double ExcludedFraction,
        string Message);

    /// <param name="keptVolumesFt3">Thể tích các solid được giữ, đơn vị nội bộ Revit (ft³).</param>
    /// <param name="excludedVolumesFt3">Thể tích các solid bị loại vì tắt hiển thị, ft³.</param>
    public static Report Describe(
        IReadOnlyCollection<double> keptVolumesFt3,
        IReadOnlyCollection<double> excludedVolumesFt3)
    {
        const double cubicFeetToCubicMetres = 0.028316846592;

        var kept = keptVolumesFt3.Where(double.IsFinite).Sum() * cubicFeetToCubicMetres;
        var excluded = excludedVolumesFt3.Where(double.IsFinite).Sum() * cubicFeetToCubicMetres;
        var total = kept + excluded;
        var fraction = total > 1e-9 ? excluded / total : 0;

        if (excludedVolumesFt3.Count == 0)
            return new Report(false, 0, 0, kept, 0, string.Empty);

        return new Report(
            true,
            excludedVolumesFt3.Count,
            excluded,
            kept,
            fraction,
            $"Đã loại {excludedVolumesFt3.Count} khối đang tắt hiển thị khỏi tập solid tính toán: " +
            $"{Format(excluded)} m³ trên tổng {Format(total)} m³ ({Format(fraction * 100)}%). " +
            $"Phần còn lại dùng để dựng biên, khung trục và cao độ lớp là {Format(kept)} m³. " +
            $"Nếu có khối đáng lẽ phải tính, bật lại hiển thị của nó rồi chạy lại Phân tích.");
    }

    private static string Format(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
