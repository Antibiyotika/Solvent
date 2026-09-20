using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.ViewModels;

/// <summary>
/// Common base for page view models that run one long maintenance
/// operation at a time behind a progress indicator — the "Run All / Scan /
/// Repair" pattern every such page in Solvent already followed, previously
/// copy-pasted into each page's code-behind.
///
/// <see cref="SolventUI.Views.OperationProgressBar"/> is a small imperative widget
/// (Begin/Report/Finish/Hide), not yet bindable, so the view still owns it
/// directly. What moves here is everything else: the view model raises
/// <see cref="OperationStarted"/>/<see cref="OperationProgress"/>/
/// <see cref="OperationFinished"/>, and the view's constructor forwards
/// them straight to its progress bar — a few lines of glue instead of a
/// duplicated try/finally per page.
/// </summary>
public abstract partial class ViewModelBase : ObservableObject
{
    private CancellationTokenSource? _cts;

    [ObservableProperty]
    private bool isBusy;

    public event Action<string>? OperationStarted;
    public event Action<TaskProgress>? OperationProgress;
    public event Action<string>? OperationFinished;

    /// <summary>
    /// Runs <paramref name="operation"/> behind <see cref="IsBusy"/> and the
    /// three operation events above. Confirms first when <paramref name="confirm"/>
    /// is given (skips the operation entirely if the user says no). A
    /// cancellation via <see cref="CancelOperationCommand"/> is treated as a
    /// normal outcome — it ends in <paramref name="cancelledMessage"/>, not
    /// an exception the caller has to handle. When the finish message needs
    /// something only known after the operation runs (e.g. "Freed 1.2 GB"),
    /// pass <paramref name="doneMessageFactory"/> instead of a fixed
    /// <paramref name="doneMessage"/> — it's evaluated right after
    /// <paramref name="operation"/> completes successfully.
    /// </summary>
    protected async Task RunOperationAsync(
        Func<IProgress<TaskProgress>, CancellationToken, Task> operation,
        string startMessage,
        string doneMessage,
        string cancelledMessage,
        (string Title, string Message)? confirm = null,
        Func<string>? doneMessageFactory = null)
    {
        if (confirm is { } c && !ConfirmationService.Ask(c.Title, c.Message))
            return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        OperationStarted?.Invoke(startMessage);
        var reporter = new Progress<TaskProgress>(p => OperationProgress?.Invoke(p));

        try
        {
            await operation(reporter, _cts.Token);
            OperationFinished?.Invoke(doneMessageFactory?.Invoke() ?? doneMessage);
        }
        catch (OperationCanceledException)
        {
            OperationFinished?.Invoke(cancelledMessage);
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void CancelOperation() => _cts?.Cancel();
}
