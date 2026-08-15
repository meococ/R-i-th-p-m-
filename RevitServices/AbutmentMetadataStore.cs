using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Structure;
using BIM.DatViet.Domain;
using BIM.DatViet.Models;
using BIM.DatViet.Infrastructure;

namespace BIM.DatViet.RevitServices;

public sealed record AbutmentOwnership(
    string AssemblyId,
    string HostUniqueId,
    string HostRole,
    string RebarZone,
    string RuleId,
    string RuleVersion,
    string GenerationId,
    string GeometryHash,
    string RuleHash,
    string ProfileId,
    string SourceEvidenceJson,
    string GeneratedUtc,
    string ToolVersion);

/// <summary>
/// What a bar bending schedule and a yard cutting list need from one rebar object and cannot get
/// from Revit itself. Revit knows a rebar's diameter and its own total length; it does not know
/// which bar position of the drawing this object is one piece of, what the finished bar measures
/// once the lap overlap is removed, or what the bar weighs.
/// </summary>
/// <remarks>
/// Two length/mass pairs, and the difference is the whole reason a Revit schedule can list finished
/// bars at all. <see cref="CutLengthMm"/> and <see cref="MassKg"/> describe <b>this object</b>, so
/// they total correctly across every piece and are what the yard cutting list is built from.
/// <see cref="BarTotalLengthMm"/> and <see cref="BarMassKg"/> describe <b>the whole bar position</b>
/// and are written identically to each of its pieces, so a schedule filtered to piece one shows one
/// row per finished bar and still totals the steel including its laps.
/// </remarks>
public sealed record AbutmentBarScheduleFacts(
    int BarPositionIndex,
    int PieceIndex,
    int SpliceCountPerPosition,
    double BendRadiusMm,
    double BarLengthMm,
    double BarTotalLengthMm,
    double CutLengthMm,
    double LapLengthMm,
    double UnitMassKgPerMetre,
    double MassKg,
    double BarMassKg,
    string TagText);

public static class AbutmentMetadataStore
{
    private static readonly Guid RebarSchemaId = new("213A8D44-6E13-4EBB-9A60-CBCBEEB0DD58");
    private static readonly Guid HostSchemaId = new("6DB09ECA-2B5D-4BCB-8BB4-527462492B56");
    private static readonly string[] SharedParameterNames =
    [
        "DVB_AssemblyId", "DVB_HostRole", "DVB_RebarZone", "DVB_RuleId",
        "DVB_RuleVersion", "DVB_GenerationId", "DVB_Managed", "DVB_CanonicalMark"
    ];

    /// <summary>
    /// The scheduling parameters, kept as a set of their own rather than appended to
    /// <see cref="SharedParameterNames"/>. The lineage set grades every managed bar in the model,
    /// including bars written by a build that predates scheduling; folding the two together would
    /// turn every one of those bars into an error the moment this wave shipped. This set is only
    /// ever asked of bars the current run created, and of bars a schedule is actually being built
    /// from — where the answer has to be refusal, not a table with blanks in it.
    /// </summary>
    private static readonly string[] ScheduleParameterNames =
    [
        "DVB_BarPosition", "DVB_PieceIndex", "DVB_SpliceCount", "DVB_BendRadius", "DVB_BarLength",
        "DVB_BarTotalLength", "DVB_CutLength", "DVB_LapLength", "DVB_UnitMass", "DVB_Mass",
        "DVB_BarMass", "DVB_TagText"
    ];

    /// <summary>Every shared parameter this build expects the runtime parameter file to define.</summary>
    public static IReadOnlyList<string> ExpectedSharedParameterNames =>
        SharedParameterNames.Concat(ScheduleParameterNames).ToArray();

    public static void WriteRebar(
        Rebar rebar,
        AbutmentOwnership value,
        string canonicalMark,
        AbutmentBarScheduleFacts? schedule = null)
    {
        var schema = GetOrCreateRebarSchema();
        var entity = new Entity(schema);
        Set(entity, schema, nameof(value.AssemblyId), value.AssemblyId);
        Set(entity, schema, nameof(value.HostUniqueId), value.HostUniqueId);
        Set(entity, schema, nameof(value.HostRole), value.HostRole);
        Set(entity, schema, nameof(value.RebarZone), value.RebarZone);
        Set(entity, schema, nameof(value.RuleId), value.RuleId);
        Set(entity, schema, nameof(value.RuleVersion), value.RuleVersion);
        Set(entity, schema, nameof(value.GenerationId), value.GenerationId);
        Set(entity, schema, nameof(value.GeometryHash), value.GeometryHash);
        Set(entity, schema, nameof(value.RuleHash), value.RuleHash);
        Set(entity, schema, nameof(value.ProfileId), value.ProfileId);
        Set(entity, schema, nameof(value.SourceEvidenceJson), value.SourceEvidenceJson);
        Set(entity, schema, nameof(value.GeneratedUtc), value.GeneratedUtc);
        Set(entity, schema, nameof(value.ToolVersion), value.ToolVersion);
        rebar.SetEntity(entity);
        WriteVisibleText(rebar, "DVB_AssemblyId", value.AssemblyId);
        WriteVisibleText(rebar, "DVB_HostRole", value.HostRole);
        WriteVisibleText(rebar, "DVB_RebarZone", value.RebarZone);
        WriteVisibleText(rebar, "DVB_RuleId", value.RuleId);
        WriteVisibleText(rebar, "DVB_RuleVersion", value.RuleVersion);
        WriteVisibleText(rebar, "DVB_GenerationId", value.GenerationId);
        WriteVisibleText(rebar, "DVB_Managed", "1");
        WriteVisibleText(rebar, "DVB_CanonicalMark", canonicalMark);
        if (schedule is null) return;
        WriteVisibleInteger(rebar, "DVB_BarPosition", schedule.BarPositionIndex);
        WriteVisibleInteger(rebar, "DVB_PieceIndex", schedule.PieceIndex);
        WriteVisibleInteger(rebar, "DVB_SpliceCount", schedule.SpliceCountPerPosition);
        WriteVisibleLengthMm(rebar, "DVB_BendRadius", schedule.BendRadiusMm);
        WriteVisibleLengthMm(rebar, "DVB_BarLength", schedule.BarLengthMm);
        WriteVisibleLengthMm(rebar, "DVB_BarTotalLength", schedule.BarTotalLengthMm);
        WriteVisibleLengthMm(rebar, "DVB_CutLength", schedule.CutLengthMm);
        WriteVisibleLengthMm(rebar, "DVB_LapLength", schedule.LapLengthMm);
        WriteVisibleNumber(rebar, "DVB_UnitMass", schedule.UnitMassKgPerMetre);
        WriteVisibleNumber(rebar, "DVB_Mass", schedule.MassKg);
        WriteVisibleNumber(rebar, "DVB_BarMass", schedule.BarMassKg);
        WriteVisibleText(rebar, "DVB_TagText", schedule.TagText);
    }

    public static void WriteHostManifest(Element host, string assemblyId, string generationId,
        string geometryHash, string ruleHash, IEnumerable<ElementId> barIds)
    {
        var schema = GetOrCreateHostSchema();
        var entity = new Entity(schema);
        Set(entity, schema, "AssemblyId", assemblyId);
        Set(entity, schema, "HostUniqueId", host.UniqueId);
        Set(entity, schema, "GenerationId", generationId);
        Set(entity, schema, "GeometryHash", geometryHash);
        Set(entity, schema, "RuleHash", ruleHash);
        Set(entity, schema, "BarElementIds", string.Join(",", barIds.Select(id => Infrastructure.ElementIdCompat.ToLong(id))));
        Set(entity, schema, "UpdatedUtc", DateTime.UtcNow.ToString("O"));
        host.SetEntity(entity);
    }

    public static bool TryRead(Rebar rebar, out AbutmentOwnership value)
    {
        value = new AbutmentOwnership(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
            string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        var schema = Schema.Lookup(RebarSchemaId);
        if (schema is null) return false;
        var entity = rebar.GetEntity(schema);
        if (!entity.IsValid()) return false;
        value = new AbutmentOwnership(
            Get(entity, schema, nameof(value.AssemblyId)), Get(entity, schema, nameof(value.HostUniqueId)),
            Get(entity, schema, nameof(value.HostRole)), Get(entity, schema, nameof(value.RebarZone)),
            Get(entity, schema, nameof(value.RuleId)), Get(entity, schema, nameof(value.RuleVersion)),
            Get(entity, schema, nameof(value.GenerationId)), Get(entity, schema, nameof(value.GeometryHash)),
            Get(entity, schema, nameof(value.RuleHash)), Get(entity, schema, nameof(value.ProfileId)),
            Get(entity, schema, nameof(value.SourceEvidenceJson)), Get(entity, schema, nameof(value.GeneratedUtc)),
            Get(entity, schema, nameof(value.ToolVersion)));
        return true;
    }

    public static IReadOnlyList<Rebar> FindOwned(
        Document document, string hostUniqueId, IEnumerable<string>? zones = null) =>
        AbutmentOwnershipIndex.Find(document, hostUniqueId, zones);

    public static IReadOnlyList<string> MissingVisibleParameters(Element element) =>
        SharedParameterNames.Where(name => element.LookupParameter(name) is null).ToArray();

    /// <summary>
    /// Scheduling parameters this element does not carry a usable value for. Presence alone is not
    /// enough: binding is per category, so a bar written before this wave gets the parameter the
    /// moment the binding is added and would answer "present" while holding nothing. A schedule
    /// built on that reads as a real table with a wrong total, which is worse than no table, so a
    /// blank counts as missing.
    /// </summary>
    public static IReadOnlyList<string> MissingScheduleParameters(Element element) =>
        ScheduleParameterNames.Where(name => !HasScheduleValue(element, name)).ToArray();

    private static bool HasScheduleValue(Element element, string name)
    {
        var parameter = element.LookupParameter(name);
        if (parameter is null || !parameter.HasValue) return false;
        return parameter.StorageType switch
        {
            StorageType.String => !string.IsNullOrWhiteSpace(parameter.AsString()),
            // A bar position and a piece are 1-based and a cut length is positive, so zero can only
            // mean "never written". Bend radius, lap length and splice count are genuinely zero on a
            // straight unspliced bar, so they are graded on presence only.
            StorageType.Integer => name is "DVB_SpliceCount" || parameter.AsInteger() > 0,
            StorageType.Double => name is "DVB_BendRadius" or "DVB_LapLength" ||
                                  parameter.AsDouble() > 0,
            _ => false
        };
    }

    /// <summary>
    /// The planned figures of one bar, in the shape both the hidden evidence and the create receipt
    /// record them in. This is the only adapter from a planned bar to those figures, so the two
    /// sinks are fed one resolved record rather than two computations that could drift apart.
    /// </summary>
    public static bool TryResolvePieceFacts(
        PlannedAbutmentRebar planned,
        out AbutmentBarPieceFacts facts,
        out string error) =>
        AbutmentBarPieceFactsKernel.TryResolve(
            new AbutmentBarPieceFactsRequest
            {
                PositionIndex = planned.PositionIndex,
                PieceIndex = planned.PieceIndex,
                PieceCount = planned.PieceCount,
                PieceLengthMm = planned.PieceLengthMm,
                PositionLengthMm = planned.PositionLengthMm,
                LapLengthMm = planned.LapLengthMm,
                ReversedLayout = planned.ReversedLayout,
                BarPositionCount = planned.Rule.StationSchedule.ExpectedBarCount,
                RebarObjectCount = planned.Rule.ExpectedRebarObjectCount,
                DiameterMm = planned.Rule.DiameterMm,
                BarTypeName = planned.Rule.BarTypeName,
                BarTypeInModel = planned.ResolvedBarTypeName
            },
            out facts,
            out error);

    /// <summary>
    /// Lineage written onto one rebar. The <c>Piece</c> block is what a bar bending schedule and a
    /// yard cutting list are rebuilt from later: which bar position this object belongs to, which
    /// piece of that position it is, what it was cut to, and which lap it takes part in. Without it
    /// a spliced layer reads back as twice as many bars as the drawing counts, with no way to tell
    /// which two belong together.
    /// </summary>
    /// <param name="piece">
    /// Resolved by <see cref="TryResolvePieceFacts"/> before the transaction opens and handed to the
    /// create receipt as the same instance; the block is serialized, never recomputed here.
    /// </param>
    public static string EvidenceJson(
        AbutmentRebarPresetV1 preset,
        PlannedAbutmentRebar planned,
        AbutmentBarPieceFacts piece)
    {
        var rule = planned.Rule;
        var evidence = preset.SourceEvidence.FirstOrDefault(item =>
            item.EvidenceId.Equals(rule.SourceEvidenceId, StringComparison.OrdinalIgnoreCase));
        return JsonSerializer.Serialize(new
        {
            Piece = piece,
            rule.LapSplice,
            rule.CanonicalMark,
            rule.SourceDrawingMark,
            rule.SourceResolutionStatus,
            rule.SourceLocator,
            rule.SourceStatus,
            rule.SourceEvidenceId,
            rule.StandardId,
            rule.ClauseReference,
            rule.ProposedCorrection,
            rule.ShapeStrategy,
            rule.GeometryTemplate,
            rule.LayoutRule,
            rule.DistributionRepresentation,
            rule.FaceRole,
            rule.LayerOrder,
            rule.ClearGapToPreviousMm,
            rule.ShapeVerified,
            rule.ShapeEvidenceId,
            rule.ShapeSignature,
            rule.StartEndGeometry,
            rule.EndEndGeometry,
            rule.StationSchedule,
            rule.FootingCover,
            rule.RequiredBarGrade,
            rule.MinimumYieldStrengthMpa,
            rule.Development,
            rule.Continuity,
            rule.PileObstaclePolicy,
            rule.OpeningObstaclePolicy,
            rule.ObstacleEvidenceId,
            rule.EngineerApprovalReference,
            Evidence = evidence
        });
    }

    private static Schema GetOrCreateRebarSchema()
    {
        var schema = Schema.Lookup(RebarSchemaId);
        if (schema is not null) return schema;
        var builder = BaseBuilder(RebarSchemaId, "DvbAbutmentRebarOwnershipV1");
        foreach (var name in new[] { "AssemblyId", "HostUniqueId", "HostRole", "RebarZone", "RuleId",
                     "RuleVersion", "GenerationId", "GeometryHash", "RuleHash", "ProfileId",
                     "SourceEvidenceJson", "GeneratedUtc", "ToolVersion" })
            builder.AddSimpleField(name, typeof(string));
        return builder.Finish();
    }

    private static Schema GetOrCreateHostSchema()
    {
        var schema = Schema.Lookup(HostSchemaId);
        if (schema is not null) return schema;
        var builder = BaseBuilder(HostSchemaId, "DvbAbutmentHostManifestV1");
        foreach (var name in new[] { "AssemblyId", "HostUniqueId", "GenerationId", "GeometryHash",
                     "RuleHash", "BarElementIds", "UpdatedUtc" })
            builder.AddSimpleField(name, typeof(string));
        return builder.Finish();
    }

    private static SchemaBuilder BaseBuilder(Guid id, string name)
    {
        var builder = new SchemaBuilder(id);
        builder.SetSchemaName(name);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        builder.SetVendorId("Development");
        return builder;
    }
    private static void Set(Entity entity, Schema schema, string name, string value) =>
        entity.Set(schema.GetField(name), value ?? string.Empty);
    private static string Get(Entity entity, Schema schema, string name) =>
        entity.Get<string>(schema.GetField(name)) ?? string.Empty;
    private static void WriteVisibleText(Element element, string name, string value)
    {
        var parameter = element.LookupParameter(name);
        if (parameter is { IsReadOnly: false, StorageType: StorageType.String }) parameter.Set(value);
    }

    /// <summary>
    /// Writes a millimetre value into a Length parameter. Revit stores every length internally in
    /// feet, so a millimetre number set straight onto the parameter would be read back as that many
    /// feet and a schedule would total three hundred times the steel.
    /// </summary>
    private static void WriteVisibleLengthMm(Element element, string name, double millimetres)
    {
        var parameter = element.LookupParameter(name);
        if (parameter is not { IsReadOnly: false, StorageType: StorageType.Double }) return;
        if (!double.IsFinite(millimetres)) return;
        parameter.Set(UnitUtils.ConvertToInternalUnits(millimetres, UnitTypeId.Millimeters));
    }

    /// <summary>
    /// Writes a unitless number. Mass is carried as a plain number in kilograms rather than a
    /// Revit mass quantity so the figure in the schedule is the figure written here, whatever unit
    /// scheme the project happens to be set to; the column header states the unit.
    /// </summary>
    private static void WriteVisibleNumber(Element element, string name, double value)
    {
        var parameter = element.LookupParameter(name);
        if (parameter is not { IsReadOnly: false, StorageType: StorageType.Double }) return;
        if (!double.IsFinite(value)) return;
        parameter.Set(value);
    }

    private static void WriteVisibleInteger(Element element, string name, int value)
    {
        var parameter = element.LookupParameter(name);
        if (parameter is { IsReadOnly: false, StorageType: StorageType.Integer }) parameter.Set(value);
    }
}
