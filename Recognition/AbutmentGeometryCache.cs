using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;
using BIM.DatViet.Models;

namespace BIM.DatViet.Recognition;

public static class AbutmentGeometryCache
{
    private static readonly ConditionalWeakTable<Document, Dictionary<string, CacheEntry>> Caches = new();
    private static readonly object Sync = new();

    public static AbutmentClassification GetOrExtract(
        Document document,
        Element host,
        AbutmentRebarPresetV1 preset)
    {
        var key = $"{host.UniqueId}|{preset.RuleHash}";
        lock (Sync)
        {
            var cache = Caches.GetValue(document, _ => new Dictionary<string, CacheEntry>(StringComparer.Ordinal));
            if (cache.TryGetValue(key, out var cached)) return cached.ToClassification();
        }

        var extracted = new AbutmentGeometryExtractor(document).Extract(host, preset);
        var entry = new CacheEntry(extracted.Status, extracted.Context, extracted.Issues.ToArray());
        lock (Sync)
        {
            var cache = Caches.GetValue(document, _ => new Dictionary<string, CacheEntry>(StringComparer.Ordinal));
            cache[key] = entry;
        }
        return entry.ToClassification();
    }

    public static void Invalidate(Document document)
    {
        lock (Sync) Caches.Remove(document);
    }

    /// <summary>
    /// Drops every cached document. Needed because DocumentClosed carries no Document to key on,
    /// and the modeless window keeps its Document alive, so the weak table alone never expires the
    /// entry for a document Revit has already closed.
    /// </summary>
    public static void InvalidateAll()
    {
        lock (Sync) Caches.Clear();
    }

    private sealed record CacheEntry(
        AbutmentResolutionStatus Status,
        AbutmentHostContext? Context,
        IReadOnlyList<ValidationIssue> Issues)
    {
        public AbutmentClassification ToClassification()
        {
            var result = new AbutmentClassification { Status = Status, Context = Context };
            result.Issues.AddRange(Issues);
            return result;
        }
    }
}
