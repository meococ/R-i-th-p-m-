using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using BIM.DatViet.Models;

namespace BIM.DatViet.RevitServices;

public sealed class RebarFactory
{
    public const string ToolVersion = "1.0.0";
    private readonly Document _document;

    public RebarFactory(Document document) => _document = document;

    public CreateReceipt CreateOrUpdate(RebarPlan plan)
    {
        var receipt = new CreateReceipt { RunId = Guid.NewGuid().ToString("N") };
        receipt.Issues.AddRange(Preflight(plan));
        if (receipt.Issues.Any(issue => issue.Severity == ValidationSeverity.Error)) return receipt;

        var primaryHost = _document.GetElement(plan.Assembly.PrimaryHostId);
        if (primaryHost is null)
        {
            receipt.Issues.Add(new ValidationIssue("PRIMARY_HOST_MISSING", ValidationSeverity.Error,
                "Không còn primary host của hố ga."));
            return receipt;
        }
        // PrimaryHostUniqueId is the immutable ownership boundary; Mark/HOGA_ID remains display metadata.
        var oldOwned = ManholeOwnershipStore.FindOwned(
            _document, plan.Assembly.ManholeKey, primaryHost.UniqueId).ToList();
        var created = new List<(PlannedBar Plan, Rebar Rebar)>();
        using var group = new TransactionGroup(_document, $"Rải thép {plan.Assembly.ManholeKey}");
        try
        {
            RevitTransactionGuard.Start(group, $"Rải thép {plan.Assembly.ManholeKey}");
            using (var createTransaction = new Transaction(_document, "Tạo kết quả thép hố ga mới"))
            {
                RevitTransactionGuard.Start(createTransaction, "Tạo kết quả thép hố ga mới");
                // Stamp identity on every source host so ManholeKey/W1 restore works from any pick.
                foreach (var sourceId in plan.Assembly.SourceElementIds.Distinct())
                {
                    if (_document.GetElement(sourceId) is { } sourceHost)
                        ManholeHostIdentityStore.Write(sourceHost, plan.Assembly);
                }
                foreach (var plannedBar in plan.Bars)
                {
                    var rebar = CreateOne(plannedBar, plan, receipt.RunId);
                    created.Add((plannedBar, rebar));
                    receipt.CreatedIds.Add(rebar.Id);
                }
                RevitTransactionGuard.Commit(createTransaction, "Tạo kết quả thép hố ga mới");
            }

            receipt.Issues.AddRange(Readback(created, plan));
            // Only hard readback failures roll back. Geometry drift (Revit host projection) is Warning.
            if (receipt.Issues.Any(issue => issue.Severity == ValidationSeverity.Error &&
                                            issue.Code.StartsWith("READBACK_", StringComparison.Ordinal)))
                throw new InvalidOperationException("Readback hard-fail: host/type/ownership/count không khớp RebarPlan.");

            if (oldOwned.Count > 0)
            {
                using var deleteTransaction = new Transaction(_document, "Gỡ kết quả cũ do tool sở hữu");
                RevitTransactionGuard.Start(deleteTransaction, "Gỡ kết quả cũ do tool sở hữu");
                foreach (var old in oldOwned)
                {
                    receipt.RemovedIds.Add(old.Id);
                    _document.Delete(old.Id);
                }
                RevitTransactionGuard.Commit(deleteTransaction, "Gỡ kết quả cũ do tool sở hữu");
            }

            receipt.UpdatedIds.AddRange(created.Select(item => item.Rebar.Id));
            RevitTransactionGuard.Assimilate(group, $"Rải thép {plan.Assembly.ManholeKey}");
            receipt.Succeeded = true;
        }
        catch (Exception exception)
        {
            var rollbackConfirmed = true;
            var rollbackError = string.Empty;
            try
            {
                RevitTransactionGuard.RollBack(group, $"Rải thép {plan.Assembly.ManholeKey}");
            }
            catch (Exception rollbackException)
            {
                rollbackConfirmed = false;
                rollbackError = rollbackException.Message;
            }
            receipt.CreatedIds.Clear();
            receipt.RemovedIds.Clear();
            receipt.UpdatedIds.Clear();
            receipt.Issues.Add(new ValidationIssue(
                rollbackConfirmed ? "CREATE_ROLLED_BACK" : "CREATE_ROLLBACK_UNCONFIRMED",
                ValidationSeverity.Error,
                rollbackConfirmed
                    ? $"Rollback toàn bộ đã được xác nhận; kết quả cũ được giữ nguyên. {exception.Message}"
                    : $"Không xác nhận được rollback; không được coi model là nguyên trạng. " +
                      $"{exception.Message} Rollback: {rollbackError}"));
        }
        return receipt;
    }

    private IReadOnlyList<ValidationIssue> Preflight(RebarPlan plan)
    {
        var issues = new List<ValidationIssue>(plan.Issues);
        if (!plan.CanCreate) return issues;
        foreach (var plannedBar in plan.Bars)
        {
            var host = _document.GetElement(plannedBar.HostId);
            if (host is null || !RebarHostData.IsValidHost(host))
                issues.Add(new ValidationIssue("REBAR_HOST_NOT_READY", ValidationSeverity.Error,
                    $"Host của {plannedBar.Key} không thể chứa Rebar.", plannedBar.HostId));
            var barType = FindType(plannedBar.BarTypeName);
            if (barType is null)
                issues.Add(new ValidationIssue("REBAR_TYPE_NOT_READY", ValidationSeverity.Error,
                    $"Không resolve được RebarBarType '{plannedBar.BarTypeName}'."));
            else if (plannedBar.Points.Count > 2 && plannedBar.Points.Zip(plannedBar.Points.Skip(1),
                         (a, b) => a.DistanceTo(b)).Any(length => length < barType.StandardBendDiameter / 2))
                issues.Add(new ValidationIssue("BEND_RADIUS_NOT_READY", ValidationSeverity.Error,
                    $"Nhánh uốn của {plannedBar.Key} ngắn hơn bán kính uốn tối thiểu của bar type.", plannedBar.HostId));
            if (plannedBar.Points.Count < 2 || plannedBar.Points.Zip(plannedBar.Points.Skip(1),
                    (a, b) => a.DistanceTo(b)).Any(length => length < 1e-6))
                issues.Add(new ValidationIssue("ZERO_LENGTH_CURVE", ValidationSeverity.Error,
                    $"{plannedBar.Key} có curve rỗng hoặc quá ngắn.", plannedBar.HostId));
            if (!IsCoplanar(plannedBar.Points, plannedBar.PlaneNormal))
                issues.Add(new ValidationIssue("CURVES_NOT_COPLANAR", ValidationSeverity.Error,
                    $"{plannedBar.Key} không đồng phẳng với normal đã lập kế hoạch.", plannedBar.HostId));
        }
        return issues;
    }

    private Rebar CreateOne(PlannedBar plannedBar, RebarPlan plan, string runId)
    {
        var host = _document.GetElement(plannedBar.HostId)
                   ?? throw new InvalidOperationException($"Không còn host {plannedBar.HostId}.");
        var barType = FindType(plannedBar.BarTypeName)
                      ?? throw new InvalidOperationException($"Thiếu type {plannedBar.BarTypeName}.");
        var curves = plannedBar.Points.Zip(plannedBar.Points.Skip(1),
            (start, end) => (Curve)Line.CreateBound(start, end)).ToList();
        var rebar = RebarApiAdapter.CreateFromCurves(_document, barType, host,
            plannedBar.PlaneNormal.Normalize(), curves)
                    ?? throw new InvalidOperationException($"Revit không tạo được {plannedBar.Key}.");
        if (plannedBar.IsSet)
        {
            using var accessor = rebar.GetShapeDrivenAccessor();
            accessor.SetLayoutAsMaximumSpacing(plannedBar.MaxSpacing, plannedBar.SetLength, true, true, true);
        }
        var partition = rebar.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM);
        if (partition is { IsReadOnly: false }) partition.Set(plannedBar.Partition);
        var primary = _document.GetElement(plan.Assembly.PrimaryHostId)
                      ?? throw new InvalidOperationException("Không còn primary host.");
        ManholeOwnershipStore.Write(rebar, new ManholeOwnership(plan.Assembly.ManholeKey, primary.UniqueId,
            plannedBar.Key, plan.SpecHash, runId, ToolVersion));
        return rebar;
    }

    private IReadOnlyList<ValidationIssue> Readback(IReadOnlyCollection<(PlannedBar Plan, Rebar Rebar)> created,
        RebarPlan plan)
    {
        var issues = new List<ValidationIssue>();
        if (created.Count != plan.Bars.Count)
            issues.Add(new ValidationIssue("READBACK_COUNT", ValidationSeverity.Error,
                $"Số Rebar element {created.Count} không khớp plan {plan.Bars.Count}."));
        foreach (var item in created)
        {
            var current = _document.GetElement(item.Rebar.Id) as Rebar;
            if (current is null)
            {
                issues.Add(new ValidationIssue("READBACK_MISSING", ValidationSeverity.Error,
                    $"Không đọc lại được {item.Plan.Key}."));
                continue;
            }
            if (current.GetHostId() != item.Plan.HostId)
                issues.Add(new ValidationIssue("READBACK_HOST", ValidationSeverity.Error,
                    $"Host đọc lại của {item.Plan.Key} không khớp.", current.Id));
            var type = _document.GetElement(current.GetTypeId()) as RebarBarType;
            if (type is null || !type.Name.Equals(item.Plan.BarTypeName, StringComparison.OrdinalIgnoreCase))
                issues.Add(new ValidationIssue("READBACK_TYPE", ValidationSeverity.Error,
                    $"Bar type đọc lại của {item.Plan.Key} không khớp.", current.Id));

            // Soft checks: Revit often projects rebar slightly into host / reverses direction.
            var partition = current.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM)?.AsString() ?? string.Empty;
            if (!partition.Equals(item.Plan.Partition, StringComparison.Ordinal))
                issues.Add(new ValidationIssue("READBACK_PARTITION", ValidationSeverity.Warning,
                    $"Partition đọc lại của {item.Plan.Key} khác plan (vẫn giữ element).", current.Id));

            var curves = current.GetCenterlineCurves(false, false, false,
                MultiplanarOption.IncludeOnlyPlanarCurves, 0);
            if (curves.Count == 0)
            {
                issues.Add(new ValidationIssue("READBACK_CURVES", ValidationSeverity.Error,
                    $"Centerline đọc lại của {item.Plan.Key} rỗng.", current.Id));
            }
            else if (!CenterlineMatches(curves, item.Plan.Points))
            {
                issues.Add(new ValidationIssue("READBACK_CENTERLINE", ValidationSeverity.Warning,
                    $"Centerline {item.Plan.Key} lệch plan > {SoftDistanceMm:0} mm / {SoftAngleDeg:0.#}° " +
                    "(Revit có thể đã project vào host; không rollback).", current.Id));
            }

            if (!ManholeOwnershipStore.TryRead(current, out var ownership) ||
                ownership.ManholeKey != plan.Assembly.ManholeKey || ownership.SpecHash != plan.SpecHash ||
                ownership.PrimaryHostUniqueId != _document.GetElement(plan.Assembly.PrimaryHostId)?.UniqueId)
                issues.Add(new ValidationIssue("READBACK_OWNERSHIP", ValidationSeverity.Error,
                    $"Metadata sở hữu của {item.Plan.Key} không khớp.", current.Id));
            if (item.Plan.IsSet && current.NumberOfBarPositions != item.Plan.PlannedQuantity)
                issues.Add(new ValidationIssue("READBACK_SET_QUANTITY", ValidationSeverity.Warning,
                    $"Số vị trí thanh của {item.Plan.Key}: plan {item.Plan.PlannedQuantity}, Revit {current.NumberOfBarPositions}.", current.Id));
        }
        return issues;
    }

    private RebarBarType? FindType(string name) => new FilteredElementCollector(_document)
        .OfClass(typeof(RebarBarType)).Cast<RebarBarType>()
        .FirstOrDefault(type => type.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool IsCoplanar(IReadOnlyList<XYZ> points, XYZ normal)
    {
        if (points.Count < 2 || normal.GetLength() < 1e-9) return false;
        var origin = points[0];
        var unit = normal.Normalize();
        return points.All(point => Math.Abs((point - origin).DotProduct(unit)) < 1e-6);
    }

    // Practical host-projection tolerances (1 mm / 0.1° was too strict for foundations).
    private const double SoftDistanceMm = 15;
    private const double SoftAngleDeg = 2;

    private static bool CenterlineMatches(IList<Curve> actual, IReadOnlyList<XYZ> planned)
    {
        var distanceTolerance = UnitUtils.ConvertToInternalUnits(SoftDistanceMm, UnitTypeId.Millimeters);
        var angleTolerance = SoftAngleDeg * Math.PI / 180;

        // Endpoint match (forward or reverse polyline).
        if (actual.Count == planned.Count - 1)
        {
            bool Endpoints(bool reverse)
            {
                for (var index = 0; index < actual.Count; index++)
                {
                    var plannedIndex = reverse ? actual.Count - 1 - index : index;
                    var expectedStart = reverse ? planned[plannedIndex + 1] : planned[plannedIndex];
                    var expectedEnd = reverse ? planned[plannedIndex] : planned[plannedIndex + 1];
                    var actualStart = actual[index].GetEndPoint(0);
                    var actualEnd = actual[index].GetEndPoint(1);
                    var endsOk =
                        (actualStart.DistanceTo(expectedStart) <= distanceTolerance &&
                         actualEnd.DistanceTo(expectedEnd) <= distanceTolerance) ||
                        (actualStart.DistanceTo(expectedEnd) <= distanceTolerance &&
                         actualEnd.DistanceTo(expectedStart) <= distanceTolerance);
                    if (!endsOk) return false;
                    var expectedDirection = (expectedEnd - expectedStart).Normalize();
                    var actualDirection = (actualEnd - actualStart).Normalize();
                    var angle = Math.Min(actualDirection.AngleTo(expectedDirection),
                        actualDirection.AngleTo(-expectedDirection));
                    if (angle > angleTolerance) return false;
                }
                return true;
            }
            if (Endpoints(false) || Endpoints(true)) return true;
        }

        // Fallback: each planned segment midpoint projects near some actual curve (host may trim ends).
        for (var index = 0; index < planned.Count - 1; index++)
        {
            var mid = (planned[index] + planned[index + 1]) / 2;
            var plannedDir = (planned[index + 1] - planned[index]).Normalize();
            var near = false;
            foreach (var curve in actual)
            {
                var result = curve.Project(mid);
                if (result is null || result.Distance > distanceTolerance) continue;
                var curveDir = (curve.GetEndPoint(1) - curve.GetEndPoint(0)).Normalize();
                var angle = Math.Min(curveDir.AngleTo(plannedDir), curveDir.AngleTo(-plannedDir));
                if (angle <= angleTolerance)
                {
                    near = true;
                    break;
                }
            }
            if (!near) return false;
        }
        return true;
    }
}
