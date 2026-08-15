namespace BIM.DatViet.Domain;

/// <summary>
/// Chọn đường kính cọc từ các nguồn bằng chứng, theo thứ tự tin cậy.
/// <para>
/// Không suy đường kính từ bounding box. Trên mố xéo, Revit trả AABB cục bộ đã biến đổi chứ không
/// bó sát: đo trên model thật, ba cọc đặt xoay 25° cho bbox 1594,7 mm trong khi thể tích của chúng
/// (71,2508 m³) trùng khít với bốn cọc còn lại và khớp hình trụ D1200 L=63 m tới 0,0007%. Phép kiểm
/// "tròn" bằng <c>dx ≈ dy</c> cũng không cứu được, vì AABB xoay vẫn vuông.
/// </para>
/// <para>
/// Sai 32,9% đường kính không dừng ở việc thanh ngắn đi: nó nong vùng loại trừ thêm gần 200 mm mỗi
/// bên, và bước clip sau đó bỏ mọi mẩu dưới ngưỡng, nên hậu quả là mất hẳn thanh.
/// </para>
/// </summary>
public static class AbutmentPileEvidenceKernel
{
    /// <summary>Nhỏ hơn cọc khoan nhồi nhỏ nhất trong thực hành cầu; dưới mức này là đo nhầm vật khác.</summary>
    public const double MinimumCredibleDiameterMm = 200;
    /// <summary>Lớn hơn cọc lớn nhất từng gặp; trên mức này là bắt phải khối bao chứ không phải cọc.</summary>
    public const double MaximumCredibleDiameterMm = 5000;

    private const double DisagreementFractionOfPreset = 0.05;
    private const double DisagreementFloorMm = 25;

    /// <summary>
    /// Dung sai đối chiếu giữa nguồn thắng và preset. Đặt trong kernel chứ không thêm trường vào
    /// topology: mọi trường mới đều đổi <c>ComputeHash</c>, làm vỡ <c>ruleHash</c> khai trong profile
    /// đóng gói và khiến resolver từ chối mở công cụ.
    /// <para>
    /// 5% của preset, sàn 25 mm. Với D1200 là 60 mm — đủ rộng cho sai số dựng hình và làm tròn, đủ
    /// hẹp để bắt ca 1594,7 mm so với 1200 mm (lệch 394,7 mm) đã đo được trên model thật.
    /// </para>
    /// </summary>
    public static double DefaultDisagreementToleranceMm(double presetDiameterMm) =>
        Math.Max(DisagreementFloorMm, Math.Abs(presetDiameterMm) * DisagreementFractionOfPreset);

    public enum DiameterEvidence
    {
        /// <summary>Đo từ mặt trụ thật của solid cọc.</summary>
        MeasuredCylindricalFace,
        /// <summary>Đọc từ tham số đường kính khai trên instance hoặc symbol.</summary>
        FamilyParameter,
        /// <summary>Không đo và không đọc được gì; dùng giá trị khai trong preset.</summary>
        PresetDefault
    }

    public readonly record struct Resolution(
        double DiameterMm,
        DiameterEvidence Evidence,
        bool DisagreesWithPreset,
        double PresetDiameterMm,
        string Note);

    /// <param name="measuredCylinderDiameterMm">Đo từ mặt trụ; null khi cọc không có mặt trụ nào.</param>
    /// <param name="parameterDiameterMm">Tham số khai trên family; null khi không có.</param>
    /// <param name="presetDiameterMm">Giá trị khai trong profile, luôn phải có.</param>
    /// <param name="disagreementToleranceMm">Lệch quá mức này giữa nguồn thắng và preset thì phải báo.</param>
    public static bool TryResolve(
        double? measuredCylinderDiameterMm,
        double? parameterDiameterMm,
        double presetDiameterMm,
        double disagreementToleranceMm,
        out Resolution resolution,
        out string error)
    {
        resolution = default;
        error = string.Empty;

        if (!IsCredible(presetDiameterMm))
        {
            error =
                $"ABUTMENT_PILE_PRESET_DIAMETER_INVALID: profile khai đường kính cọc " +
                $"{Format(presetDiameterMm)} mm, ngoài dải tin được " +
                $"[{MinimumCredibleDiameterMm:0}; {MaximumCredibleDiameterMm:0}] mm; " +
                $"không có nguồn nào để rơi về khi không đo được cọc. Sửa pileDefaultDiameterMm.";
            return false;
        }
        if (!double.IsFinite(disagreementToleranceMm) || disagreementToleranceMm < 0)
        {
            error =
                $"ABUTMENT_PILE_DIAMETER_TOLERANCE_INVALID: dung sai đối chiếu đường kính cọc là " +
                $"{Format(disagreementToleranceMm)} mm; phải là số hữu hạn không âm.";
            return false;
        }

        var measured = Credible(measuredCylinderDiameterMm);
        var declared = Credible(parameterDiameterMm);

        // Mặt trụ đo được thắng tham số khai: nó là hình học sẽ đúc thật, không phải con số ai đó gõ.
        var (diameter, evidence) = measured is not null
            ? (measured.Value, DiameterEvidence.MeasuredCylindricalFace)
            : declared is not null
                ? (declared.Value, DiameterEvidence.FamilyParameter)
                : (presetDiameterMm, DiameterEvidence.PresetDefault);

        var gap = Math.Abs(diameter - presetDiameterMm);
        var disagrees = evidence != DiameterEvidence.PresetDefault && gap > disagreementToleranceMm;

        resolution = new Resolution(
            diameter,
            evidence,
            disagrees,
            presetDiameterMm,
            BuildNote(evidence, diameter, presetDiameterMm, gap, disagrees, measured, declared));
        return true;
    }

    private static string BuildNote(
        DiameterEvidence evidence,
        double diameter,
        double presetDiameterMm,
        double gap,
        bool disagrees,
        double? measured,
        double? declared)
    {
        var source = evidence switch
        {
            DiameterEvidence.MeasuredCylindricalFace => $"đo mặt trụ {Format(diameter)} mm",
            DiameterEvidence.FamilyParameter => $"tham số family {Format(diameter)} mm",
            _ => $"preset {Format(diameter)} mm"
        };

        if (evidence == DiameterEvidence.PresetDefault)
            return
                $"Không đo được mặt trụ và không đọc được tham số đường kính nào trên cọc; dùng " +
                $"{source}. Xác nhận cọc đúng là hình trụ và đúng cỡ trước khi tạo thép.";

        if (!disagrees)
            return $"Đường kính cọc lấy theo {source}, khớp preset {Format(presetDiameterMm)} mm.";

        var other = measured is not null && declared is not null
            ? $" (tham số family khai {Format(declared.Value)} mm)"
            : string.Empty;
        return
            $"Đường kính cọc lấy theo {source}{other} nhưng preset khai " +
            $"{Format(presetDiameterMm)} mm — lệch {Format(gap)} mm. Vùng loại trừ quanh cọc tính " +
            $"theo số đo, nên sai ở đây làm thép bị cắt thừa hoặc thiếu quanh mỗi cọc. Đối chiếu " +
            $"bản vẽ móng rồi sửa một trong hai.";
    }

    private static double? Credible(double? value) =>
        value is { } candidate && IsCredible(candidate) ? candidate : null;

    private static bool IsCredible(double value) =>
        double.IsFinite(value) &&
        value > MinimumCredibleDiameterMm &&
        value < MaximumCredibleDiameterMm;

    private static string Format(double value) =>
        value.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
