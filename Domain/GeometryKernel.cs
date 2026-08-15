namespace BIM.DatViet.Domain;

public readonly record struct PlanPoint2(double X, double Y)
{
    public static PlanPoint2 operator +(PlanPoint2 a, PlanPoint2 b) => new(a.X + b.X, a.Y + b.Y);
    public static PlanPoint2 operator -(PlanPoint2 a, PlanPoint2 b) => new(a.X - b.X, a.Y - b.Y);
    public static PlanPoint2 operator *(PlanPoint2 value, double scale) => new(value.X * scale, value.Y * scale);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public double Dot(PlanPoint2 other) => X * other.X + Y * other.Y;
    public PlanPoint2 Normalize() => Length > 1e-12
        ? new PlanPoint2(X / Length, Y / Length)
        : throw new InvalidOperationException("Cannot normalize a zero plan vector.");
}

public readonly record struct PlanSegment2(PlanPoint2 Start, PlanPoint2 End)
{
    public double Length => (End - Start).Length;
}

/// <summary>Pure deterministic geometry functions shared by planning and unit tests.</summary>
public static class GeometryKernel
{
    /// <summary>
    /// Converts drawing offsets measured along an authoritative boundary axis into
    /// offsets normal to the bar centerline. This keeps skew/parallelogram schedules
    /// tied to their source dimension axis instead of hard-coding a project angle.
    /// </summary>
    public static IReadOnlyList<double> ProjectOffsetsNormalToBar(
        IReadOnlyList<double> sourceOffsets,
        PlanPoint2 barDirection,
        PlanPoint2 sourceReferenceDirection)
    {
        if (sourceOffsets.Count == 0) return [];
        var bar = barDirection.Normalize();
        var reference = sourceReferenceDirection.Normalize();
        var stationNormal = new PlanPoint2(-bar.Y, bar.X);
        var scale = Math.Abs(reference.Dot(stationNormal));
        if (scale <= 1e-9)
            throw new InvalidOperationException(
                "Station reference axis is parallel to the bar; its offsets cannot define bar stations.");
        return sourceOffsets.Select(offset => offset * scale).ToArray();
    }

    public static IReadOnlyList<double> EvenPositions(double min, double max, double maximumSpacing)
    {
        if (max <= min || maximumSpacing <= 0) return [];
        var count = CountPositions(max - min, maximumSpacing);
        if (count <= 1) return [min];
        var actual = (max - min) / (count - 1);
        return Enumerable.Range(0, count).Select(index => min + index * actual).ToArray();
    }

    public static int CountPositions(double length, double maximumSpacing) =>
        length <= 0 || maximumSpacing <= 0 ? 0 : Math.Max(2, (int)Math.Ceiling(length / maximumSpacing) + 1);

    /// <summary>Monotonic-chain hull, returned counter-clockwise without collinear middle points.</summary>
    public static IReadOnlyList<PlanPoint2> ConvexHull(IReadOnlyCollection<PlanPoint2> points)
    {
        if (points.Count < 3) return [];
        var ordered = points.OrderBy(point => point.X).ThenBy(point => point.Y).ToList();
        var unique = new List<PlanPoint2>(ordered.Count);
        foreach (var point in ordered)
        {
            if (unique.Count == 0 || (point - unique[^1]).Length > 1e-8)
                unique.Add(point);
        }
        if (unique.Count < 3) return [];

        var lower = new List<PlanPoint2>();
        foreach (var point in unique)
        {
            while (lower.Count >= 2 && Cross(lower[^1] - lower[^2], point - lower[^1]) <= 1e-10)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(point);
        }

        var upper = new List<PlanPoint2>();
        for (var i = unique.Count - 1; i >= 0; i--)
        {
            var point = unique[i];
            while (upper.Count >= 2 && Cross(upper[^1] - upper[^2], point - upper[^1]) <= 1e-10)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(point);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);
        lower.AddRange(upper);
        return lower;
    }

    public static double SignedArea(IReadOnlyList<PlanPoint2> polygon)
    {
        if (polygon.Count < 3) return 0;
        var twiceArea = 0d;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            twiceArea += a.X * b.Y - a.Y * b.X;
        }
        return twiceArea / 2;
    }

    /// <summary>Insets a convex polygon by the same perpendicular distance from every edge.</summary>
    public static IReadOnlyList<PlanPoint2> InsetConvexPolygon(
        IReadOnlyList<PlanPoint2> polygon, double offset)
    {
        if (offset < 0 || !IsFinite(offset)) return [];
        return InsetConvexPolygon(polygon, _ => offset);
    }

    /// <summary>
    /// Insets a convex polygon using the distance selected for each inward unit edge normal.
    /// The source is normalized counter-clockwise before the selector is evaluated.
    /// </summary>
    public static IReadOnlyList<PlanPoint2> InsetConvexPolygon(
        IReadOnlyList<PlanPoint2> polygon,
        Func<PlanPoint2, double> offsetForInwardNormal)
    {
        if (polygon.Count < 3 || offsetForInwardNormal is null) return [];
        var source = SignedArea(polygon) >= 0 ? polygon.ToList() : polygon.Reverse().ToList();
        var edges = new PlanPoint2[source.Count];
        var normals = new PlanPoint2[source.Count];
        var offsets = new double[source.Count];
        for (var i = 0; i < source.Count; i++)
        {
            var edge = source[(i + 1) % source.Count] - source[i];
            if (edge.Length <= 1e-12) return [];
            var normal = new PlanPoint2(-edge.Y, edge.X).Normalize();
            var offset = offsetForInwardNormal(normal);
            if (offset < 0 || !IsFinite(offset)) return [];
            edges[i] = edge;
            normals[i] = normal;
            offsets[i] = offset;
        }

        var result = new List<PlanPoint2>(source.Count);
        for (var i = 0; i < source.Count; i++)
        {
            var previousIndex = (i - 1 + source.Count) % source.Count;
            var previousShifted = source[previousIndex] +
                                  normals[previousIndex] * offsets[previousIndex];
            var currentShifted = source[i] + normals[i] * offsets[i];
            if (!TryLineIntersection(
                    previousShifted, edges[previousIndex],
                    currentShifted, edges[i], out var vertex))
                return [];
            result.Add(vertex);
        }

        if (Math.Abs(SignedArea(result)) <= 1e-10) return [];
        foreach (var vertex in result)
        for (var i = 0; i < source.Count; i++)
        {
            if (Cross(edges[i], vertex - source[i]) <
                offsets[i] * edges[i].Length - 1e-9)
                return [];
        }
        return result;
    }

    /// <summary>
    /// Clips an infinite line to a convex polygon. Direction is normalized; returned t values are distances.
    /// </summary>
    public static bool TryClipLineToConvexPolygon(
        IReadOnlyList<PlanPoint2> polygon, PlanPoint2 origin, PlanPoint2 direction,
        out double start, out double end)
    {
        start = double.NegativeInfinity;
        end = double.PositiveInfinity;
        if (polygon.Count < 3 || direction.Length <= 1e-12) return false;
        var dir = direction.Normalize();
        var orientation = SignedArea(polygon) >= 0 ? 1d : -1d;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var edge = polygon[(i + 1) % polygon.Count] - a;
            var baseSide = orientation * Cross(edge, origin - a);
            var rate = orientation * Cross(edge, dir);
            if (Math.Abs(rate) <= 1e-12)
            {
                if (baseSide < -1e-9) return false;
                continue;
            }

            var boundary = -baseSide / rate;
            if (rate > 0)
                start = Math.Max(start, boundary);
            else
                end = Math.Min(end, boundary);
            if (end <= start + 1e-9) return false;
        }
        return IsFinite(start) && IsFinite(end);
    }

    /// <summary>Finds the long-edge and independent end-edge directions of a convex footing polygon.</summary>
    public static bool TryFindPrimaryEdgeDirections(
        IReadOnlyList<PlanPoint2> polygon,
        out PlanPoint2 longitudinal, out PlanPoint2 transverse, out double acuteAngleDeg)
    {
        longitudinal = transverse = default;
        acuteAngleDeg = 0;
        // Direction extraction is only valid for the verified four-edge pilot parallelogram.
        if (!TryValidateParallelogram(polygon, 1e-3, 1e-6, out _, out _)) return false;
        if (polygon.Count < 4) return false;
        var edges = new List<(PlanPoint2 Direction, double Length)>(polygon.Count);
        for (var i = 0; i < polygon.Count; i++)
        {
            var edge = polygon[(i + 1) % polygon.Count] - polygon[i];
            if (edge.Length > 1e-9) edges.Add((edge.Normalize(), edge.Length));
        }
        if (edges.Count < 4) return false;

        var primary = edges.OrderByDescending(edge => edge.Length).First();
        var secondary = edges
            .Where(edge => Math.Abs(Cross(primary.Direction, edge.Direction)) > 1e-3)
            .OrderByDescending(edge => edge.Length)
            .FirstOrDefault();
        if (secondary.Length <= 1e-9) return false;

        longitudinal = primary.Direction;
        transverse = secondary.Direction;
        var dot = Math.Clamp(Math.Abs(longitudinal.Dot(transverse)), 0, 1);
        acuteAngleDeg = Math.Acos(dot) * 180 / Math.PI;
        return acuteAngleDeg is > 5 and < 175;
    }

    public static bool TryValidateParallelogram(
        IReadOnlyList<PlanPoint2> polygon, double relativeLengthTolerance, double absoluteTolerance,
        out double longEdgeLength, out double endEdgeLength)
    {
        longEdgeLength = endEdgeLength = 0;
        if (polygon.Count != 4 || relativeLengthTolerance < 0 || absoluteTolerance < 0) return false;
        var edges = Enumerable.Range(0, 4)
            .Select(index => polygon[(index + 1) % 4] - polygon[index])
            .ToArray();
        var lengths = edges.Select(edge => edge.Length).ToArray();
        if (lengths.Any(length => length <= absoluteTolerance)) return false;

        var parallelTolerance = 1e-4;
        if (Math.Abs(Cross(edges[0].Normalize(), edges[2].Normalize())) > parallelTolerance
            || Math.Abs(Cross(edges[1].Normalize(), edges[3].Normalize())) > parallelTolerance)
            return false;
        if (!NearlyEqual(lengths[0], lengths[2], relativeLengthTolerance, absoluteTolerance)
            || !NearlyEqual(lengths[1], lengths[3], relativeLengthTolerance, absoluteTolerance))
            return false;

        longEdgeLength = Math.Max(lengths[0], lengths[1]);
        endEdgeLength = Math.Min(lengths[0], lengths[1]);
        return true;
    }

    public static bool IsInsideConvexPolygon(IReadOnlyList<PlanPoint2> polygon, PlanPoint2 point)
    {
        if (polygon.Count < 3) return false;
        var orientation = SignedArea(polygon) >= 0 ? 1d : -1d;
        for (var i = 0; i < polygon.Count; i++)
        {
            var edge = polygon[(i + 1) % polygon.Count] - polygon[i];
            if (orientation * Cross(edge, point - polygon[i]) < -1e-9) return false;
        }
        return true;
    }

    /// <summary>
    /// Production mat kernel: inward cover, true normal-spacing stations, then polygon scanline chords.
    /// </summary>
    public static IReadOnlyList<PlanSegment2> PlanConvexMatLines(
        IReadOnlyList<PlanPoint2> polygon, PlanPoint2 direction,
        double centerlineOffset, double maximumSpacing,
        double startStationClearance, double endStationClearance,
        bool includeFirst, bool includeLast)
    {
        if (maximumSpacing <= 0 || direction.Length <= 1e-12 ||
            startStationClearance < 0 || endStationClearance < 0) return [];
        var inset = InsetConvexPolygon(polygon, centerlineOffset);
        if (inset.Count < 3) return [];
        var along = direction.Normalize();
        var stationNormal = new PlanPoint2(-along.Y, along.X);
        var minStation = inset.Min(point => point.Dot(stationNormal)) + startStationClearance;
        var maxStation = inset.Max(point => point.Dot(stationNormal)) - endStationClearance;
        if (maxStation < minStation - 1e-9) return [];
        var stations = EvenPositions(minStation, maxStation, maximumSpacing).ToList();
        if (!includeLast && stations.Count > 0) stations.RemoveAt(stations.Count - 1);
        if (!includeFirst && stations.Count > 0) stations.RemoveAt(0);
        if (stations.Count == 0) stations.Add((minStation + maxStation) / 2);

        var segments = new List<PlanSegment2>(stations.Count);
        foreach (var station in stations)
        {
            var origin = stationNormal * station;
            if (!TryClipLineToConvexPolygon(inset, origin, along, out var start, out var end))
                continue;
            segments.Add(new PlanSegment2(origin + along * start, origin + along * end));
        }
        return segments;
    }

    /// <summary>
    /// Clips explicit absolute station projections to a convex centerline-boundary polygon.
    /// One segment must be returned for every valid station.
    /// </summary>
    public static IReadOnlyList<PlanSegment2> PlanConvexMatLinesAtStations(
        IReadOnlyList<PlanPoint2> centerlineBoundary,
        PlanPoint2 direction,
        IReadOnlyList<double> stationProjections)
    {
        if (centerlineBoundary.Count < 3 || direction.Length <= 1e-12 ||
            stationProjections.Count == 0 ||
            stationProjections.Any(station => !IsFinite(station)))
            return [];
        var along = direction.Normalize();
        var stationNormal = new PlanPoint2(-along.Y, along.X);
        var segments = new List<PlanSegment2>(stationProjections.Count);
        foreach (var station in stationProjections)
        {
            var origin = stationNormal * station;
            if (!TryClipLineToConvexPolygon(
                    centerlineBoundary, origin, along, out var start, out var end))
                return [];
            segments.Add(new PlanSegment2(origin + along * start, origin + along * end));
        }
        return segments;
    }

    /// <summary>
    /// Clips explicit absolute station projections to a simple face polygon.
    /// The station axis is explicit so vertical and horizontal surface rules keep a stable datum.
    /// Production currently accepts exactly one chord per station; concave split chords fail closed.
    /// </summary>
    public static IReadOnlyList<PlanSegment2> PlanPolygonMatLinesAtStations(
        IReadOnlyList<PlanPoint2> polygon,
        PlanPoint2 direction,
        PlanPoint2 stationAxis,
        double centerlineOffset,
        IReadOnlyList<double> stationProjections)
    {
        if (polygon.Count < 3 || direction.Length <= 1e-12 || stationAxis.Length <= 1e-12 ||
            centerlineOffset < 0 || stationProjections.Count == 0 ||
            stationProjections.Any(station => !IsFinite(station)))
            return [];

        var along = direction.Normalize();
        var station = stationAxis.Normalize();
        if (Math.Abs(along.Dot(station)) > 1e-8) return [];

        var work = polygon.ToList();
        var area = SignedArea(work);
        if (Math.Abs(area) <= 1e-12) return [];
        if (area < 0) work.Reverse();

        if (centerlineOffset > 1e-12)
        {
            // A robust concave offset is not implemented; do not silently lose edge cover.
            if (!IsConvexPolygon(work)) return [];
            var inset = InsetConvexPolygon(work, centerlineOffset);
            if (inset.Count < 3) return [];
            work = inset.ToList();
        }

        var segments = new List<PlanSegment2>(stationProjections.Count);
        foreach (var projection in stationProjections)
        {
            var origin = station * projection;
            var chords = ClipLineToSimplePolygon(work, origin, along);
            if (chords.Count != 1) return [];
            segments.Add(chords[0]);
        }
        return segments;
    }

    
    /// <summary>
    /// Scanline mat lines for a simple planar polygon (convex or concave).
    /// Does not fake geometry with a convex hull. Returns every chord on each station.
    /// Inset is applied only when the polygon is convex; otherwise edge cover is left to host clip.
    /// </summary>
    public static IReadOnlyList<PlanSegment2> PlanPolygonMatLines(
        IReadOnlyList<PlanPoint2> polygon, PlanPoint2 direction,
        double centerlineOffset, double maximumSpacing,
        double startStationClearance, double endStationClearance,
        bool includeFirst, bool includeLast)
    {
        if (maximumSpacing <= 0 || direction.Length <= 1e-12 ||
            startStationClearance < 0 || endStationClearance < 0 ||
            polygon.Count < 3) return [];

        var work = polygon.ToList();
        var area = SignedArea(work);
        if (Math.Abs(area) <= 1e-12) return [];
        if (area < 0) work.Reverse();

        var convex = IsConvexPolygon(work);
        if (convex && centerlineOffset > 1e-12)
        {
            var inset = InsetConvexPolygon(work, centerlineOffset);
            if (inset.Count >= 3) work = inset.ToList();
        }

        var along = direction.Normalize();
        var stationNormal = new PlanPoint2(-along.Y, along.X);
        var minStation = work.Min(point => point.Dot(stationNormal)) + startStationClearance;
        var maxStation = work.Max(point => point.Dot(stationNormal)) - endStationClearance;
        if (maxStation < minStation - 1e-9) return [];

        var stations = EvenPositions(minStation, maxStation, maximumSpacing).ToList();
        if (!includeLast && stations.Count > 0) stations.RemoveAt(stations.Count - 1);
        if (!includeFirst && stations.Count > 0) stations.RemoveAt(0);
        if (stations.Count == 0) stations.Add((minStation + maxStation) / 2);

        var segments = new List<PlanSegment2>();
        foreach (var station in stations)
        {
            var origin = stationNormal * station;
            foreach (var chord in ClipLineToSimplePolygon(work, origin, along))
                segments.Add(chord);
        }
        return segments;
    }

    public static bool IsConvexPolygon(IReadOnlyList<PlanPoint2> polygon)
    {
        if (polygon.Count < 3) return false;
        var orientation = SignedArea(polygon) >= 0 ? 1d : -1d;
        var signSeen = 0;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var c = polygon[(i + 2) % polygon.Count];
            var cross = orientation * Cross(b - a, c - b);
            if (Math.Abs(cross) <= 1e-10) continue;
            var sign = cross > 0 ? 1 : -1;
            if (signSeen == 0) signSeen = sign;
            else if (sign != signSeen) return false;
        }
        return true;
    }

    public static IReadOnlyList<PlanSegment2> ClipLineToSimplePolygon(
        IReadOnlyList<PlanPoint2> polygon, PlanPoint2 origin, PlanPoint2 direction)
    {
        if (polygon.Count < 3 || direction.Length <= 1e-12) return [];
        var dir = direction.Normalize();
        var hits = new List<double>();
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var edge = b - a;
            var den = Cross(dir, edge);
            if (Math.Abs(den) <= 1e-12) continue;
            var ao = a - origin;
            var t = Cross(ao, edge) / den;
            var u = Cross(ao, dir) / den;
            if (u < -1e-9 || u > 1 + 1e-9) continue;
            hits.Add(t);
        }

        hits.Sort();
        var unique = new List<double>();
        foreach (var hit in hits)
        {
            if (unique.Count == 0 || Math.Abs(unique[^1] - hit) > 1e-7)
                unique.Add(hit);
        }

        var segments = new List<PlanSegment2>();
        for (var i = 0; i + 1 < unique.Count; i += 2)
        {
            var t0 = unique[i];
            var t1 = unique[i + 1];
            if (t1 - t0 <= 1e-8) continue;
            var mid = origin + dir * ((t0 + t1) * 0.5);
            if (!IsInsideSimplePolygon(polygon, mid)) continue;
            segments.Add(new PlanSegment2(origin + dir * t0, origin + dir * t1));
        }
        return segments;
    }

    public static bool IsInsideSimplePolygon(IReadOnlyList<PlanPoint2> polygon, PlanPoint2 point)
    {
        if (polygon.Count < 3) return false;
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var a = polygon[i];
            var b = polygon[(i + 1) % polygon.Count];
            var denom = b.Y - a.Y;
            if (Math.Abs(denom) < 1e-18) denom = 1e-18;
            var intersect = ((a.Y > point.Y) != (b.Y > point.Y)) &&
                            (point.X < (b.X - a.X) * (point.Y - a.Y) / denom + a.X);
            if (intersect) inside = !inside;
        }
        if (inside) return true;
        for (var i = 0; i < polygon.Count; i++)
        {
            if (DistancePointToSegment(point, polygon[i], polygon[(i + 1) % polygon.Count]) <= 1e-7)
                return true;
        }
        return false;
    }


    public static double DistancePointToSegment(
        PlanPoint2 point, PlanPoint2 start, PlanPoint2 end)
    {
        var segment = end - start;
        var lengthSquared = segment.Dot(segment);
        if (lengthSquared <= 1e-18) return (point - start).Length;
        var parameter = Math.Clamp((point - start).Dot(segment) / lengthSquared, 0, 1);
        return (point - (start + segment * parameter)).Length;
    }

    /// <summary>Returns true when a circle overlaps a polygon interior or any boundary segment.</summary>
    public static bool CircleIntersectsPolygon(
        IReadOnlyList<PlanPoint2> polygon, PlanPoint2 center, double radius)
    {
        if (polygon.Count < 3 || radius < 0 || !IsFinite(radius)) return false;
        if (IsInsideConvexPolygon(polygon, center)) return true;
        for (var index = 0; index < polygon.Count; index++)
        {
            var start = polygon[index];
            var end = polygon[(index + 1) % polygon.Count];
            if (DistancePointToSegment(center, start, end) <= radius + 1e-9)
                return true;
        }
        return false;
    }


    public static double PileCenterlineExclusionRadius(
        double pileRadius, double surfaceClearance, double barDiameter)
    {
        if (pileRadius <= 0) throw new ArgumentOutOfRangeException(nameof(pileRadius));
        if (surfaceClearance < 0) throw new ArgumentOutOfRangeException(nameof(surfaceClearance));
        if (barDiameter <= 0) throw new ArgumentOutOfRangeException(nameof(barDiameter));
        return pileRadius + surfaceClearance + barDiameter / 2;
    }

    /// <summary>
    /// Planning envelope adds a placement margin beyond the required finished-model clearance.
    /// Readback still validates the unmodified required clearance.
    /// </summary>
    public static double PilePlanningExclusionRadius(
        double pileRadius, double requiredSurfaceClearance, double placementMargin, double barDiameter)
    {
        if (placementMargin < 0) throw new ArgumentOutOfRangeException(nameof(placementMargin));
        return PileCenterlineExclusionRadius(
            pileRadius, requiredSurfaceClearance + placementMargin, barDiameter);
    }

    /// <summary>
    /// True only when the pile's physical vertical envelope intersects the swept bar range.
    /// A pile that stops below or starts above the bar layer does not break a continuous mat.
    /// </summary>
    public static bool OverlapsPileVerticalEnvelope(
        double pileMinZ, double pileMaxZ, double barMinZ, double barMaxZ)
    {
        if (pileMaxZ < pileMinZ) throw new ArgumentOutOfRangeException(nameof(pileMaxZ));
        if (barMaxZ < barMinZ) throw new ArgumentOutOfRangeException(nameof(barMaxZ));
        return pileMinZ <= barMaxZ && barMinZ <= pileMaxZ;
    }

    private static bool NearlyEqual(double a, double b, double relativeTolerance, double absoluteTolerance) =>
        Math.Abs(a - b) <= Math.Max(absoluteTolerance, Math.Max(Math.Abs(a), Math.Abs(b)) * relativeTolerance);

    private static bool TryLineIntersection(
        PlanPoint2 originA, PlanPoint2 directionA,
        PlanPoint2 originB, PlanPoint2 directionB,
        out PlanPoint2 intersection)
    {
        intersection = default;
        var denominator = Cross(directionA, directionB);
        if (Math.Abs(denominator) <= 1e-12) return false;
        var t = Cross(originB - originA, directionB) / denominator;
        intersection = originA + directionA * t;
        return IsFinite(intersection.X) && IsFinite(intersection.Y);
    }

    private static double Cross(PlanPoint2 a, PlanPoint2 b) => a.X * b.Y - a.Y * b.X;

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    public static IReadOnlyList<(double Start, double End)> ClipInterval(double min, double max,
        IEnumerable<(double Start, double End)> exclusions, double minimumLength)
    {
        var intervals = new List<(double Start, double End)> { (min, max) };
        foreach (var exclusion in exclusions.OrderBy(item => item.Start))
        {
            var next = new List<(double Start, double End)>();
            foreach (var interval in intervals)
            {
                if (exclusion.End <= interval.Start || exclusion.Start >= interval.End)
                {
                    next.Add(interval);
                    continue;
                }
                if (exclusion.Start - interval.Start >= minimumLength)
                    next.Add((interval.Start, Math.Min(exclusion.Start, interval.End)));
                if (interval.End - exclusion.End >= minimumLength)
                    next.Add((Math.Max(exclusion.End, interval.Start), interval.End));
            }
            intervals = next;
        }
        return intervals;
    }


    public static double PipeEnvelopeRadius(double outsideDiameter, double directionDotNormal, double expansion)
    {
        var incidence = Math.Max(0.2, Math.Abs(directionDotNormal));
        return outsideDiameter / 2 / incidence + expansion;
    }

    public static IReadOnlyList<int> NumberWallsFromProjectNorth(IReadOnlyList<(double X, double Y)> outwardNormals)
    {
        return outwardNormals.Select((normal, index) => new
            {
                Index = index,
                Angle = NormalizeAngle(Math.Atan2(normal.X, normal.Y))
            })
            .OrderBy(value => value.Angle).ThenBy(value => value.Index)
            .Select(value => value.Index).ToArray();
    }

    /// <summary>
    /// Maps wall rebar set parameters to centerline (along, z) endpoints.
    /// Vertical: first=along, second=z0, third=z1 → bar at fixed along from z0 to z1.
    /// Horizontal: first=along0, second=z, third=along1 → bar at fixed z from along0 to along1.
    /// </summary>
    public static (double StartAlong, double StartZ, double EndAlong, double EndZ) WallSetCenterline(
        bool vertical, double first, double second, double third) =>
        vertical
            ? (first, second, first, third)
            : (first, second, third, second);

    private static double NormalizeAngle(double angle) => angle < 0 ? angle + Math.PI * 2 : angle;
}
