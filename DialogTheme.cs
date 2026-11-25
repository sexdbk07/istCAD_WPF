using System.Windows;

namespace iStCAD_1;

/// <summary>Inherited marker for light (white background, black text) child windows. App-level TextBlock styling is light-on-dark and
/// would otherwise leak into templated content (tab headers, button captions, list items, popups); the marker lets it switch to black.</summary>
public static class DialogTheme
{
    public static readonly DependencyProperty IsLightProperty = DependencyProperty.RegisterAttached(
        "IsLight", typeof(bool), typeof(DialogTheme), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetIsLight(DependencyObject element) => (bool)element.GetValue(IsLightProperty);
    public static void SetIsLight(DependencyObject element, bool value) => element.SetValue(IsLightProperty, value);
}
