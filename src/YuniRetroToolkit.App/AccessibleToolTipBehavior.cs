using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace YuniRetroToolkit.App;

public static class AccessibleToolTipBehavior
{
    public static readonly DependencyProperty OpenOnKeyboardFocusProperty = DependencyProperty.RegisterAttached(
        "OpenOnKeyboardFocus", typeof(bool), typeof(AccessibleToolTipBehavior), new PropertyMetadata(false, Changed));
    public static void SetOpenOnKeyboardFocus(DependencyObject element, bool value) => element.SetValue(OpenOnKeyboardFocusProperty, value);
    public static bool GetOpenOnKeyboardFocus(DependencyObject element) => (bool)element.GetValue(OpenOnKeyboardFocusProperty);
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        element.GotKeyboardFocus -= GotFocus;
        element.LostKeyboardFocus -= LostFocus;
        if ((bool)e.NewValue) { element.GotKeyboardFocus += GotFocus; element.LostKeyboardFocus += LostFocus; }
    }
    private static void GotFocus(object sender, KeyboardFocusChangedEventArgs e) { if (sender is FrameworkElement { ToolTip: ToolTip tip }) tip.IsOpen = true; }
    private static void LostFocus(object sender, KeyboardFocusChangedEventArgs e) { if (sender is FrameworkElement { ToolTip: ToolTip tip }) tip.IsOpen = false; }
}
