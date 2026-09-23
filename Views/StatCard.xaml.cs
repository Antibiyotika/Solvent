using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace SolventUI.Views;

/// <summary>
/// Reusable metric tile: icon + label + value + optional progress bar + hint.
/// Plain DependencyProperties (not ElementName bindings) push values straight
/// onto the named children in StatCard.xaml, so a page can bind e.g.
/// <c>Value="{Binding CpuText}"</c> the same way it would on any built-in control.
/// </summary>
public partial class StatCard : UserControl
{
    public StatCard()
    {
        InitializeComponent();
    }

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(SymbolRegular), typeof(StatCard),
        new PropertyMetadata(SymbolRegular.Circle24, OnIconChanged));

    public SymbolRegular Icon
    {
        get => (SymbolRegular)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((StatCard)d).IconHost.Symbol = (SymbolRegular)e.NewValue;

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(StatCard),
        new PropertyMetadata(string.Empty, (d, e) => ((StatCard)d).TitleText.Text = (string)e.NewValue));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(StatCard),
        new PropertyMetadata(string.Empty, (d, e) => ((StatCard)d).ValueText.Text = (string)e.NewValue));

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint), typeof(string), typeof(StatCard),
        new PropertyMetadata(string.Empty, OnHintChanged));

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    private static void OnHintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (StatCard)d;
        var text = (string)e.NewValue;
        card.HintText.Text = text;
        card.HintText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    public static readonly DependencyProperty ShowProgressProperty = DependencyProperty.Register(
        nameof(ShowProgress), typeof(bool), typeof(StatCard),
        new PropertyMetadata(false, (d, e) =>
            ((StatCard)d).ProgressHost.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    public bool ShowProgress
    {
        get => (bool)GetValue(ShowProgressProperty);
        set => SetValue(ShowProgressProperty, value);
    }

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(StatCard),
        new PropertyMetadata(0d, (d, e) => ((StatCard)d).ProgressHost.Value = (double)e.NewValue));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }
}
