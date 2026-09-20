using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using SolventUI.ViewModels;

namespace SolventUI.Views;

/// <summary>
/// View half of Cleanup: binds to <see cref="CleanupViewModel"/> for
/// everything (scan results, large files, duplicates). What's left here is
/// only what has no bindable surface: the shared <see cref="OperationProgressBar"/>
/// event bridge (see <see cref="ViewModels.ViewModelBase"/>), forwarding the two
/// quick-access card clicks that need a real page navigation, and reading
/// the large-files threshold ComboBox's selected Tag — a plain XAML
/// convenience, not app logic.
/// </summary>
public partial class CleanupPage : Page
{
    private readonly CleanupViewModel _viewModel;

    public CleanupPage(CleanupViewModel viewModel)
    {
        // _viewModel must be assigned before InitializeComponent(): the
        // ComboBox's SelectedIndex="1" in XAML fires SelectionChanged
        // synchronously while the component tree is being built, which
        // calls ThresholdCombo_OnSelectionChanged below — if that ran
        // before this field was set, it would hit a null _viewModel.
        _viewModel = viewModel;
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

    private void ThresholdCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ThresholdCombo.SelectedItem is ComboBoxItem { Tag: string tagText } && long.TryParse(tagText, out var bytes))
            _viewModel.SetLargeFilesThreshold(bytes);
    }

    private void StartupCard_OnClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        NavigationService?.Navigate(new StartupManagerPage(SolventUI.App.Services.GetRequiredService<StartupManagerViewModel>()));

    private void ScheduleCard_OnClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        NavigationService?.Navigate(new ScheduleManagerPage(SolventUI.App.Services.GetRequiredService<ScheduleManagerViewModel>()));

    // MouseLeftButtonUp on a Border isn't natively bindable to an ICommand
    // without a behaviors library, so these two stay as one-line forwards
    // rather than pulling in a dependency for two clicks.
    private void LargeFilesCard_OnClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        _viewModel.ShowLargeFilesCommand.Execute(null);

    private void DuplicatesCard_OnClick(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        _viewModel.ShowDuplicatesCommand.Execute(null);
}
