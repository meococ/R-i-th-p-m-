using System.Globalization;
using System.Text;

namespace BIM.DatViet.Domain;

/// <summary>
/// Phân mặt cắt lõi mố thành Bệ / Thân / Tường đỉnh bằng <b>dải cao độ</b>, không bằng đếm đỉnh.
/// <para>
/// Thay cho <see cref="AbutmentCoreProfileKernel"/>, vốn đòi đúng 14 đỉnh theo một thứ tự cố định
/// và gán vùng bằng chỉ số cứng. Chữ ký đó không phủ nổi ngay ba mặt cắt của <b>cùng một mố</b>:
/// log của family <c>MO CAU - LBH</c> ghi mặt cắt TRÁI trượt ở mắt xích "backwall return/corbel
/// chain" trong khi Center và Right qua được.
/// </para>
/// <para>
/// Cách làm ở đây đọc thứ mà mọi mố chữ U đều có, bất kể chi tiết vai kê hay vút: bề rộng tiết diện
/// đổi đột ngột ở đâu. Trên chính dữ liệu chuẩn của adapter cũ, ba vai đo được là 0 (75,9%),
/// 9,843 (64,3%) và 12,451 (37,5%). Hai vai thấp nhất chia ra Bệ / Thân / Tường đỉnh; vai thứ ba là
/// đỉnh corbel, nằm <b>trên</b> mốc Thân nên không ảnh hưởng phép chia. Nói cách khác chuỗi corbel
/// không cần được hiểu, chỉ cần không nằm dưới mốc — trong khi chữ ký cũ phải khớp từng đỉnh của nó
/// và chết vì đúng chỗ đó.
/// </para>
/// <para>
/// Vùng sinh ra là dải ngang thuần, nên có thể lệch chút ít so với vùng gán theo chỉ số của adapter
/// cũ ở chỗ có cạnh xiên vắt ngang mốc vai. Đó là chủ ý: dải ngang là thứ định nghĩa được cho mọi
/// hình, còn "đỉnh số 10" thì không.
/// </para>
/// </summary>
public static class AbutmentCoreProfileBandKernel
{
    public enum RegionKind { Footing, Stem, Backwall }

    /// <summary>Một mức cao độ mà bề rộng tiết diện đổi đột ngột.</summary>
    public readonly record struct Shoulder(
        double Elevation,
        double WidthBelow,
        double WidthAbove,
        double RelativeStep);

    public readonly record struct BandRegion(
        RegionKind Kind,
        IReadOnlyList<PlanPoint2> Polygon,
        double Area,
        double MinElevation,
        double MaxElevation);

    public readonly record struct Options(
        double ClosureTolerance,
        double LevelTolerance,
        double MinimumWidthDropRatio,
        double AreaTolerance);

    public readonly record struct Resolution(
        IReadOnlyList<PlanPoint2> OrderedProfile,
        BandRegion Footing,
        BandRegion Stem,
        BandRegion Backwall,
        double FootingTopElevation,
        double StemTopElevation,
        IReadOnlyList<Shoulder> Shoulders,
        double ProfileArea,
        double RegionAreaSum);

    public const string LevelsNotSeparableCode = "ABUTMENT_CORE_PROFILE_BAND_LEVELS_NOT_SEPARABLE";
    public const string FootingTopUnresolvedCode = "ABUTMENT_CORE_PROFILE_BAND_FOOTING_TOP_UNRESOLVED";
    public const string StemTopUnresolvedCode = "ABUTMENT_CORE_PROFILE_BAND_STEM_TOP_UNRESOLVED";
    public const string RegionNotSimpleCode = "ABUTMENT_CORE_PROFILE_BAND_REGION_NOT_SIMPLE";
    public const string AreaMismatchCode = "ABUTMENT_CORE_PROFILE_BAND_AREA_MISMATCH";
    public const string LoopInvalidCode = "ABUTMENT_CORE_PROFILE_BAND_LOOP_INVALID";

    public static Options DefaultOptions(double closureTolerance, double levelTolerance) =>
        new(closureTolerance, levelTolerance, MinimumWidthDropRatio: 0.15,
            AreaTolerance: Math.Max(1e-6, levelTolerance * levelTolerance * 100));

    public static bool TryResolve(
        IReadOnlyCollection<PlanSegment2> segments,
        Options options,
        out Resolution resolution,
        out string error)
    {
        resolution = default;
        error = string.Empty;

        if (segments is null || segments.Count < 4)
        {
            error = $"{LoopInvalidCode}: cần ít nhất 4 cạnh để tạo một tiết diện kín; nhận được " +
                    $"{segments?.Count ?? 0}.";
            return false;
        }
        if (!(options.ClosureTolerance > 0) || !(options.LevelTolerance > 0) ||
            !(options.MinimumWidthDropRatio > 0) || options.MinimumWidthDropRatio >= 1)
        {
            error = $"{LoopInvalidCode}: tham số phân dải không hợp lệ (closure=" +
                    $"{F(options.ClosureTolerance)}, level={F(options.LevelTolerance)}, " +
                    $"dropRatio={F(options.MinimumWidthDropRatio)}).";
            return false;
        }

        if (!TryOrderLoop(segments, options.ClosureTolerance, out var loop, out error)) return false;
        loop = Simplify(loop, options.ClosureTolerance);
        if (loop.Count < 3)
        {
            error = $"{LoopInvalidCode}: sau khi gộp đỉnh trùng và bỏ đỉnh thẳng hàng chỉ còn " +
                    $"{loop.Count} đỉnh.";
            return false;
        }

        var profileArea = Area(loop);
        if (profileArea <= options.AreaTolerance)
        {
            error = $"{LoopInvalidCode}: tiết diện có diện tích {F(profileArea)}, coi như suy biến.";
            return false;
        }

        if (!TryLevels(loop, options.LevelTolerance, out var levels, out error)) return false;
        var shoulders = FindShoulders(loop, levels);
        var table = DescribeShoulders(shoulders);

        var significant = shoulders
            .Where(item => item.RelativeStep >= options.MinimumWidthDropRatio)
            .OrderBy(item => item.Elevation)
            .ToList();

        if (significant.Count == 0)
        {
            error = $"{FootingTopUnresolvedCode}: không đo được vai nào có bề rộng tụt tối thiểu " +
                    $"{F(options.MinimumWidthDropRatio * 100)}%; {table} Không tách được Bệ khỏi " +
                    $"Thân. Khai stemTopElevation từ tham số family hoặc hạ ngưỡng tụt bề rộng rồi " +
                    $"chạy lại Phân tích.";
            return false;
        }
        if (significant.Count == 1)
        {
            error = $"{StemTopUnresolvedCode}: chỉ đo được một vai ở cao độ " +
                    $"{F(significant[0].Elevation)}; {table} Tách được Bệ nhưng không tách được Thân " +
                    $"khỏi Tường đỉnh nên hai zone đó bị khóa.";
            return false;
        }

        var footingTop = significant[0].Elevation;
        var stemTop = significant[1].Elevation;
        var bottom = loop.Min(point => point.Y);
        var top = loop.Max(point => point.Y);

        if (!TryBand(loop, bottom, footingTop, RegionKind.Footing, options, out var footing, out error) ||
            !TryBand(loop, footingTop, stemTop, RegionKind.Stem, options, out var stem, out error) ||
            !TryBand(loop, stemTop, top, RegionKind.Backwall, options, out var backwall, out error))
            return false;

        var regionSum = footing.Area + stem.Area + backwall.Area;
        if (Math.Abs(profileArea - regionSum) > options.AreaTolerance)
        {
            error = $"{AreaMismatchCode}: tổng ba vùng {F(regionSum)} không dựng lại được diện tích " +
                    $"tiết diện {F(profileArea)} (lệch {F(Math.Abs(profileArea - regionSum))}); " +
                    $"phân dải đã bỏ sót hoặc đếm trùng một phần hình. {table}";
            return false;
        }

        resolution = new Resolution(
            loop, footing, stem, backwall, footingTop, stemTop, shoulders, profileArea, regionSum);
        return true;
    }

    /// <summary>Bảng vai đo được — thứ kỹ sư cần đọc khi một family mới không phân dải được.</summary>
    public static string DescribeShoulders(IReadOnlyList<Shoulder> shoulders)
    {
        if (shoulders.Count == 0) return "tiết diện đo được không có vai nào.";
        var builder = new StringBuilder("tiết diện đo được có các vai [");
        for (var index = 0; index < shoulders.Count; index++)
        {
            if (index > 0) builder.Append("; ");
            var shoulder = shoulders[index];
            builder.Append(CultureInfo.InvariantCulture,
                $"{F(shoulder.Elevation)}: {F(shoulder.WidthBelow)}→{F(shoulder.WidthAbove)} " +
                $"({F(shoulder.RelativeStep * 100)}%)");
        }
        return builder.Append("].").ToString();
    }

    private static bool TryOrderLoop(
        IReadOnlyCollection<PlanSegment2> segments,
        double closureTolerance,
        out IReadOnlyList<PlanPoint2> loop,
        out string error)
    {
        loop = [];
        error = string.Empty;
        var source = segments.Select(segment => new AbutmentProfileMassKernel.ProfileSegment3(
                new AbutmentProfileMassKernel.ProfilePoint3(segment.Start.X, 0, segment.Start.Y),
                new AbutmentProfileMassKernel.ProfilePoint3(segment.End.X, 0, segment.End.Y)))
            .ToArray();

        if (!AbutmentProfileMassKernel.TryOrderClosedLoop(
                source, closureTolerance, out var ordered, out _, out var orderError))
        {
            error = $"{LoopInvalidCode}: các cạnh không tạo thành một vòng kín duy nhất: {orderError}";
            return false;
        }

        loop = ordered.Select(point => new PlanPoint2(point.X, point.Z)).ToArray();
        if (loop.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            error = $"{LoopInvalidCode}: tiết diện chứa đỉnh không hữu hạn.";
            return false;
        }
        return true;
    }

    /// <summary>Gộp đỉnh trùng và bỏ đỉnh nằm giữa hai cạnh thẳng hàng.</summary>
    private static IReadOnlyList<PlanPoint2> Simplify(
        IReadOnlyList<PlanPoint2> loop, double tolerance)
    {
        var deduped = new List<PlanPoint2>();
        foreach (var point in loop)
        {
            if (deduped.Count > 0 && Near(deduped[^1], point, tolerance)) continue;
            deduped.Add(point);
        }
        while (deduped.Count > 1 && Near(deduped[0], deduped[^1], tolerance)) deduped.RemoveAt(deduped.Count - 1);
        if (deduped.Count < 3) return deduped;

        var kept = new List<PlanPoint2>();
        for (var index = 0; index < deduped.Count; index++)
        {
            var previous = deduped[(index - 1 + deduped.Count) % deduped.Count];
            var current = deduped[index];
            var next = deduped[(index + 1) % deduped.Count];
            var cross = (current.X - previous.X) * (next.Y - previous.Y) -
                        (current.Y - previous.Y) * (next.X - previous.X);
            var span = Math.Max(Distance(previous, next), tolerance);
            if (Math.Abs(cross) / span > tolerance) kept.Add(current);
        }
        return kept.Count >= 3 ? kept : deduped;
    }

    /// <summary>Một cụm cao độ: các đỉnh cách nhau dưới dung sai được coi là cùng một mức.</summary>
    private readonly record struct Level(double Minimum, double Maximum, double Representative);

    private static bool TryLevels(
        IReadOnlyList<PlanPoint2> loop,
        double levelTolerance,
        out IReadOnlyList<Level> levels,
        out string error)
    {
        error = string.Empty;
        var ordered = loop.Select(point => point.Y).OrderBy(value => value).ToList();
        var clustered = new List<Level>();
        foreach (var value in ordered)
        {
            if (clustered.Count > 0 && value - clustered[^1].Maximum <= levelTolerance)
            {
                var open = clustered[^1];
                clustered[^1] = open with { Maximum = value };
                continue;
            }
            clustered.Add(new Level(value, value, value));
        }
        levels = clustered;

        if (clustered.Count < 3)
        {
            error = $"{LevelsNotSeparableCode}: chỉ gom được {clustered.Count} mức cao độ khác nhau " +
                    $"với dung sai {F(levelTolerance)}; một mố chữ U cần ít nhất ba mức để có Bệ, " +
                    $"Thân và Tường đỉnh.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Đo bề rộng ở <b>giữa dải</b> chứ không dò sát mốc.
    /// <para>
    /// Một cụm cao độ có thể dày tới cả dung sai — chuỗi corbel của mố pilot có hai mức cách nhau
    /// 0,016 nên bị gộp làm một. Dò sát mốc sẽ rơi vào giữa cạnh xiên của chính cụm đó và đọc ra
    /// một bước nhảy bề rộng không có thật (đo được 23% ở mốc 12,451). Giữa dải là chỗ duy nhất
    /// tiết diện chắc chắn không còn mơ hồ.
    /// </para>
    /// </summary>
    private static IReadOnlyList<Shoulder> FindShoulders(
        IReadOnlyList<PlanPoint2> loop,
        IReadOnlyList<Level> levels)
    {
        var shoulders = new List<Shoulder>();
        for (var index = 1; index < levels.Count - 1; index++)
        {
            var below = Width(loop, Midpoint(levels[index - 1], levels[index]), out _);
            var above = Width(loop, Midpoint(levels[index], levels[index + 1]), out _);
            var reference = Math.Max(below, above);
            if (reference <= 0) continue;
            shoulders.Add(new Shoulder(
                levels[index].Representative, below, above, (below - above) / reference));
        }
        return shoulders;
    }

    private static double Midpoint(Level lower, Level upper) => (lower.Maximum + upper.Minimum) / 2;

    /// <summary>Tổng bề rộng tiết diện tại một cao độ, kèm số đoạn rời tạo nên nó.</summary>
    private static double Width(IReadOnlyList<PlanPoint2> loop, double elevation, out int runCount)
    {
        var crossings = new List<double>();
        for (var index = 0; index < loop.Count; index++)
        {
            var a = loop[index];
            var b = loop[(index + 1) % loop.Count];
            if (a.Y <= elevation && b.Y > elevation || b.Y <= elevation && a.Y > elevation)
                crossings.Add(a.X + (b.X - a.X) * (elevation - a.Y) / (b.Y - a.Y));
        }
        crossings.Sort();
        runCount = crossings.Count / 2;
        var total = 0d;
        for (var index = 0; index + 1 < crossings.Count; index += 2)
            total += crossings[index + 1] - crossings[index];
        return total;
    }

    private static bool TryBand(
        IReadOnlyList<PlanPoint2> loop,
        double lower,
        double upper,
        RegionKind kind,
        Options options,
        out BandRegion region,
        out string error)
    {
        region = default;
        error = string.Empty;

        // Một dải chỉ hợp lệ khi nó là một vòng đơn. Đếm số đoạn rời ở giữa dải là phép kiểm rẻ và
        // dứt khoát: mố có lỗ rỗng hoặc hai thân rời sẽ lộ ra ngay thay vì bị gộp im lặng.
        Width(loop, (lower + upper) / 2, out var runs);
        if (runs != 1)
        {
            error = $"{RegionNotSimpleCode}: dải {kind} từ {F(lower)} đến {F(upper)} cắt ra {runs} " +
                    $"vùng rời ở giữa dải; phân dải chỉ giải được vùng liền một mạch.";
            return false;
        }

        // Giữ phần y ≥ lower rồi giữ tiếp phần y ≤ upper.
        var clipped = ClipAbove(ClipBelow(loop, lower), upper);
        clipped = Simplify(clipped, options.ClosureTolerance);
        if (clipped.Count < 3)
        {
            error = $"{RegionNotSimpleCode}: dải {kind} từ {F(lower)} đến {F(upper)} cắt ra đa giác " +
                    $"chỉ {clipped.Count} đỉnh.";
            return false;
        }

        var area = Area(clipped);
        if (area <= options.AreaTolerance)
        {
            error = $"{RegionNotSimpleCode}: dải {kind} từ {F(lower)} đến {F(upper)} có diện tích " +
                    $"{F(area)}, coi như suy biến.";
            return false;
        }

        region = new BandRegion(kind, clipped, area,
            clipped.Min(point => point.Y), clipped.Max(point => point.Y));
        return true;
    }

    /// <summary>Giữ phần <c>y ≥ threshold</c>.</summary>
    private static IReadOnlyList<PlanPoint2> ClipBelow(IReadOnlyList<PlanPoint2> loop, double threshold) =>
        Clip(loop, point => point.Y >= threshold, threshold);

    /// <summary>Giữ phần <c>y ≤ threshold</c>.</summary>
    private static IReadOnlyList<PlanPoint2> ClipAbove(IReadOnlyList<PlanPoint2> loop, double threshold) =>
        Clip(loop, point => point.Y <= threshold, threshold);

    private static IReadOnlyList<PlanPoint2> Clip(
        IReadOnlyList<PlanPoint2> loop, Func<PlanPoint2, bool> keep, double threshold)
    {
        var result = new List<PlanPoint2>();
        for (var index = 0; index < loop.Count; index++)
        {
            var current = loop[index];
            var next = loop[(index + 1) % loop.Count];
            var keepCurrent = keep(current);
            var keepNext = keep(next);

            if (keepCurrent) result.Add(current);
            if (keepCurrent == keepNext) continue;
            if (Math.Abs(next.Y - current.Y) < double.Epsilon) continue;
            var t = (threshold - current.Y) / (next.Y - current.Y);
            result.Add(new PlanPoint2(current.X + (next.X - current.X) * t, threshold));
        }
        return result;
    }

    private static double Area(IReadOnlyList<PlanPoint2> polygon)
    {
        var sum = 0d;
        for (var index = 0; index < polygon.Count; index++)
        {
            var a = polygon[index];
            var b = polygon[(index + 1) % polygon.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(sum) / 2;
    }

    private static bool Near(PlanPoint2 first, PlanPoint2 second, double tolerance) =>
        Distance(first, second) <= tolerance;

    private static double Distance(PlanPoint2 first, PlanPoint2 second) =>
        Math.Sqrt((first.X - second.X) * (first.X - second.X) +
                  (first.Y - second.Y) * (first.Y - second.Y));

    private static string F(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}
