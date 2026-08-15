using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace BIM.DatViet.RevitServices;

public sealed class RebarTypeInfo
{
    public required ElementId Id { get; init; }
    public required string Name { get; init; }
    public required double Diameter { get; init; }
    public required string MaterialName { get; init; }
    public double? MinimumYieldStrengthMpa { get; init; }
}

/// <summary>
/// Catalog of <see cref="RebarBarType"/> in the active document.
/// Resolves by exact name first, then by diameter token (D20 / 20 / #20 / 20mm).
/// Does not create or duplicate types.
/// </summary>
public sealed class RebarTypeCatalog
{
    private static readonly Regex DiameterToken = new(
        @"(?:^|[^\d.])(?:D|d|#)?\s*(\d+(?:[.,]\d+)?)\s*(?:mm)?(?:$|[^\d.])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, RebarTypeInfo> _types;
    private readonly List<RebarTypeInfo> _ordered;

    public RebarTypeCatalog(Document document)
    {
        _ordered = new FilteredElementCollector(document)
            .OfClass(typeof(RebarBarType))
            .Cast<RebarBarType>()
            .Select(type =>
            {
                var (materialName, minimumYieldStrengthMpa) =
                    ReadMaterialStrength(document, type);
                return new RebarTypeInfo
                {
                    Id = type.Id,
                    Name = type.Name,
                    Diameter = type.BarModelDiameter,
                    MaterialName = materialName,
                    MinimumYieldStrengthMpa = minimumYieldStrengthMpa
                };
            })
            .GroupBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _types = _ordered.ToDictionary(
            type => type.Name,
            type => type,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<string> Names => _ordered.Select(type => type.Name).ToArray();
    public IReadOnlyList<RebarTypeInfo> Types => _ordered;
    public int Count => _ordered.Count;
    public bool IsEmpty => _ordered.Count == 0;

    public bool TryGet(string name, out RebarTypeInfo info) =>
        _types.TryGetValue(name ?? string.Empty, out info!);

    /// <summary>
    /// Resolve a rule bar type: exact name → diameter-compatible name token → measured diameter.
    /// </summary>
    public bool TryResolve(
        string requestedName,
        double expectedDiameterMm,
        out RebarTypeInfo info,
        out string error)
    {
        info = null!;
        error = string.Empty;

        if (IsEmpty)
        {
            error =
                "Model chưa có RebarBarType nào. Hãy tạo/load hoặc Transfer Project Standards cho " +
                "Rebar Bar Types D12/D16/D20/D25/D32 trước khi rải thép; RebarShape không thay thế RebarBarType.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(requestedName) &&
            TryGet(requestedName.Trim(), out info))
        {
            if (IsDiameterCompatible(info.Diameter, expectedDiameterMm))
                return true;

            var actualMm = ToMm(info.Diameter);
            error =
                $"Type '{info.Name}' có đường kính D{actualMm:0.#} nhưng rule cần D{expectedDiameterMm:0.#}.";
            info = null!;
            return false;
        }

        if (expectedDiameterMm > 0 &&
            TryFindByDiameterMm(expectedDiameterMm, out info))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(requestedName) &&
            TryParseDiameterToken(requestedName, out var tokenMm) &&
            TryFindByDiameterMm(tokenMm, out info))
        {
            return true;
        }

        var available = string.Join(", ", Names.Take(12));
        if (Names.Count > 12) available += ", …";
        error =
            $"Không tìm thấy RebarBarType khớp '{requestedName}' (D{expectedDiameterMm:0.#}). " +
            $"Type hiện có trong model: {available}.";
        info = null!;
        return false;
    }

    public bool TryFindByDiameterMm(double diameterMm, out RebarTypeInfo info)
    {
        info = null!;
        if (diameterMm <= 0) return false;

        RebarTypeInfo? best = null;
        var bestDelta = double.MaxValue;
        foreach (var candidate in _ordered)
        {
            var actualMm = ToMm(candidate.Diameter);
            var delta = Math.Abs(actualMm - diameterMm);
            if (delta > 0.55) continue;
            // Prefer names that look like Dxx when deltas tie.
            var nameBonus = NameLooksLikeDiameter(candidate.Name, diameterMm) ? -0.001 : 0;
            var score = delta + nameBonus;
            if (score >= bestDelta) continue;
            bestDelta = score;
            best = candidate;
        }

        if (best is null) return false;
        info = best;
        return true;
    }

    public static bool TryParseDiameterToken(string text, out double diameterMm)
    {
        diameterMm = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) &&
            plain > 0 && plain < 100)
        {
            diameterMm = plain;
            return true;
        }

        // D20 / d16 / #12 / 20mm
        var match = Regex.Match(
            trimmed,
            @"^(?:D|d|#)?\s*(\d+(?:[.,]\d+)?)\s*(?:mm)?$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            match = DiameterToken.Match(" " + trimmed + " ");
            if (!match.Success) return false;
        }

        var token = match.Groups[1].Value.Replace(',', '.');
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            value <= 0 || value >= 100)
            return false;

        diameterMm = value;
        return true;
    }

    private static bool IsDiameterCompatible(double internalDiameter, double expectedMm) =>
        expectedMm <= 0 || Math.Abs(ToMm(internalDiameter) - expectedMm) <= 0.55;

    private static bool NameLooksLikeDiameter(string name, double diameterMm)
    {
        if (!TryParseDiameterToken(name, out var parsed)) return false;
        return Math.Abs(parsed - diameterMm) <= 0.55;
    }

    private static double ToMm(double internalDiameter) =>
        UnitUtils.ConvertFromInternalUnits(internalDiameter, UnitTypeId.Millimeters);
    private static (string MaterialName, double? MinimumYieldStrengthMpa)
        ReadMaterialStrength(Document document, RebarBarType type)
    {
        var materialId = type.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM)?.AsElementId()
                         ?? ElementId.InvalidElementId;
        if (document.GetElement(materialId) is not Material material)
            return (string.Empty, null);
        if (document.GetElement(material.StructuralAssetId) is not PropertySetElement propertySet)
            return (material.Name, null);

        using var asset = propertySet.GetStructuralAsset();
        var minimumYieldStrengthMpa = UnitUtils.ConvertFromInternalUnits(
            asset.MinimumYieldStress,
            UnitTypeId.Megapascals);
        return double.IsFinite(minimumYieldStrengthMpa) && minimumYieldStrengthMpa > 0
            ? (material.Name, minimumYieldStrengthMpa)
            : (material.Name, null);
    }

}
