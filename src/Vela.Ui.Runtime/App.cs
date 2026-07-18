using Avalonia;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Vela.Ui;

/// <summary>Avalonia application host used by Vela GUI programs.</summary>
public sealed class App : Application
{
    /// <inheritdoc />
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml")));
        RequestedThemeVariant = ThemeVariant.Default;
    }
}
