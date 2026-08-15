namespace BIM.DatViet.Domain;

public readonly record struct AbutmentWingFaceSemanticsKernel(
    int InnerFaceIndex,
    int OuterFaceIndex,
    double InnerDirectionScore,
    double OuterDirectionScore);

public enum AbutmentWingProfileDeclaredSideKernel
{
    Unknown,
    Left,
    Right
}

public enum AbutmentWingProfileSideKernel
{
    Left,
    Right
}

public enum AbutmentWingProfileFaceRoleKernel
{
    Inner,
    Outer
}

public readonly record struct AbutmentWingProfileOccurrenceKernel(
    long SourceElementIdValue,
    string OccurrenceKey,
    AbutmentWingProfileDeclaredSideKernel DeclaredSide,
    double OriginLongitudinal,
    double OriginTransverse,
    double NormalLongitudinal,
    double NormalTransverse);

public readonly record struct AbutmentWingProfileRoleKernel(
    long SourceElementIdValue,
    string OccurrenceKey,
    AbutmentWingProfileSideKernel Side,
    AbutmentWingProfileFaceRoleKernel FaceRole,
    double CoreDirectionScore);

/// <summary>
/// Pure geometry helpers for resolving asymmetric wing profile semantics.
/// Family parameters are checksums only; observed planar faces remain authoritative.
/// </summary>
public static class AbutmentWingProfileKernel
{
    /// <summary>
    /// Resolves the four physical wing-profile occurrences without using their names as
    /// geometry authority. LEFT/RIGHT type tokens are corroborating identity only: side is
    /// measured from the occurrence station relative to the core, while inner/outer is
    /// resolved from the oriented profile plane relative to the core.
    /// </summary>
    public static bool TryResolveProfileRoles(
        IReadOnlyCollection<AbutmentWingProfileOccurrenceKernel> occurrences,
        double coreLongitudinal,
        double coreTransverse,
        double stationTolerance,
        double directionTolerance,
        out IReadOnlyList<AbutmentWingProfileRoleKernel> roles,
        out string error)
    {
        roles = [];
        error = string.Empty;
        if (occurrences.Count != 4)
        {
            error = $"Wing profile topology requires exactly four occurrences; found {occurrences.Count}.";
            return false;
        }
        if (!double.IsFinite(coreLongitudinal) || !double.IsFinite(coreTransverse) ||
            !double.IsFinite(stationTolerance) || stationTolerance <= 0 ||
            !double.IsFinite(directionTolerance) || directionTolerance <= 0)
        {
            error = "Wing profile core coordinates and tolerances must be finite and tolerances must be positive.";
            return false;
        }
        if (occurrences.Any(item =>
                string.IsNullOrWhiteSpace(item.OccurrenceKey) ||
                !double.IsFinite(item.OriginLongitudinal) ||
                !double.IsFinite(item.OriginTransverse) ||
                !double.IsFinite(item.NormalLongitudinal) ||
                !double.IsFinite(item.NormalTransverse)) ||
            occurrences.Select(item => item.OccurrenceKey)
                .Distinct(StringComparer.Ordinal).Count() != occurrences.Count)
        {
            error = "Wing profile occurrences must have unique keys and finite placement evidence.";
            return false;
        }

        var measured = new List<(AbutmentWingProfileOccurrenceKernel Occurrence,
            AbutmentWingProfileSideKernel Side, double Score)>();
        foreach (var occurrence in occurrences)
        {
            var stationDelta = occurrence.OriginLongitudinal - coreLongitudinal;
            if (Math.Abs(stationDelta) <= stationTolerance)
            {
                error = $"Wing profile '{occurrence.OccurrenceKey}' is on the core station; LEFT/RIGHT is ambiguous.";
                return false;
            }
            var side = stationDelta < 0
                ? AbutmentWingProfileSideKernel.Left
                : AbutmentWingProfileSideKernel.Right;
            var expectedDeclaredSide = side == AbutmentWingProfileSideKernel.Left
                ? AbutmentWingProfileDeclaredSideKernel.Left
                : AbutmentWingProfileDeclaredSideKernel.Right;
            if (occurrence.DeclaredSide != expectedDeclaredSide)
            {
                error = $"Wing profile '{occurrence.OccurrenceKey}' type identity does not corroborate its measured {side} station.";
                return false;
            }

            var normalLength = Math.Sqrt(
                occurrence.NormalLongitudinal * occurrence.NormalLongitudinal +
                occurrence.NormalTransverse * occurrence.NormalTransverse);
            if (!double.IsFinite(normalLength) || normalLength <= 1e-9)
            {
                error = $"Wing profile '{occurrence.OccurrenceKey}' has a degenerate horizontal plane normal.";
                return false;
            }
            var score =
                (coreLongitudinal - occurrence.OriginLongitudinal) *
                occurrence.NormalLongitudinal / normalLength +
                (coreTransverse - occurrence.OriginTransverse) *
                occurrence.NormalTransverse / normalLength;
            measured.Add((occurrence, side, score));
        }

        var resolved = new List<AbutmentWingProfileRoleKernel>(4);
        foreach (var side in new[] { AbutmentWingProfileSideKernel.Left, AbutmentWingProfileSideKernel.Right })
        {
            var pair = measured.Where(item => item.Side == side)
                .OrderBy(item => item.Occurrence.OccurrenceKey, StringComparer.Ordinal)
                .ToArray();
            if (pair.Length != 2)
            {
                error = $"Wing profile topology requires exactly two occurrences on {side}; found {pair.Length}.";
                return false;
            }
            var positive = pair.Where(item => item.Score > directionTolerance).ToArray();
            var negative = pair.Where(item => item.Score < -directionTolerance).ToArray();
            if (positive.Length != 1 || negative.Length != 1)
            {
                error = $"Wing profile {side} inner/outer handedness is ambiguous relative to the core.";
                return false;
            }
            resolved.Add(new AbutmentWingProfileRoleKernel(
                positive[0].Occurrence.SourceElementIdValue,
                positive[0].Occurrence.OccurrenceKey,
                side,
                AbutmentWingProfileFaceRoleKernel.Inner,
                positive[0].Score));
            resolved.Add(new AbutmentWingProfileRoleKernel(
                negative[0].Occurrence.SourceElementIdValue,
                negative[0].Occurrence.OccurrenceKey,
                side,
                AbutmentWingProfileFaceRoleKernel.Outer,
                negative[0].Score));
        }

        roles = resolved
            .OrderBy(item => item.Side)
            .ThenBy(item => item.FaceRole)
            .ThenBy(item => item.OccurrenceKey, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    public static bool TryResolveInnerOuter(
        IReadOnlyList<AbutmentPlanarFaceKernel> faces,
        AbutmentSurfacePairKernel pair,
        double coreLongitudinal,
        double coreTransverse,
        out AbutmentWingFaceSemanticsKernel semantics)
    {
        semantics = default;
        var firstMatches = faces.Where(face => face.Index == pair.FrontFaceIndex).Take(2).ToList();
        var secondMatches = faces.Where(face => face.Index == pair.BackFaceIndex).Take(2).ToList();
        if (firstMatches.Count != 1 || secondMatches.Count != 1)
            return false;
        var first = firstMatches[0];
        var second = secondMatches[0];

        var firstScore = DirectionScore(first, coreLongitudinal, coreTransverse);
        var secondScore = DirectionScore(second, coreLongitudinal, coreTransverse);
        if (!double.IsFinite(firstScore) || !double.IsFinite(secondScore) ||
            Math.Abs(firstScore - secondScore) <= 1e-9)
            return false;

        var inner = firstScore > secondScore ? first : second;
        var outer = inner.Index == first.Index ? second : first;
        var innerScore = Math.Max(firstScore, secondScore);
        var outerScore = Math.Min(firstScore, secondScore);
        // The core must be seen by exactly one of the opposing face normals.
        if (innerScore <= 1e-9 || outerScore >= -1e-9)
            return false;

        semantics = new AbutmentWingFaceSemanticsKernel(
            inner.Index, outer.Index, innerScore, outerScore);
        return true;
    }

    public static double ResolveExpectedLength(
        int wingSide,
        double leftLength,
        double rightLength,
        double fallbackLength)
    {
        var sideLength = wingSide < 0 ? leftLength : rightLength;
        if (sideLength > 1e-9) return sideLength;
        return fallbackLength > 1e-9 ? fallbackLength : 0;
    }

    public static bool LengthMatches(double expected, double observed, double tolerance) =>
        double.IsFinite(expected) && double.IsFinite(observed) && double.IsFinite(tolerance) &&
        expected > 0 && observed > 0 && tolerance >= 0 &&
        Math.Abs(expected - observed) <= tolerance + 1e-9;

    private static double DirectionScore(
        AbutmentPlanarFaceKernel face,
        double coreLongitudinal,
        double coreTransverse) =>
        (coreLongitudinal - face.OriginLongitudinal) * face.NormalLongitudinal +
        (coreTransverse - face.OriginTransverse) * face.NormalTransverse;
}
