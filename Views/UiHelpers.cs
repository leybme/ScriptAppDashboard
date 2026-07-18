using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
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

    public static async Task<bool> Confirm(Window owner, string title, string text)
    {
        var result = false;
        var win = BuildShell(title, text, out var buttons);
        var no = new Button { Content = "Cancel", MinWidth = 80 };
        no.Click += (_, _) => win.Close();
        var yes = new Button { Content = "Remove", MinWidth = 80, Classes = { "accent" } };
        yes.Click += (_, _) => { result = true; win.Close(); };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);
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
