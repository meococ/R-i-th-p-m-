using Autodesk.Revit.DB;

namespace BIM.DatViet.RevitServices;

internal static class RevitTransactionGuard
{
    public static void Start(TransactionGroup group, string operation)
    {
        var status = group.Start();
        if (status != TransactionStatus.Started)
            throw new InvalidOperationException($"Không thể bắt đầu TransactionGroup '{operation}': {status}.");
    }

    public static void Start(Transaction transaction, string operation)
    {
        var status = transaction.Start();
        if (status != TransactionStatus.Started)
            throw new InvalidOperationException($"Không thể bắt đầu Transaction '{operation}': {status}.");
    }

    public static void Commit(Transaction transaction, string operation)
    {
        var status = transaction.Commit();
        if (status != TransactionStatus.Committed)
            throw new InvalidOperationException($"Không thể commit Transaction '{operation}': {status}.");
    }

    public static void Assimilate(TransactionGroup group, string operation)
    {
        var status = group.Assimilate();
        if (status != TransactionStatus.Committed)
            throw new InvalidOperationException($"Không thể assimilate TransactionGroup '{operation}': {status}.");
    }

    public static void RollBack(TransactionGroup group, string operation)
    {
        var current = group.GetStatus();
        if (current == TransactionStatus.RolledBack) return;
        if (current != TransactionStatus.Started)
            throw new InvalidOperationException(
                $"Không thể rollback TransactionGroup '{operation}' từ trạng thái {current}.");
        var status = group.RollBack();
        if (status != TransactionStatus.RolledBack)
            throw new InvalidOperationException(
                $"Không thể xác nhận rollback TransactionGroup '{operation}': {status}.");
    }

    public static void RollBack(Transaction transaction, string operation)
    {
        var current = transaction.GetStatus();
        if (current == TransactionStatus.RolledBack) return;
        if (current != TransactionStatus.Started)
            throw new InvalidOperationException(
                $"Không thể rollback Transaction '{operation}' từ trạng thái {current}.");
        var status = transaction.RollBack();
        if (status != TransactionStatus.RolledBack)
            throw new InvalidOperationException(
                $"Không thể xác nhận rollback Transaction '{operation}': {status}.");
    }
}
