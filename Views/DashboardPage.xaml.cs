using System.ComponentModel;
using System.Windows.Controls;
using SolventUI.ViewModels;

namespace SolventUI.Views;

/// <summary>
/// View half of the Dashboard: owns no state and no logic of its own —
/// everything shown here comes from <see cref="DashboardViewModel"/> via
/// bindings (see DashboardPage.xaml). What's left in code-behind is only
/// what has no bindable surface yet: forwarding the view model's operation
/// events to the imperative <see cref="OperationProgressBar"/> widget, and
/// starting/stopping the live-resource timer with the page's lifetime.
/// </summary>
public partial class DashboardPage : Page
{
    private readonly DashboardViewModel _viewModel;

    public DashboardPage(DashboardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        viewModel.OperationStarted += ProgressPanel.Begin;
        viewModel.OperationProgress += ProgressPanel.Report;
        viewModel.OperationFinished += OnOperationFinished;
        ProgressPanel.CancelRequested += (_, _) => viewModel.CancelOperationCommand.Execute(null);
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Loaded += (_, _) => viewModel.OnLoaded();
        Unloaded += (_, _) =>
        {
            viewModel.OnUnloaded();
            viewModel.OperationStarted -= ProgressPanel.Begin;
            viewModel.OperationProgress -= ProgressPanel.Report;
            viewModel.OperationFinished -= OnOperationFinished;
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        };
    }

    // See the HealthBrushKey property doc in DashboardViewModel: a plain
    // converter binding on Foreground would only resolve once, so instead
    // the page re-applies SetResourceReference itself whenever the key
    // changes — same live theme-tracking the old code-behind had.
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DashboardViewModel.HealthBrushKey))
            HealthScoreLabel.SetResourceReference(TextBlock.ForegroundProperty, _viewModel.HealthBrushKey);
    }

    private async void OnOperationFinished(string message)
    {
        ProgressPanel.Finish(message);
        await Task.Delay(1500);
        ProgressPanel.Hide();
    }
}
