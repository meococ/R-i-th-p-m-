using System.Runtime.CompilerServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;

namespace BIM.DatViet.RevitServices;

internal static class AbutmentOwnershipIndex
{
    private static readonly ConditionalWeakTable<Document, DocumentIndex> Indexes = new();

    public static IReadOnlyList<Rebar> Find(
        Document document,
        string hostUniqueId,
        IEnumerable<string>? zones)
    {
        var allowed = zones?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var index = Indexes.GetValue(document, Build);
        if (!index.ByPrimaryHost.TryGetValue(hostUniqueId, out var rows)) return [];
        return rows
            .Where(row => allowed is null || allowed.Contains(row.Metadata.RebarZone))
            .Select(row => document.GetElement(row.Id) as Rebar)
            .Where(rebar => rebar is not null)
            .Cast<Rebar>()
            .ToArray();
    }

    public static void Invalidate(Document document) => Indexes.Remove(document);

    /// <summary>
    /// Drops every cached document. DocumentClosed carries no Document to key on, and the modeless
    /// window keeps its Document alive, so the weak table alone never expires the entry for a
    /// document Revit has already closed.
    /// </summary>
    public static void InvalidateAll() => Indexes.Clear();

    private static DocumentIndex Build(Document document)
    {
        var byHost = new Dictionary<string, List<OwnershipRow>>(StringComparer.Ordinal);
        foreach (var rebar in new FilteredElementCollector(document).OfClass(typeof(Rebar)).Cast<Rebar>())
        {
            if (!AbutmentMetadataStore.TryRead(rebar, out var metadata) ||
                string.IsNullOrWhiteSpace(metadata.HostUniqueId))
                continue;
            if (!byHost.TryGetValue(metadata.HostUniqueId, out var rows))
            {
                rows = [];
                byHost.Add(metadata.HostUniqueId, rows);
            }
            rows.Add(new OwnershipRow(rebar.Id, metadata));
        }
        return new DocumentIndex(byHost);
    }

    private sealed record OwnershipRow(ElementId Id, AbutmentOwnership Metadata);
    private sealed record DocumentIndex(Dictionary<string, List<OwnershipRow>> ByPrimaryHost);
}
