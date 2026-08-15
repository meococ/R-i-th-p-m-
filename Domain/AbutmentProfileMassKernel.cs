using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BIM.DatViet.Models;

namespace BIM.DatViet.Domain;

/// <summary>
/// Pure helpers for optional shared nested Mass evidence (PROFILE_TM / PROFILE_TM_L / PROFILE_TM_R).
/// PROFILE bounding boxes are not structural zone geometry.
/// </summary>
public static class AbutmentProfileMassKernel
{

    public enum ProfileStationRole
    {
        Unknown,
        Left,
        Center,
        Right
    }

    public readonly record struct ProfilePoint3(double X, double Y, double Z)
    {
        public static ProfilePoint3 operator +(ProfilePoint3 a, ProfilePoint3 b) =>
            new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static ProfilePoint3 operator -(ProfilePoint3 a, ProfilePoint3 b) =>
            new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static ProfilePoint3 operator *(ProfilePoint3 value, double scale) =>
            new(value.X * scale, value.Y * scale, value.Z * scale);
        public double Dot(ProfilePoint3 other) => X * other.X + Y * other.Y + Z * other.Z;
        public double Length => Math.Sqrt(Dot(this));
        public double DistanceTo(ProfilePoint3 other) => (this - other).Length;
        public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    }

    public readonly record struct ProfileSegment3(ProfilePoint3 Start, ProfilePoint3 End);

    public readonly record struct ProfileStation(long SourceElementIdValue, double Coordinate);

    public readonly record struct ProfileMassHashRow(
        long SourceElementIdValue,
        string FamilyName,
        string TypeName,
        string ParameterChecksum,
        ProfileStationRole Role,
        double OriginX,
        double OriginY,
        double OriginZ,
        double MinX,
        double MinY,
        double MinZ,
        double MaxX,
        double MaxY,
        double MaxZ,
        IReadOnlyList<ProfilePoint3> OrderedLoop);


    public static bool MatchesAnyToken(string value, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return tokens.Any(token =>
            !string.IsNullOrWhiteSpace(token) &&
            value.IndexOf(token.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
    }

    public static bool IsCenterProfileType(string typeName) =>
        typeName.Equals("PROFILE_TM", StringComparison.OrdinalIgnoreCase);

    public static bool IsLeftProfileType(string typeName) =>
        typeName.Equals("PROFILE_TM_L", StringComparison.OrdinalIgnoreCase) ||
        typeName.EndsWith("_L", StringComparison.OrdinalIgnoreCase);

    public static bool IsRightProfileType(string typeName) =>
        typeName.Equals("PROFILE_TM_R", StringComparison.OrdinalIgnoreCase) ||
        typeName.EndsWith("_R", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Assigns L/C/R from measured station coordinates. Names are deliberately excluded
    /// from the decision so skewed and explicitly handed nested instances cannot be mirrored.
    /// </summary>
    public static bool TryAssignStationRoles(
        IReadOnlyCollection<ProfileStation> stations,
        double minimumSeparation,
        out IReadOnlyDictionary<long, ProfileStationRole> roles,
        out string error)
    {
        roles = new Dictionary<long, ProfileStationRole>();
        error = string.Empty;
        if (stations.Count != 3 || minimumSeparation < 0 ||
            stations.Any(item => !double.IsFinite(item.Coordinate)) ||
            stations.Select(item => item.SourceElementIdValue).Distinct().Count() != 3)
        {
            error = "Profile stations must contain exactly three finite, unique instances.";
            return false;
        }

        var ordered = stations
            .OrderBy(item => item.Coordinate)
            .ThenBy(item => item.SourceElementIdValue)
            .ToArray();
        if (ordered[1].Coordinate - ordered[0].Coordinate <= minimumSeparation ||
            ordered[2].Coordinate - ordered[1].Coordinate <= minimumSeparation)
        {
            error = "Profile station coordinates are duplicated or too close to establish handedness.";
            return false;
        }

        roles = new Dictionary<long, ProfileStationRole>
        {
            [ordered[0].SourceElementIdValue] = ProfileStationRole.Left,
            [ordered[1].SourceElementIdValue] = ProfileStationRole.Center,
            [ordered[2].SourceElementIdValue] = ProfileStationRole.Right
        };
        return true;
    }

    /// <summary>
    /// Orders an unordered line-segment collection into one simple topological cycle.
    /// End points are clustered only within the explicit closure tolerance.
    /// </summary>
    public static bool TryOrderClosedLoop(
        IReadOnlyCollection<ProfileSegment3> segments,
        double closureTolerance,
        out IReadOnlyList<ProfilePoint3> orderedLoop,
        out double maximumEndpointGap,
        out string error)
    {
        orderedLoop = [];
        maximumEndpointGap = 0;
        error = string.Empty;
        if (segments.Count < 3 || !double.IsFinite(closureTolerance) || closureTolerance <= 0 ||
            segments.Any(segment => !segment.Start.IsFinite || !segment.End.IsFinite ||
                                    segment.Start.DistanceTo(segment.End) <= closureTolerance * 1e-3))
        {
            error = "Profile loop contains invalid or degenerate segments.";
            return false;
        }

        var nodes = new List<ProfilePoint3>();
        var endpointSamples = new List<List<ProfilePoint3>>();
        int ResolveNode(ProfilePoint3 point)
        {
            for (var index = 0; index < nodes.Count; index++)
            {
                if (nodes[index].DistanceTo(point) > closureTolerance) continue;
                endpointSamples[index].Add(point);
                return index;
            }
            nodes.Add(point);
            endpointSamples.Add([point]);
            return nodes.Count - 1;
        }

        var edges = segments
            .Select(segment => (A: ResolveNode(segment.Start), B: ResolveNode(segment.End)))
            .ToArray();
        if (edges.Any(edge => edge.A == edge.B))
        {
            error = "Profile loop collapses an edge within closure tolerance.";
            return false;
        }

        for (var index = 0; index < nodes.Count; index++)
        {
            var samples = endpointSamples[index];
            nodes[index] = new ProfilePoint3(
                samples.Average(point => point.X),
                samples.Average(point => point.Y),
                samples.Average(point => point.Z));
            for (var first = 0; first < samples.Count; first++)
            for (var second = first + 1; second < samples.Count; second++)
                maximumEndpointGap = Math.Max(
                    maximumEndpointGap,
                    samples[first].DistanceTo(samples[second]));
        }

        var incident = Enumerable.Range(0, nodes.Count)
            .ToDictionary(node => node, _ => new List<int>());
        for (var edgeIndex = 0; edgeIndex < edges.Length; edgeIndex++)
        {
            incident[edges[edgeIndex].A].Add(edgeIndex);
            incident[edges[edgeIndex].B].Add(edgeIndex);
        }
        if (incident.Values.Any(list => list.Count != 2))
        {
            error = "Profile segments do not form one closed degree-two loop.";
            return false;
        }

        var start = Enumerable.Range(0, nodes.Count)
            .OrderBy(index => nodes[index].X)
            .ThenBy(index => nodes[index].Y)
            .ThenBy(index => nodes[index].Z)
            .First();
        var firstEdge = incident[start]
            .OrderBy(edgeIndex =>
            {
                var edge = edges[edgeIndex];
                var other = edge.A == start ? edge.B : edge.A;
                return nodes[other].X;
            })
            .ThenBy(edgeIndex =>
            {
                var edge = edges[edgeIndex];
                var other = edge.A == start ? edge.B : edge.A;
                return nodes[other].Y;
            })
            .ThenBy(edgeIndex =>
            {
                var edge = edges[edgeIndex];
                var other = edge.A == start ? edge.B : edge.A;
                return nodes[other].Z;
            })
            .First();

        var used = new HashSet<int>();
        var result = new List<ProfilePoint3> { nodes[start] };
        var current = start;
        var nextEdge = firstEdge;
        while (true)
        {
            if (!used.Add(nextEdge))
            {
                error = "Profile traversal repeated an edge before closing.";
                return false;
            }
            var edge = edges[nextEdge];
            var next = edge.A == current ? edge.B : edge.A;
            if (next == start) break;
            result.Add(nodes[next]);
            current = next;
            var candidates = incident[current].Where(index => !used.Contains(index)).ToArray();
            if (candidates.Length != 1)
            {
                error = "Profile traversal is disconnected or ambiguous.";
                return false;
            }
            nextEdge = candidates[0];
        }

        if (used.Count != edges.Length || result.Count != nodes.Count)
        {
            error = "Profile contains multiple loops or disconnected segments.";
            return false;
        }
        orderedLoop = result;
        return true;
    }

    public static double MaximumPlaneDeviation(
        IReadOnlyCollection<ProfilePoint3> points,
        ProfilePoint3 planeOrigin,
        ProfilePoint3 planeNormal)
    {
        var length = planeNormal.Length;
        if (points.Count == 0 || length <= 1e-12 || !double.IsFinite(length))
            return double.PositiveInfinity;
        var unit = planeNormal * (1 / length);
        return points.Max(point => Math.Abs((point - planeOrigin).Dot(unit)));
    }

    public static bool HasSelfIntersection(
        IReadOnlyList<ProfilePoint3> loop,
        ProfilePoint3 planeOrigin,
        ProfilePoint3 basisU,
        ProfilePoint3 basisV,
        double tolerance)
    {
        if (loop.Count < 3 || basisU.Length <= 1e-12 || basisV.Length <= 1e-12)
            return true;
        var u = basisU * (1 / basisU.Length);
        var v = basisV * (1 / basisV.Length);
        var points = loop.Select(point =>
        {
            var offset = point - planeOrigin;
            return new PlanPoint2(offset.Dot(u), offset.Dot(v));
        }).ToArray();
        for (var i = 0; i < points.Length; i++)
        {
            var iNext = (i + 1) % points.Length;
            for (var j = i + 1; j < points.Length; j++)
            {
                var jNext = (j + 1) % points.Length;
                if (i == j || iNext == j || jNext == i) continue;
                if (SegmentsIntersect(points[i], points[iNext], points[j], points[jNext], tolerance))
                    return true;
            }
        }
        return false;
    }

    public static bool TryResolveFootingElevations(
        IReadOnlyCollection<ProfilePoint3> loop,
        double levelTolerance,
        out double bottomElevation,
        out double topElevation)
    {
        bottomElevation = topElevation = 0;
        if (loop.Count < 3 || levelTolerance <= 0 || loop.Any(point => !point.IsFinite)) return false;
        var levels = loop.Select(point => point.Z).OrderBy(value => value).ToList();
        var unique = new List<double>();
        foreach (var level in levels)
        {
            if (unique.Count == 0 || Math.Abs(level - unique[^1]) > levelTolerance)
                unique.Add(level);
        }
        if (unique.Count < 2) return false;
        bottomElevation = unique[0];
        topElevation = unique[1];
        return topElevation - bottomElevation > levelTolerance;
    }

    private static bool SegmentsIntersect(
        PlanPoint2 a, PlanPoint2 b, PlanPoint2 c, PlanPoint2 d, double tolerance)
    {
        static double Cross(PlanPoint2 p, PlanPoint2 q, PlanPoint2 r) =>
            (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        var abC = Cross(a, b, c);
        var abD = Cross(a, b, d);
        var cdA = Cross(c, d, a);
        var cdB = Cross(c, d, b);
        if (abC * abD < -tolerance * tolerance && cdA * cdB < -tolerance * tolerance)
            return true;
        return GeometryKernel.DistancePointToSegment(a, c, d) <= tolerance ||
               GeometryKernel.DistancePointToSegment(b, c, d) <= tolerance ||
               GeometryKernel.DistancePointToSegment(c, a, b) <= tolerance ||
               GeometryKernel.DistancePointToSegment(d, a, b) <= tolerance;
    }


    public static string ComputeEvidenceHash(IEnumerable<ProfileMassHashRow> masses)
    {
        static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        var rows = masses
            .Select(mass => string.Join("|",
                mass.FamilyName.Trim().ToUpperInvariant(),
                mass.TypeName.Trim().ToUpperInvariant(),
                mass.Role.ToString().ToUpperInvariant(),
                F(mass.OriginX), F(mass.OriginY), F(mass.OriginZ),
                F(mass.MinX), F(mass.MinY), F(mass.MinZ),
                F(mass.MaxX), F(mass.MaxY), F(mass.MaxZ),
                string.Join(";", mass.OrderedLoop.Select(point =>
                    string.Join(",", F(point.X), F(point.Y), F(point.Z))))))
            .OrderBy(row => row, StringComparer.Ordinal);
        var text = string.Join(";", rows);
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text)))
            .Replace("-", string.Empty, StringComparison.Ordinal);
    }
}
