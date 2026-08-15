using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;
using BIM.DatViet.Models;

namespace BIM.DatViet.RevitServices;

public static class ManholeHostIdentityStore
{
    // V2: W1 stored as unitless string (X|Y|Z). V1 double fields required Spec/units and failed Finish.
    private static readonly Guid SchemaId = new("A3C8E1F2-9B4D-4E70-8C1A-5D6F7A8B9C0D");
    private const string SchemaName = "DvbManholeHostIdentityV2";

    public static void Write(Element host, ManholeAssembly assembly)
    {
        var schema = GetOrCreateSchema();
        var w1 = assembly.Walls.Single(wall => wall.Number == 1);
        var entity = new Entity(schema);
        entity.Set(schema.GetField("ManholeKey"), assembly.ManholeKey);
        entity.Set(schema.GetField("W1Outward"), FormatVector(w1.Outward));
        host.SetEntity(entity);
    }

    public static bool TryRead(Element host, out string manholeKey, out XYZ w1Outward)
    {
        manholeKey = string.Empty;
        w1Outward = XYZ.Zero;
        var schema = Schema.Lookup(SchemaId);
        if (schema is null) return false;
        var entity = host.GetEntity(schema);
        if (!entity.IsValid()) return false;
        manholeKey = entity.Get<string>(schema.GetField("ManholeKey"));
        if (!TryParseVector(entity.Get<string>(schema.GetField("W1Outward")), out w1Outward))
            w1Outward = XYZ.Zero;
        return !string.IsNullOrWhiteSpace(manholeKey);
    }

    private static Schema GetOrCreateSchema()
    {
        var existing = Schema.Lookup(SchemaId);
        if (existing is not null) return existing;
        var builder = new SchemaBuilder(SchemaId);
        builder.SetSchemaName(SchemaName);
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);
        // Revit VendorId: length 4..253 (Application.VendorIdIsValid). Match .addin development id.
        builder.SetVendorId("Development");
        builder.AddSimpleField("ManholeKey", typeof(string));
        // String avoids "Units are required for field" on double SpecTypeId.
        builder.AddSimpleField("W1Outward", typeof(string));
        return builder.Finish();
    }

    private static string FormatVector(XYZ value) =>
        string.Format(CultureInfo.InvariantCulture, "{0:R}|{1:R}|{2:R}", value.X, value.Y, value.Z);

    private static bool TryParseVector(string? text, out XYZ vector)
    {
        vector = XYZ.Zero;
        if (text is null || string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('|');
        if (parts.Length != 3) return false;
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y)) return false;
        if (!double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return false;
        vector = new XYZ(x, y, z);
        return true;
    }
}
