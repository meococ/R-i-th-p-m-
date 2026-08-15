using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace BIM.DatViet.Domain;

public readonly record struct AbutmentVector(double X, double Y, double Z)
{
    public static AbutmentVector operator +(AbutmentVector a, AbutmentVector b) =>
        new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static AbutmentVector operator -(AbutmentVector a, AbutmentVector b) =>
        new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static AbutmentVector operator *(AbutmentVector value, double scale) =>
        new(value.X * scale, value.Y * scale, value.Z * scale);

    public double Length => Math.Sqrt(Dot(this));
    public double Dot(AbutmentVector other) => X * other.X + Y * other.Y + Z * other.Z;
    public AbutmentVector Cross(AbutmentVector other) =>
        new(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);

    public AbutmentVector Normalize()
    {
        var length = Length;
        if (length < 1e-9) throw new InvalidOperationException("Vector có chiều dài bằng 0.");
        return this * (1d / length);
    }
}

public readonly record struct AbutmentFrameKernel(
    AbutmentVector Origin,
    AbutmentVector Longitudinal,
    AbutmentVector Transverse,
    AbutmentVector Vertical)
{
    public AbutmentVector ToWorld(double longitudinal, double transverse, double vertical) =>
        Origin + Longitudinal * longitudinal + Transverse * transverse + Vertical * vertical;

    public AbutmentVector ToLocal(AbutmentVector world)
    {
        var offset = world - Origin;
        return new AbutmentVector(offset.Dot(Longitudinal), offset.Dot(Transverse), offset.Dot(Vertical));
    }
}

public readonly record struct AbutmentBoxKernel(
    double MinLongitudinal,
    double MaxLongitudinal,
    double MinTransverse,
    double MaxTransverse,
    double MinVertical,
    double MaxVertical)
{
    public double LongitudinalLength => MaxLongitudinal - MinLongitudinal;
    public double TransverseLength => MaxTransverse - MinTransverse;
    public double VerticalLength => MaxVertical - MinVertical;
    public double Volume => Math.Max(0, LongitudinalLength) * Math.Max(0, TransverseLength) * Math.Max(0, VerticalLength);
    public double CenterLongitudinal => (MinLongitudinal + MaxLongitudinal) / 2;
    public double CenterTransverse => (MinTransverse + MaxTransverse) / 2;
    public double CenterVertical => (MinVertical + MaxVertical) / 2;
}

public enum AbutmentCoreZone
{
    Footing,
    Stem,
    Backwall,
    WingwallLeft,
    WingwallRight
}

public readonly record struct AbutmentTopologySpecKernel(
    double FootingDepth,
    double BackwallHeight,
    double WingwallLength,
    double StemThickness,
    double FootingLength = 0,
    double FootingWidth = 0,
    double ToeWidth = 0,
    double StemBaseThickness = 0,
    double HeelWidth = 0);

public readonly record struct AbutmentWeightedDirection(AbutmentVector Direction, double Length);

/// <summary>
/// Authoritative bottom/top elevation of the footing prism in the local frame, measured from
/// the PROFILE station sections. Supersedes any elevation derived from the host bounding box.
/// </summary>
public readonly record struct AbutmentVerticalRangeKernel(double Bottom, double Top);

public static class AbutmentKernel
{
    public static bool TrySelectDominantHorizontalDirection(
        IReadOnlyCollection<AbutmentWeightedDirection> segments,
        AbutmentVector verticalCandidate,
        AbutmentVector orientationHint,
        out AbutmentVector dominant,
        out string error)
    {
        dominant = default;
        error = string.Empty;
        AbutmentVector vertical;
        AbutmentVector hint;
        try
        {
            vertical = verticalCandidate.Normalize();
            hint = orientationHint - vertical * orientationHint.Dot(vertical);
            hint = hint.Normalize();
        }
        catch (InvalidOperationException)
        {
            error = "ABUTMENT_FRAME_DIRECTION_INPUT_INVALID";
            return false;
        }

        const double parallelCosine = 0.9993908270190958; // 2 degrees.
        var groups = new List<DirectionGroup>();
        foreach (var segment in segments.Where(item => item.Length > 1e-6))
        {
            var projected = segment.Direction - vertical * segment.Direction.Dot(vertical);
            if (projected.Length < 1e-6) continue;
            var direction = projected.Normalize();
            var group = groups.FirstOrDefault(item => Math.Abs(item.Direction.Dot(direction)) >= parallelCosine);
            if (group is null)
            {
                group = new DirectionGroup(direction);
                groups.Add(group);
            }
            group.Add(direction, segment.Length);
        }

        if (groups.Count == 0)
        {
            error = "ABUTMENT_FRAME_HORIZONTAL_EDGE_MISSING";
            return false;
        }

        var ranked = groups.OrderByDescending(item => item.MaxLength)
            .ThenByDescending(item => item.TotalLength).ToList();
        if (ranked.Count > 1 && ranked[1].MaxLength >= ranked[0].MaxLength * 0.98 &&
            Math.Abs(ranked[0].Direction.Dot(ranked[1].Direction)) < parallelCosine)
        {
            error = "ABUTMENT_FRAME_HORIZONTAL_DIRECTION_AMBIGUOUS";
            return false;
        }

        dominant = ranked[0].Direction;
        if (dominant.Dot(hint) < 0) dominant *= -1;
        return true;
    }

    public static bool IdentityContainsAny(string value, IReadOnlyCollection<string> tokens)
    {
        if (tokens.Count == 0) return true;
        var normalizedValue = NormalizeIdentity(value);
        return tokens.Any(token =>
            normalizedValue.IndexOf(NormalizeIdentity(token), StringComparison.Ordinal) >= 0);
    }

    public static bool TryCreateFrame(
        AbutmentVector origin,
        AbutmentVector longitudinalCandidate,
        AbutmentVector transverseCandidate,
        AbutmentVector verticalCandidate,
        out AbutmentFrameKernel frame,
        out string error)
    {
        frame = default;
        error = string.Empty;
        try
        {
            var vertical = verticalCandidate.Normalize();
            if (vertical.Z < 0) vertical *= -1;

            var longitudinalProjected = longitudinalCandidate - vertical * longitudinalCandidate.Dot(vertical);
            if (longitudinalProjected.Length < 1e-6)
            {
                error = "ABUTMENT_FRAME_LONGITUDINAL_INVALID";
                return false;
            }

            var longitudinal = longitudinalProjected.Normalize();
            var transverse = vertical.Cross(longitudinal).Normalize();
            if (transverse.Dot(transverseCandidate) < 0)
            {
                longitudinal *= -1;
                transverse = vertical.Cross(longitudinal).Normalize();
            }

            if (Math.Abs(longitudinal.Dot(transverse)) > 1e-8 ||
                Math.Abs(longitudinal.Dot(vertical)) > 1e-8 ||
                Math.Abs(transverse.Dot(vertical)) > 1e-8)
            {
                error = "ABUTMENT_FRAME_NOT_ORTHONORMAL";
                return false;
            }

            frame = new AbutmentFrameKernel(origin, longitudinal, transverse, vertical);
            return true;
        }
        catch (InvalidOperationException)
        {
            error = "ABUTMENT_FRAME_ZERO_VECTOR";
            return false;
        }
    }

    public static double ResolveCenterlineOffset(double cover, double barDiameter)
    {
        if (cover <= 0) throw new ArgumentOutOfRangeException(nameof(cover));
        if (barDiameter <= 0) throw new ArgumentOutOfRangeException(nameof(barDiameter));
        return cover + barDiameter / 2d;
    }

    public static bool TryResolveCover(double? approvedZoneOverride, double? hostFaceCover,
        out double cover, out string error)
    {
        cover = 0;
        error = string.Empty;
        if (approvedZoneOverride is > 0)
        {
            cover = approvedZoneOverride.Value;
            return true;
        }
        if (hostFaceCover is > 0)
        {
            cover = hostFaceCover.Value;
            return true;
        }
        error = "REBAR_COVER_UNRESOLVED";
        return false;
    }

    public static bool TryPartitionCoreZones(AbutmentBoxKernel box, AbutmentTopologySpecKernel spec,
        out IReadOnlyDictionary<AbutmentCoreZone, AbutmentBoxKernel> zones, out string error) =>
        TryPartitionCoreZones(box, spec, localPoints: null, out zones, out error);

    public static bool TryPartitionCoreZones(AbutmentBoxKernel box, AbutmentTopologySpecKernel spec,
        IReadOnlyList<AbutmentVector>? localPoints,
        out IReadOnlyDictionary<AbutmentCoreZone, AbutmentBoxKernel> zones, out string error)
    {
        zones = new Dictionary<AbutmentCoreZone, AbutmentBoxKernel>();
        error = string.Empty;
        if (box.Volume <= 1e-12 || spec.FootingDepth <= 0 || spec.FootingDepth >= box.VerticalLength ||
            spec.BackwallHeight <= 0 || spec.WingwallLength <= 0 ||
            spec.StemThickness <= 0)
        {
            error = "ABUTMENT_TOPOLOGY_DIMENSIONS_AMBIGUOUS";
            return false;
        }

        if (!TryResolveFootingBox(box, spec, localPoints, out var footing, out error))
            return false;

        var stemBase = spec.StemBaseThickness > 1e-9 ? spec.StemBaseThickness : spec.StemThickness;
        if (stemBase <= 0 || stemBase >= footing.TransverseLength)
        {
            error = "ABUTMENT_TOPOLOGY_DIMENSIONS_AMBIGUOUS";
            return false;
        }

        if (spec.WingwallLength * 2 >= footing.LongitudinalLength)
        {
            error = "ABUTMENT_TOPOLOGY_DIMENSIONS_AMBIGUOUS";
            return false;
        }

        var footingTop = footing.MaxVertical;
        var backwallBottom = Math.Max(footingTop, box.MaxVertical - spec.BackwallHeight);
        if (backwallBottom <= footingTop)
        {
            error = "ABUTMENT_STEM_HEIGHT_AMBIGUOUS";
            return false;
        }

        if (!TryResolveStemTransverse(footing, spec.ToeWidth, stemBase, spec.HeelWidth,
                out var stemMinT, out var stemMaxT, out error))
            return false;

        // Above footing, stem zone uses top thickness centered on the base stem strip.
        var stemCenterT = (stemMinT + stemMaxT) / 2;
        var stemTopMinT = stemCenterT - spec.StemThickness / 2;
        var stemTopMaxT = stemCenterT + spec.StemThickness / 2;
        if (stemTopMinT < footing.MinTransverse || stemTopMaxT > footing.MaxTransverse)
        {
            stemTopMinT = stemMinT;
            stemTopMaxT = stemMaxT;
        }

        zones = new Dictionary<AbutmentCoreZone, AbutmentBoxKernel>
        {
            [AbutmentCoreZone.Footing] = footing,
            [AbutmentCoreZone.Stem] = new(footing.MinLongitudinal + spec.WingwallLength,
                footing.MaxLongitudinal - spec.WingwallLength, stemTopMinT, stemTopMaxT, footingTop, backwallBottom),
            [AbutmentCoreZone.Backwall] = new(footing.MinLongitudinal + spec.WingwallLength,
                footing.MaxLongitudinal - spec.WingwallLength, stemTopMinT, stemTopMaxT, backwallBottom, box.MaxVertical),
            [AbutmentCoreZone.WingwallLeft] = new(footing.MinLongitudinal,
                footing.MinLongitudinal + spec.WingwallLength, footing.MinTransverse, footing.MaxTransverse,
                footingTop, box.MaxVertical),
            [AbutmentCoreZone.WingwallRight] = new(footing.MaxLongitudinal - spec.WingwallLength,
                footing.MaxLongitudinal, footing.MinTransverse, footing.MaxTransverse, footingTop, box.MaxVertical)
        };
        return true;
    }

    public static bool TryResolveFootingBox(AbutmentBoxKernel overall, AbutmentTopologySpecKernel spec,
        out AbutmentBoxKernel footing, out string error) =>
        TryResolveFootingBox(overall, spec, localPoints: null, profileVertical: null, out footing, out error);

    public static bool TryResolveFootingBox(AbutmentBoxKernel overall, AbutmentTopologySpecKernel spec,
        IReadOnlyList<AbutmentVector>? localPoints, out AbutmentBoxKernel footing, out string error) =>
        TryResolveFootingBox(overall, spec, localPoints, profileVertical: null, out footing, out error);

    /// <summary>
    /// Builds the footing prism. When local solid points are supplied, horizontal placement is
    /// anchored to the bottom footing band (not the wing-inflated overall AABB center). When a
    /// profile elevation range is supplied it owns both faces, because the overall AABB floor
    /// belongs to the lowest solid of the whole host and would shift every cover plane.
    /// </summary>
    public static bool TryResolveFootingBox(AbutmentBoxKernel overall, AbutmentTopologySpecKernel spec,
        IReadOnlyList<AbutmentVector>? localPoints, AbutmentVerticalRangeKernel? profileVertical,
        out AbutmentBoxKernel footing, out string error)
    {
        footing = default;
        error = string.Empty;
        var footingBottom = overall.MinVertical;
        var footingTop = overall.MinVertical + spec.FootingDepth;
        if (profileVertical is { } vertical)
        {
            if (!double.IsFinite(vertical.Bottom) || !double.IsFinite(vertical.Top) ||
                vertical.Top - vertical.Bottom <= 1e-9)
            {
                error = "ABUTMENT_FOOTING_PROFILE_ELEVATION_INVALID";
                return false;
            }
            footingBottom = vertical.Bottom;
            footingTop = vertical.Top;
        }
        if (footingTop >= overall.MaxVertical)
        {
            error = "ABUTMENT_TOPOLOGY_DIMENSIONS_AMBIGUOUS";
            return false;
        }

        // Default: overall horizontal extents (legacy).
        var minL = overall.MinLongitudinal;
        var maxL = overall.MaxLongitudinal;
        var minT = overall.MinTransverse;
        var maxT = overall.MaxTransverse;
        var centerL = overall.CenterLongitudinal;
        var centerT = overall.CenterTransverse;

        // Prefer bottom-band solid points so splayed wings do not shift the footing prism.
        if (localPoints is { Count: > 0 })
        {
            var bandTop = footingTop + Math.Max(1e-3, spec.FootingDepth * 0.05);
            var band = localPoints
                .Where(p => p.Z >= footingBottom - 1e-4 && p.Z <= bandTop)
                .ToList();
            if (band.Count >= 4)
            {
                minL = band.Min(p => p.X);
                maxL = band.Max(p => p.X);
                minT = band.Min(p => p.Y);
                maxT = band.Max(p => p.Y);
                centerL = (minL + maxL) / 2;
                centerT = (minT + maxT) / 2;
            }
        }

        if (spec.FootingLength > 1e-9)
        {
            if (spec.FootingLength > overall.LongitudinalLength + 1e-6)
            {
                error = "ABUTMENT_FOOTING_LENGTH_EXCEEDS_HOST";
                return false;
            }
            // Keep the LxB prism inside the observed bottom band when available.
            var half = spec.FootingLength / 2;
            minL = centerL - half;
            maxL = centerL + half;
            if (minL < overall.MinLongitudinal) { minL = overall.MinLongitudinal; maxL = minL + spec.FootingLength; }
            if (maxL > overall.MaxLongitudinal) { maxL = overall.MaxLongitudinal; minL = maxL - spec.FootingLength; }
        }

        if (spec.FootingWidth > 1e-9)
        {
            if (spec.FootingWidth > overall.TransverseLength + 1e-6)
            {
                error = "ABUTMENT_FOOTING_WIDTH_EXCEEDS_HOST";
                return false;
            }
            var half = spec.FootingWidth / 2;
            minT = centerT - half;
            maxT = centerT + half;
            if (minT < overall.MinTransverse) { minT = overall.MinTransverse; maxT = minT + spec.FootingWidth; }
            if (maxT > overall.MaxTransverse) { maxT = overall.MaxTransverse; minT = maxT - spec.FootingWidth; }
        }

        footing = new AbutmentBoxKernel(minL, maxL, minT, maxT, footingBottom, footingTop);
        if (footing.Volume <= 1e-12)
        {
            error = "ABUTMENT_TOPOLOGY_DIMENSIONS_AMBIGUOUS";
            return false;
        }
        return true;
    }

    public static bool TryResolveStemTransverse(AbutmentBoxKernel footing, double toeWidth, double stemBaseThickness,
        double heelWidth, out double stemMinT, out double stemMaxT, out string error)
    {
        stemMinT = 0;
        stemMaxT = 0;
        error = string.Empty;
        if (stemBaseThickness <= 1e-9 || stemBaseThickness >= footing.TransverseLength)
        {
            error = "ABUTMENT_TOPOLOGY_DIMENSIONS_AMBIGUOUS";
            return false;
        }

        if (toeWidth > 1e-9 && heelWidth > 1e-9)
        {
            var span = toeWidth + stemBaseThickness + heelWidth;
            if (Math.Abs(span - footing.TransverseLength) > Math.Max(1e-4, footing.TransverseLength * 1e-3))
            {
                error = "ABUTMENT_TOE_HEEL_WIDTH_MISMATCH";
                return false;
            }
            stemMinT = footing.MinTransverse + toeWidth;
            stemMaxT = stemMinT + stemBaseThickness;
            return true;
        }

        var centerT = footing.CenterTransverse;
        stemMinT = centerT - stemBaseThickness / 2;
        stemMaxT = centerT + stemBaseThickness / 2;
        return true;
    }

    public static (AbutmentBoxKernel Toe, AbutmentBoxKernel Heel) SplitToeHeel(
        AbutmentBoxKernel footing, double stemCenterTransverse)
    {
        if (stemCenterTransverse <= footing.MinTransverse || stemCenterTransverse >= footing.MaxTransverse)
            throw new ArgumentOutOfRangeException(nameof(stemCenterTransverse));
        var toe = footing with { MaxTransverse = stemCenterTransverse };
        var heel = footing with { MinTransverse = stemCenterTransverse };
        return (toe, heel);
    }

    public enum AbutmentAxisChoice
    {
        Keep,
        Swap,
        /// <summary>Hai phương án sai lệch như nhau; đo đạc không đủ để quyết.</summary>
        Ambiguous
    }

    /// <summary>Sai lệch tương đối phải vượt mức này thì mới coi là một phương án thắng.</summary>
    public const double DefaultAxisAmbiguityToleranceRatio = 0.02;

    /// <summary>
    /// Chọn phương ngang nào là trục dọc, dựa trên gợi ý L/B của bệ.
    /// <para>
    /// Trả về ba trạng thái chứ không phải hai. Khi <b>cả hai</b> range đo được đều lớn hơn
    /// <b>cả hai</b> kích thước khai — đúng kịch bản mố chữ U có tường cánh xoè làm phình hộp bao
    /// theo cả hai phương — hai sai lệch bằng nhau về mặt đại số:
    /// <c>keep = (X−L) + (Y−B)</c> và <c>swap = (X−B) + (Y−L)</c> cùng bằng <c>X+Y−L−B</c>.
    /// Với số đo thật của mố này: 3931 + 20384 = 13931 + 10384 = 24315.
    /// </para>
    /// <para>
    /// Bản cũ trả <c>false</c> ở tình huống đó, khiến caller hiểu nhầm là "giữ nguyên đã đúng" và
    /// đi tiếp bằng một tiêu chí khác hẳn mà không ai khai. Nói thẳng ra là <c>Ambiguous</c> để
    /// caller phải quyết bằng bằng chứng khác hoặc dừng lại.
    /// </para>
    /// </summary>
    public static AbutmentAxisChoice ChooseLongitudinalAxis(
        double longitudinalRange,
        double transverseRange,
        double footingLength,
        double footingWidth,
        double ambiguityToleranceRatio,
        out double keepError,
        out double swapError)
    {
        keepError = double.NaN;
        swapError = double.NaN;
        if (footingLength <= 1e-9 || footingWidth <= 1e-9) return AbutmentAxisChoice.Keep;

        keepError = Math.Abs(longitudinalRange - footingLength) + Math.Abs(transverseRange - footingWidth);
        swapError = Math.Abs(longitudinalRange - footingWidth) + Math.Abs(transverseRange - footingLength);

        var margin = Math.Abs(keepError - swapError);
        var scale = Math.Max(footingLength, footingWidth);
        if (margin <= Math.Max(1e-6, scale * Math.Max(0, ambiguityToleranceRatio)))
            return AbutmentAxisChoice.Ambiguous;

        return swapError < keepError ? AbutmentAxisChoice.Swap : AbutmentAxisChoice.Keep;
    }

    /// <summary>
    /// Chooses which horizontal axis should be longitudinal using footing L/B hints.
    /// Returns true when axes should be swapped (current longitudinal better matches width).
    /// </summary>
    [Obsolete("Dùng ChooseLongitudinalAxis: hàm này gộp Ambiguous vào false nên caller không phân biệt được.")]
    public static bool ShouldSwapHorizontalAxes(double longitudinalRange, double transverseRange,
        double footingLength, double footingWidth)
    {
        if (footingLength <= 1e-9 || footingWidth <= 1e-9) return false;
        var keepError = Math.Abs(longitudinalRange - footingLength) + Math.Abs(transverseRange - footingWidth);
        var swapError = Math.Abs(longitudinalRange - footingWidth) + Math.Abs(transverseRange - footingLength);
        return swapError + 1e-6 < keepError;
    }

    public static bool LineageMatches(string expectedHostUniqueId, string expectedZone, string expectedRuleHash,
        string actualHostUniqueId, string actualZone, string actualRuleHash) =>
        expectedHostUniqueId.Equals(actualHostUniqueId, StringComparison.Ordinal) &&
        expectedZone.Equals(actualZone, StringComparison.OrdinalIgnoreCase) &&
        expectedRuleHash.Equals(actualRuleHash, StringComparison.OrdinalIgnoreCase);

    public static string ComputeGeometryHash(IEnumerable<IEnumerable<AbutmentVector>> paths, double quantum = 1e-5)
    {
        if (quantum <= 0) throw new ArgumentOutOfRangeException(nameof(quantum));
        var builder = new StringBuilder();
        foreach (var path in paths)
        {
            foreach (var point in path)
            {
                builder.Append(Math.Round(point.X / quantum)).Append(',')
                    .Append(Math.Round(point.Y / quantum)).Append(',')
                    .Append(Math.Round(point.Z / quantum)).Append(';');
            }
            builder.Append('|');
        }

        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString())))
            .Replace("-", string.Empty);
    }

    public static double OverlapRatio(AbutmentBoxKernel value, AbutmentBoxKernel reference)
    {
        var overlapL = Math.Max(0, Math.Min(value.MaxLongitudinal, reference.MaxLongitudinal) -
                                   Math.Max(value.MinLongitudinal, reference.MinLongitudinal));
        var overlapT = Math.Max(0, Math.Min(value.MaxTransverse, reference.MaxTransverse) -
                                   Math.Max(value.MinTransverse, reference.MinTransverse));
        var overlapV = Math.Max(0, Math.Min(value.MaxVertical, reference.MaxVertical) -
                                   Math.Max(value.MinVertical, reference.MinVertical));
        var overlap = overlapL * overlapT * overlapV;
        return value.Volume <= 1e-12 ? 0 : overlap / value.Volume;
    }

    private static string NormalizeIdentity(string value)
    {
        var normalized = (value ?? string.Empty).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            var mapped = character == '\u0111' ? 'd' : character == '\u0110' ? 'D' : character;
            if (char.IsLetterOrDigit(mapped)) builder.Append(char.ToUpperInvariant(mapped));
        }
        return builder.ToString();
    }

    private sealed class DirectionGroup
    {
        private AbutmentVector _weighted;

        internal DirectionGroup(AbutmentVector direction) => Direction = direction;
        internal AbutmentVector Direction { get; private set; }
        internal double MaxLength { get; private set; }
        internal double TotalLength { get; private set; }

        internal void Add(AbutmentVector direction, double length)
        {
            if (Direction.Dot(direction) < 0) direction *= -1;
            _weighted += direction * length;
            TotalLength += length;
            MaxLength = Math.Max(MaxLength, length);
            Direction = _weighted.Normalize();
        }
    }
}
