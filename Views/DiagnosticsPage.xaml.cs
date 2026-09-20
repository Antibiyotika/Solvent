using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using SolventUI.Services;
using SolventUI.ViewModels;

namespace SolventUI.Views;

/// <summary>
/// View half of Diagnostics: score, summary, findings list and each fix
/// button bind straight to <see cref="DiagnosticsViewModel"/>. What's left
/// here is what genuinely belongs to the view: the real OS SaveFileDialog
/// for Export, and performing the one navigation a fix can request.
/// </summary>
public partial class DiagnosticsPage : Page
{
    private readonly DiagnosticsViewModel _viewModel;

    public DiagnosticsPage(DiagnosticsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        viewModel.NavigateToStartupManagerRequested += OnNavigateToStartupManagerRequested;
        Unloaded += (_, _) =>
        {
            viewModel.NavigateToStartupManagerRequested -= OnNavigateToStartupManagerRequested;
            viewModel.Detach();
        };
    }

    private void OnNavigateToStartupManagerRequested() =>
        NavigationService?.Navigate(new StartupManagerPage(SolventUI.App.Services.GetRequiredService<StartupManagerViewModel>()));

    private async void Export_OnClick(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        var htmlFilterLabel = loc.Get("Diag_FilterHtml");
        var textFilterLabel = loc.Get("Diag_FilterText");
        var dialog = new SaveFileDialog
        {
            Title = loc.Get("Diag_ExportDialogTitle"),
            Filter = $"{htmlFilterLabel}|*.html|{textFilterLabel}|*.txt",
            FileName = $"Solvent-HealthCheck-{DateTime.Now:yyyy-MM-dd}.html",
        };

        if (dialog.ShowDialog() == true)
            await _viewModel.ExportAsync(dialog.FileName);
    }
}
