using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using BIM.DatViet.Recognition;

namespace BIM.DatViet.RevitServices;

public static class RevitDocumentCacheLifecycle
{
    private static ControlledApplication? _application;

    public static void Register(ControlledApplication application)
    {
        if (_application is not null) return;
        _application = application;
        _application.DocumentChanged += OnDocumentChanged;
        _application.DocumentClosing += OnDocumentClosing;
        _application.DocumentClosed += OnDocumentClosed;
        _application.DocumentOpened += OnDocumentOpened;
    }

    public static void Unregister()
    {
        if (_application is null) return;
        _application.DocumentChanged -= OnDocumentChanged;
        _application.DocumentClosing -= OnDocumentClosing;
        _application.DocumentClosed -= OnDocumentClosed;
        _application.DocumentOpened -= OnDocumentOpened;
        _application = null;
    }

    private static void OnDocumentChanged(object? sender, DocumentChangedEventArgs args) =>
        InvalidateDocument(args.GetDocument());

    private static void OnDocumentClosing(object? sender, DocumentClosingEventArgs args) =>
        InvalidateDocument(args.Document);

    private static void OnDocumentOpened(object? sender, DocumentOpenedEventArgs args) =>
        InvalidateDocument(args.Document);

    /// <summary>
    /// DocumentClosed carries no Document, so there is nothing to key an invalidation on. Drop
    /// everything: a stale entry that outlives its document is worse than a cold cache, because the
    /// modeless window holds the Document alive and the entry keeps looking valid.
    /// </summary>
    private static void OnDocumentClosed(object? sender, DocumentClosedEventArgs args)
    {
        AbutmentOwnershipIndex.InvalidateAll();
        AbutmentGeometryCache.InvalidateAll();
    }

    private static void InvalidateDocument(Document? document)
    {
        if (document is null) return;
        AbutmentOwnershipIndex.Invalidate(document);
        AbutmentGeometryCache.Invalidate(document);
    }
}
