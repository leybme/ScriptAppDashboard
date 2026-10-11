using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ScriptAppDashboard.Views;

public static class UiHelpers
{
    public static Bitmap? TryLoadBitmap(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            return new Bitmap(path);
        }
        catch
        {
            return null; // not a readable image — fall back to the generated tile icon
        }
    }
}

public static class Dialogs
{
    public static async Task Message(Window owner, string title, string text)
    {
        var win = BuildShell(title, text, out var buttons);
        var ok = new Button { Content = "OK", MinWidth = 80 };
        var window = win;
        ok.Click += (_, _) => window.Close();
        buttons.Children.Add(ok);
        await win.ShowDialog(owner);
    }

    public static async Task<bool> Confirm(Window owner, string title, string text, string confirmLabel = "Remove")
    {
        var result = false;
        var win = BuildShell(title, text, out var buttons);
        var no = new Button { Content = "Cancel", MinWidth = 80 };
        no.Click += (_, _) => win.Close();
        var yes = new Button { Content = confirmLabel, MinWidth = 80, Classes = { "accent" } };
        yes.Click += (_, _) => { result = true; win.Close(); };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
        await win.ShowDialog(owner);
        return result;
    }

    /// <summary>
    /// Asks for a short text (Enter sends, Shift+Enter starts a new line); null when cancelled. Each suggestion is a
    /// button that puts its text in the box.
    /// </summary>
    public static async Task<string?> Prompt(Window owner, string title, string text, string initial, string watermark,
        string okLabel, params string[] suggestions)
    {
        string? result = null;
        var win = BuildShell(title, text, out var buttons);
        win.Width = 540;
        var box = new TextBox
        {
            Text = initial,
            Watermark = watermark,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 64,
            MaxHeight = 180,
        };
        AutomationProperties.SetName(box, "Instruction");
        void Send()
        {
            if ((box.Text ?? "").Trim().Length == 0) return;
            result = box.Text!.Trim();
            win.Close();
        }
        box.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            {
                e.Handled = true;
                Send();
            }
        }, RoutingStrategies.Tunnel);
        win.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) win.Close();
        };

        var content = (StackPanel)win.Content!;
        content.Children.Insert(1, box);
        if (suggestions.Length > 0)
        {
            var chips = new WrapPanel();
            foreach (var suggestion in suggestions)
            {
                var chip = new Button { Content = suggestion, FontSize = 12, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(8, 3) };
                chip.Click += (_, _) =>
                {
                    box.Text = suggestion;
                    box.Focus();
                    box.CaretIndex = suggestion.Length;
                };
                chips.Children.Add(chip);
            }
            content.Children.Insert(2, new StackPanel
            {
                Spacing = 4,
                Children = { new TextBlock { Text = "Examples:", FontSize = 12, Foreground = Brushes.Gray }, chips },
            });
        }

        var cancel = new Button { Content = "Cancel", MinWidth = 80 };
        cancel.Click += (_, _) => win.Close();
        var ok = new Button { Content = okLabel, MinWidth = 80, Classes = { "accent" } };
        ok.Click += (_, _) => Send();
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        win.Opened += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        await win.ShowDialog(owner);
        return result;
    }

    private static Window BuildShell(string title, string text, out StackPanel buttons)
    {
        buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        return new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                    buttons,
                },
            },
        };
    }
}
