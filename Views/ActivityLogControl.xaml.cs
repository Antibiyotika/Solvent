using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Controls;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.Views;

public partial class ActivityLogControl : UserControl
{
    public ObservableCollection<LogEntry> Entries => LogService.Instance.Entries;

    public ActivityLogControl()
    {
        InitializeComponent();
        DataContext = this;
        Entries.CollectionChanged += OnEntriesChanged;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Auto-scroll to the newest line and give the pulse dot a brief flash.
        LogScroll.ScrollToBottom();
        PulseDot.BeginAnimation(System.Windows.UIElement.OpacityProperty, null);
        PulseDot.Opacity = 1;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, System.TimeSpan.FromSeconds(1.2));
        PulseDot.BeginAnimation(System.Windows.UIElement.OpacityProperty, fade);
    }
}
