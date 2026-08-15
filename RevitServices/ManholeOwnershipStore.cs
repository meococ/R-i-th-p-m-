using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using Autodesk.Revit.DB.Structure;

namespace BIM.DatViet.RevitServices;

public sealed record ManholeOwnership(
    string ManholeKey, string PrimaryHostUniqueId, string ComponentKey, string SpecHash, string RunId, string ToolVersion);

public static class ManholeOwnershipStore
{
    private static readonly Guid SchemaId = new("D5CE5260-8161-4573-8544-24F52A0A933B");
    private const string SchemaName = "DvbManholeRebarOwnershipV1";

    public static void Write(Element element, ManholeOwnership ownership)
    {
        var schema = GetOrCreateSchema();
        var entity = new Entity(schema);
        entity.Set(schema.GetField(nameof(ManholeOwnership.ManholeKey)), ownership.ManholeKey);
        entity.Set(schema.GetField(nameof(ManholeOwnership.PrimaryHostUniqueId)), ownership.PrimaryHostUniqueId);
        entity.Set(schema.GetField(nameof(ManholeOwnership.ComponentKey)), ownership.ComponentKey);
        entity.Set(schema.GetField(nameof(ManholeOwnership.SpecHash)), ownership.SpecHash);
        entity.Set(schema.GetField(nameof(ManholeOwnership.RunId)), ownership.RunId);
        entity.Set(schema.GetField(nameof(ManholeOwnership.ToolVersion)), ownership.ToolVersion);
        element.SetEntity(entity);
    }

    public static bool TryRead(Element element, out ManholeOwnership ownership)
    {
        ownership = new ManholeOwnership(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
        var schema = Schema.Lookup(SchemaId);
        if (schema is null) return false;
        var entity = element.GetEntity(schema);
        if (!entity.IsValid()) return false;
        ownership = new ManholeOwnership(
            entity.Get<string>(schema.GetField(nameof(ManholeOwnership.ManholeKey))),
            entity.Get<string>(schema.GetField(nameof(ManholeOwnership.PrimaryHostUniqueId))),
            entity.Get<string>(schema.GetField(nameof(ManholeOwnership.ComponentKey))),
            entity.Get<string>(schema.GetField(nameof(ManholeOwnership.SpecHash))),
            entity.Get<string>(schema.GetField(nameof(ManholeOwnership.RunId))),
            entity.Get<string>(schema.GetField(nameof(ManholeOwnership.ToolVersion))));
        return true;
    }

    /// <summary>
    /// Find tool-owned Rebar by the stable primary-host identity. The display key is retained as
    /// a second guard so legacy data remains readable without allowing equal Marks to collide.
    /// </summary>
    public static IReadOnlyList<Rebar> FindOwned(
        Document document,
        string manholeKey,
        string primaryHostUniqueId)
    {
        return new FilteredElementCollector(document).OfClass(typeof(Rebar)).Cast<Rebar>()
            .Where(rebar => TryRead(rebar, out var value) &&
                            value.ManholeKey.Equals(manholeKey, StringComparison.OrdinalIgnoreCase) &&
                            value.PrimaryHostUniqueId.Equals(primaryHostUniqueId, StringComparison.Ordinal))
            .ToArray();
    }

    private static Schema GetOrCreateSchema()
    {
        var existing = Schema.Lookup(SchemaId);
        if (existing is not null) return existing;
        var builder = new SchemaBuilder(SchemaId);
        builder.SetSchemaName(SchemaName);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        // Revit requires VendorId length 4..253 (see Application.VendorIdIsValid). "DVB" (3 chars) throws.
        builder.SetVendorId("Development");
        builder.AddSimpleField(nameof(ManholeOwnership.ManholeKey), typeof(string));
        builder.AddSimpleField(nameof(ManholeOwnership.PrimaryHostUniqueId), typeof(string));
        builder.AddSimpleField(nameof(ManholeOwnership.ComponentKey), typeof(string));
        builder.AddSimpleField(nameof(ManholeOwnership.SpecHash), typeof(string));
        builder.AddSimpleField(nameof(ManholeOwnership.RunId), typeof(string));
        builder.AddSimpleField(nameof(ManholeOwnership.ToolVersion), typeof(string));
        return builder.Finish();
    }
}
