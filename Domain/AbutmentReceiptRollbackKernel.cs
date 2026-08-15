namespace BIM.DatViet.Domain;

/// <summary>
/// Quyết định biên lai giữ hay xoá gì sau khi rollback một TransactionGroup Create thất bại.
/// <para>
/// Phân biệt hai loại nội dung. <b>Bằng chứng</b> là danh sách id đã tạo và đã gỡ: nó mô tả thứ
/// runtime đã chạm vào model. <b>Báo cáo</b> là tổng hợp theo lớp và tổng cộng: nó mô tả thứ kế
/// hoạch định làm.
/// </para>
/// <para>
/// Rollback đã xác nhận thì model trở lại nguyên trạng, bằng chứng không còn mô tả gì nên xoá.
/// Rollback <b>không</b> xác nhận thì không ai biết model đang ở trạng thái nào, và danh sách id
/// là manh mối duy nhất để tìm lại thép mồ côi — xoá nó là xoá bằng chứng đúng lúc cần nhất.
/// Báo cáo thì xoá ở cả hai nhánh, vì để lại tổng hợp trên một biên lai thất bại sẽ bị đọc nhầm
/// thành "chừng này thép đã vào model".
/// </para>
/// </summary>
public static class AbutmentReceiptRollbackKernel
{
    public const string RolledBackCode = "ABUTMENT_CREATE_ROLLED_BACK";
    public const string RollbackUnconfirmedCode = "ABUTMENT_CREATE_ROLLBACK_UNCONFIRMED";

    public readonly record struct Decision(
        bool ClearTouchedIds,
        bool ClearDerivedSummary,
        string Code,
        string Message);

    /// <param name="rollbackConfirmed">
    /// <c>true</c> chỉ khi lời gọi rollback trả về mà không ném. Bất kỳ nghi ngờ nào cũng phải
    /// truyền <c>false</c>.
    /// </param>
    /// <param name="failureMessage">Thông điệp của exception đã làm hỏng Create.</param>
    /// <param name="rollbackError">Thông điệp của exception khi rollback, rỗng nếu rollback không ném.</param>
    /// <param name="createdIdCount">Số id đã tạo trước khi lỗi; đi vào thông điệp để người đọc biết quy mô phải dọn.</param>
    /// <param name="removedIdCount">Số id đã gỡ trước khi lỗi.</param>
    public static Decision Decide(
        bool rollbackConfirmed,
        string failureMessage,
        string rollbackError,
        int createdIdCount,
        int removedIdCount)
    {
        var failure = string.IsNullOrWhiteSpace(failureMessage)
            ? "Không có thông điệp lỗi."
            : failureMessage.Trim();

        // Code đi riêng trong trường Code; message không lặp lại nó để UI không in hai lần.
        if (rollbackConfirmed)
            return new Decision(
                ClearTouchedIds: true,
                ClearDerivedSummary: true,
                Code: RolledBackCode,
                Message:
                $"TransactionGroup rollback đã được xác nhận; thép managed cũ và thép manual " +
                $"không đổi. {failure}");

        var rollback = string.IsNullOrWhiteSpace(rollbackError)
            ? "không có thông điệp rollback"
            : rollbackError.Trim();

        return new Decision(
            ClearTouchedIds: false,
            ClearDerivedSummary: true,
            Code: RollbackUnconfirmedCode,
            Message:
            $"Không xác nhận được rollback TransactionGroup; không được coi model là nguyên trạng. " +
            $"{failure} Rollback: {rollback}. Biên lai giữ nguyên {createdIdCount} id đã tạo và " +
            $"{removedIdCount} id đã gỡ để truy vết — các id này có thể vẫn còn hoặc đã mất trong " +
            $"model. Kiểm và dọn tay theo danh sách trong biên lai trước khi Create lại.");
    }
}
