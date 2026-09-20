using System.Windows.Controls;
using SolventUI.ViewModels;

namespace SolventUI.Views;

/// <summary>
/// View half of Repair &amp; Security: every button binds straight to a
/// <see cref="RepairSecurityViewModel"/> command. Only the shared
/// <see cref="OperationProgressBar"/> event bridge stays in code-behind
/// (see <see cref="ViewModels.ViewModelBase"/>).
/// </summary>
public partial class RepairSecurityPage : Page
{
    public RepairSecurityPage(RepairSecurityViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.OperationStarted += ProgressPanel.Begin;
        viewModel.OperationProgress += ProgressPanel.Report;
        viewModel.OperationFinished += OnOperationFinished;
        ProgressPanel.CancelRequested += (_, _) => viewModel.CancelOperationCommand.Execute(null);

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
