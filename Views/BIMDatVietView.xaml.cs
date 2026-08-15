using System.Windows;
using BIM.DatViet.ViewModels;

namespace BIM.DatViet.Views;

public sealed partial class BIMDatVietView : Window
{
    private BIMDatVietViewModel ViewModel => (BIMDatVietViewModel)DataContext;

    public BIMDatVietView(BIMDatVietViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnAnalyze(object sender, RoutedEventArgs e) => ViewModel.Analyze();
    private void OnRotateW1(object sender, RoutedEventArgs e) => ViewModel.RotateWallOne();
    private void OnCreate(object sender, RoutedEventArgs e) => ViewModel.Create();
    private void OnConfirmationChanged(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ViewModel.Analyze();
    }
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnClosed(object? sender, EventArgs e) => ViewModel.ClearPreview();
}
