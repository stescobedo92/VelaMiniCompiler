using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Vela.Ui;

/// <summary>High-level desktop GUI helpers for Vela programs.</summary>
public static class VelaGui
{
    private static string? _headlessPromptResult;
    private static string? _lastFormOutput;
    private static int _lastCounterValue;
    private static bool _appConfigured;

    /// <summary>Gets or sets whether GUI calls should avoid creating windows.</summary>
    public static bool Headless
    {
        get
        {
            if (string.Equals(Environment.GetEnvironmentVariable("VELA_UI_HEADLESS"), "1", StringComparison.Ordinal)
                || string.Equals(Environment.GetEnvironmentVariable("VELA_UI_HEADLESS"), "true", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return field;
        }
        set;
    }

    /// <summary>Gets the text shown by the most recent interactive form action in headless mode.</summary>
    public static string LastFormOutput => _lastFormOutput ?? string.Empty;

    /// <summary>Gets the final counter value from the most recent counter application run.</summary>
    public static int LastCounterValue => _lastCounterValue;

    /// <summary>Configures the value returned by <see cref="Prompt"/> and form actions while headless.</summary>
    public static void SetHeadlessPromptResult(string? value) => _headlessPromptResult = value;

    /// <summary>Initializes Avalonia when not running headless.</summary>
    internal static void EnsureReady()
    {
        if (Headless || _appConfigured)
        {
            return;
        }

        // Configuration is completed by the first Run call.
        _appConfigured = true;
    }

    /// <summary>Shows a modal message dialog.</summary>
    public static void ShowMessage(string title, string message)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);
        if (Headless)
        {
            Console.WriteLine($"[gui] {title}: {message}");
            return;
        }

        var window = CreateDialog(title, message, includeInput: false, initial: string.Empty, out _);
        window.ShowDialog(GetActiveWindow()).GetAwaiter().GetResult();
    }

    /// <summary>Shows a single-line prompt dialog and returns the confirmed text.</summary>
    public static string Prompt(string title, string label, string initialValue)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(initialValue);
        if (Headless)
        {
            var result = _headlessPromptResult ?? initialValue;
            _lastFormOutput = result;
            return result;
        }

        var window = CreateDialog(title, label, includeInput: true, initial: initialValue, out var box);
        var ok = window.ShowDialog<bool>(GetActiveWindow()).GetAwaiter().GetResult();
        return ok ? box.Text ?? initialValue : initialValue;
    }

    /// <summary>Runs the Hello Form interaction.</summary>
    public static int RunHelloForm(
        string windowTitle,
        string formTitle,
        string fieldLabel,
        string initialValue,
        string actionLabel)
    {
        ArgumentNullException.ThrowIfNull(windowTitle);
        ArgumentNullException.ThrowIfNull(formTitle);
        ArgumentNullException.ThrowIfNull(fieldLabel);
        ArgumentNullException.ThrowIfNull(initialValue);
        ArgumentNullException.ThrowIfNull(actionLabel);

        if (Headless)
        {
            var message = _headlessPromptResult ?? initialValue;
            _lastFormOutput = message;
            Console.WriteLine($"[gui] {formTitle}: {message}");
            return 0;
        }

        var form = VelaGuiComponents.CreateForm(windowTitle, 440, 220);
        var heading = VelaGuiComponents.AddLabel(form, formTitle, 0, 0);
        _ = heading;
        var input = VelaGuiComponents.AddTextBox(form, initialValue, 0, 0, 400, 28);
        var output = VelaGuiComponents.AddLabel(form, string.Empty, 0, 0);
        var show = VelaGuiComponents.AddButton(form, actionLabel, 0, 0, 100, 32);
        VelaGuiComponents.OnClick(show, () =>
        {
            var text = VelaGuiComponents.GetText(input);
            VelaGuiComponents.SetText(output, text);
            _lastFormOutput = text;
        });
        return VelaGuiComponents.Run(form);
    }

    /// <summary>Runs a multi-window counter demo.</summary>
    public static int RunCounterApp(string mainTitle, string secondaryTitle, int initialCount)
    {
        ArgumentNullException.ThrowIfNull(mainTitle);
        ArgumentNullException.ThrowIfNull(secondaryTitle);
        _lastCounterValue = initialCount;
        if (Headless)
        {
            _lastCounterValue = initialCount + 1;
            _lastFormOutput = _lastCounterValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Console.WriteLine($"[gui] {mainTitle}: counter={_lastCounterValue}; secondary={secondaryTitle}");
            return 0;
        }

        var form = VelaGuiComponents.CreateForm(mainTitle, 460, 280);
        var counter = VelaGuiComponents.AddLabel(form, initialCount.ToString(System.Globalization.CultureInfo.InvariantCulture), 0, 0);
        var status = VelaGuiComponents.AddLabel(form, "Ready.", 0, 0);
        var plus = VelaGuiComponents.AddButton(form, "+1", 0, 0, 90, 34);
        var minus = VelaGuiComponents.AddButton(form, "-1", 0, 0, 90, 34);
        var reset = VelaGuiComponents.AddButton(form, "Reset", 0, 0, 90, 34);
        var open = VelaGuiComponents.AddButton(form, "Open details", 0, 0, 120, 34);
        var count = initialCount;
        void SetCount(int value, string message)
        {
            count = value;
            _lastCounterValue = count;
            _lastFormOutput = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            VelaGuiComponents.SetText(counter, _lastFormOutput);
            VelaGuiComponents.SetText(status, message);
        }

        VelaGuiComponents.OnClick(plus, () => SetCount(count + 1, "Incremented."));
        VelaGuiComponents.OnClick(minus, () => SetCount(count - 1, "Decremented."));
        VelaGuiComponents.OnClick(reset, () => SetCount(initialCount, "Reset."));
        VelaGuiComponents.OnClick(open, () =>
        {
            var details = VelaGuiComponents.CreateForm(secondaryTitle, 320, 160);
            var mirrored = VelaGuiComponents.AddLabel(details, "Mirrored count: " + count, 0, 0);
            var refresh = VelaGuiComponents.AddButton(details, "Refresh", 0, 0, 100, 32);
            VelaGuiComponents.OnClick(refresh, () => VelaGuiComponents.SetText(mirrored, "Mirrored count: " + count));
            VelaGuiComponents.ShowOwned(details, form);
            VelaGuiComponents.SetText(status, "Opened the secondary window.");
        });
        return VelaGuiComponents.Run(form);
    }

    private static Window GetActiveWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is { } main)
        {
            return main;
        }

        return new Window();
    }

    private static Window CreateDialog(string title, string message, bool includeInput, string initial, out TextBox input)
    {
        input = new TextBox { Text = initial, IsVisible = includeInput, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 80, IsVisible = includeInput };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
            Children = { ok, cancel }
        };
        var root = new StackPanel
        {
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                input,
                buttons
            }
        };
        var window = new Window
        {
            Title = title,
            Width = 420,
            Height = includeInput ? 180 : 140,
            CanResize = false,
            Content = root,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        ok.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);
        return window;
    }
}
