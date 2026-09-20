using System.Windows;
using System.Windows.Controls;
using SolventUI.Models;
using SolventUI.ViewModels;

namespace SolventUI.Views;

/// <summary>
/// View half of Startup Manager: the list, search box, and toggle/remove
/// buttons all bind straight to <see cref="StartupManagerViewModel"/>.
/// What's left in code-behind: the shared <see cref="OperationProgressBar"/>
/// event bridge (see <see cref="ViewModels.ViewModelBase"/>), and forwarding
/// the ToggleSwitch's Checked/Unchecked — its TwoWay binding already wrote
/// IsEnabled, this just tells the view model to persist that.
/// </summary>
public partial class StartupManagerPage : Page
{
    private readonly StartupManagerViewModel _viewModel;

    public StartupManagerPage(StartupManagerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = _viewModel = viewModel;

        viewModel.OperationStarted += ProgressPanel.Begin;
        viewModel.OperationProgress += ProgressPanel.Report;
        viewModel.OperationFinished += OnOperationFinished;
        ProgressPanel.CancelRequested += (_, _) => viewModel.CancelOperationCommand.Execute(null);

        Loaded += async (_, _) => await viewModel.LoadItemsCommand.ExecuteAsync(null);
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
        await Task.Delay(800);
        ProgressPanel.Hide();
    }

    private void Toggle_OnChanged(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: StartupItem item })
            _viewModel.PersistEnabledState(item);
    }
}
