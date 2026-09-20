using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.ViewModels;

/// <summary>The auto-clean schedule form: enable toggle, day of week, time of day, Save.</summary>
public sealed partial class ScheduleManagerViewModel : ObservableObject
{
    public List<string> Hours { get; } = Enumerable.Range(0, 24).Select(h => h.ToString("00")).ToList();
    public List<string> Minutes { get; } = Enumerable.Range(0, 12).Select(i => (i * 5).ToString("00")).ToList();

    [ObservableProperty] private bool isEnabled;
    [ObservableProperty] private DayOfWeek selectedDay = DayOfWeek.Monday;
    [ObservableProperty] private string selectedHour = "03";
    [ObservableProperty] private string selectedMinute = "00";
    [ObservableProperty] private bool isSaving;

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var hour = int.TryParse(SelectedHour, out var h) ? h : 3;
            var minute = int.TryParse(SelectedMinute, out var m) ? m : 0;

            var settings = new ScheduleSettings
            {
                IsEnabled = IsEnabled,
                DayOfWeek = SelectedDay,
                TimeOfDay = new TimeSpan(hour, minute, 0),
            };

            await ScheduleManagerService.ApplyAsync(settings);
        }
        finally
        {
            IsSaving = false;
        }
    }
}
