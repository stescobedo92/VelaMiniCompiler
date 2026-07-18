using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace Vela.Ui;

/// <summary>Opaque handle for a Vela desktop form.</summary>
public sealed class GuiForm : IDisposable
{
    private readonly Window? _window;
    private readonly Panel _root;
    private readonly Menu? _menu;
    private bool _open = true;
    private bool _headlessShown;
    private int _headlessTicks;
    private static bool _lifetimeStarted;

    internal GuiForm(string title, int width, int height, string layout, bool headless)
    {
        Title = title;
        LayoutKind = layout;
        _root = layout switch
        {
            "row" => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12) },
            "grid" => new WrapPanel { Margin = new Thickness(12) },
            _ => new StackPanel { Orientation = Orientation.Vertical, Spacing = 8, Margin = new Thickness(12) }
        };

        if (headless)
        {
            _window = null;
            _menu = null;
            return;
        }

        _menu = new Menu();
        var dock = new DockPanel();
        DockPanel.SetDock(_menu, Dock.Top);
        dock.Children.Add(_menu);
        dock.Children.Add(_root);
        _window = new Window
        {
            Title = title,
            Width = Math.Max(160, width),
            Height = Math.Max(120, height),
            Content = dock,
            WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        _window.Closed += (_, _) => _open = false;
    }

    /// <summary>Gets the window title.</summary>
    public string Title { get; }

    /// <summary>Gets the layout mode: column, row, or grid.</summary>
    public string LayoutKind { get; }

    internal Window? Native => _window;

    internal Panel Root => _root;

    internal Menu? Menu => _menu;

    internal bool IsHeadless => _window is null;

    /// <summary>Returns whether the form is still open.</summary>
    public bool IsOpen => IsHeadless ? _headlessShown && _headlessTicks < 3 : _open;

    internal void TickHeadless()
    {
        if (IsHeadless && _headlessShown)
        {
            _headlessTicks++;
        }
    }

    internal GuiControl AddControl(string kind, string text, Control? control)
    {
        var handle = new GuiControl(this, kind, text, control);
        if (control is not null)
        {
            _root.Children.Add(control);
        }

        return handle;
    }

    internal void ShowInternal(GuiForm? owner)
    {
        if (IsHeadless)
        {
            _headlessShown = true;
            Console.WriteLine($"[gui] show form '{Title}'");
            return;
        }

        ArgumentNullException.ThrowIfNull(_window);
        if (owner?.Native is { } ownerWindow)
        {
            _window.Show(ownerWindow);
        }
        else
        {
            _window.Show();
        }
    }

    internal int RunInternal()
    {
        if (IsHeadless)
        {
            _headlessShown = true;
            Console.WriteLine($"[gui] run form '{Title}' (headless)");
            _open = false;
            return 0;
        }

        ArgumentNullException.ThrowIfNull(_window);
        if (_lifetimeStarted)
        {
            _window.Show();
            return Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var closed = new TaskCompletionSource();
                _window.Closed += (_, _) => closed.TrySetResult();
                await closed.Task.ConfigureAwait(true);
                return 0;
            }).GetAwaiter().GetResult();
        }

        _lifetimeStarted = true;
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace()
            .StartWithClassicDesktopLifetime(
                Array.Empty<string>(),
                lifetime =>
                {
                    lifetime.MainWindow = _window;
                    lifetime.ShutdownMode = ShutdownMode.OnMainWindowClose;
                });
        return 0;
    }

    internal void CloseInternal()
    {
        _open = false;
        _window?.Close();
    }

    /// <inheritdoc />
    public void Dispose() => CloseInternal();
}

/// <summary>Opaque handle for a control owned by a <see cref="GuiForm"/>.</summary>
public sealed class GuiControl
{
    private readonly GuiForm _form;
    private readonly Control? _control;
    private string _text;
    private bool _checked;
    private int _progress;
    private bool _clicked;
    private readonly List<Action> _clickHandlers = [];
    private readonly List<Action<string>> _textHandlers = [];
    private readonly List<Action<bool>> _checkedHandlers = [];
    private readonly List<Action<int>> _valueHandlers = [];
    private readonly ObservableCollection<string> _listItems = [];
    private readonly ObservableCollection<string> _gridRows = [];
    private string _comboSelected = string.Empty;
    private int _value;

    internal GuiControl(GuiForm form, string kind, string text, Control? control)
    {
        _form = form;
        Kind = kind;
        _text = text;
        _control = control;
        if (control is Button button)
        {
            button.Click += (_, _) =>
            {
                _clicked = true;
                foreach (var handler in _clickHandlers)
                {
                    handler();
                }
            };
        }

        if (control is TextBox box)
        {
            box.PropertyChanged += (_, args) =>
            {
                if (args.Property == TextBox.TextProperty)
                {
                    var value = box.Text ?? string.Empty;
                    _text = value;
                    foreach (var handler in _textHandlers)
                    {
                        handler(value);
                    }
                }
            };
        }

        if (control is CheckBox check)
        {
            check.IsCheckedChanged += (_, _) =>
            {
                _checked = check.IsChecked == true;
                foreach (var handler in _checkedHandlers)
                {
                    handler(_checked);
                }
            };
        }

        if (control is RadioButton radio)
        {
            radio.IsCheckedChanged += (_, _) =>
            {
                _checked = radio.IsChecked == true;
                foreach (var handler in _checkedHandlers)
                {
                    handler(_checked);
                }
            };
        }

        if (control is Slider slider)
        {
            _value = (int)slider.Value;
            slider.PropertyChanged += (_, args) =>
            {
                if (args.Property == RangeBase.ValueProperty)
                {
                    _value = (int)slider.Value;
                    foreach (var handler in _valueHandlers)
                    {
                        handler(_value);
                    }
                }
            };
        }

        if (control is NumericUpDown numeric)
        {
            _value = (int)(numeric.Value ?? 0);
            numeric.ValueChanged += (_, _) =>
            {
                _value = (int)(numeric.Value ?? 0);
                foreach (var handler in _valueHandlers)
                {
                    handler(_value);
                }
            };
        }

        if (control is ListBox list)
        {
            list.ItemsSource = _listItems;
        }

        if (control is DataGrid grid)
        {
            grid.ItemsSource = _gridRows.Select(static row => new GridRow(row)).ToList();
        }
    }

    /// <summary>Gets the control kind.</summary>
    public string Kind { get; }

    /// <summary>Gets or sets the control text.</summary>
    public string Text
    {
        get
        {
            if (_control is TextBox box)
            {
                return box.Text ?? string.Empty;
            }

            if (_control is TextBlock block)
            {
                return block.Text ?? string.Empty;
            }

            if (_control is CheckBox check)
            {
                return check.Content?.ToString() ?? string.Empty;
            }

            if (_control is Button button)
            {
                return button.Content?.ToString() ?? string.Empty;
            }

            return _text;
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _text = value;
            if (_control is TextBox box)
            {
                box.Text = value;
            }
            else if (_control is TextBlock block)
            {
                block.Text = value;
            }
            else if (_control is CheckBox check)
            {
                check.Content = value;
            }
            else if (_control is Button button)
            {
                button.Content = value;
            }
        }
    }

    internal void OnClick(Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _clickHandlers.Add(handler);
    }

    internal void OnTextChanged(Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _textHandlers.Add(handler);
    }

    internal void OnCheckedChanged(Action<bool> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _checkedHandlers.Add(handler);
    }

    internal bool ConsumeClick()
    {
        if (!_clicked)
        {
            return false;
        }

        _clicked = false;
        return true;
    }

    /// <summary>Simulates a click for tests and headless scripting.</summary>
    public void ScriptClick()
    {
        _clicked = true;
        foreach (var handler in _clickHandlers)
        {
            handler();
        }
    }

    internal bool Checked
    {
        get => _control switch
        {
            CheckBox box => box.IsChecked == true,
            RadioButton radio => radio.IsChecked == true,
            _ => _checked
        };
        set
        {
            _checked = value;
            if (_control is CheckBox box)
            {
                box.IsChecked = value;
            }
            else if (_control is RadioButton radio)
            {
                radio.IsChecked = value;
            }
        }
    }

    internal int Value
    {
        get => _control switch
        {
            Slider slider => (int)slider.Value,
            NumericUpDown numeric => (int)(numeric.Value ?? 0),
            _ => _value
        };
        set
        {
            _value = value;
            if (_control is Slider slider)
            {
                slider.Value = value;
            }
            else if (_control is NumericUpDown numeric)
            {
                numeric.Value = value;
            }
        }
    }

    internal void OnValueChanged(Action<int> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _valueHandlers.Add(handler);
    }

    internal int Progress
    {
        get => _control is ProgressBar bar ? (int)bar.Value : _progress;
        set
        {
            var clamped = Math.Clamp(value, 0, 100);
            _progress = clamped;
            if (_control is ProgressBar bar)
            {
                bar.Value = clamped;
            }
        }
    }

    internal void ComboAdd(string item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_comboSelected.Length == 0)
        {
            _comboSelected = item;
        }

        if (_control is ComboBox combo)
        {
            combo.Items.Add(item);
            if (combo.SelectedIndex < 0)
            {
                combo.SelectedIndex = 0;
                combo.SelectedItem = item;
            }
        }
    }

    internal string ComboSelected()
    {
        if (_control is ComboBox combo)
        {
            if (combo.SelectedItem is { } selected)
            {
                return selected.ToString() ?? string.Empty;
            }

            if (combo.SelectedIndex >= 0 && combo.SelectedIndex < combo.ItemCount)
            {
                return combo.Items[combo.SelectedIndex]?.ToString() ?? string.Empty;
            }
        }

        return _comboSelected;
    }

    internal void ListAdd(string item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _listItems.Add(item);
        if (_control is ListBox list)
        {
            list.ItemsSource = null;
            list.ItemsSource = _listItems;
        }
    }

    internal void ListClear()
    {
        _listItems.Clear();
        if (_control is ListBox list)
        {
            list.ItemsSource = null;
            list.ItemsSource = _listItems;
        }
    }

    internal string ListSelected() =>
        _control is ListBox list ? list.SelectedItem?.ToString() ?? string.Empty : string.Empty;

    internal void GridAddRow(string row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _gridRows.Add(row);
        if (_control is DataGrid grid)
        {
            grid.ItemsSource = _gridRows.Select(static value => new GridRow(value)).ToList();
        }
    }

    internal void GridClear()
    {
        _gridRows.Clear();
        if (_control is DataGrid grid)
        {
            grid.ItemsSource = Array.Empty<GridRow>();
        }
    }

    internal void SetEnabled(bool enabled)
    {
        if (_control is not null)
        {
            _control.IsEnabled = enabled;
        }
    }

    internal void SetVisible(bool visible)
    {
        if (_control is not null)
        {
            _control.IsVisible = visible;
        }
    }

    internal sealed record GridRow(string Value);
}

/// <summary>Factory helpers for composing desktop UI from Vela (Avalonia-backed, cross-platform).</summary>
public static class VelaGuiComponents
{
    /// <summary>Creates a top-level form using a vertical column layout.</summary>
    public static GuiForm CreateForm(string title, int width, int height) =>
        CreateFormLayout(title, width, height, "column");

    /// <summary>Creates a top-level form with an explicit layout: column, row, or grid.</summary>
    public static GuiForm CreateFormLayout(string title, int width, int height, string layout)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(layout);
        VelaGui.EnsureReady();
        var normalized = layout.ToLowerInvariant() switch
        {
            "row" => "row",
            "grid" => "grid",
            _ => "column"
        };
        return new GuiForm(title, width, height, normalized, VelaGui.Headless);
    }

    /// <summary>Adds a label.</summary>
    public static GuiControl AddLabel(GuiForm form, string text, int x, int y)
    {
        _ = x;
        _ = y;
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(text);
        if (form.IsHeadless)
        {
            return form.AddControl("label", text, null);
        }

        return form.AddControl("label", text, new TextBlock { Text = text });
    }

    /// <summary>Adds a button.</summary>
    public static GuiControl AddButton(GuiForm form, string text, int x, int y, int width, int height)
    {
        _ = x;
        _ = y;
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(text);
        if (form.IsHeadless)
        {
            return form.AddControl("button", text, null);
        }

        return form.AddControl("button", text, new Button
        {
            Content = text,
            MinWidth = Math.Max(24, width),
            MinHeight = Math.Max(24, height)
        });
    }

    /// <summary>Adds a text box.</summary>
    public static GuiControl AddTextBox(GuiForm form, string text, int x, int y, int width, int height)
    {
        _ = x;
        _ = y;
        _ = height;
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(text);
        if (form.IsHeadless)
        {
            return form.AddControl("textbox", text, null);
        }

        return form.AddControl("textbox", text, new TextBox
        {
            Text = text,
            MinWidth = Math.Max(40, width)
        });
    }

    /// <summary>Adds a checkbox.</summary>
    public static GuiControl AddCheckBox(GuiForm form, string text, int x, int y)
    {
        _ = x;
        _ = y;
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(text);
        if (form.IsHeadless)
        {
            return form.AddControl("checkbox", text, null);
        }

        return form.AddControl("checkbox", text, new CheckBox { Content = text });
    }

    /// <summary>Adds a progress bar.</summary>
    public static GuiControl AddProgress(GuiForm form, int x, int y, int width, int height)
    {
        _ = x;
        _ = y;
        ArgumentNullException.ThrowIfNull(form);
        if (form.IsHeadless)
        {
            return form.AddControl("progress", string.Empty, null);
        }

        return form.AddControl("progress", string.Empty, new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            MinWidth = Math.Max(40, width),
            MinHeight = Math.Max(16, height)
        });
    }

    /// <summary>Adds a combo box.</summary>
    public static GuiControl AddComboBox(GuiForm form, int x, int y, int width, int height)
    {
        _ = x;
        _ = y;
        _ = height;
        ArgumentNullException.ThrowIfNull(form);
        if (form.IsHeadless)
        {
            return form.AddControl("combo", string.Empty, null);
        }

        return form.AddControl("combo", string.Empty, new ComboBox { MinWidth = Math.Max(40, width) });
    }

    /// <summary>Adds a slider.</summary>
    public static GuiControl AddSlider(GuiForm form, int minimum, int maximum, int value, int x, int y, int width, int height)
    {
        _ = x;
        _ = y;
        _ = height;
        ArgumentNullException.ThrowIfNull(form);
        var min = Math.Min(minimum, maximum);
        var max = Math.Max(minimum, maximum);
        var initial = Math.Clamp(value, min, max);
        if (form.IsHeadless)
        {
            var control = form.AddControl("slider", string.Empty, null);
            control.Value = initial;
            return control;
        }

        return form.AddControl("slider", string.Empty, new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = initial,
            MinWidth = Math.Max(40, width)
        });
    }

    /// <summary>Adds a multiline text area.</summary>
    public static GuiControl AddTextArea(GuiForm form, string text, int x, int y, int width, int height)
    {
        _ = x;
        _ = y;
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(text);
        if (form.IsHeadless)
        {
            return form.AddControl("textarea", text, null);
        }

        return form.AddControl("textarea", text, new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = Math.Max(80, width),
            MinHeight = Math.Max(60, height)
        });
    }

    /// <summary>Adds a numeric up/down control.</summary>
    public static GuiControl AddNumeric(GuiForm form, int value, int minimum, int maximum, int x, int y, int width, int height)
    {
        _ = x;
        _ = y;
        _ = height;
        ArgumentNullException.ThrowIfNull(form);
        var min = Math.Min(minimum, maximum);
        var max = Math.Max(minimum, maximum);
        var initial = Math.Clamp(value, min, max);
        if (form.IsHeadless)
        {
            var control = form.AddControl("numeric", string.Empty, null);
            control.Value = initial;
            return control;
        }

        return form.AddControl("numeric", string.Empty, new NumericUpDown
        {
            Minimum = min,
            Maximum = max,
            Value = initial,
            MinWidth = Math.Max(40, width)
        });
    }

    /// <summary>Adds a radio button.</summary>
    public static GuiControl AddRadio(GuiForm form, string text, int x, int y)
    {
        _ = x;
        _ = y;
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(text);
        if (form.IsHeadless)
        {
            return form.AddControl("radio", text, null);
        }

        return form.AddControl("radio", text, new RadioButton { Content = text });
    }

    /// <summary>Adds a horizontal separator.</summary>
    public static GuiControl AddSeparator(GuiForm form, int x, int y, int width)
    {
        _ = x;
        _ = y;
        ArgumentNullException.ThrowIfNull(form);
        if (form.IsHeadless)
        {
            return form.AddControl("separator", string.Empty, null);
        }

        return form.AddControl("separator", string.Empty, new Separator
        {
            MinWidth = Math.Max(20, width)
        });
    }

    /// <summary>Adds a list box.</summary>
    public static GuiControl AddList(GuiForm form, int x, int y, int width, int height)
    {
        _ = x;
        _ = y;
        ArgumentNullException.ThrowIfNull(form);
        if (form.IsHeadless)
        {
            return form.AddControl("list", string.Empty, null);
        }

        return form.AddControl("list", string.Empty, new ListBox
        {
            MinWidth = Math.Max(80, width),
            MinHeight = Math.Max(80, height)
        });
    }

    /// <summary>Adds a single-column data grid.</summary>
    public static GuiControl AddGrid(GuiForm form, int x, int y, int width, int height)
    {
        _ = x;
        _ = y;
        ArgumentNullException.ThrowIfNull(form);
        if (form.IsHeadless)
        {
            return form.AddControl("grid", string.Empty, null);
        }

        var grid = new DataGrid
        {
            AutoGenerateColumns = true,
            IsReadOnly = true,
            MinWidth = Math.Max(80, width),
            MinHeight = Math.Max(80, height)
        };
        return form.AddControl("grid", string.Empty, grid);
    }

    /// <summary>Adds a top-level menu item that invokes <paramref name="handler"/> when selected.</summary>
    public static void AddMenuItem(GuiForm form, string path, Action handler)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(handler);
        if (form.IsHeadless || form.Menu is null)
        {
            return;
        }

        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return;
        }

        var items = form.Menu.Items;
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            var existing = items.OfType<MenuItem>().FirstOrDefault(item => string.Equals(item.Header?.ToString(), part, StringComparison.Ordinal));
            if (existing is null)
            {
                existing = new MenuItem { Header = part };
                items.Add(existing);
            }

            if (index == parts.Length - 1)
            {
                existing.AddHandler(MenuItem.ClickEvent, (_, _) => handler());
                return;
            }

            items = existing.Items;
        }
    }

    /// <summary>Shows an open-file dialog and returns the selected path, or empty text.</summary>
    public static string OpenFile(GuiForm form, string title)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(title);
        if (form.IsHeadless || form.Native is null)
        {
            return string.Empty;
        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var files = await form.Native.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            }).ConfigureAwait(true);
            return files.Count > 0 ? files[0].TryGetLocalPath() ?? string.Empty : string.Empty;
        }).GetAwaiter().GetResult();
    }

    /// <summary>Shows a save-file dialog and returns the selected path, or empty text.</summary>
    public static string SaveFile(GuiForm form, string title, string suggestedName)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(suggestedName);
        if (form.IsHeadless || form.Native is null)
        {
            return string.Empty;
        }

        return Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var file = await form.Native.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedName
            }).ConfigureAwait(true);
            return file?.TryGetLocalPath() ?? string.Empty;
        }).GetAwaiter().GetResult();
    }

    /// <summary>Registers a click callback.</summary>
    public static void OnClick(GuiControl control, Action handler)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(handler);
        control.OnClick(handler);
    }

    /// <summary>Registers a text-changed callback.</summary>
    public static void OnTextChanged(GuiControl control, Action<string> handler)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(handler);
        control.OnTextChanged(handler);
    }

    /// <summary>Registers a checked-changed callback.</summary>
    public static void OnCheckedChanged(GuiControl control, Action<bool> handler)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(handler);
        control.OnCheckedChanged(handler);
    }

    /// <summary>Shows a form modelessly.</summary>
    public static void Show(GuiForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        form.ShowInternal(null);
    }

    /// <summary>Shows a form owned by another form.</summary>
    public static void ShowOwned(GuiForm form, GuiForm owner)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(owner);
        form.ShowInternal(owner);
    }

    /// <summary>Runs the UI message loop until the form closes.</summary>
    public static int Run(GuiForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return form.RunInternal();
    }

    /// <summary>Pumps UI work once (no-op under Avalonia except headless ticking).</summary>
    public static void ProcessEvents(GuiForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (VelaGui.Headless)
        {
            form.TickHeadless();
            return;
        }

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Returns whether the form is still open.</summary>
    public static bool IsOpen(GuiForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return form.IsOpen;
    }

    /// <summary>Closes the form.</summary>
    public static void Close(GuiForm form)
    {
        ArgumentNullException.ThrowIfNull(form);
        form.CloseInternal();
    }

    /// <summary>Sets control text.</summary>
    public static void SetText(GuiControl control, string text)
    {
        ArgumentNullException.ThrowIfNull(control);
        ArgumentNullException.ThrowIfNull(text);
        control.Text = text;
    }

    /// <summary>Gets control text.</summary>
    public static string GetText(GuiControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.Text;
    }

    /// <summary>Returns whether a button was clicked since the previous poll.</summary>
    public static bool WasClicked(GuiControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.ConsumeClick();
    }

    /// <summary>Returns whether a checkbox is checked.</summary>
    public static bool IsChecked(GuiControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.Checked;
    }

    /// <summary>Sets whether a checkbox is checked.</summary>
    public static void SetChecked(GuiControl control, bool value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.Checked = value;
    }

    /// <summary>Sets a progress bar value from 0 to 100.</summary>
    public static void SetProgress(GuiControl control, int value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.Progress = value;
    }

    /// <summary>Gets a slider/numeric value.</summary>
    public static int GetValue(GuiControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.Value;
    }

    /// <summary>Sets a slider/numeric value.</summary>
    public static void SetValue(GuiControl control, int value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.Value = value;
    }

    /// <summary>Registers a value-changed handler for sliders and numeric controls.</summary>
    public static void OnValueChanged(GuiControl control, Action<int> handler)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.OnValueChanged(handler);
    }

    /// <summary>Adds an item to a combo box.</summary>
    public static void ComboAdd(GuiControl control, string item)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.ComboAdd(item);
    }

    /// <summary>Returns the selected combo box text.</summary>
    public static string ComboSelected(GuiControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.ComboSelected();
    }

    /// <summary>Adds an item to a list box.</summary>
    public static void ListAdd(GuiControl control, string item)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.ListAdd(item);
    }

    /// <summary>Clears a list box.</summary>
    public static void ListClear(GuiControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.ListClear();
    }

    /// <summary>Returns the selected list box text.</summary>
    public static string ListSelected(GuiControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.ListSelected();
    }

    /// <summary>Adds a row to a data grid.</summary>
    public static void GridAddRow(GuiControl control, string row)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.GridAddRow(row);
    }

    /// <summary>Clears a data grid.</summary>
    public static void GridClear(GuiControl control)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.GridClear();
    }

    /// <summary>Enables or disables a control.</summary>
    public static void SetEnabled(GuiControl control, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetEnabled(enabled);
    }

    /// <summary>Shows or hides a control.</summary>
    public static void SetVisible(GuiControl control, bool visible)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetVisible(visible);
    }
}
