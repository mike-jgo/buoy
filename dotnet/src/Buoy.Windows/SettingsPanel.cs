using Buoy.Core.Settings;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Buoy.Windows;

/// <summary>
/// The Settings flyout on the footer gear (the Mac's Settings popover):
/// appearance, behaviour, the global shortcut, the version and Quit. Every
/// change goes straight to <see cref="SettingsStore"/>, which applies it at once.
/// </summary>
internal sealed class SettingsPanel : StackPanel
{
    private readonly SettingsStore _settings;
    private readonly Button _shortcut = new() { MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _shortcutHint = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private bool _recording;

    public SettingsPanel(SettingsStore settings, Action quit)
    {
        _settings = settings;
        Width = 260;
        Spacing = 14;
        var value = settings.Value;

        var theme = new ComboBox
        {
            Header = "Theme",
            ItemsSource = new[] { "System", "Light", "Dark" },
            SelectedIndex = (int)value.Theme,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        theme.SelectionChanged += (_, _) => settings.Update(s => s.Theme = (AppTheme)theme.SelectedIndex);

        var size = new Slider
        {
            Header = "Text size",
            Minimum = 11,
            Maximum = 24,
            StepFrequency = 1,
            Value = value.FontSize,
        };
        size.ValueChanged += (_, e) => settings.Update(s => s.FontSize = e.NewValue);

        var onTop = new ToggleSwitch { Header = "Keep on top of other windows", IsOn = value.AlwaysOnTop };
        onTop.Toggled += (_, _) => settings.Update(s => s.AlwaysOnTop = onTop.IsOn);

        var atLogin = new ToggleSwitch { Header = "Open at sign-in", IsOn = value.LaunchAtLogin };
        atLogin.Toggled += (_, _) => settings.Update(s => s.LaunchAtLogin = atLogin.IsOn);

        _shortcut.Content = ShortcutLabel();
        _shortcut.Click += (_, _) => StartRecording();
        _shortcut.PreviewKeyDown += OnRecordKey;
        _shortcut.LostFocus += (_, _) => StopRecording();
        var shortcut = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Show or hide Buoy" },
                _shortcut,
                _shortcutHint,
            },
        };

        var quitButton = new Button { Content = "Quit Buoy" };
        quitButton.Click += (_, _) => quit();
        var about = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
            Children =
            {
                new TextBlock
                {
                    Text = $"Buoy {App.Version}",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                },
            },
        };
        Grid.SetColumn(quitButton, 1);
        about.Children.Add(quitButton);

        Children.Add(theme);
        Children.Add(size);
        Children.Add(onTop);
        Children.Add(atLogin);
        Children.Add(shortcut);
        Children.Add(about);
    }

    private string ShortcutLabel() =>
        (KeyChord.Parse(_settings.Value.GlobalShortcut) ?? KeyChord.Default).ToString().Replace("+", " + ");

    private void StartRecording()
    {
        _recording = true;
        _shortcut.Content = "Press a shortcut…";
        _shortcutHint.Visibility = Visibility.Collapsed;
    }

    private void StopRecording()
    {
        _recording = false;
        _shortcut.Content = ShortcutLabel();
    }

    /// <summary>
    /// Records a chord: Ctrl, Alt or Win (Shift optional) with a letter, digit,
    /// F-key or Space. Escape cancels. Whether another app already holds it is
    /// only known when the app tries to register it.
    /// </summary>
    private void OnRecordKey(object sender, KeyRoutedEventArgs e)
    {
        if (!_recording) return;
        e.Handled = true;
        if (e.Key == VirtualKey.Escape)
        {
            StopRecording();
            return;
        }
        if (e.Key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift or VirtualKey.LeftWindows
            or VirtualKey.RightWindows or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.LeftMenu or VirtualKey.RightMenu or VirtualKey.LeftShift or VirtualKey.RightShift)
            return; // wait for the key itself

        bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
        var parts = new List<string>();
        if (Down(VirtualKey.Control)) parts.Add("Ctrl");
        if (Down(VirtualKey.Menu)) parts.Add("Alt");
        if (Down(VirtualKey.Shift)) parts.Add("Shift");
        if (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows)) parts.Add("Win");
        parts.Add(e.Key switch
        {
            >= VirtualKey.A and <= VirtualKey.Z => ((char)e.Key).ToString(),
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)e.Key).ToString(),
            >= VirtualKey.F1 and <= VirtualKey.F24 => $"F{e.Key - VirtualKey.F1 + 1}",
            VirtualKey.Space => "Space",
            _ => "?",
        });

        if (KeyChord.Parse(string.Join("+", parts)) is not { } chord)
        {
            _shortcutHint.Text = "Use Ctrl, Alt or Win with a letter, number, F-key or Space.";
            _shortcutHint.Visibility = Visibility.Visible;
            return;
        }
        _settings.Update(s => s.GlobalShortcut = chord.ToString());
        StopRecording();
    }
}
