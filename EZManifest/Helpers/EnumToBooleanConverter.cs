using Microsoft.UI.Xaml.Data;

namespace EZManifest.Helpers;

public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (parameter is not string enumString)
            throw new ArgumentException("Converter parameter must be an enum name.");

        if (value is null)
            return false;

        return Enum.Parse(value.GetType(), enumString).Equals(value);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (parameter is not string enumString)
            throw new ArgumentException("Converter parameter must be an enum name.");

        return Enum.Parse(targetType, enumString);
    }
}
