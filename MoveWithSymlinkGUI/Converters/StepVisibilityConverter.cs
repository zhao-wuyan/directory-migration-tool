using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace MoveWithSymlinkGUI;

public class StepVisibilityConverter : IValueConverter
{
    public int Step { get; set; }
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is int currentStep)
        {
            bool isMatch = currentStep == Step;
            if (Invert)
                isMatch = !isMatch;
            
            return isMatch ? Visibility.Visible : Visibility.Collapsed;
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}

