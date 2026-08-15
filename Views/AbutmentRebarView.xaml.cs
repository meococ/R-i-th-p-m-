using System.Linq;
using System.Windows;
using BIM.DatViet.Controllers;
using BIM.DatViet.Infrastructure;
using BIM.DatViet.ViewModels;

namespace BIM.DatViet.Views;

public partial class AbutmentRebarView : Window
{
    public AbutmentRebarViewModel ViewModel { get; }

    public AbutmentRebarView(
        AbutmentRebarController controller,
        AbutmentRevitExternalEventDispatcher dispatcher)
    {
        InitializeComponent();
        MaxWidth = SystemParameters.WorkArea.Width;
        MaxHeight = SystemParameters.WorkArea.Height;
        ViewModel = new AbutmentRebarViewModel(controller, dispatcher);
        DataContext = ViewModel;

        ViewModel.RequestViewportLoadScene += OnLoadScene;
        ViewModel.RequestViewportHighlight += OnHighlight;
        ViewModel.RequestViewportRenderMode += OnSetViewportRenderMode;
        Unloaded += (s, e) =>
        {
            ViewModel.RequestViewportLoadScene -= OnLoadScene;
            ViewModel.RequestViewportHighlight -= OnHighlight;
            ViewModel.RequestViewportRenderMode -= OnSetViewportRenderMode;
        };
    }

    public void PrepareInitialPreview() => ViewModel.AnalyzeForInitialPreview();


    private void OnLoadScene(Models.AbutmentHostContext ctx, System.Collections.Generic.IReadOnlyList<Models.AbutmentZoneDescriptor> zones)
    {
        try
        {
            ZoneViewport.SetRenderMode(ViewModel.ViewportRenderMode);
            ZoneViewport.LoadScene(ctx, zones);
        }
        catch (Exception exception)
        {
            Infrastructure.AbutmentDiagnostics.LogException("ABUTMENT_VIEWPORT_LOAD_SCENE_FAILED", exception);
            ViewModel.ShowViewportLoadFailure(exception.GetBaseException().Message);
        }
    }


    private void OnHighlight(Models.AbutmentZoneKind kind, Models.AbutmentRebarPlan? plan, bool showRebars)
    {
        var desc = ViewModel.SelectedZoneDescriptor?.Kind == kind
            ? ViewModel.SelectedZoneDescriptor
            : ViewModel.Controller.Classification?.Zones.FirstOrDefault(z => z.Kind == kind);
        if (desc is not null) ZoneViewport.ShowSelection(desc, plan, showRebars);
    }

    private void OnSetViewportRenderMode(Models.AbutmentViewportRenderMode renderMode) =>
        ZoneViewport.SetRenderMode(renderMode);


    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
