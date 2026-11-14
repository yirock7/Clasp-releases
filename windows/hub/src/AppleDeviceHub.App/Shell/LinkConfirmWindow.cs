using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace AppleDeviceHub.App.Shell;

/// <summary>
/// A small window that asks "A link asked Clasp to ... Allow?" before a clasp:// link or a second
/// launch may change the sound output or repair the connection. Built in code (no XAML), shown
/// centred on the monitor under the pointer, closed for good after the answer. Closing the window,
/// Esc or waiting out <see cref="Timeout"/> all mean "Don't allow".
/// </summary>
internal sealed class LinkConfirmWindow : Window
{
    private const double WidthDip = 400;
    private const double HeightDip = 190;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;

    private LinkConfirmWindow(string text, ElementTheme theme)
    {
        Title = "Clasp";
        var allow = new Button { Content = "Allow", MinWidth = 96 };
        var deny = new Button { Content = "Don't allow", MinWidth = 96 };
        allow.Click += (_, _) => Finish(true);
        deny.Click += (_, _) => Finish(false);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(allow);
        buttons.Children.Add(deny);
        var message = new TextBlock { Text = text, TextWrapping = TextWrapping.WrapWholeWords, FontSize = 15 };
        var panel = new Grid { Padding = new Thickness(24), RowSpacing = 20, RequestedTheme = theme };
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(buttons, 1);
        panel.Children.Add(message);
        panel.Children.Add(buttons);
        Content = panel;
        panel.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) Finish(false);
        };

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }
        var (x, y) = NativeMethods.CursorPosition();
        var monitor = MonitorDetails.FromPoint(x, y);
        int w = (int)Math.Round(WidthDip * monitor.Scale);
        int h = (int)Math.Round(HeightDip * monitor.Scale);
        var work = monitor.WorkArea;
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
        AppWindow.Closing += (_, _) => _answer.TrySetResult(false);

        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = Timeout;
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => Finish(false);
        _timer.Start();
    }

    private void Finish(bool allowed)
    {
        _timer.Stop();
        _answer.TrySetResult(allowed);
        try { Close(); }
        catch (Exception ex) { LocalLog.Write($"Link prompt close failed ({ex.GetType().Name})."); }
    }

    /// <summary>Asks the question and returns the answer. Must be called on the UI thread. Never throws; failure means "no".</summary>
    public static async Task<bool> AskAsync(string text, ElementTheme theme)
    {
        try
        {
            var window = new LinkConfirmWindow(text, theme);
            window.AppWindow.Show(true);
            window.Activate();
            return await window._answer.Task;
        }
        catch (Exception ex)
        {
            LocalLog.Write($"Link prompt failed ({ex.GetType().Name}); treated as Don't allow.");
            return false;
        }
    }
}
