using System.Windows.Controls;
using SolventUI.ViewModels;

namespace SolventUI.Views;

/// <summary>
/// View half of Performance: nine buttons, each bound straight to a
/// <see cref="PerformanceViewModel"/> command. Only the shared
/// <see cref="OperationProgressBar"/> event bridge (see
/// <see cref="ViewModels.ViewModelBase"/>) and the one-time drive-kind
/// detection on load stay in code-behind.
/// </summary>
public partial class PerformancePage : Page
{
    private readonly PerformanceViewModel _viewModel;

    public PerformancePage(PerformanceViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        viewModel.OperationStarted += ProgressPanel.Begin;
        viewModel.OperationProgress += ProgressPanel.Report;
        viewModel.OperationFinished += OnOperationFinished;
        ProgressPanel.CancelRequested += (_, _) => viewModel.CancelOperationCommand.Execute(null);

        Loaded += async (_, _) => await viewModel.OnLoadedAsync();
        Unloaded += (_, _) =>
        {
            viewModel.OperationStarted -= ProgressPanel.Begin;
            viewModel.OperationProgress -= ProgressPanel.Report;
            viewModel.OperationFinished -= OnOperationFinished;
        };
    }

    private async void OnOperationFinished(string message)
    {
        ProgressPanel.Finish(message);
        await Task.Delay(1200);
        ProgressPanel.Hide();
    }
}
