namespace BIM.DatViet.Domain;

/// <summary>
/// Pure, versioned topology adapter for the standard 14-edge PROFILE_TM section.
/// Semantic regions come from the validated topology signature; all dimensions come
/// from the supplied geometry. Unknown profile topologies fail closed.
/// </summary>
public static class AbutmentCoreProfileKernel
{
    public enum RegionKind
    {
        Footing,
        Stem,
        Backwall
    }

    public readonly record struct Region(
        RegionKind Kind,
        IReadOnlyList<PlanPoint2> Polygon,
        double Area);

    public readonly record struct Resolution(
        IReadOnlyList<PlanPoint2> OrderedProfile,
        Region Footing,
        Region Stem,
        Region Backwall,
        PlanSegment2 FootingStemInterface,
        PlanSegment2 StemBackwallInterface,
        double ProfileArea,
        double RegionAreaSum);

    /// <summary>
    /// Orders an arbitrary segment collection, validates the complete ProfileTm14V1
    /// signature, then partitions it into non-overlapping Footing, Stem and Backwall regions.
    /// X is the section-horizontal coordinate and Y is elevation.
    /// </summary>
    public static bool TryResolveProfileTm14V1(
        IReadOnlyCollection<PlanSegment2> segments,
        double closureTolerance,
        double signatureTolerance,
        out Resolution resolution,
        out string error)
    {
        resolution = default;
        error = string.Empty;
        if (segments is null || segments.Count != 14)
        {
            error = "ProfileTm14V1 requires exactly 14 segments.";
            return false;
        }
        if (!double.IsFinite(closureTolerance) || closureTolerance <= 0 ||
            !double.IsFinite(signatureTolerance) || signatureTolerance <= 0)
        {
            error = "ProfileTm14V1 tolerances must be finite and positive.";
            return false;
        }

        var source = segments.Select(segment =>
            new AbutmentProfileMassKernel.ProfileSegment3(
                new AbutmentProfileMassKernel.ProfilePoint3(
                    segment.Start.X, 0, segment.Start.Y),
                new AbutmentProfileMassKernel.ProfilePoint3(
                    segment.End.X, 0, segment.End.Y)))
            .ToArray();
        if (!AbutmentProfileMassKernel.TryOrderClosedLoop(
                source,
                closureTolerance,
                out var ordered3,
                out _,
                out var orderError))
        {
            error = $"ProfileTm14V1 is not one closed loop: {orderError}";
            return false;
        }

        // The count gate deliberately precedes every topology index access.
        if (ordered3.Count != 14)
        {
            error = $"ProfileTm14V1 ordered loop must contain 14 vertices; found {ordered3.Count}.";
            return false;
        }
        var p = ordered3.Select(point => new PlanPoint2(point.X, point.Z)).ToArray();
        if (p.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            error = "ProfileTm14V1 contains a non-finite vertex.";
            return false;
        }
        if (AbutmentProfileMassKernel.HasSelfIntersection(
                ordered3,
                new AbutmentProfileMassKernel.ProfilePoint3(0, 0, 0),
                new AbutmentProfileMassKernel.ProfilePoint3(1, 0, 0),
                new AbutmentProfileMassKernel.ProfilePoint3(0, 0, 1),
                closureTolerance))
        {
            error = "ProfileTm14V1 self-intersects.";
            return false;
        }
        if (!MatchesProfileTm14Signature(p, signatureTolerance, out error))
            return false;

        var footingPolygon = Points(p, 0, 1, 2, 11, 12, 13);
        var stemPolygon = Points(p, 2, 3, 4, 10, 11);
        var backwallPolygon = Points(p, 4, 5, 6, 7, 8, 9, 10);
        if (!TryCreateRegion(RegionKind.Footing, footingPolygon, closureTolerance,
                out var footing, out error) ||
            !TryCreateRegion(RegionKind.Stem, stemPolygon, closureTolerance,
                out var stem, out error) ||
            !TryCreateRegion(RegionKind.Backwall, backwallPolygon, closureTolerance,
                out var backwall, out error))
            return false;

        var footingStem = new PlanSegment2(p[2], p[11]);
        var stemBackwall = new PlanSegment2(p[4], p[10]);
        if (footingStem.Length <= signatureTolerance || stemBackwall.Length <= signatureTolerance)
        {
            error = "ProfileTm14V1 contains a degenerate semantic interface.";
            return false;
        }

        var profileArea = Math.Abs(GeometryKernel.SignedArea(p));
        var regionAreaSum = footing.Area + stem.Area + backwall.Area;
        var areaTolerance = Math.Max(
            signatureTolerance * signatureTolerance * p.Length,
            profileArea * 1e-9);
        if (profileArea <= areaTolerance || Math.Abs(profileArea - regionAreaSum) > areaTolerance)
        {
            error = "ProfileTm14V1 regions do not reproduce the authoritative profile area.";
            return false;
        }
        if (!HasExpectedRegionEdgeUnion(
                p,
                [footing, stem, backwall],
                [footingStem, stemBackwall]))
        {
            error = "ProfileTm14V1 semantic interfaces do not reproduce the authoritative boundary.";
            return false;
        }

        resolution = new Resolution(
            p,
            footing,
            stem,
            backwall,
            footingStem,
            stemBackwall,
            profileArea,
            regionAreaSum);
        return true;
    }

    private static bool MatchesProfileTm14Signature(
        IReadOnlyList<PlanPoint2> p,
        double tolerance,
        out string error)
    {
        error = string.Empty;
        bool Near(double first, double second) => Math.Abs(first - second) <= tolerance;
        bool Less(double first, double second) => first < second - tolerance;
        bool Horizontal(int first, int second) =>
            Near(p[first].Y, p[second].Y) && Math.Abs(p[first].X - p[second].X) > tolerance;
        bool Vertical(int first, int second) =>
            Near(p[first].X, p[second].X) && Math.Abs(p[first].Y - p[second].Y) > tolerance;

        var bottom = p.Min(point => point.Y);
        if (!Near(p[0].Y, bottom) || !Near(p[13].Y, bottom) || !Horizontal(13, 0) ||
            !Vertical(0, 1) || !Vertical(12, 13))
            return Fail("footing outer boundary", out error);

        var footingTop = p[1].Y;
        if (!Less(bottom, footingTop) ||
            !Near(p[2].Y, footingTop) || !Near(p[11].Y, footingTop) || !Near(p[12].Y, footingTop) ||
            !Horizontal(1, 2) || !Horizontal(11, 12) ||
            !Less(p[1].X, p[2].X) || !Less(p[2].X, p[11].X) || !Less(p[11].X, p[12].X))
            return Fail("footing top and stem-base shoulders", out error);

        if (!Vertical(2, 3) || !Horizontal(3, 4) || !Vertical(4, 5) ||
            !Less(footingTop, p[3].Y) || !Less(p[3].X, p[4].X) || !Less(p[3].Y, p[5].Y))
            return Fail("front stem/backwall shoulder", out error);

        if (!Horizontal(5, 6) || !Vertical(6, 7) ||
            !Less(p[5].X, p[6].X) || !Less(p[7].Y, p[6].Y))
            return Fail("backwall crown", out error);

        // The transition 7-8 may carry a small design fall, hence elevation is compared
        // within the explicit signature tolerance rather than forced horizontal.
        if (!Near(p[7].Y, p[8].Y) || !Less(p[7].X, p[8].X) || !Vertical(8, 9) ||
            !Less(p[9].Y, p[8].Y) || !Less(p[10].X, p[9].X) || !Less(p[10].Y, p[9].Y))
            return Fail("backwall return/corbel chain", out error);

        if (!Vertical(10, 11) ||
            !Near(p[6].X, p[7].X) || !Near(p[7].X, p[10].X) || !Near(p[10].X, p[11].X) ||
            !Less(footingTop, p[4].Y) || !Less(p[4].Y, p[10].Y) ||
            !Less(p[10].Y, p[9].Y) || !Less(p[9].Y, p[7].Y) || !Less(p[7].Y, p[5].Y))
            return Fail("rear stem/backwall interface", out error);

        return true;

        static bool Fail(string part, out string message)
        {
            message = $"ProfileTm14V1 signature mismatch at {part}.";
            return false;
        }
    }

    private static bool TryCreateRegion(
        RegionKind kind,
        IReadOnlyList<PlanPoint2> polygon,
        double tolerance,
        out Region region,
        out string error)
    {
        region = default;
        error = string.Empty;
        var area = Math.Abs(GeometryKernel.SignedArea(polygon));
        if (area <= tolerance * tolerance)
        {
            error = $"ProfileTm14V1 {kind} region has zero area.";
            return false;
        }
        var points3 = polygon.Select(point =>
            new AbutmentProfileMassKernel.ProfilePoint3(point.X, 0, point.Y)).ToArray();
        if (AbutmentProfileMassKernel.HasSelfIntersection(
                points3,
                new AbutmentProfileMassKernel.ProfilePoint3(0, 0, 0),
                new AbutmentProfileMassKernel.ProfilePoint3(1, 0, 0),
                new AbutmentProfileMassKernel.ProfilePoint3(0, 0, 1),
                tolerance))
        {
            error = $"ProfileTm14V1 {kind} region self-intersects.";
            return false;
        }
        region = new Region(kind, polygon, area);
        return true;
    }

    private static IReadOnlyList<PlanPoint2> Points(
        IReadOnlyList<PlanPoint2> source,
        params int[] indexes) => indexes.Select(index => source[index]).ToArray();

    /// <summary>
    /// Region index rings cancel only the two intended semantic interfaces; every
    /// remaining edge is one of the 14 authoritative profile edges.
    /// </summary>
    private static bool HasExpectedRegionEdgeUnion(
        IReadOnlyList<PlanPoint2> profile,
        IReadOnlyList<Region> regions,
        IReadOnlyList<PlanSegment2> interfaces)
    {
        var counts = new Dictionary<(PlanPoint2 A, PlanPoint2 B), int>();
        foreach (var region in regions)
        for (var index = 0; index < region.Polygon.Count; index++)
        {
            var key = Edge(
                region.Polygon[index],
                region.Polygon[(index + 1) % region.Polygon.Count]);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        if (interfaces.Any(segment =>
                !counts.TryGetValue(Edge(segment.Start, segment.End), out var count) || count != 2))
            return false;
        for (var index = 0; index < profile.Count; index++)
        {
            var edge = Edge(profile[index], profile[(index + 1) % profile.Count]);
            if (!counts.TryGetValue(edge, out var count) || count != 1)
                return false;
        }
        return counts.Count == profile.Count + interfaces.Count;
    }

    private static (PlanPoint2 A, PlanPoint2 B) Edge(PlanPoint2 first, PlanPoint2 second) =>
        Compare(first, second) <= 0 ? (first, second) : (second, first);

    private static int Compare(PlanPoint2 first, PlanPoint2 second)
    {
        var x = first.X.CompareTo(second.X);
        return x != 0 ? x : first.Y.CompareTo(second.Y);
    }
}
