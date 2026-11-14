using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace JblControl;

/// <summary>Enum ↔ RadioButton.IsChecked (ConverterParameter = nombre del valor).</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value?.ToString() == p?.ToString();

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is true ? Enum.Parse(t, p.ToString()!) : Binding.DoNothing;
}

/// <summary>int ↔ RadioButton.IsChecked (ConverterParameter = indice).</summary>
public sealed class IndexToBoolConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is int i && i.ToString(c) == p?.ToString();

    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is true ? int.Parse(p.ToString()!, c) : Binding.DoNothing;
}

public sealed class PercentTextConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is int v ? $"{v}%" : "--";

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class PercentValueConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is int v ? (double)v : 0.0;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Color de la barra de bateria: verde, naranja (&lt;= 30) o rojo (&lt;= 15).</summary>
public sealed class BatteryBrushConverter : IValueConverter
{
    public Brush Good { get; set; } = Brushes.Green;
    public Brush Warn { get; set; } = Brushes.Orange;
    public Brush Low { get; set; } = Brushes.Red;

    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        int v when v <= 15 => Low,
        int v when v <= 30 => Warn,
        _ => Good,
    };

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Visible si el valor no es null (o lo contrario con Invert).</summary>
public sealed class NotNullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type t, object p, CultureInfo c) =>
        (value != null) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>bool? → bool (solo true es true).</summary>
public sealed class IsTrueConverter : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c) => value is true;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>bool? → Visible salvo que sea true.</summary>
public sealed class NotTrueToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class StateBrushConverter : IValueConverter
{
    public Brush Connected { get; set; } = Brushes.Green;
    public Brush Searching { get; set; } = Brushes.Orange;
    public Brush Disconnected { get; set; } = Brushes.Gray;

    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        ConnectionState.Connected => Connected,
        ConnectionState.Searching => Searching,
        _ => Disconnected,
    };

    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
