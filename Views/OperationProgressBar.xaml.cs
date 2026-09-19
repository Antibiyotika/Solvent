using System.Windows;
using System.Windows.Controls;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.Views;

/// <summary>
/// A status line + progress bar + Cancel button, meant to be dropped near
/// the top of any page that runs a long TaskService/service operation.
/// The owning page creates a <see cref="System.Threading.CancellationTokenSource"/>,
/// passes a <c>new Progress&lt;TaskProgress&gt;(bar.Report)</c> into the service
/// call, and cancels the token when <see cref="CancelRequested"/> fires.
/// </summary>
public partial class OperationProgressBar : UserControl
{
    public event EventHandler? CancelRequested;

    public OperationProgressBar() => InitializeComponent();

    public void Begin(string initialMessage)
    {
        StatusText.Text = initialMessage;
        Bar.IsIndeterminate = true;
        Bar.Value = 0;
        CancelButton.IsEnabled = true;
        CancelButton.Content = LocalizationService.Instance.Get("Progress_Cancel");
        Visibility = Visibility.Visible;
    }

    public void Report(TaskProgress progress)
    {
        StatusText.Text = progress.Message;
        if (progress.IsIndeterminate)
        {
            Bar.IsIndeterminate = true;
        }
        else
        {
            Bar.IsIndeterminate = false;
            Bar.Maximum = progress.Total;
            Bar.Value = progress.Current;
        }
    }

    public void Finish(string finalMessage)
    {
        StatusText.Text = finalMessage;
        Bar.IsIndeterminate = false;
        Bar.Value = Bar.Maximum;
        CancelButton.IsEnabled = false;
    }

    public void Hide() => Visibility = Visibility.Collapsed;

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        var loc = LocalizationService.Instance;
        CancelButton.IsEnabled = false;
        CancelButton.Content = loc.Get("Progress_Cancelling");
        StatusText.Text = loc.Get("Progress_CancellingStatus");
        CancelRequested?.Invoke(this, EventArgs.Empty);
    }
}
