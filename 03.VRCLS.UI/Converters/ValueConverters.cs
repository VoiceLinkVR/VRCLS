using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace VRCLS.UI.Converters
{
    internal static class ConverterBrushes
    {
        internal static readonly SolidColorBrush Success = Create(76, 175, 80);
        internal static readonly SolidColorBrush Warning = Create(255, 193, 7);
        internal static readonly SolidColorBrush Error = Create(244, 67, 54);
        internal static readonly SolidColorBrush Neutral = Create(158, 158, 158);
        internal static readonly SolidColorBrush SourceDesktop = Create(255, 152, 0, 170);
        internal static readonly SolidColorBrush SourceMic = Create(76, 175, 80, 170);
        internal static readonly SolidColorBrush SourcePhoto = Create(33, 150, 243, 170);

        private static SolidColorBrush Create(byte r, byte g, byte b, byte a = 255)
        {
            var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
            brush.Freeze();
            return brush;
        }
    }

    public class VolumeToPercentageConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is float volume)
            {
                // Convert a small linear amplitude (0..1) to a more readable 0..100 meter using dB scaling.
                // -60 dB -> 0%, 0 dB -> 100% (clamped).
                var v = Math.Max(volume, 0.000001f);
                var db = 20.0 * Math.Log10(v);
                var normalized = (db + 60.0) / 60.0;
                return Math.Clamp(normalized, 0.0, 1.0) * 100.0;
            }
            return 0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class ListeningStatusConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isListening)
            {
                return isListening ? "Stop Listening" : "Start Listening";
            }
            return "Start Listening";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class ListeningColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isListening)
            {
                return isListening
                    ? ConverterBrushes.Error
                    : ConverterBrushes.Success;
            }
            return ConverterBrushes.Success;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class StatusColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                return status switch
                {
                    "连接成功" => ConverterBrushes.Success,  // 绿色
                    "已连接" => ConverterBrushes.Success,
                    "当前处于匿名试用模式" => ConverterBrushes.Success,
                    "已通过官网授权登录" => ConverterBrushes.Success,
                    "已登录旧版后端" => ConverterBrushes.Success,
                    "正在连接..." => ConverterBrushes.Warning,  // 黄色
                    _ when status.StartsWith("连接失败") => ConverterBrushes.Error,  // 红色
                    _ when status.StartsWith("连接错误") => ConverterBrushes.Error,  // 红色
                    _ => ConverterBrushes.Neutral  // 灰色
                };
            }
            return ConverterBrushes.Neutral;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class StringVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string str && !string.IsNullOrWhiteSpace(str))
            {
                return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class InverseStringVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string str && !string.IsNullOrWhiteSpace(str))
            {
                return Visibility.Collapsed;
            }

            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? Visibility.Collapsed : Visibility.Visible;
            }
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class InverseBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return true;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return !boolValue;
            }
            return Binding.DoNothing;
        }
    }

    public class StringEqualityToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string str && parameter is string param)
            {
                return string.Equals(str, param, StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// Converts an integer to a boolean based on equality with the parameter.
    /// Used for RadioButton binding to integer properties.
    /// </summary>
    public class IntEqualityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intValue && parameter is string paramStr && int.TryParse(paramStr, out int paramInt))
            {
                return intValue == paramInt;
            }
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue && boolValue && parameter is string paramStr && int.TryParse(paramStr, out int paramInt))
            {
                return paramInt;
            }
            return Binding.DoNothing;
        }
    }

    public class IntEqualityToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int intValue && parameter is string paramStr && int.TryParse(paramStr, out int paramInt))
            {
                return intValue == paramInt ? Visibility.Visible : Visibility.Collapsed;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class MultiAndVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values == null || values.Length == 0) return Visibility.Collapsed;
            foreach (var value in values)
            {
                switch (value)
                {
                    case bool boolValue when !boolValue:
                        return Visibility.Collapsed;
                    case string str when string.IsNullOrWhiteSpace(str):
                        return Visibility.Collapsed;
                }
            }
            return Visibility.Visible;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
