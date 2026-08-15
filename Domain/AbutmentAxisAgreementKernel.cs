using System.Globalization;

namespace BIM.DatViet.Domain;

/// <summary>
/// Phân loại mức đồng thuận giữa hai phương, đo bằng trị tuyệt đối của tích vô hướng.
/// <para>
/// Tồn tại vì hằng số <c>0.9</c> đang được dùng cho nhiều bài toán khác nhau trong lớp nhận diện,
/// và <c>0.9</c> ứng với <b>25,84°</b> — trong khi góc xéo thật của mố pilot là <b>25,00°</b>. Biên
/// còn lại chưa tới một độ, mà không chỗ nào in ra con số đó, nên không ai biết mình đang đứng sát
/// mép.
/// </para>
/// <para>
/// Kernel này <b>không</b> đổi ngưỡng. Đổi ngưỡng khi chưa đo được giá trị thật trên family là canh
/// bạc: siết lại có thể chặn đứng cả pipeline PROFILE đang chạy được. Việc của nó là làm cho khoảng
/// cách tới ngưỡng trở thành thứ đọc được trong log.
/// </para>
/// </summary>
public static class AbutmentAxisAgreementKernel
{
    public const string NearThresholdCode = "ABUTMENT_AXIS_AGREEMENT_NEAR_THRESHOLD";

    /// <summary>Rộng bằng khoảng 1,5° ở lân cận 0,9 — đủ để bắt ca 25,00° so với 25,84°.</summary>
    public const double DefaultWarningBand = 0.012;

    public enum Agreement
    {
        Rejected,
        /// <summary>Đạt, nhưng biên còn lại hẹp tới mức một mố hơi khác là trượt.</summary>
        AcceptedNearThreshold,
        Accepted
    }

    public readonly record struct Assessment(
        Agreement Agreement,
        double Dot,
        double AngleDeg,
        double MinimumDot,
        double MinimumAngleDeg,
        double MarginDeg);

    public static Assessment Classify(double dot, double minimumDot, double warningBand)
    {
        var magnitude = Math.Abs(dot);
        var angle = ToAngleDeg(magnitude);
        var minimumAngle = ToAngleDeg(Math.Abs(minimumDot));
        var margin = minimumAngle - angle;

        var agreement = magnitude < minimumDot
            ? Agreement.Rejected
            : magnitude < minimumDot + Math.Max(0, warningBand)
                ? Agreement.AcceptedNearThreshold
                : Agreement.Accepted;

        return new Assessment(agreement, magnitude, angle, minimumDot, minimumAngle, margin);
    }

    /// <param name="subject">Đại lượng đang được kiểm, ví dụ "pháp tuyến mặt cắt trạm PROFILE_TM".</param>
    public static string DescribeNearThreshold(Assessment assessment, string subject) =>
        $"{subject} lệch {Format(assessment.AngleDeg)}° so với phương tham chiếu, trong khi ngưỡng " +
        $"chấp nhận là {Format(assessment.MinimumAngleDeg)}° — chỉ còn {Format(assessment.MarginDeg)}° " +
        $"biên. Một mố có góc xéo lớn hơn chút ít sẽ trượt ngưỡng này và mất nhận diện, mà không có " +
        $"nhánh Ambiguous nào đỡ. Ghi lại con số để chốt ngưỡng theo số đo thật thay vì hằng số.";

    private static double ToAngleDeg(double dot) =>
        Math.Acos(Math.Clamp(dot, -1, 1)) * 180 / Math.PI;

    private static string Format(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
