using AppleDeviceHub.Core.Presentation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AppleDeviceHub.App.Views;

/// <summary>
/// The hand-off notice. Take back asks the existing connection view model to reconnect (one
/// bounded Windows request, then a check for an active endpoint) and shows progress and result.
/// Raises <see cref="DismissRequested"/> (close button) and <see cref="ConnectedAgain"/>.
/// </summary>
public sealed partial class HandoffNoticeView : UserControl
{
    private CompanionConnectionViewModel? _connection;
    private bool _attempted;

    public HandoffNoticeView()
    {
        InitializeComponent();
        TitleText.Text = HandoffNoticeText.Title;
        DetailText.Text = HandoffNoticeText.Detail;
        TakeBackButton.Content = HandoffNoticeText.TakeBackLabel;
        CardSurface.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Height - e.PreviousSize.Height) >= 0.5) ContentHeightChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public CompanionConnectionViewModel? Connection
    {
        get => _connection;
        set
        {
            if (ReferenceEquals(_connection, value)) return;
            if (_connection is not null) _connection.PropertyChanged -= OnConnectionPropertyChanged;
            _connection = value;
            if (_connection is not null) _connection.PropertyChanged += OnConnectionPropertyChanged;
            UpdateState();
        }
    }

    public event EventHandler? DismissRequested;

    /// <summary>Windows reports the AirPods connected again after Take back.</summary>
    public event EventHandler? ConnectedAgain;

    public event EventHandler? ContentHeightChanged;

    /// <summary>Fresh notice: clear any result left from the previous episode.</summary>
    public void Reset()
    {
        _attempted = false;
        UpdateState();
    }

    private async void OnTakeBackClick(object sender, RoutedEventArgs e)
    {
        if (Connection is not { CanConnect: true } connection) return;
        _attempted = true;
        try { await connection.ConnectAsync(); }
        catch (Exception) { /* The result is shown in the status line. */ }
        UpdateState();
    }

    private void OnDismissClick(object sender, RoutedEventArgs e) => DismissRequested?.Invoke(this, EventArgs.Empty);

    private void OnConnectionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (DispatcherQueue.HasThreadAccess) UpdateState();
        else DispatcherQueue.TryEnqueue(UpdateState);
    }

    private void UpdateState()
    {
        var connection = Connection;
        bool connecting = connection?.IsConnecting == true;
        bool connected = connection?.IsConnected == true;
        string status = HandoffNoticeText.Status(connecting, connected, _attempted, connection?.DetailText);
        StatusText.Text = status;
        StatusText.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
        TakeBackButton.IsEnabled = connection?.CanConnect == true;
        TakeBackButton.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
        if (connected && _attempted) ConnectedAgain?.Invoke(this, EventArgs.Empty);
    }
}
