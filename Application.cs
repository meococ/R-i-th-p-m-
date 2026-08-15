using BIM.DatViet.Commands;
using Nice3point.Revit.Toolkit.External;

namespace BIM.DatViet
{
    /// <summary>
    ///     Application entry point
    /// </summary>
    [UsedImplicitly]
    public class Application : ExternalApplication
    {
        public override void OnStartup()
        {
            Preview.RebarPreviewServer.Register();
            RevitServices.RevitDocumentCacheLifecycle.Register(Application.ControlledApplication);
            CreateRibbon();
        }

        public override void OnShutdown()
        {
            RevitServices.RevitDocumentCacheLifecycle.Unregister();
            Preview.RebarPreviewServer.Unregister();
        }
        private void CreateRibbon()
        {
            var panel = Application.CreatePanel("Commands","ĐVB_ADDIN");

            // Two-line caption pushes text under the large icon (Revit ribbon layout).
            panel.AddPushButton<StartupCommand>("Rải Thép\nHố Ga")
                .SetImage("/BIM.DatViet;component/Resources/Icons/RibbonIcon16.png")
                .SetLargeImage("/BIM.DatViet;component/Resources/Icons/RibbonIcon32.png");

            panel.AddPushButton<AbutmentRebarCommand>("Rải Thép\nMố Cầu")
                .SetImage("/BIM.DatViet;component/Resources/Icons/RibbonIcon16.png")
                .SetLargeImage("/BIM.DatViet;component/Resources/Icons/RibbonIcon32.png");

            panel.AddPushButton<AbutmentRebarScheduleCommand>("Bảng Thống Kê\nThép Mố")
                .SetImage("/BIM.DatViet;component/Resources/Icons/RibbonIcon16.png")
                .SetLargeImage("/BIM.DatViet;component/Resources/Icons/RibbonIcon32.png");

            panel.AddPushButton<AbutmentCuttingListCommand>("Bảng Cắt Phôi\nThép Mố")
                .SetImage("/BIM.DatViet;component/Resources/Icons/RibbonIcon16.png")
                .SetLargeImage("/BIM.DatViet;component/Resources/Icons/RibbonIcon32.png");
        }
    }
}
