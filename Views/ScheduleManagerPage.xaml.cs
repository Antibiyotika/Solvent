using System.Windows.Controls;
using SolventUI.ViewModels;

namespace SolventUI.Views;

/// <summary>Schedule form: every control binds straight to <see cref="ScheduleManagerViewModel"/> — nothing left for code-behind.</summary>
public partial class ScheduleManagerPage : Page
{
    public ScheduleManagerPage(ScheduleManagerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
