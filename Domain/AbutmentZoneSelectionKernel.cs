using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BIM.DatViet.Models;

namespace BIM.DatViet.Domain;

public readonly record struct AbutmentZoneSelectionEvidence(
    AbutmentZoneKind Kind,
    string ZoneCode,
    string HostUniqueId,
    string GeometryHash,
    double MinX,
    double MinY,
    double MinZ,
    double MaxX,
    double MaxY,
    double MaxZ,
    IReadOnlyList<int> SupportingSolidIndexes);

public static class AbutmentZoneSelectionKernel
{
    public const string ZoneCodePrefix = "GEOMETRY:";
    public static bool Validate(
        AbutmentZoneSelectionEvidence candidate,
        AbutmentZoneSelectionEvidence canonical,
        double tolerance,
        out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(candidate.HostUniqueId) ||
            !candidate.HostUniqueId.Equals(canonical.HostUniqueId, StringComparison.Ordinal))
        {
            error = "Selection không thuộc đúng host hiện hành.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(candidate.GeometryHash) ||
            !candidate.GeometryHash.Equals(canonical.GeometryHash, StringComparison.OrdinalIgnoreCase))
        {
            error = "Geometry hash của selection đã cũ hoặc không khớp.";
            return false;
        }
        if (candidate.Kind != canonical.Kind)
        {
            error = "Selection không khớp loại khu vực canonical.";
            return false;
        }
        var expectedCode = ZoneCodePrefix + candidate.Kind;
        if (string.IsNullOrWhiteSpace(candidate.ZoneCode) ||
            !candidate.ZoneCode.Equals(expectedCode, StringComparison.OrdinalIgnoreCase) ||
            !candidate.ZoneCode.Equals(canonical.ZoneCode, StringComparison.OrdinalIgnoreCase))
        {
            error = "Zone code không khớp canonical geometry của khu vực.";
            return false;
        }
        if (!candidate.SupportingSolidIndexes.OrderBy(index => index)
                .SequenceEqual(canonical.SupportingSolidIndexes.OrderBy(index => index)))
        {
            error = "Selection không khớp tập solid canonical.";
            return false;
        }

        tolerance = Math.Max(1e-9, tolerance);
        var boundsMatch = NearlyEqual(candidate.MinX, canonical.MinX, tolerance) &&
                          NearlyEqual(candidate.MinY, canonical.MinY, tolerance) &&
                          NearlyEqual(candidate.MinZ, canonical.MinZ, tolerance) &&
                          NearlyEqual(candidate.MaxX, canonical.MaxX, tolerance) &&
                          NearlyEqual(candidate.MaxY, canonical.MaxY, tolerance) &&
                          NearlyEqual(candidate.MaxZ, canonical.MaxZ, tolerance);
        if (!boundsMatch)
        {
            error = "Giới hạn selection không khớp khu vực canonical.";
            return false;
        }

        return true;
    }

    public static string ComputeFingerprint(AbutmentZoneSelectionEvidence evidence)
    {
        var payload = string.Join("|",
            evidence.Kind,
            evidence.ZoneCode.Trim(),
            evidence.HostUniqueId.Trim(),
            evidence.GeometryHash.Trim().ToUpperInvariant(),
            Format(evidence.MinX),
            Format(evidence.MinY),
            Format(evidence.MinZ),
            Format(evidence.MaxX),
            Format(evidence.MaxY),
            Format(evidence.MaxZ),
            string.Join(",", evidence.SupportingSolidIndexes.OrderBy(index => index)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static bool NearlyEqual(double left, double right, double tolerance) =>
        Math.Abs(left - right) <= tolerance;

    private static string Format(double value) =>
        value.ToString("R", CultureInfo.InvariantCulture);
}
