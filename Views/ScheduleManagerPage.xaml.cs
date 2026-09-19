using System.Windows;
using System.Windows.Controls;
using SolventUI.Models;
using SolventUI.Services;

namespace SolventUI.Views;

public partial class ScheduleManagerPage : Page
{
    public ScheduleManagerPage()
    {
        InitializeComponent();
        Loaded += (_, _) => InitializeControls();
    }

    private void InitializeControls()
    {
        for (var h = 0; h < 24; h++)
            HourCombo.Items.Add(h.ToString("00"));
        for (var m = 0; m < 60; m += 5)
            MinuteCombo.Items.Add(m.ToString("00"));

        HourCombo.SelectedItem = "03";
        MinuteCombo.SelectedItem = "00";
    }

    private async void Save_OnClick(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false;
        try
        {
            var day = DayCombo.SelectedItem is ComboBoxItem dayItem
                ? Enum.Parse<DayOfWeek>(dayItem.Tag?.ToString() ?? "Monday")
                : DayOfWeek.Monday;

            var hour = int.TryParse(HourCombo.SelectedItem as string, out var h) ? h : 3;
            var minute = int.TryParse(MinuteCombo.SelectedItem as string, out var m) ? m : 0;

            var settings = new ScheduleSettings
            {
                IsEnabled = EnabledToggle.IsChecked == true,
                DayOfWeek = day,
                TimeOfDay = new TimeSpan(hour, minute, 0)
            };

            await ScheduleManagerService.ApplyAsync(settings);
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }
}
