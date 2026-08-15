using System.Globalization;
using System.Text.Json;
using System.IO;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using BIM.DatViet.Infrastructure;
using BIM.DatViet.Domain;
using BIM.DatViet.Models;
using BIM.DatViet.Planning;

namespace BIM.DatViet.RevitServices;

public sealed class AbutmentRebarFactory
{
    public const string ToolVersion = "1.8.0";
    private readonly Document _document;
    public AbutmentRebarFactory(Document document) => _document = document;

    public AbutmentCreateReceipt Execute(
        ReviewedAbutmentPlanSnapshot snapshot,
        AbutmentWorkflowMode mode)
    {
        var plan = snapshot.Plan;
        var targetZone = snapshot.Zone;
        if (!snapshot.PlanHash.Equals(plan.PlanHash, StringComparison.OrdinalIgnoreCase) ||
            !snapshot.PlanHash.Equals(AbutmentRebarPlanner.ComputePlanHash(plan), StringComparison.OrdinalIgnoreCase) ||
            !snapshot.RuleHash.Equals(plan.Preset.RuleHash, StringComparison.OrdinalIgnoreCase) ||
            !snapshot.GeometryHash.Equals(plan.GeometryHash, StringComparison.OrdinalIgnoreCase))
        {
            var invalid = NewReceipt(plan, mode);
            invalid.Issues.Add(new ValidationIssue(
                "ABUTMENT_REVIEWED_SNAPSHOT_INVALID",
                ValidationSeverity.Error,
                "Reviewed snapshot hash không còn khớp plan được chuyển tới Create.",
                plan.Context.Host.Id));
            WriteReceiptFile(invalid);
            return invalid;
        }
        var host = plan.Context.Host;
        var receipt = NewReceipt(plan, mode);

        var existingOwned = AbutmentMetadataStore.FindOwned(_document, host.UniqueId).ToList();
        receipt.Issues.AddRange(Preflight(plan, mode, targetZone, existingOwned));
        if (receipt.Issues.Any(issue => issue.Severity == ValidationSeverity.Error))
        {
            WriteReceiptFile(receipt);
            return receipt;
        }

        var targetBars = plan.Bars.Where(bar => bar.Zone == targetZone).ToList();
        if (!TryBuildScheduleFacts(plan, targetBars, out var scheduleFacts, out var scheduleFactsError))
        {
            receipt.Issues.Add(new ValidationIssue(
                "ABUTMENT_SCHEDULE_FACTS_UNRESOLVED",
                ValidationSeverity.Error,
                scheduleFactsError,
                host.Id));
            WriteReceiptFile(receipt);
            return receipt;
        }
        if (!TryBuildPieceFacts(targetBars, out var pieceFacts, out var pieceFactsError))
        {
            receipt.Issues.Add(new ValidationIssue(
                "ABUTMENT_BAR_PIECE_FACTS_UNRESOLVED",
                ValidationSeverity.Error,
                pieceFactsError,
                host.Id));
            WriteReceiptFile(receipt);
            return receipt;
        }
        if (!TryBuildLayerSummary(
                targetBars, scheduleFacts, pieceFacts,
                out var layerSummary, out var summaryTotals, out var summaryError))
        {
            receipt.Issues.Add(new ValidationIssue(
                "ABUTMENT_RECEIPT_SUMMARY_UNRESOLVED",
                ValidationSeverity.Error,
                summaryError,
                host.Id));
            WriteReceiptFile(receipt);
            return receipt;
        }
        var oldOwned = mode == AbutmentWorkflowMode.RebuildZone
            ? existingOwned.Where(rebar => AbutmentMetadataStore.TryRead(rebar, out var metadata) &&
                                           metadata.HostRole.Equals(
                                               targetZone.ToString(), StringComparison.OrdinalIgnoreCase)).ToList()
            : [];
        var created = new List<(PlannedAbutmentRebar Planned, Rebar Rebar)>();
        using var group = new TransactionGroup(_document, $"DVB Abutment Reinforcement {receipt.GenerationId}");
        try
        {
            RevitTransactionGuard.Start(group, $"DVB Abutment Reinforcement {receipt.GenerationId}");
            RunWithConstrainedPlacementDisabled(() =>
            {
                using (var transaction = new Transaction(_document, "Create replacement abutment reinforcement"))
                {
                    RevitTransactionGuard.Start(transaction, "Create replacement abutment reinforcement");
                    foreach (var bindingIssue in AbutmentSharedParameterBinder.EnsureBound(_document))
                        receipt.Issues.Add(new ValidationIssue("ABUTMENT_SHARED_PARAMETER_BIND_FAILED", ValidationSeverity.Error,
                            bindingIssue, host.Id));
                    if (receipt.Issues.Any(issue => issue.Severity == ValidationSeverity.Error))
                        throw new InvalidOperationException("Required visible metadata parameters could not be bound.");


                    foreach (var planned in targetBars)
                    {
                        // One resolved measurement record per bar, handed both to the hidden evidence
                        // and to the receipt line, so neither can describe the bar differently.
                        var piece = pieceFacts[planned.Key];
                        var rebar = CreateOne(
                            plan, planned, receipt.GenerationId, scheduleFacts[planned.Key], piece);
                        created.Add((planned, rebar));
                        receipt.CreatedIds.Add(rebar.Id);
                        receipt.CreatedItems.Add(new AbutmentCreatedRebarReceipt
                        {
                            Key = planned.Key,
                            ZoneCode = planned.ZoneCode,
                            RebarId = rebar.Id,
                            Piece = piece
                        });
                    }
                    _document.Regenerate();
                    RevitTransactionGuard.Commit(transaction, "Create replacement abutment reinforcement");
                }
            });

            receipt.Issues.AddRange(Readback(plan, created));
            if (receipt.Issues.Any(issue => issue.Severity == ValidationSeverity.Error))
                throw new InvalidOperationException("Replacement readback or geometry validation failed.");
            receipt.LayerSummary.AddRange(layerSummary);
            receipt.SummaryTotals = summaryTotals;

            using (var replace = new Transaction(_document, "Replace previous DVB-managed zone results"))
            {
                RevitTransactionGuard.Start(replace, "Replace previous DVB-managed zone results");
                foreach (var previous in oldOwned)
                {
                    receipt.RemovedIds.Add(previous.Id);
                    _document.Delete(previous.Id);
                }
                _document.Regenerate();
                var allManaged = AbutmentMetadataStore.FindOwned(_document, host.UniqueId).ToList();
                var generatedUtc = DateTime.UtcNow.ToString("O");
                foreach (var managed in allManaged)
                {
                    if (!AbutmentMetadataStore.TryRead(managed, out var metadata))
                        throw new InvalidOperationException($"Không đọc được metadata của Rebar {managed.Id}.");
                    var canonicalMark = plan.Bars.SingleOrDefault(item =>
                        item.Key.Equals(metadata.RuleId, StringComparison.OrdinalIgnoreCase))?.Rule.CanonicalMark
                        ?? string.Empty;
                    AbutmentMetadataStore.WriteRebar(managed, metadata with
                    {
                        GenerationId = receipt.GenerationId,
                        GeneratedUtc = generatedUtc
                    }, canonicalMark);
                }
                AbutmentMetadataStore.WriteHostManifest(host, AssemblyId(plan), receipt.GenerationId,
                    plan.GeometryHash, plan.Preset.RuleHash, allManaged.Select(item => item.Id));
                RevitTransactionGuard.Commit(replace, "Replace previous DVB-managed zone results");
            }
            receipt.Succeeded = true;
            // Receipt persistence is part of the success contract. Verify it before
            // assimilating so an I/O failure still rolls back the model mutation.
            WriteReceiptFile(receipt, required: true);
            RevitTransactionGuard.Assimilate(group, $"DVB Abutment Reinforcement {receipt.GenerationId}");
            // The preflight lookup populated the document-scoped ownership cache with
            // the previous generation. Re-scan after commit so the modeless UI cannot
            // briefly offer Create (and risk a duplicate) against deleted element ids.
            AbutmentOwnershipIndex.Invalidate(_document);
        }
        catch (Exception exception)
        {
            var rollbackConfirmed = true;
            var rollbackError = string.Empty;
            try
            {
                RevitTransactionGuard.RollBack(
                    group, $"DVB Abutment Reinforcement {receipt.GenerationId}");
            }
            catch (Exception rollbackException)
            {
                rollbackConfirmed = false;
                rollbackError = rollbackException.Message;
            }
            // Whether rollback succeeded or not, cached ownership rows may no longer
            // describe the document. Force the next safety gate to read the model.
            AbutmentOwnershipIndex.Invalidate(_document);
            // An unconfirmed rollback leaves the model in an unknown state, and the id lists are
            // the only trail back to orphaned rebar. Clearing them there would delete the evidence
            // exactly when it is needed, so the kernel decides what survives.
            var rollbackDecision = AbutmentReceiptRollbackKernel.Decide(
                rollbackConfirmed,
                exception.Message,
                rollbackError,
                receipt.CreatedIds.Count,
                receipt.RemovedIds.Count);
            if (rollbackDecision.ClearTouchedIds)
            {
                receipt.CreatedIds.Clear();
                receipt.CreatedItems.Clear();
                receipt.RemovedIds.Clear();
            }
            if (rollbackDecision.ClearDerivedSummary)
            {
                receipt.LayerSummary.Clear();
                receipt.SummaryTotals = null;
            }
            receipt.Succeeded = false;
            receipt.Issues.Add(new ValidationIssue(
                rollbackDecision.Code,
                ValidationSeverity.Error,
                rollbackDecision.Message,
                host.Id));
            WriteReceiptFile(receipt);
            return receipt;
        }

        return receipt;
    }



    private IReadOnlyList<ValidationIssue> Preflight(
        AbutmentRebarPlan plan,
        AbutmentWorkflowMode mode,
        AbutmentZoneKind targetZone,
        IReadOnlyList<Rebar> existingOwned)
    {
        var issues = new List<ValidationIssue>(plan.Issues);
        var targetBars = plan.Bars.Where(bar => bar.Zone == targetZone).ToList();
        var existingTarget = existingOwned
            .Where(rebar => AbutmentMetadataStore.TryRead(rebar, out var metadata) &&
                            metadata.HostRole.Equals(
                                targetZone.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!plan.CanCreateZone(targetZone))
            issues.Add(new ValidationIssue("ABUTMENT_PLAN_NOT_CREATABLE", ValidationSeverity.Error,
                "Plan has hard errors.", plan.Context.Host.Id));
        if (_document.IsReadOnly)
            issues.Add(new ValidationIssue("ABUTMENT_DOCUMENT_READ_ONLY", ValidationSeverity.Error,
                "Document is read-only."));
        if (targetBars.Count == 0)
            issues.Add(new ValidationIssue("ABUTMENT_TARGET_ZONE_EMPTY", ValidationSeverity.Error,
                $"Khu vực {targetZone} không có quy tắc Rebar đang bật.", plan.Context.Host.Id));
        if (mode == AbutmentWorkflowMode.Create && existingTarget.Count > 0)
            issues.Add(new ValidationIssue("ABUTMENT_CREATE_ALREADY_EXISTS", ValidationSeverity.Error,
                "Khu vực đã có Rebar do DVB quản lý; hãy dùng Tạo lại vùng.",
                existingTarget.Select(rebar => rebar.Id).ToArray()));
        if (mode == AbutmentWorkflowMode.RebuildZone && existingTarget.Count == 0)
            issues.Add(new ValidationIssue("ABUTMENT_REBUILD_REQUIRES_EXISTING", ValidationSeverity.Error,
                "Khu vực chưa có Rebar do DVB quản lý; hãy dùng Tạo thép.", plan.Context.Host.Id));
        if (targetBars.Any(bar => bar.Rule.ShapeStrategy == AbutmentShapeStrategy.CustomServerFreeForm))
            issues.Add(new ValidationIssue("ABUTMENT_FREEFORM_REFERENCE_UNRESOLVED", ValidationSeverity.Error,
                "A free-form rule reached Create without a stable face/distribution reference."));
        if (issues.All(issue =>
                !issue.Code.Equals("REBAR_SHAPE_CATALOG_EMPTY", StringComparison.OrdinalIgnoreCase)) &&
            targetBars.Any(bar => bar.Rule.RequiresExistingRebarShape) &&
            !new FilteredElementCollector(_document).OfClass(typeof(RebarShape)).Any())
        {
            issues.Add(new ValidationIssue(
                "REBAR_SHAPE_CATALOG_EMPTY",
                ValidationSeverity.Error,
                "Model không còn RebarShape. Không thể tạo shape-driven Rebar; load/Transfer Project Standards " +
                "Rebar Shapes trước khi tạo.",
                plan.Context.Host.Id));
        }
        var shapeCatalog = new FilteredElementCollector(_document)
            .OfClass(typeof(RebarShape))
            .Cast<RebarShape>()
            .ToList();
        var hookCatalog = new FilteredElementCollector(_document)
            .OfClass(typeof(RebarHookType))
            .Cast<RebarHookType>()
            .ToList();
        foreach (var rule in targetBars
                     .Select(bar => bar.Rule)
                     .DistinctBy(rule => rule.RuleId, StringComparer.OrdinalIgnoreCase))
        {
            if (rule.ShapeStrategy == AbutmentShapeStrategy.ShapeDriven)
            {
                var matches = shapeCatalog.Where(shape =>
                    shape.Name.Equals(
                        rule.ShapeSignature.RebarShapeName,
                        StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count != 1)
                    issues.Add(new ValidationIssue(
                        "ABUTMENT_REBAR_SHAPE_LIBRARY_MISMATCH",
                        ValidationSeverity.Error,
                        $"{rule.RuleId}: RebarShape '{rule.ShapeSignature.RebarShapeName}' " +
                        $"phải tồn tại duy nhất; tìm thấy {matches.Count}.",
                        plan.Context.Host.Id));
                else if (!matches[0].RebarStyle.ToString().Equals(
                             rule.ShapeSignature.RebarStyle,
                             StringComparison.OrdinalIgnoreCase))
                    issues.Add(new ValidationIssue(
                        "ABUTMENT_REBAR_SHAPE_STYLE_MISMATCH",
                        ValidationSeverity.Error,
                        $"{rule.RuleId}: RebarShape '{matches[0].Name}' có style " +
                        $"{matches[0].RebarStyle}, khác {rule.ShapeSignature.RebarStyle}.",
                        matches[0].Id, plan.Context.Host.Id));
            }

            foreach (var endIntent in new[] { rule.StartEndGeometry, rule.EndEndGeometry }
                         .Where(intent => intent.Kind == AbutmentEndGeometryKind.Hook))
            {
                var matches = hookCatalog.Count(hook =>
                    hook.Name.Equals(endIntent.HookSpec.TypeName, StringComparison.OrdinalIgnoreCase));
                if (matches != 1)
                    issues.Add(new ValidationIssue(
                        "ABUTMENT_REBAR_HOOK_LIBRARY_MISMATCH",
                        ValidationSeverity.Error,
                        $"{rule.RuleId}: RebarHookType '{endIntent.HookSpec.TypeName}' " +
                        $"phải tồn tại duy nhất; tìm thấy {matches}.",
                        plan.Context.Host.Id));
            }
        }
        var barTypeCatalog = new RebarTypeCatalog(_document);
        foreach (var bar in targetBars
                     .GroupBy(item => new
                     {
                         item.Rule.RuleId,
                         item.ResolvedBarTypeId,
                         item.ResolvedBarTypeName
                     })
                     .Select(group => group.First()))
        {
            if (!barTypeCatalog.TryGet(bar.ResolvedBarTypeName, out var currentType) ||
                currentType.Id != bar.ResolvedBarTypeId)
            {
                issues.Add(new ValidationIssue(
                    "ABUTMENT_REBAR_TYPE_CHANGED",
                    ValidationSeverity.Error,
                    $"RebarBarType đã review '{bar.ResolvedBarTypeName}' không còn khớp model.",
                    plan.Context.Host.Id));
                continue;
            }
            var gradeAssessment = AbutmentBarTypeGradeKernel.Assess(
                currentType.MinimumYieldStrengthMpa,
                bar.Rule.MinimumYieldStrengthMpa);
            if (gradeAssessment == AbutmentBarTypeGradeAssessment.MetadataMissing)
            {
                issues.Add(new ValidationIssue(
                    "ABUTMENT_REBAR_GRADE_METADATA_MISSING",
                    ValidationSeverity.Error,
                    $"{bar.Rule.RuleId}: RebarBarType '{currentType.Name}' không có " +
                    $"Material/Structural Asset nên không đọc được fy; rule yêu cầu mác " +
                    $"{bar.Rule.RequiredBarGrade} với fy ≥ {bar.Rule.MinimumYieldStrengthMpa:0.#} MPa. " +
                    "Gán Structural Asset cho RebarBarType này trong Revit rồi Phân tích lại.",
                    currentType.Id, plan.Context.Host.Id));
            }
            else if (gradeAssessment == AbutmentBarTypeGradeAssessment.BelowRequired)
            {
                issues.Add(new ValidationIssue(
                    "ABUTMENT_REBAR_GRADE_MISMATCH",
                    ValidationSeverity.Error,
                    $"RebarBarType '{currentType.Name}' có fy {currentType.MinimumYieldStrengthMpa:0.#} MPa; " +
                    $"rule yêu cầu mác {bar.Rule.RequiredBarGrade}, fy ≥ " +
                    $"{bar.Rule.MinimumYieldStrengthMpa:0.#} MPa.",
                    currentType.Id, plan.Context.Host.Id));
            }
        }
        foreach (var bar in targetBars)
        {
            if (bar.Points.Count < 2)
                issues.Add(new ValidationIssue("ABUTMENT_BAR_POINTS_INVALID", ValidationSeverity.Error,
                    $"Rule '{bar.Key}' has fewer than 2 centerline points.", plan.Context.Host.Id));
            else if (!IsCoplanar(bar.Points, bar.PlaneNormal))
                issues.Add(new ValidationIssue("ABUTMENT_BAR_NOT_COPLANAR", ValidationSeverity.Error,
                    $"Rule '{bar.Key}' centerline is not coplanar with its layout normal.", plan.Context.Host.Id));
        }
        foreach (var hostGroup in targetBars.GroupBy(bar =>
                     IntendedCreateHost(plan, bar)?.Id ?? bar.HostId))
        {
            var sample = hostGroup.First();
            var hostElement = IntendedCreateHost(plan, sample);
            if (hostElement is null)
            {
                issues.Add(new ValidationIssue(
                    "ABUTMENT_REBAR_HOST_INVALID",
                    ValidationSeverity.Error,
                    $"Host id {sample.HostId} của {hostGroup.Count()} thanh không còn tồn tại.",
                    plan.Context.Host.Id));
                continue;
            }

            if (RebarHostData.IsValidHost(hostElement)) continue;
            var capability = RebarHostData.GetRebarHostData(hostElement) is null
                ? "Category này không hỗ trợ reinforcement."
                : "Family có dữ liệu rebar host nhưng instance hiện tại chưa hợp lệ; kiểm tra Can Host Rebar và vật liệu bê tông.";
            issues.Add(new ValidationIssue(
                "ABUTMENT_REBAR_HOST_INVALID",
                ValidationSeverity.Error,
                $"Host '{hostElement.Name}' ({hostElement.Id}) không thể host {hostGroup.Count()} thanh. {capability}",
                hostElement.Id, plan.Context.Host.Id));
        }

        var duplicates = existingOwned
            .GroupBy(rebar => AbutmentMetadataStore.TryRead(rebar, out var value) ? value.RuleId : string.Empty)
            .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1);
        foreach (var duplicate in duplicates)
            issues.Add(new ValidationIssue("ABUTMENT_EXISTING_MANAGED_DUPLICATE", ValidationSeverity.Error,
                $"Existing managed key '{duplicate.Key}' contains {duplicate.Count()} Rebar elements.",
                duplicate.Select(item => item.Id).ToArray()));

        if (existingOwned.Count > 0)
        {
            var untouchedMetadata = existingOwned
                .Select(rebar => (Rebar: rebar, Read: AbutmentMetadataStore.TryRead(rebar, out var metadata), Metadata: metadata))
                .Where(item => item.Read &&
                               !item.Metadata.HostRole.Equals(
                                   targetZone.ToString(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            var stale = untouchedMetadata.Where(item =>
                !item.Metadata.RuleHash.Equals(plan.Preset.RuleHash, StringComparison.OrdinalIgnoreCase) ||
                !item.Metadata.GeometryHash.Equals(plan.GeometryHash, StringComparison.OrdinalIgnoreCase)).ToList();
            if (stale.Count > 0)
                issues.Add(new ValidationIssue("ABUTMENT_ZONE_OPERATION_STALE_ASSEMBLY", ValidationSeverity.Error,
                    "Không thể thay đổi riêng khu vực khi geometry/rule hash của vùng giữ lại đã thay đổi.",
                    stale.Select(item => item.Rebar.Id).ToArray()));

            var validUntouchedKeys = plan.Bars.Where(bar => bar.Zone != targetZone)
                .Select(bar => bar.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = untouchedMetadata
                .Where(item => !validUntouchedKeys.Contains(item.Metadata.RuleId))
                .ToList();
            if (unknown.Count > 0)
                issues.Add(new ValidationIssue("ABUTMENT_ZONE_OPERATION_UNKNOWN_REBAR", ValidationSeverity.Error,
                    "Một hoặc nhiều Rebar của khu vực giữ lại không còn thuộc plan hiện hành.",
                    unknown.Select(item => item.Rebar.Id).ToArray()));
        }
        return issues;
    }


    /// <summary>
    /// Resolves, for every bar about to be created, the figures a bar bending schedule and a yard
    /// cutting list are rebuilt from. It runs before the transaction opens so a layer whose mass or
    /// tag cannot be resolved blocks Create instead of producing bars that read back as
    /// unschedulable. Everything here comes from the plan and the standard block; nothing is
    /// invented per bar.
    /// </summary>
    private static bool TryBuildScheduleFacts(
        AbutmentRebarPlan plan,
        IReadOnlyList<PlannedAbutmentRebar> targetBars,
        out IReadOnlyDictionary<string, AbutmentBarScheduleFacts> factsByKey,
        out string error)
    {
        var facts = new Dictionary<string, AbutmentBarScheduleFacts>(StringComparer.OrdinalIgnoreCase);
        factsByKey = facts;
        error = string.Empty;
        if (targetBars.Count == 0) return true;

        var layers = targetBars
            .GroupBy(bar => bar.Rule.RuleId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().Rule)
            .ToList();
        if (!AbutmentRebarTagKernel.TryResolveMarkLabels(
                layers
                    .Select(rule => new AbutmentTagMarkInput
                    {
                        RuleId = rule.RuleId,
                        DrawingMark = rule.SourceDrawingMark,
                        CanonicalMark = rule.CanonicalMark
                    })
                    .ToList(),
                out var markLabels,
                out error))
            return false;

        // The bar count a tag states is the number of bar positions this plan actually lays out,
        // not a number re-read from the preset: the tag has to describe the model it is written on.
        var positionCounts = targetBars
            .GroupBy(bar => bar.Rule.RuleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(bar => bar.PositionIndex).Distinct().Count(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var planned in targetBars)
        {
            var rule = planned.Rule;
            if (!plan.Preset.TryResolveStandardParameters(rule, out var standard, out var standardError))
            {
                error = $"{rule.RuleId}: không tra được khối tham số tiêu chuẩn nên không tính được " +
                        $"khối lượng cho bảng thống kê — {standardError}";
                return false;
            }
            if (!AbutmentBarMassKernel.TryUnitMassKgPerMetre(
                    rule.DiameterMm, standard.SteelDensityKgPerM3, out var unitMass, out var massError))
            {
                error = $"{rule.RuleId}: {massError}";
                return false;
            }
            if (!AbutmentBarMassKernel.TryMassKg(
                    unitMass, planned.PieceLengthMm, out var massKg, out massError))
            {
                error = $"{planned.Key}: {massError}";
                return false;
            }
            // Steel of the whole bar position: the finished length plus what each lap overlaps.
            // Written identically to every piece of the position so a schedule filtered to piece
            // one lists finished bars and still totals the steel the yard cuts.
            var barTotalLengthMm =
                planned.PositionLengthMm + (planned.PieceCount - 1) * planned.LapLengthMm;
            if (!AbutmentBarMassKernel.TryMassKg(
                    unitMass, barTotalLengthMm, out var barMassKg, out massError))
            {
                error = $"{planned.Key}: {massError}";
                return false;
            }
            if (!AbutmentRebarTagKernel.TryComposeTagText(
                    new AbutmentRebarTagRequest
                    {
                        MarkLabel = markLabels[rule.RuleId],
                        BarPositionCount = positionCounts[rule.RuleId],
                        BarDiameterMm = rule.DiameterMm,
                        DimensionedPitchMm = rule.SpacingMm > 0 ? rule.SpacingMm : null,
                        PieceIndex = planned.PieceIndex,
                        PieceCount = planned.PieceCount
                    },
                    out var tagText,
                    out var tagError))
            {
                error = $"{planned.Key}: {tagError}";
                return false;
            }

            facts[planned.Key] = new AbutmentBarScheduleFacts(
                planned.PositionIndex,
                planned.PieceIndex,
                planned.PieceCount - 1,
                ResolveBendRadiusMm(rule),
                planned.PositionLengthMm,
                barTotalLengthMm,
                planned.PieceLengthMm,
                planned.LapLengthMm,
                unitMass,
                massKg,
                barMassKg,
                tagText);
        }
        return true;
    }

    /// <summary>
    /// Resolves the measurement block of every bar about to be created, once, before the transaction
    /// opens. The same instance is then written into the bar's hidden evidence and into its receipt
    /// line, which is what makes "the receipt agrees with the model" true by construction rather
    /// than by two computations happening to agree.
    /// </summary>
    private static bool TryBuildPieceFacts(
        IReadOnlyList<PlannedAbutmentRebar> targetBars,
        out IReadOnlyDictionary<string, AbutmentBarPieceFacts> factsByKey,
        out string error)
    {
        var facts = new Dictionary<string, AbutmentBarPieceFacts>(StringComparer.OrdinalIgnoreCase);
        factsByKey = facts;
        error = string.Empty;
        foreach (var planned in targetBars)
        {
            if (!AbutmentMetadataStore.TryResolvePieceFacts(planned, out var piece, out var pieceError))
            {
                error = $"{planned.Key}: {pieceError}";
                return false;
            }
            facts[planned.Key] = piece;
        }
        return true;
    }

    /// <summary>
    /// Rolls the run up into one row per layer for the receipt. Built from the very records the bars
    /// are written from, and through the same kernels the bar bending schedule and the cutting list
    /// use, so the receipt cannot quote a quantity those two tables would later contradict. It also
    /// means the reconciliation gate between the two tables now fires before Create commits.
    /// </summary>
    private static bool TryBuildLayerSummary(
        IReadOnlyList<PlannedAbutmentRebar> targetBars,
        IReadOnlyDictionary<string, AbutmentBarScheduleFacts> scheduleFacts,
        IReadOnlyDictionary<string, AbutmentBarPieceFacts> pieceFacts,
        out IReadOnlyList<AbutmentReceiptLayerSummary> layers,
        out AbutmentReceiptTotals? totals,
        out string error)
    {
        layers = [];
        totals = null;
        var rows = new List<AbutmentReceiptSummaryRow>(targetBars.Count);
        foreach (var planned in targetBars)
        {
            var schedule = scheduleFacts[planned.Key];
            var piece = pieceFacts[planned.Key];
            rows.Add(new AbutmentReceiptSummaryRow
            {
                RuleId = planned.Rule.RuleId,
                BarTypeInModel = piece.BarTypeInModel,
                Bar = new AbutmentScheduleBarRow
                {
                    ElementLabel = planned.Key,
                    InternalMark = planned.Rule.CanonicalMark,
                    DrawingMark = planned.Rule.SourceDrawingMark,
                    BarDiameterMm = piece.DiameterMm,
                    BendRadiusMm = schedule.BendRadiusMm,
                    BarPositionIndex = piece.PositionIndex,
                    PieceIndex = piece.PieceIndex,
                    SpliceCountPerPosition = piece.SpliceCountPerPosition,
                    CutLengthMm = piece.PieceLengthMm,
                    BarLengthMm = piece.PositionLengthMm,
                    LapLengthMm = piece.LapLengthMm,
                    UnitMassKgPerMetre = schedule.UnitMassKgPerMetre
                }
            });
        }

        if (!AbutmentReceiptSummaryKernel.TryBuild(rows, out layers, out var built, out error))
            return false;
        totals = built;
        return true;
    }

    /// <summary>
    /// Bend radius the schedule column reports: the larger radius the rule details at either end,
    /// and zero for a straight bar. A bar whose ends are declared <c>None</c> has no bend, which is
    /// a real answer rather than a missing one.
    /// </summary>
    private static double ResolveBendRadiusMm(AbutmentZoneRule rule)
    {
        static double Radius(AbutmentEndGeometryIntent intent) =>
            intent.Kind == AbutmentEndGeometryKind.Hook && double.IsFinite(intent.HookSpec.BendRadiusMm)
                ? Math.Max(0, intent.HookSpec.BendRadiusMm)
                : 0;
        return Math.Max(Radius(rule.StartEndGeometry), Radius(rule.EndEndGeometry));
    }

    private Rebar CreateOne(
        AbutmentRebarPlan plan,
        PlannedAbutmentRebar planned,
        string generationId,
        AbutmentBarScheduleFacts scheduleFacts,
        AbutmentBarPieceFacts pieceFacts)
    {
        var host = _document.GetElement(planned.HostId)
                   ?? throw new InvalidOperationException($"Host {planned.HostId} no longer exists.");
        var rebar = CreateGeometry(plan, planned);
        var partition = rebar.get_Parameter(BuiltInParameter.NUMBER_PARTITION_PARAM);
        if (partition is { IsReadOnly: false }) partition.Set($"{AssemblyId(plan)}-{planned.ZoneCode}");
        var scheduleMark = rebar.get_Parameter(BuiltInParameter.REBAR_ELEM_SCHEDULE_MARK);
        if (scheduleMark is { IsReadOnly: false })
            scheduleMark.Set(planned.Rule.SourceDrawingMark);
        var metadata = new AbutmentOwnership(AssemblyId(plan), plan.Context.Host.UniqueId, planned.Zone.ToString(),
            planned.ZoneCode, planned.Key, plan.Preset.RuleVersion, generationId, plan.GeometryHash,
            plan.Preset.RuleHash, plan.Preset.ProfileId,
            AbutmentMetadataStore.EvidenceJson(plan.Preset, planned, pieceFacts),
            DateTime.UtcNow.ToString("O"), ToolVersion);
        AbutmentMetadataStore.WriteRebar(rebar, metadata, planned.Rule.CanonicalMark, scheduleFacts);
        return rebar;
    }

    private Element? IntendedCreateHost(AbutmentRebarPlan plan, PlannedAbutmentRebar planned)
    {
        if (plan.Preset.Topology.HostAssemblyStrategy == AbutmentHostAssemblyStrategy.SingleHost ||
            plan.Context.ComponentHosts.Count <= 1)
        {
            return plan.Context.Host;
        }

        return _document.GetElement(planned.HostId);
    }

    private Element ResolveCreateHost(AbutmentRebarPlan plan, PlannedAbutmentRebar planned)
    {
        var primary = plan.Context.Host
                      ?? throw new InvalidOperationException("Plan primary host is null.");
        var host = IntendedCreateHost(plan, planned)
                   ?? throw new InvalidOperationException(
                       $"Host {planned.HostId} no longer exists for '{planned.Key}'.");
        var isValidHost = RebarHostData.IsValidHost(host);
        var hostData = RebarHostData.GetRebarHostData(host);

        AbutmentDiagnostics.LogMessage(
            "ABUTMENT_CREATE_HOST_RESOLVE",
            $"key={planned.Key}; plannedHost={planned.HostId}; primary={primary.Id}; " +
            $"use={host.Id}; name='{host.Name}'; category='{host.Category?.Name}'; " +
            $"rebarHostData={(hostData is null ? "NULL" : "PRESENT")}; isValidHost={isValidHost}; " +
            $"strategy={plan.Preset.Topology.HostAssemblyStrategy}; components={plan.Context.ComponentHosts.Count}");

        if (!isValidHost)
        {
            var capability = hostData is null
                ? "Category cannot host reinforcement."
                : "Rebar host data exists, but IsValidHost=false. Check Can Host Rebar and concrete material.";
            throw new InvalidOperationException(
                $"Host is not a valid rebar host (use={host.Id}, planned={planned.HostId}, " +
                $"primary={primary.Id}, key={planned.Key}). {capability}");
        }

        return host;
    }

    private Rebar CreateGeometry(AbutmentRebarPlan plan, PlannedAbutmentRebar planned)
    {
        var host = ResolveCreateHost(plan, planned);
        if (_document.GetElement(planned.ResolvedBarTypeId) is not RebarBarType type ||
            !type.Name.Equals(planned.ResolvedBarTypeName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Rebar type đã review '{planned.ResolvedBarTypeName}' ({planned.ResolvedBarTypeId}) không còn hợp lệ.");
        var diameterMm = UnitUtils.ConvertFromInternalUnits(type.BarModelDiameter, UnitTypeId.Millimeters);
        if (Math.Abs(diameterMm - planned.Rule.DiameterMm) > 0.5)
            throw new InvalidOperationException(
                $"Rebar type '{type.Name}' đã đổi đường kính sau Review.");

        var curves = planned.Points.Zip(planned.Points.Skip(1),
            (start, end) => (Curve)Line.CreateBound(start, end)).ToList();
        Rebar? rebar;
        try
        {
            rebar = planned.Rule.ShapeStrategy switch
            {
                AbutmentShapeStrategy.ShapeDriven => RebarApiAdapter.CreateFromCurves(
                    _document,
                    type,
                    ParseRebarStyle(planned.Rule.ShapeSignature.RebarStyle),
                    host,
                    planned.PlaneNormal,
                    curves,
                    planned.Rule.StartEndGeometry,
                    planned.Rule.EndEndGeometry,
                    createNewShape: planned.Rule.ShapeSignature.CreateNewShape),
                AbutmentShapeStrategy.CurveDrivenFreeForm =>
                    RebarApiAdapter.CreateStraightFreeForm(_document, type, host, curves),
                _ => throw new InvalidOperationException(
                    $"Shape strategy '{planned.Rule.ShapeStrategy}' chưa được runtime hỗ trợ.")
            };
        }
        catch (Exception ex)
        {
            AbutmentDiagnostics.LogException("ABUTMENT_CREATE_GEOMETRY_FAILED", ex);
            throw new InvalidOperationException(
                $"Create geometry failed for '{planned.Key}' on host {host.Id} ('{host.Name}', cat={host.Category?.Name}). " +
                $"strategy={planned.Rule.ShapeStrategy}; type={type.Name}; points={planned.Points.Count}; " +
                $"normal={planned.PlaneNormal}; api={ex.GetBaseException().Message}", ex);
        }
        if (rebar is null)
            throw new InvalidOperationException(
                $"Không tạo được Rebar cho '{planned.Key}' bằng strategy {planned.Rule.ShapeStrategy}. " +
                "Với ShapeDriven, kiểm tra RebarShape sẵn có có đủ tham số để Revit khớp/tạo shape.");
        if (planned.DistributionRepresentation == AbutmentDistributionRepresentation.ExplicitStations)
            return rebar;

        using var accessor = rebar.GetShapeDrivenAccessor();
        if (planned.Rule.LayoutRule == AbutmentLayoutRule.FixedNumber)
            accessor.SetLayoutAsFixedNumber(planned.Rule.FixedNumber, planned.DistributionLength, true,
                planned.Rule.IncludeFirst, planned.Rule.IncludeLast);
        else
            accessor.SetLayoutAsMaximumSpacing(planned.Spacing, planned.DistributionLength, true,
                planned.Rule.IncludeFirst, planned.Rule.IncludeLast);
        return rebar;
    }

    private IReadOnlyList<ValidationIssue> Readback(AbutmentRebarPlan plan,
        IReadOnlyCollection<(PlannedAbutmentRebar Planned, Rebar Rebar)> created)
    {
        var issues = new List<ValidationIssue>();
        if (created.Count == 0)
            issues.Add(new ValidationIssue("ABUTMENT_READBACK_EMPTY", ValidationSeverity.Error,
                "No Rebar element was created."));

        foreach (var item in created)
        {
            if (_document.GetElement(item.Rebar.Id) is not Rebar current)
            {
                issues.Add(new ValidationIssue("ABUTMENT_READBACK_MISSING", ValidationSeverity.Error,
                    $"Cannot read back '{item.Planned.Key}'."));
                continue;
            }

            if (current.GetHostId() != item.Planned.HostId)
                issues.Add(new ValidationIssue("ABUTMENT_READBACK_HOST", ValidationSeverity.Error,
                    $"Host mismatch for '{item.Planned.Key}'.", current.Id));

            if (current.GetTypeId() != item.Planned.ResolvedBarTypeId)
                issues.Add(new ValidationIssue("ABUTMENT_READBACK_BAR_TYPE", ValidationSeverity.Error,
                    $"Bar type mismatch for '{item.Planned.Key}'.", current.Id));
            var scheduleMark = current.get_Parameter(BuiltInParameter.REBAR_ELEM_SCHEDULE_MARK)?.AsString();
            if (!string.Equals(
                    scheduleMark,
                    item.Planned.Rule.SourceDrawingMark,
                    StringComparison.OrdinalIgnoreCase))
                issues.Add(new ValidationIssue(
                    "ABUTMENT_READBACK_SOURCE_DRAWING_MARK",
                    ValidationSeverity.Error,
                    $"Schedule Mark '{scheduleMark ?? "<none>"}' khác sourceDrawingMark " +
                    $"'{item.Planned.Rule.SourceDrawingMark}' của reviewed plan.",
                    current.Id));
            var canonicalMark = current.LookupParameter("DVB_CanonicalMark")?.AsString();
            if (!string.Equals(
                    canonicalMark,
                    item.Planned.Rule.CanonicalMark,
                    StringComparison.OrdinalIgnoreCase))
                issues.Add(new ValidationIssue(
                    "ABUTMENT_READBACK_CANONICAL_MARK",
                    ValidationSeverity.Error,
                    $"DVB_CanonicalMark '{canonicalMark ?? "<none>"}' khác canonicalMark " +
                    $"'{item.Planned.Rule.CanonicalMark}' của reviewed plan.",
                    current.Id));
            if (item.Planned.Rule.ShapeStrategy == AbutmentShapeStrategy.ShapeDriven)
                issues.AddRange(ValidateShapeSignature(item.Planned.Rule, current));

            issues.AddRange(ValidateCenterline(plan, item.Planned, current));

            if (!AbutmentMetadataStore.TryRead(current, out var metadata) ||
                !metadata.RuleId.Equals(item.Planned.Key, StringComparison.OrdinalIgnoreCase) ||
                !metadata.RuleHash.Equals(plan.Preset.RuleHash, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ValidationIssue("ABUTMENT_READBACK_METADATA", ValidationSeverity.Error,
                    $"Lineage metadata mismatch for '{item.Planned.Key}'.", current.Id));
            }

            var missing = AbutmentMetadataStore.MissingVisibleParameters(current);
            if (missing.Count > 0)
                issues.Add(new ValidationIssue("ABUTMENT_READBACK_VISIBLE_METADATA", ValidationSeverity.Error,
                    $"Missing visible parameters: {string.Join(", ", missing)}.", current.Id));

            // Only bars this run created are graded on the scheduling parameters. Bars written by a
            // build that predates them are not in this collection and stay untouched; a schedule
            // built over them refuses by naming them instead of quietly totalling blanks.
            var missingSchedule = AbutmentMetadataStore.MissingScheduleParameters(current);
            if (missingSchedule.Count > 0)
                issues.Add(new ValidationIssue(
                    "ABUTMENT_READBACK_SCHEDULE_METADATA",
                    ValidationSeverity.Error,
                    $"'{item.Planned.Key}' thiếu tham số thống kê: {string.Join(", ", missingSchedule)}.",
                    current.Id));


            if (item.Planned.DistributionRepresentation == AbutmentDistributionRepresentation.NativeRebarSet &&
                current.NumberOfBarPositions != item.Planned.PlannedQuantity)
                issues.Add(new ValidationIssue("ABUTMENT_READBACK_SET_QUANTITY", ValidationSeverity.Error,
                    $"Set quantity '{item.Planned.Key}': plan {item.Planned.PlannedQuantity}, Revit {current.NumberOfBarPositions}.",
                    current.Id));
            if (item.Planned.DistributionRepresentation == AbutmentDistributionRepresentation.ExplicitStations &&
                current.NumberOfBarPositions != 1)
                issues.Add(new ValidationIssue("ABUTMENT_READBACK_EXPLICIT_QUANTITY", ValidationSeverity.Error,
                    $"Explicit station '{item.Planned.Key}' phải có đúng một vị trí Rebar.", current.Id));

            issues.AddRange(ValidateRebarInsideHost(plan.Context, current, item.Planned));
        }
        foreach (var group in created
                     .Where(item => item.Planned.Rule.LayoutRule == AbutmentLayoutRule.StationSchedule)
                     .GroupBy(item => item.Planned.Rule.RuleId, StringComparer.OrdinalIgnoreCase))
            issues.AddRange(ValidateStationPositionsAndPieces(group.Key, group.ToList()));
        issues.AddRange(ValidateActualMatLayerSeparation(plan, created));

        return issues;
    }

    /// <summary>
    /// Grades a station-scheduled rule on both levels the drawing and the yard use. The layer has to
    /// carry every bar position the drawing states, and every position has to carry exactly the
    /// pieces it was planned from, overlapping by at least its lap. Counting rebar objects alone
    /// would pass a spliced layer whose pieces landed on the wrong positions, and counting positions
    /// alone would pass one missing half of every bar.
    ///
    /// A rule that declares no splice has one piece per position, so the position count is the rebar
    /// count and the message is the one this gate always produced.
    /// </summary>
    private static IReadOnlyList<ValidationIssue> ValidateStationPositionsAndPieces(
        string ruleId,
        IReadOnlyList<(PlannedAbutmentRebar Planned, Rebar Rebar)> created)
    {
        var issues = new List<ValidationIssue>();
        var rule = created[0].Planned.Rule;
        var expected = rule.StationSchedule.ExpectedBarCount;
        var piecesPerPosition = rule.PiecesPerBarPosition;
        var positionCount = created.Select(item => item.Planned.PositionIndex).Distinct().Count();
        if (expected <= 0 || positionCount != expected)
        {
            issues.Add(new ValidationIssue(
                "ABUTMENT_READBACK_STATION_COUNT",
                ValidationSeverity.Error,
                piecesPerPosition > 1
                    ? $"{ruleId}: plan yêu cầu {expected} vị trí thanh, readback có {positionCount} vị trí " +
                      $"trên {created.Count} Rebar."
                    : $"{ruleId}: plan yêu cầu {expected} explicit station, readback có {created.Count} Rebar."));
            return issues;
        }

        var measured = new List<AbutmentMeasuredBarPiece>(created.Count);
        foreach (var item in created)
        {
            // A single-piece position has no overlap to measure, and its path may be a polyline whose
            // two ends do not define an axis at all. Only a spliced position is projected.
            if (piecesPerPosition == 1)
            {
                measured.Add(new AbutmentMeasuredBarPiece(item.Planned.PositionIndex, 0, 0));
                continue;
            }
            if (!TryMeasurePieceAlongPosition(item.Planned, item.Rebar, out var piece, out var error))
            {
                issues.Add(new ValidationIssue(
                    "ABUTMENT_READBACK_PIECE_UNMEASURABLE",
                    ValidationSeverity.Error,
                    $"{item.Planned.Key}: {error}",
                    item.Rebar.Id));
                return issues;
            }
            measured.Add(piece);
        }

        var lapLengthMm = created.Max(item => item.Planned.LapLengthMm);
        if (!AbutmentBarPieceKernel.TryVerifyMeasuredPositions(
                expected, piecesPerPosition, lapLengthMm, measured, PieceOverlapToleranceMm,
                out var verifyError))
            issues.Add(new ValidationIssue(
                "ABUTMENT_READBACK_BAR_PIECE",
                ValidationSeverity.Error,
                $"{ruleId}: {verifyError}"));
        return issues;
    }

    /// <summary>
    /// Millimeters two pieces may fall short of their planned overlap and still be accepted. Same
    /// order as the centerline readback tolerance, because both grade Revit's own rounding of an
    /// endpoint and neither may absorb a real detailing error.
    /// </summary>
    private const double PieceOverlapToleranceMm = 2;

    /// <summary>
    /// Projects one created Rebar onto the axis of the bar position it belongs to, so two pieces of
    /// one position become two intervals on one number line and their overlap is a subtraction. The
    /// axis is the planned position's own direction, measured from the planned start of piece one.
    /// </summary>
    private static bool TryMeasurePieceAlongPosition(
        PlannedAbutmentRebar planned,
        Rebar rebar,
        out AbutmentMeasuredBarPiece piece,
        out string error)
    {
        piece = default;
        error = string.Empty;
        IList<Curve> curves;
        try
        {
            curves = rebar.GetCenterlineCurves(
                false, true, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
        }
        catch (Exception exception)
        {
            error = $"không đọc được centerline để đo đoạn — {exception.Message}";
            return false;
        }
        if (curves.Count == 0)
        {
            error = "centerline rỗng nên không đo được vị trí đoạn trên thanh.";
            return false;
        }

        var span = planned.Points[^1] - planned.Points[0];
        if (span.GetLength() <= 1e-12)
        {
            error = "đoạn thép có hai đầu trùng nhau nên không xác định được trục đo.";
            return false;
        }
        var direction = span.Normalize();
        // Piece two starts inside the position, so the position origin is walked back from its own
        // planned start; every piece of one position is then measured from that same point.
        var origin = planned.PieceIndex == 1
            ? planned.Points[0]
            : planned.Points[0] - direction * UnitUtils.ConvertToInternalUnits(
                planned.PositionLengthMm - planned.PieceLengthMm, UnitTypeId.Millimeters);

        var actualStart = curves[0].GetEndPoint(0);
        var actualEnd = curves[^1].GetEndPoint(1);
        var first = UnitUtils.ConvertFromInternalUnits(
            (actualStart - origin).DotProduct(direction), UnitTypeId.Millimeters);
        var second = UnitUtils.ConvertFromInternalUnits(
            (actualEnd - origin).DotProduct(direction), UnitTypeId.Millimeters);
        piece = new AbutmentMeasuredBarPiece(
            planned.PositionIndex, Math.Min(first, second), Math.Max(first, second));
        return true;
    }

    private static IReadOnlyList<ValidationIssue> ValidateCenterline(
        AbutmentRebarPlan plan,
        PlannedAbutmentRebar planned,
        Rebar current)
    {
        var issues = new List<ValidationIssue>();
        IList<Curve> curves;
        try
        {
            curves = current.GetCenterlineCurves(
                false, true, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
        }
        catch (Exception exception)
        {
            issues.Add(new ValidationIssue("ABUTMENT_READBACK_CENTERLINE_UNAVAILABLE",
                ValidationSeverity.Error,
                $"Không đọc được centerline '{planned.Key}': {exception.Message}", current.Id));
            return issues;
        }
        if (planned.Rule.ShapeStrategy == AbutmentShapeStrategy.CurveDrivenFreeForm)
        {
            using var accessor = current.GetFreeFormAccessor();
            if (accessor.WorkshopInstructions != RebarWorkInstructions.Straight)
                issues.Add(new ValidationIssue(
                    "ABUTMENT_READBACK_WORKSHOP_INSTRUCTIONS",
                    ValidationSeverity.Error,
                    $"Free-form Rebar '{planned.Key}' không giữ workshop instruction Straight.",
                    current.Id));
        }
        else
        {
            issues.AddRange(ValidateHookIntent(planned.Rule.StartEndGeometry, current, 0, "đầu"));
            issues.AddRange(ValidateHookIntent(planned.Rule.EndEndGeometry, current, 1, "cuối"));
        }

        if (curves.Count != planned.Points.Count - 1)
        {
            issues.Add(new ValidationIssue("ABUTMENT_READBACK_CENTERLINE_SEGMENTS",
                ValidationSeverity.Error,
                $"Centerline '{planned.Key}' có {curves.Count} segment; plan có {planned.Points.Count - 1}.",
                current.Id));
            return issues;
        }

        var actual = new List<XYZ> { curves[0].GetEndPoint(0) };
        actual.AddRange(curves.Select(curve => curve.GetEndPoint(1)));

        const double lateralToleranceMm = 2;
        const double extensionToleranceMm = 2;
        var lateralTolerance = UnitUtils.ConvertToInternalUnits(
            lateralToleranceMm, UnitTypeId.Millimeters);
        var extensionTolerance = UnitUtils.ConvertToInternalUnits(
            extensionToleranceMm, UnitTypeId.Millimeters);
        var shorteningToleranceMm =
            plan.Preset.Topology.RevitEndpointShorteningToleranceMm;
        var shorteningTolerance = UnitUtils.ConvertToInternalUnits(
            shorteningToleranceMm, UnitTypeId.Millimeters);

        if (planned.Points.Count == 2 && actual.Count == 2)
        {
            var comparison = AbutmentCenterlineReadbackKernel.CompareStraightBar(
                ToKernelPoint(planned.Points[0]),
                ToKernelPoint(planned.Points[1]),
                ToKernelPoint(actual[0]),
                ToKernelPoint(actual[1]),
                lateralTolerance,
                shorteningTolerance,
                extensionTolerance);
            if (!comparison.Accepted)
            {
                issues.Add(new ValidationIssue("ABUTMENT_READBACK_CENTERLINE_MISMATCH",
                    ValidationSeverity.Error,
                    $"Centerline '{planned.Key}' không đạt kiểm tra sau tạo. " +
                    $"Lateral start/end={Mm(comparison.StartLateralOffset):0.###}/" +
                    $"{Mm(comparison.EndLateralOffset):0.###} mm; " +
                    $"shorten start/end={Mm(comparison.StartShortening):0.###}/" +
                    $"{Mm(comparison.EndShortening):0.###} mm; " +
                    $"extend start/end={Mm(comparison.StartExtension):0.###}/" +
                    $"{Mm(comparison.EndExtension):0.###} mm. " +
                    $"Giới hạn: lateral {lateralToleranceMm:0.###} mm, " +
                    $"shorten {shorteningToleranceMm:0.###} mm, extend {extensionToleranceMm:0.###} mm.",
                    current.Id));
                return issues;
            }

            return issues;
        }

        var tolerance = lateralTolerance;
        var forward = actual.Zip(planned.Points, (a, b) => a.DistanceTo(b)).All(distance => distance <= tolerance);
        var reverse = actual.Zip(planned.Points.Reverse(), (a, b) => a.DistanceTo(b)).All(distance => distance <= tolerance);
        if (!forward && !reverse)
            issues.Add(new ValidationIssue("ABUTMENT_READBACK_CENTERLINE_MISMATCH",
                ValidationSeverity.Error,
                $"Centerline '{planned.Key}' khác reviewed plan quá 2 mm.", current.Id));
        return issues;

        static CenterlinePoint3 ToKernelPoint(XYZ point) => new(point.X, point.Y, point.Z);
        static double Mm(double value) => double.IsFinite(value)
            ? UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters)
            : double.PositiveInfinity;
    }

#pragma warning disable CS0618
    private static IReadOnlyList<ValidationIssue> ValidateHookIntent(
        AbutmentEndGeometryIntent intent,
        Rebar current,
        int end,
        string label)
    {
        var issues = new List<ValidationIssue>();
        var actualId = current.GetHookTypeId(end);
        if (intent.Kind == AbutmentEndGeometryKind.None)
        {
            if (actualId != ElementId.InvalidElementId)
                issues.Add(new ValidationIssue(
                    "ABUTMENT_READBACK_UNEXPECTED_HOOK",
                    ValidationSeverity.Error,
                    $"{label} thanh có hook ngoài reviewed plan.",
                    current.Id));
            return issues;
        }

        if (intent.Kind != AbutmentEndGeometryKind.Hook)
        {
            issues.Add(new ValidationIssue(
                "ABUTMENT_READBACK_END_GEOMETRY_UNSUPPORTED",
                ValidationSeverity.Error,
                $"{label} thanh dùng end geometry '{intent.Kind}' không hỗ trợ readback.",
                current.Id));
            return issues;
        }

        var hookSpec = intent.HookSpec;
        if (current.Document.GetElement(actualId) is not RebarHookType hookType ||
            !hookType.Name.Equals(hookSpec.TypeName, StringComparison.OrdinalIgnoreCase))
            issues.Add(new ValidationIssue(
                "ABUTMENT_READBACK_HOOK_TYPE",
                ValidationSeverity.Error,
                $"{label} thanh: plan hook '{hookSpec.TypeName}', Revit hook " +
                $"'{(current.Document.GetElement(actualId) as RebarHookType)?.Name ?? "<none>"}'.",
                current.Id));
#if REVIT2027_OR_GREATER
        var actualOrientation = current.GetTerminationOrientation(end).ToString();
#else
        var actualOrientation = current.GetHookOrientation(end).ToString();
#endif
        if (!actualOrientation.Equals(hookSpec.Orientation, StringComparison.OrdinalIgnoreCase))
            issues.Add(new ValidationIssue(
                "ABUTMENT_READBACK_HOOK_ORIENTATION",
                ValidationSeverity.Error,
                $"{label} thanh: hook orientation khác reviewed plan.",
                current.Id));


        var expectedRotation = hookSpec.RotationDegrees * Math.PI / 180;
#if REVIT2027_OR_GREATER
        var actualRotation = current.GetTerminationRotationAngle(end);
#else
        var actualRotation = current.GetHookRotationAngle(end);
#endif
        if (Math.Abs(expectedRotation - actualRotation) > 1e-8)
            issues.Add(new ValidationIssue(
                "ABUTMENT_READBACK_HOOK_ROTATION",
                ValidationSeverity.Error,
                $"{label} thanh: hook rotation khác reviewed plan.",
                current.Id));
        using var bendData = current.GetBendData();
        var actualAngleDegrees = (end == 0 ? bendData.HookAngle0 : bendData.HookAngle1) *
                                 180 / Math.PI;
        var actualLegLengthMm = UnitUtils.ConvertFromInternalUnits(
            end == 0 ? bendData.HookLength0 : bendData.HookLength1,
            UnitTypeId.Millimeters);
        var actualBendRadiusMm = UnitUtils.ConvertFromInternalUnits(
            bendData.HookBendRadius,
            UnitTypeId.Millimeters);
        if (Math.Abs(actualAngleDegrees - hookSpec.AngleDegrees) > 0.01)
            issues.Add(new ValidationIssue(
                "ABUTMENT_READBACK_HOOK_ANGLE",
                ValidationSeverity.Error,
                $"{label} thanh: hook angle {actualAngleDegrees:0.###}° khác " +
                $"{hookSpec.AngleDegrees:0.###}°.",
                current.Id));
        if (Math.Abs(actualLegLengthMm - hookSpec.LegLengthMm) > 0.5)
            issues.Add(new ValidationIssue(
                "ABUTMENT_READBACK_HOOK_LEG",
                ValidationSeverity.Error,
                $"{label} thanh: hook leg {actualLegLengthMm:0.###} mm khác " +
                $"{hookSpec.LegLengthMm:0.###} mm.",
                current.Id));
        if (Math.Abs(actualBendRadiusMm - hookSpec.BendRadiusMm) > 0.5)
            issues.Add(new ValidationIssue(
                "ABUTMENT_READBACK_HOOK_BEND_RADIUS",
                ValidationSeverity.Error,
                $"{label} thanh: hook bend radius {actualBendRadiusMm:0.###} mm khác " +
                $"{hookSpec.BendRadiusMm:0.###} mm.",
                current.Id));
        return issues;
    }

    private static IReadOnlyList<ValidationIssue> ValidateShapeSignature(
        AbutmentZoneRule rule,
        Rebar current)
    {
        var issues = new List<ValidationIssue>();
        var expected = rule.ShapeSignature;
        RebarShape? shape = null;
        try
        {
            var shapeId = current.get_Parameter(BuiltInParameter.REBAR_SHAPE)?.AsElementId()
                          ?? ElementId.InvalidElementId;
            shape = current.Document.GetElement(shapeId) as RebarShape;
        }
        catch (Exception exception)
        {
            issues.Add(new ValidationIssue(
                "ABUTMENT_READBACK_SHAPE_UNAVAILABLE",
                ValidationSeverity.Error,
                $"Không đọc được RebarShape: {exception.Message}",
                current.Id));
        }
        if (shape is null)
        {
            issues.Add(new ValidationIssue(
                "ABUTMENT_READBACK_SHAPE_MISSING",
                ValidationSeverity.Error,
                $"Rebar không có RebarShape '{expected.RebarShapeName}'.",
                current.Id));
            return issues;
        }
        if (!shape.Name.Equals(expected.RebarShapeName, StringComparison.OrdinalIgnoreCase))
            issues.Add(new ValidationIssue(
                "ABUTMENT_READBACK_SHAPE_NAME",
                ValidationSeverity.Error,
                $"RebarShape '{shape.Name}' khác '{expected.RebarShapeName}'.",
                current.Id, shape.Id));
        if (!shape.RebarStyle.ToString().Equals(expected.RebarStyle, StringComparison.OrdinalIgnoreCase))
            issues.Add(new ValidationIssue(
                "ABUTMENT_READBACK_SHAPE_STYLE",
                ValidationSeverity.Error,
                $"RebarStyle '{shape.RebarStyle}' khác '{expected.RebarStyle}'.",
                current.Id, shape.Id));

        using var accessor = current.GetShapeDrivenAccessor();
        var drivingCurves = accessor.ComputeDrivingCurves();
        if (drivingCurves.Count != expected.SegmentCount)
            issues.Add(new ValidationIssue(
                "ABUTMENT_READBACK_SHAPE_SEGMENT_COUNT",
                ValidationSeverity.Error,
                $"RebarShape có {drivingCurves.Count} segment, reviewed plan có {expected.SegmentCount}.",
                current.Id, shape.Id));
        var actualSignature = ComputeSegmentSignature(drivingCurves);
        if (!actualSignature.Equals(expected.SegmentSignature, StringComparison.OrdinalIgnoreCase))
            issues.Add(new ValidationIssue(
                "ABUTMENT_READBACK_SHAPE_SEGMENT_SIGNATURE",
                ValidationSeverity.Error,
                $"Segment signature '{actualSignature}' khác '{expected.SegmentSignature}'.",
                current.Id, shape.Id));
        return issues;
    }

    private static string ComputeSegmentSignature(IList<Curve> curves) =>
        string.Join("|", curves.Select(curve => curve switch
        {
            Line => $"L:{Mm(curve.Length)}",
            Arc arc => $"A:{Mm(arc.Radius)}:{Degrees(arc.Length / arc.Radius)}",
            _ => $"{curve.GetType().Name}:{Mm(curve.Length)}"
        }));

    private static string Mm(double internalValue) =>
        UnitUtils.ConvertFromInternalUnits(internalValue, UnitTypeId.Millimeters)
            .ToString("0.###", CultureInfo.InvariantCulture);

    private static string Degrees(double radians) =>
        (radians * 180 / Math.PI).ToString("0.###", CultureInfo.InvariantCulture);

    private static RebarStyle ParseRebarStyle(string value) =>
        value.Equals("StirrupTie", StringComparison.OrdinalIgnoreCase)
            ? RebarStyle.StirrupTie
            : RebarStyle.Standard;
#pragma warning restore CS0618


    private static IReadOnlyList<ValidationIssue> ValidateActualMatLayerSeparation(
        AbutmentRebarPlan plan,
        IReadOnlyCollection<(PlannedAbutmentRebar Planned, Rebar Rebar)> created)
    {
        var issues = new List<ValidationIssue>();
        var pairs = new[]
        {
            (AbutmentGeometryTemplate.BottomLongitudinal, AbutmentGeometryTemplate.BottomTransverse),
            (AbutmentGeometryTemplate.TopLongitudinal, AbutmentGeometryTemplate.TopTransverse)
        };
        foreach (var (firstTemplate, secondTemplate) in pairs)
        {
            var first = LayerDepths(firstTemplate);
            var second = LayerDepths(secondTemplate);
            if (first.Count == 0 || second.Count == 0) continue;
            var firstDiameter = created.First(item =>
                item.Planned.Rule.GeometryTemplate == firstTemplate).Planned.Rule.DiameterMm;
            var secondDiameter = created.First(item =>
                item.Planned.Rule.GeometryTemplate == secondTemplate).Planned.Rule.DiameterMm;
            var required = UnitUtils.ConvertToInternalUnits(
                (firstDiameter + secondDiameter) / 2, UnitTypeId.Millimeters);
            var tolerance = UnitUtils.ConvertToInternalUnits(0.5, UnitTypeId.Millimeters);
            var closest = (Distance: double.PositiveInfinity, FirstId: ElementId.InvalidElementId,
                SecondId: ElementId.InvalidElementId);
            foreach (var a in first)
            foreach (var b in second)
            {
                var distance = Math.Abs(a.Depth - b.Depth);
                if (distance < closest.Distance) closest = (distance, a.Id, b.Id);
            }
            if (closest.Distance + tolerance >= required) continue;
            issues.Add(new ValidationIssue("ABUTMENT_READBACK_MAT_LAYER_CLASH",
                ValidationSeverity.Error,
                $"{firstTemplate}/{secondTemplate} actual centerline separation " +
                $"{UnitUtils.ConvertFromInternalUnits(closest.Distance, UnitTypeId.Millimeters):0.#} mm " +
                $"is below the no-overlap minimum " +
                $"{UnitUtils.ConvertFromInternalUnits(required, UnitTypeId.Millimeters):0.#} mm.",
                closest.FirstId, closest.SecondId, plan.Context.Host.Id));
        }
        return issues;

        List<(double Depth, ElementId Id)> LayerDepths(AbutmentGeometryTemplate template)
        {
            var values = new List<(double Depth, ElementId Id)>();
            foreach (var item in created.Where(item =>
                         item.Planned.Rule.GeometryTemplate == template))
            {
                try
                {
                    var curves = item.Rebar.GetCenterlineCurves(
                        false, false, false, MultiplanarOption.IncludeOnlyPlanarCurves, 0);
                    if (curves.Count == 0) continue;
                    var depth = (curves[0].GetEndPoint(0) - plan.Context.Frame.Origin)
                        .DotProduct(plan.Context.Frame.Vertical);
                    values.Add((depth, item.Rebar.Id));
                }
                catch (Autodesk.Revit.Exceptions.InvalidObjectException)
                {
                    // The per-element readback already reports this as an Error.
                }
            }
            return values;
        }
    }

    // Ownership, host containment, pile clearance, and layer separation remain fail-closed.
    private const double ContainmentToleranceMm = 5;

    private IReadOnlyList<ValidationIssue> ValidateRebarInsideHost(AbutmentHostContext context, Rebar rebar,
        PlannedAbutmentRebar planned)
    {
        var issues = new List<ValidationIssue>();
        var tolerance = UnitUtils.ConvertToInternalUnits(ContainmentToleranceMm, UnitTypeId.Millimeters);
        // Solids captured at Analyze become invalid after Create/Regenerate — always re-collect
        // from the exact component host assigned to this planned bar.
        var componentHost = _document.GetElement(planned.HostId);
        var solids = componentHost is null ? [] : CollectHostSolids(componentHost);
        if (solids.Count == 0)
        {
            issues.Add(new ValidationIssue("ABUTMENT_HOST_SOLID_UNAVAILABLE", ValidationSeverity.Error,
                $"Cannot re-collect host solids for containment check of '{planned.Key}'.",
                rebar.Id, planned.HostId, context.Host.Id));
            return issues;
        }

        using var options = new SolidCurveIntersectionOptions();
        for (var position = 0; position < rebar.NumberOfBarPositions; position++)
        {
            if (!rebar.DoesBarExistAtPosition(position)) continue;
            IList<Curve> curves;
            try
            {
                curves = rebar.GetCenterlineCurves(false, false, false,
                    MultiplanarOption.IncludeOnlyPlanarCurves, position);
            }
            catch (Autodesk.Revit.Exceptions.InvalidObjectException)
            {
                issues.Add(new ValidationIssue("ABUTMENT_READBACK_REBAR_INVALID", ValidationSeverity.Error,
                    $"Rebar object invalid while reading '{planned.Key}' pos {position}.", rebar.Id));
                continue;
            }

            foreach (var curve in curves)
            {
                if (curve.Length <= 1e-9) continue;
                var coveredIntervals = new List<(double Start, double End)>();
                foreach (var solid in solids)
                {
                    try
                    {
                        using var result = solid.IntersectWithCurve(curve, options);
                        for (var index = 0; index < result.SegmentCount; index++)
                        {
                            var segment = result.GetCurveSegment(index);
                            var startProjection = curve.Project(segment.GetEndPoint(0));
                            var endProjection = curve.Project(segment.GetEndPoint(1));
                            if (startProjection is null || endProjection is null) continue;
                            var start = curve.ComputeNormalizedParameter(startProjection.Parameter);
                            var end = curve.ComputeNormalizedParameter(endProjection.Parameter);
                            coveredIntervals.Add((Math.Min(start, end), Math.Max(start, end)));
                        }
                    }
                    catch (Autodesk.Revit.Exceptions.InvalidObjectException)
                    {
                        // Another regeneration invalidated this handle: containment must fail below.
                    }
                }

                var uncovered = GeometryKernel.ClipInterval(0, 1, coveredIntervals, 0)
                    .Sum(interval => interval.End - interval.Start);
                var residual = curve.Length * Math.Clamp(uncovered, 0, 1);
                if (residual > tolerance)
                    issues.Add(new ValidationIssue("ABUTMENT_REBAR_OUTSIDE_HOST_ENVELOPE",
                        ValidationSeverity.Error,
                        $"Centerline '{planned.Key}' pos {position} extends " +
                        $"{UnitUtils.ConvertFromInternalUnits(residual, UnitTypeId.Millimeters):0.#} mm " +
                        $"outside current host solids (tolerance {ContainmentToleranceMm:0.#} mm).",
                        rebar.Id, context.Host.Id));
                issues.AddRange(ValidatePileClearance(context, curve, planned, position, rebar.Id));
            }
        }
        return issues;
    }

    private static IReadOnlyList<ValidationIssue> ValidatePileClearance(
        AbutmentHostContext context, Curve curve, PlannedAbutmentRebar planned,
        int position, ElementId rebarId)
    {
        if (context.Piles.Count == 0 ||
            planned.Zone != AbutmentZoneKind.Footing ||
            planned.Rule.PileObstaclePolicy == AbutmentObstaclePolicy.ContinueThroughMonolithicHost)
            return [];
        var issues = new List<ValidationIssue>();
        var barRadius = UnitUtils.ConvertToInternalUnits(
            planned.Rule.DiameterMm / 2, UnitTypeId.Millimeters);
        var tolerance = UnitUtils.ConvertToInternalUnits(1, UnitTypeId.Millimeters);
        var points = curve.Tessellate();
        if (points.Count < 2) points = [curve.GetEndPoint(0), curve.GetEndPoint(1)];
        var barMinZ = points.Min(point => point.Z) - barRadius;
        var barMaxZ = points.Max(point => point.Z) + barRadius;
        foreach (var pile in context.Piles)
        {
            if (!GeometryKernel.OverlapsPileVerticalEnvelope(
                    pile.MinZ, pile.MaxZ, barMinZ, barMaxZ))
                continue;
            var minimumCenterlineDistance = GeometryKernel.PileCenterlineExclusionRadius(
                pile.PileRadius, pile.Clearance, 2 * barRadius);
            var actual = double.PositiveInfinity;
            for (var index = 1; index < points.Count; index++)
                actual = Math.Min(actual, GeometryKernel.DistancePointToSegment(
                    new PlanPoint2(pile.Center.X, pile.Center.Y),
                    new PlanPoint2(points[index - 1].X, points[index - 1].Y),
                    new PlanPoint2(points[index].X, points[index].Y)));
            if (actual + tolerance >= minimumCenterlineDistance) continue;
            var actualFaceClear = actual - pile.PileRadius - barRadius;
            issues.Add(new ValidationIssue("ABUTMENT_REBAR_PILE_CLEARANCE",
                ValidationSeverity.Error,
                $"Centerline '{planned.Key}' pos {position} leaves only " +
                $"{UnitUtils.ConvertFromInternalUnits(actualFaceClear, UnitTypeId.Millimeters):0.#} mm " +
                $"clear to pile {pile.SourceId}; required " +
                $"{UnitUtils.ConvertFromInternalUnits(pile.Clearance, UnitTypeId.Millimeters):0.#} mm.",
                rebarId, pile.SourceId, context.Host.Id));
        }
        return issues;
    }

    private List<Solid> CollectHostSolids(Element host)
    {
        var solids = new List<Solid>();
        var options = new Options
        {
            ComputeReferences = false,
            IncludeNonVisibleObjects = true,
            DetailLevel = ViewDetailLevel.Fine
        };
        CollectSolids(host.get_Geometry(options), Transform.Identity, solids);
        return solids;
    }

    private static void CollectSolids(GeometryElement? geometry, Transform transform, ICollection<Solid> target)
    {
        if (geometry is null) return;
        foreach (var item in geometry)
        {
            switch (item)
            {
                case Solid solid when solid.Volume > 1e-9 && solid.Faces.Size > 0:
                    try { target.Add(transform.IsIdentity ? solid : SolidUtils.CreateTransformed(solid, transform)); }
                    catch (Autodesk.Revit.Exceptions.ArgumentOutOfRangeException) { }
                    catch (Autodesk.Revit.Exceptions.InvalidObjectException) { }
                    break;
                case GeometryInstance instance:
                    CollectSolids(instance.GetSymbolGeometry(), transform.Multiply(instance.Transform), target);
                    break;
            }
        }
    }

    private static bool IsCoplanar(IReadOnlyList<XYZ> points, XYZ normal)
    {
        if (points.Count < 2 || normal.GetLength() < 1e-9) return false;
        var origin = points[0];
        var unit = normal.Normalize();
        return points.All(point => Math.Abs((point - origin).DotProduct(unit)) < 1e-5);
    }

    private static void RunWithConstrainedPlacementDisabled(Action action)
    {
#if REVIT2027_OR_GREATER
        action();
#else
        var constrainedPlacementWasEnabled = RebarConstraintsManager.IsRebarConstrainedPlacementEnabled;
        try
        {
            RebarConstraintsManager.IsRebarConstrainedPlacementEnabled = false;
            action();
        }
        finally
        {
            RebarConstraintsManager.IsRebarConstrainedPlacementEnabled = constrainedPlacementWasEnabled;
        }
#endif
    }





    internal static void PersistAuditReceipt(AbutmentCreateReceipt receipt) =>
        WriteReceiptFile(receipt);

    private static void WriteReceiptFile(AbutmentCreateReceipt receipt, bool required = false)
    {
        string? temporaryPath = null;
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DVB_ADDIN", "Receipts", "Abutment");
            Directory.CreateDirectory(root);
            var path = string.IsNullOrWhiteSpace(receipt.ReceiptPath)
                ? Path.Combine(root, $"{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{receipt.GenerationId}.json")
                : receipt.ReceiptPath;
            temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
            receipt.ReceiptPath = path;
            receipt.ReceiptPersisted = true;
            receipt.ReceiptWriteError = string.Empty;
            var json = JsonSerializer.Serialize(new
            {
                receipt.GenerationId,
                receipt.DocumentPath,
                receipt.DocumentKey,
                receipt.HostUniqueId,
                receipt.RuleHash,
                receipt.GeometryHash,
                receipt.PlanHash,
                receipt.Operation,
                receipt.Succeeded,
                receipt.ReceiptPersisted,
                receipt.ReceiptPath,
                receipt.ReceiptWriteError,
                CreatedIds = receipt.CreatedIds.Select(id => ElementIdCompat.ToLong(id)),
                // Key, ZoneCode and RebarId keep their names and positions: a receipt written by
                // this build still reads to anything that read the identifier-only shape, and the
                // measurements arrive as one added member per line.
                CreatedItems = receipt.CreatedItems.Select(item => new
                {
                    item.Key,
                    item.ZoneCode,
                    RebarId = ElementIdCompat.ToLong(item.RebarId),
                    item.Piece
                }),
                LayerSummary = receipt.LayerSummary,
                receipt.SummaryTotals,
                RemovedIds = receipt.RemovedIds.Select(id => ElementIdCompat.ToLong(id)),
                Issues = receipt.Issues.Select(issue => new { issue.Code, Severity = issue.Severity.ToString(), issue.Message })
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(temporaryPath, json);
            if (File.Exists(path))
                File.Replace(temporaryPath, path, null);
            else
                File.Move(temporaryPath, path);
            temporaryPath = null;
        }
        catch (Exception exception)
        {
            receipt.ReceiptPersisted = false;
            receipt.ReceiptWriteError = exception.Message;
            receipt.Issues.Add(new ValidationIssue(
                "ABUTMENT_RECEIPT_WRITE_FAILED",
                ValidationSeverity.Error,
                $"Không lưu được receipt kiểm toán: {exception.Message}"));
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
                catch
                {
                    // Best-effort cleanup of the uniquely named incomplete receipt only.
                }
            }
            if (required)
                throw new IOException("Receipt kiểm toán là bắt buộc; transaction phải rollback.", exception);
        }
    }

    private AbutmentCreateReceipt NewReceipt(AbutmentRebarPlan plan, AbutmentWorkflowMode mode) => new()
    {
        Operation = mode.ToString(),
        GenerationId = Guid.NewGuid().ToString("N"),
        DocumentPath = _document.PathName,
        DocumentKey = $"{_document.Title}|{_document.PathName}",
        HostUniqueId = plan.Context.Host.UniqueId,
        RuleHash = plan.Preset.RuleHash,
        GeometryHash = plan.GeometryHash,
        PlanHash = plan.PlanHash,
    };
    private static string AssemblyId(AbutmentRebarPlan plan) =>
        $"ABT-{ElementIdCompat.ToLong(plan.Context.Host.Id)}";
}
