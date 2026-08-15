namespace BIM.DatViet.Domain;

/// <summary>
/// Quyết định lấy host từ selection sẵn có hay phải hỏi người dùng chọn.
/// <para>
/// Tách riêng khỏi <c>AbutmentRebarCommand</c> vì lớp đó phụ thuộc <c>Autodesk.Revit.UI</c> nên
/// không nạp được vào test project. Ở đây chỉ còn phép đếm, test được offline.
/// </para>
/// <para>
/// Vì sao cần: bản Release trước đây gọi thẳng <c>PickObject</c>, tức <b>bắt buộc</b> phải có cú
/// click của người thật. Điều đó khiến add-in không thể do agent điều khiển, và làm mất luôn thói
/// quen Revit chuẩn "chọn trước rồi bấm nút".
/// </para>
/// </summary>
public static class AbutmentHostSelectionKernel
{
    public enum Outcome
    {
        /// <summary>Đúng một phần tử hợp lệ đang được chọn — dùng luôn, không hỏi.</summary>
        UsePreselected,

        /// <summary>Không có phần tử hợp lệ nào đang chọn — phải hỏi.</summary>
        PromptNothingValidSelected,

        /// <summary>
        /// Nhiều phần tử hợp lệ đang chọn — <b>phải hỏi</b>. Tự chọn đại một cái là rải thép nhầm mố.
        /// </summary>
        PromptAmbiguousSelection
    }

    public readonly record struct Decision(Outcome Outcome, string Reason)
    {
        public bool ShouldPrompt => Outcome != Outcome.UsePreselected;
    }

    /// <param name="validPreselectedCount">
    /// Số phần tử trong selection hiện tại đã qua <c>AbutmentSelectionFilter</c>. Phần tử không qua
    /// bộ lọc phải được loại TRƯỚC khi gọi hàm này — chọn kèm một cái cột không được biến lựa chọn
    /// một mố thành nhập nhằng.
    /// </param>
    public static Decision Decide(int validPreselectedCount) => validPreselectedCount switch
    {
        1 => new Decision(
            Outcome.UsePreselected,
            "Dùng Generic Model mố cầu đang được chọn sẵn."),
        <= 0 => new Decision(
            Outcome.PromptNothingValidSelected,
            "Selection hiện tại không có Generic Model nào đã bật rebar host."),
        _ => new Decision(
            Outcome.PromptAmbiguousSelection,
            $"Selection hiện tại có {validPreselectedCount} mố hợp lệ nên không suy ra được ý định; " +
            "chọn đúng một mố rồi thử lại.")
    };
}
