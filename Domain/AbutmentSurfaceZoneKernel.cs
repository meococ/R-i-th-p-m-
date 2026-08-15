namespace BIM.DatViet.Domain;

public readonly record struct AbutmentPlanarFaceKernel(
    int Index,
    double MinLongitudinal,
    double MaxLongitudinal,
    double MinTransverse,
    double MaxTransverse,
    double MinVertical,
    double MaxVertical,
    double NormalLongitudinal,
    double NormalTransverse,
    double NormalVertical,
    double OriginLongitudinal,
    double OriginTransverse,
    double OriginVertical,
    double Area)
{
    public double LongitudinalLength => MaxLongitudinal - MinLongitudinal;
    public double TransverseDepth => MaxTransverse - MinTransverse;
    public double Height => MaxVertical - MinVertical;
    public double CenterLongitudinal => (MinLongitudinal + MaxLongitudinal) / 2;
}

public readonly record struct AbutmentSurfacePairKernel(
    int FrontFaceIndex,
    int BackFaceIndex,
    double PlaneSeparation,
    double OverlapLength);

/// <summary>
/// Selects opposing measured planar faces for canonical wall zones. Preset dimensions are
/// tolerances/identity hints only; returned faces always come from observed host geometry.
/// </summary>
public static class AbutmentSurfaceZoneKernel
{
    public static bool TryResolveStemPair(
        IReadOnlyList<AbutmentPlanarFaceKernel> faces,
        double footingTop,
        double footingLength,
        double minimumStemHeight,
        double expectedThickness,
        double elevationTolerance,
        double thicknessTolerance,
        out AbutmentSurfacePairKernel pair)
    {
        var candidates = faces.Where(face =>
                IsTransverseWallFace(face) &&
                face.MinVertical <= footingTop + elevationTolerance &&
                face.MaxVertical >= footingTop + minimumStemHeight &&
                face.LongitudinalLength >= footingLength * 0.55)
            .ToList();
        return TryResolveOpposingPair(
            candidates,
            expectedThickness,
            thicknessTolerance,
            footingLength * 0.45,
            face => face.MinLongitudinal,
            face => face.MaxLongitudinal,
            face => face.NormalTransverse,
            out pair);
    }

    public static bool TryResolveBackwallPair(
        IReadOnlyList<AbutmentPlanarFaceKernel> faces,
        double footingTop,
        double footingLength,
        double minimumBaseElevation,
        double minimumFaceHeight,
        double expectedThickness,
        double thicknessTolerance,
        out AbutmentSurfacePairKernel pair)
    {
        var candidates = faces.Where(face =>
                IsTransverseWallFace(face) &&
                face.MinVertical >= footingTop + minimumBaseElevation &&
                face.Height >= minimumFaceHeight &&
                face.LongitudinalLength >= footingLength * 0.45)
            .ToList();
        return TryResolveOpposingPair(
            candidates,
            expectedThickness,
            thicknessTolerance,
            footingLength * 0.40,
            face => face.MinLongitudinal,
            face => face.MaxLongitudinal,
            face => face.NormalTransverse,
            out pair);
    }

    public static IReadOnlyList<AbutmentSurfacePairKernel> ResolveWingPairs(
        IReadOnlyList<AbutmentPlanarFaceKernel> faces,
        double footingCenterLongitudinal,
        double minimumWingSpan,
        double minimumWingHeight,
        double expectedThickness,
        double thicknessTolerance) =>
        ResolveWingPairs(
            faces,
            footingCenterLongitudinal,
            minimumWingSpan,
            minimumWingSpan,
            minimumWingHeight,
            expectedThickness,
            thicknessTolerance);

    public static IReadOnlyList<AbutmentSurfacePairKernel> ResolveWingPairs(
        IReadOnlyList<AbutmentPlanarFaceKernel> faces,
        double footingCenterLongitudinal,
        double minimumWingSpanLeft,
        double minimumWingSpanRight,
        double minimumWingHeight,
        double expectedThickness,
        double thicknessTolerance)
    {
        var candidates = faces.Where(face =>
                Math.Abs(face.NormalVertical) <= 0.05 &&
                Math.Abs(face.NormalLongitudinal) >= 0.65 &&
                face.Height >= minimumWingHeight)
            .ToList();

        var pairs = new List<AbutmentSurfacePairKernel>();
        foreach (var side in new[] { -1, 1 })
        {
            var minimumWingSpan = side < 0 ? minimumWingSpanLeft : minimumWingSpanRight;
            if (minimumWingSpan <= 0) continue;
            var sideFaces = candidates.Where(face =>
                    face.TransverseDepth >= minimumWingSpan &&
                    (side < 0
                        ? face.CenterLongitudinal < footingCenterLongitudinal
                        : face.CenterLongitudinal > footingCenterLongitudinal))
                .ToList();
            if (!TryResolveOpposingPair(
                    sideFaces,
                    expectedThickness,
                    thicknessTolerance,
                    minimumWingSpan * 0.55,
                    face => face.MinTransverse,
                    face => face.MaxTransverse,
                    face => side * face.NormalLongitudinal,
                    out var pair))
                continue;
            pairs.Add(pair);
        }
        return pairs;
    }

    private static bool IsTransverseWallFace(AbutmentPlanarFaceKernel face) =>
        Math.Abs(face.NormalVertical) <= 0.05 &&
        Math.Abs(face.NormalTransverse) >= 0.97;

    private static bool TryResolveOpposingPair(
        IReadOnlyList<AbutmentPlanarFaceKernel> candidates,
        double expectedThickness,
        double thicknessTolerance,
        double minimumOverlap,
        Func<AbutmentPlanarFaceKernel, double> minAlong,
        Func<AbutmentPlanarFaceKernel, double> maxAlong,
        Func<AbutmentPlanarFaceKernel, double> outwardSign,
        out AbutmentSurfacePairKernel pair)
    {
        pair = default;
        if (expectedThickness <= 0 || thicknessTolerance < 0) return false;

        var matches = new List<(AbutmentSurfacePairKernel Pair, double ThicknessError, double Area)>();
        for (var firstIndex = 0; firstIndex < candidates.Count; firstIndex++)
        for (var secondIndex = firstIndex + 1; secondIndex < candidates.Count; secondIndex++)
        {
            var first = candidates[firstIndex];
            var second = candidates[secondIndex];
            var normalDot = first.NormalLongitudinal * second.NormalLongitudinal +
                            first.NormalTransverse * second.NormalTransverse +
                            first.NormalVertical * second.NormalVertical;
            if (normalDot > -0.97) continue;

            var dx = second.OriginLongitudinal - first.OriginLongitudinal;
            var dy = second.OriginTransverse - first.OriginTransverse;
            var dz = second.OriginVertical - first.OriginVertical;
            var separation = Math.Abs(
                dx * first.NormalLongitudinal +
                dy * first.NormalTransverse +
                dz * first.NormalVertical);
            var thicknessError = Math.Abs(separation - expectedThickness);
            if (thicknessError > thicknessTolerance + 1e-9) continue;

            var overlap = Math.Min(maxAlong(first), maxAlong(second)) -
                          Math.Max(minAlong(first), minAlong(second));
            if (overlap < minimumOverlap) continue;

            var front = outwardSign(first) < outwardSign(second) ? first : second;
            var back = front.Index == first.Index ? second : first;
            matches.Add((
                new AbutmentSurfacePairKernel(front.Index, back.Index, separation, overlap),
                thicknessError,
                first.Area + second.Area));
        }

        if (matches.Count == 0) return false;
        pair = matches
            .OrderBy(match => match.ThicknessError)
            .ThenByDescending(match => match.Pair.OverlapLength)
            .ThenByDescending(match => match.Area)
            .ThenBy(match => match.Pair.FrontFaceIndex)
            .First().Pair;
        return true;
    }
}
