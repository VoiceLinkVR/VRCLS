using System;
using System.Globalization;
using System.Windows.Data;

namespace VRCLS.UI.Converters
{
    public class SourceToColorConverter : IValueConverter
    {
        public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var source = value as string ?? string.Empty;
            return source switch
            {
                "Desktop" => ConverterBrushes.SourceDesktop, // Orange-ish
                "Photo" => ConverterBrushes.SourcePhoto, // Blue-ish
                _ => ConverterBrushes.SourceMic // Green-ish (Mic)
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
