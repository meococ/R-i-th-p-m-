using System.Globalization;

namespace BIM.DatViet.Domain;

public readonly record struct AbutmentPileHashInput(
    long SourceId,
    double CenterX,
    double CenterY,
    double MinZ,
    double MaxZ,
    double PileRadius,
    double Clearance);

public readonly record struct AbutmentHashPoint3(double X, double Y, double Z);

public readonly record struct AbutmentPlanarFaceHashInput(
    long HostId,
    double OriginX,
    double OriginY,
    double OriginZ,
    double NormalX,
    double NormalY,
    double NormalZ,
    double Area,
    IReadOnlyList<AbutmentHashPoint3> BoundaryVertices);


public static class AbutmentGeometryHashKernel
{
    public static string SerializePiles(IEnumerable<AbutmentPileHashInput> piles)
    {
        ArgumentNullException.ThrowIfNull(piles);
        static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        return string.Join("|", piles
            .OrderBy(pile => pile.SourceId)
            .ThenBy(pile => pile.CenterX)
            .ThenBy(pile => pile.CenterY)
            .ThenBy(pile => pile.MinZ)
            .ThenBy(pile => pile.MaxZ)
            .ThenBy(pile => pile.PileRadius)
            .ThenBy(pile => pile.Clearance)
            .Select(pile => string.Join(",",
                pile.SourceId.ToString(CultureInfo.InvariantCulture),
                F(pile.CenterX), F(pile.CenterY), F(pile.MinZ), F(pile.MaxZ),
                F(pile.PileRadius), F(pile.Clearance))));
    }

    public static string SerializePlanarFaces(IEnumerable<AbutmentPlanarFaceHashInput> faces)
    {
        ArgumentNullException.ThrowIfNull(faces);
        static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        static string Point(AbutmentHashPoint3 point) =>
            string.Join(",", F(point.X), F(point.Y), F(point.Z));

        return string.Join("|", faces
            .Select(face =>
            {
                var vertices = string.Join(";", face.BoundaryVertices
                    .Select(Point)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal));
                return string.Join(",",
                           face.HostId.ToString(CultureInfo.InvariantCulture),
                           F(face.OriginX), F(face.OriginY), F(face.OriginZ),
                           F(face.NormalX), F(face.NormalY), F(face.NormalZ),
                           F(face.Area)) +
                       ";" + vertices;
            })
            .OrderBy(value => value, StringComparer.Ordinal));
    }
}
