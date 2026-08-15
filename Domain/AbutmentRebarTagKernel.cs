using System.Globalization;

namespace BIM.DatViet.Domain;

/// <summary>Identity of one layer as the tag composer needs to see it.</summary>
public sealed record AbutmentTagMarkInput
{
    public required string RuleId { get; init; }

    /// <summary>Mark printed on the drawing. Two mats of one drawing mark share this value.</summary>
    public required string DrawingMark { get; init; }

    /// <summary>Mark the add-in uses internally, unique per zone by preset validation.</summary>
    public required string CanonicalMark { get; init; }
}

/// <summary>What one rebar object's tag text is composed from.</summary>
public sealed record AbutmentRebarTagRequest
{
    /// <summary>Mark label resolved by <see cref="AbutmentRebarTagKernel.TryResolveMarkLabels"/>.</summary>
    public required string MarkLabel { get; init; }

    /// <summary>
    /// Bars the drawing counts for this layer — bar positions, not rebar objects. A lapped layer
    /// holds twice as many objects as the drawing counts, and the drawing number is the one a
    /// quantity surveyor reads back.
    /// </summary>
    public required int BarPositionCount { get; init; }

    public required double BarDiameterMm { get; init; }

    /// <summary>
    /// Pitch as the drawing dimensions it. For a mat crossing the bridge at a skew this is the
    /// pitch measured along the dimension line, not the smaller pitch normal to the bar: the tag
    /// sits beside that dimension string and has to agree with it.
    ///
    /// Null for a layer laid out by anything other than a pitch, whose tag then states only the
    /// count and the diameter. Writing a pitch a layer does not have is worse than omitting one.
    /// </summary>
    public required double? DimensionedPitchMm { get; init; }

    public required int PieceIndex { get; init; }

    public required int PieceCount { get; init; }
}

/// <summary>
/// Composes the text a Vietnamese rebar tag carries, as a pure kernel: no Revit API, no I/O.
///
/// A draftsman writes <c>105Ø20a150</c> — 105 bars of Ø20 at 150 pitch. Revit cannot produce that
/// number by itself here, because each bar of these mats is its own element rather than a rebar
/// set, so its quantity tags have nothing to count. The text is therefore composed once at Create
/// time and written to a parameter; the tag family only has to read that parameter.
///
/// Two decisions are carried in the composed string and both are deliberate:
///
/// The number is the <b>bar position count</b>, never the rebar object count. A lapped layer holds
/// two objects per position, and a tag reading 82 on a drawing that dimensions 41 bars would be
/// read as 82 bars by everybody downstream. The second piece of a position therefore repeats the
/// same label with a continuation marker instead of contributing a count of its own.
///
/// The pitch is the <b>dimensioned</b> pitch. Where the mat crosses the abutment at a skew the
/// pitch normal to the bar is smaller — 136 mm against a dimensioned 150 mm at 65° — and that
/// smaller number is what the clear-spacing checks of §5.10.3.1.1 work in. It is not what the tag
/// says, because the tag sits next to the dimension chain the setting-out crew works from.
/// </summary>
public static class AbutmentRebarTagKernel
{
    /// <summary>
    /// Resolves one display label per rule, keyed by rule id. A drawing mark carried by a single
    /// layer is used as it stands; a drawing mark shared by more than one layer is suffixed from
    /// each layer's own canonical mark, so the two mats read apart on the sheet while the drawing
    /// mark is still the first thing in the string.
    ///
    /// Nothing is invented: the suffix is a token that already exists in the preset and is already
    /// written to every bar as <c>DVB_CanonicalMark</c>, so a label can always be walked back to
    /// both marks.
    /// </summary>
    public static bool TryResolveMarkLabels(
        IReadOnlyList<AbutmentTagMarkInput> layers,
        out IReadOnlyDictionary<string, string> labelByRuleId,
        out string error)
    {
        labelByRuleId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;
        if (layers is null || layers.Count == 0)
        {
            error = "ABUTMENT_TAG_LAYERS_UNDECLARED: chưa có lớp thép nào để đặt nhãn.";
            return false;
        }

        var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in layers)
        {
            if (string.IsNullOrWhiteSpace(layer.RuleId))
            {
                error = "ABUTMENT_TAG_RULE_ID_REQUIRED: một lớp thép không có ruleId nên không gắn " +
                        "được nhãn cho thanh của nó.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(layer.DrawingMark))
            {
                error = $"ABUTMENT_TAG_DRAWING_MARK_REQUIRED: rule '{layer.RuleId}' chưa khai " +
                        "sourceDrawingMark nên nhãn không truy được về mark trên bản vẽ.";
                return false;
            }
            if (labels.ContainsKey(layer.RuleId))
            {
                error = $"ABUTMENT_TAG_RULE_DUPLICATE: ruleId '{layer.RuleId}' xuất hiện nhiều lần.";
                return false;
            }
            labels[layer.RuleId] = string.Empty;
        }

        foreach (var group in layers.GroupBy(
                     layer => layer.DrawingMark.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var drawingMark = group.First().DrawingMark.Trim();
            if (group.Count() == 1)
            {
                labels[group.First().RuleId] = drawingMark;
                continue;
            }

            foreach (var layer in group)
            {
                if (string.IsNullOrWhiteSpace(layer.CanonicalMark))
                {
                    error = $"ABUTMENT_TAG_CANONICAL_MARK_REQUIRED: mark bản vẽ '{drawingMark}' do " +
                            $"{group.Count()} lớp thép cùng mang, nhưng rule '{layer.RuleId}' không có " +
                            "canonicalMark để phân biệt hai lớp trên nhãn.";
                    return false;
                }
                labels[layer.RuleId] = $"{drawingMark}-{DisambiguatingToken(layer.CanonicalMark)}";
            }
        }

        var collision = labels
            .GroupBy(entry => entry.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(entry => entry.Count() > 1);
        if (collision is not null)
        {
            error = $"ABUTMENT_TAG_MARK_INDISTINGUISHABLE: nhãn '{collision.Key}' rơi vào " +
                    $"{collision.Count()} rule ({string.Join(", ", collision.Select(item => item.Key))}), " +
                    "nên hai lớp thép khác nhau sẽ đọc như một trên bản vẽ.";
            return false;
        }

        labelByRuleId = labels;
        return true;
    }

    /// <summary>
    /// The part of a canonical mark that tells two mats of one drawing mark apart: whatever follows
    /// its last hyphen, which is how the preset already spells the top and bottom mat of a mark.
    /// A canonical mark without a hyphen contributes all of itself.
    /// </summary>
    private static string DisambiguatingToken(string canonicalMark)
    {
        var mark = canonicalMark.Trim();
        var separator = mark.LastIndexOf('-');
        if (separator < 0 || separator == mark.Length - 1) return mark;
        return mark[(separator + 1)..];
    }

    /// <summary>
    /// Composes the tag text of one rebar object. The first piece of a bar position carries the
    /// drawing label exactly as the sheet needs it; a further piece repeats it with a
    /// <c>[n/N]</c> continuation marker so a tag placed on the wrong half of a lapped bar is
    /// visible on the sheet instead of silently doubling the bar count.
    /// </summary>
    public static bool TryComposeTagText(
        AbutmentRebarTagRequest request,
        out string tagText,
        out string error)
    {
        tagText = string.Empty;
        error = string.Empty;
        if (request is null)
        {
            error = "ABUTMENT_TAG_REQUEST_UNDECLARED: chưa có dữ liệu để soạn nội dung nhãn.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(request.MarkLabel))
        {
            error = "ABUTMENT_TAG_MARK_LABEL_REQUIRED: nhãn phải mở đầu bằng số hiệu thanh thép.";
            return false;
        }
        if (request.BarPositionCount <= 0)
        {
            error = $"ABUTMENT_TAG_BAR_COUNT_INVALID: số thanh {request.BarPositionCount} phải dương; " +
                    "nhãn ghi số thanh bản vẽ đếm, không phải số đối tượng trong model.";
            return false;
        }
        if (!double.IsFinite(request.BarDiameterMm) || request.BarDiameterMm <= 0)
        {
            error = $"ABUTMENT_TAG_BAR_DIAMETER_INVALID: đường kính {request.BarDiameterMm} mm không " +
                    "dương hữu hạn.";
            return false;
        }
        if (request.DimensionedPitchMm is { } declaredPitch &&
            (!double.IsFinite(declaredPitch) || declaredPitch <= 0))
        {
            error = $"ABUTMENT_TAG_PITCH_INVALID: bước {declaredPitch} mm không dương hữu hạn.";
            return false;
        }
        if (request.PieceCount <= 0 || request.PieceIndex <= 0 || request.PieceIndex > request.PieceCount)
        {
            error = $"ABUTMENT_TAG_PIECE_INVALID: đoạn {request.PieceIndex}/{request.PieceCount} không " +
                    "hợp lệ; số thứ tự đoạn phải nằm trong 1..số đoạn.";
            return false;
        }

        var label = request.MarkLabel.Trim();
        var diameter = request.BarDiameterMm.ToString("0.###", CultureInfo.InvariantCulture);
        tagText = $"{label} {request.BarPositionCount}Ø{diameter}";
        if (request.DimensionedPitchMm is { } pitch)
            tagText += $"a{pitch.ToString("0.###", CultureInfo.InvariantCulture)}";
        if (request.PieceCount > 1 && request.PieceIndex > 1)
            tagText += $" [{request.PieceIndex}/{request.PieceCount}]";
        return true;
    }
}
