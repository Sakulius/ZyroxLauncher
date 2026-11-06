using System.IO;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Zyrox.Services;

namespace Zyrox.Pages;

public partial class ServersPage : UserControl, IPage
{
    private LauncherState _state = null!;
    private CancellationTokenSource? _ping;

    public ServersPage()
    {
        InitializeComponent();
    }

    public void Initialize(LauncherState state) => _state = state;

    public void OnShown() => BuildList();

    private void BuildList()
    {
        _ping?.Cancel();
        _ping = new CancellationTokenSource();
        ServerList.Children.Clear();
        var profile = _state.SelectedProfile;
        Hint.Text = $"Servers of {profile.Name}. See who is online and join in one click.";
        var servers = _state.ServersOf(profile);
        if (servers.Count == 0)
            ServerList.Children.Add(Ui.Card(Ui.Muted("No servers yet. Add your first one on the right.")));

        foreach (var server in servers)
            ServerList.Children.Add(BuildRow(server, _ping.Token));
    }

    private Border BuildRow(ServerEntry server, CancellationToken cancellationToken)
    {
        var row = new DockPanel();

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(actions, Dock.Right);
        actions.Children.Add(Ui.Button("Remove", (_, _) =>
        {
            _state.Settings.Servers.Remove(server);
            _state.Save();
            BuildList();
        }));
        actions.Children.Add(Ui.Button("Join", (_, _) => _state.RequestJoin(server), primary: true));
        row.Children.Add(actions);

        var icon = new Image { RenderTransformOrigin = new Point(0.5, 0.5) };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(icon, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
        var frame = Ui.IconFrame(icon);
        DockPanel.SetDock(frame, Dock.Left);
        row.Children.Add(frame);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Title(server.Name));
        var motd = Ui.Muted(server.Address);
        motd.MaxHeight = 36;
        motd.TextTrimming = TextTrimming.CharacterEllipsis;
        var status = Ui.Muted("Checking ...");
        text.Children.Add(motd);
        text.Children.Add(status);
        row.Children.Add(text);

        _ = PingAsync(server, icon, motd, status, cancellationToken);
        return Ui.Card(row);
    }

    private static async Task PingAsync(ServerEntry server, Image icon, TextBlock motd, TextBlock status, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ServerService.PingAsync(server.Address, cancellationToken);
            icon.Source = result.Icon;
            if (result.Motd.Length > 0)
                motd.Text = result.Motd;
            status.Text = $"{result.Online} of {result.Max} online  ·  {result.LatencyMs} ms";
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or IOException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            if (!cancellationToken.IsCancellationRequested)
                status.Text = "Unreachable";
        }
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        if (address.Length == 0 || address.Any(char.IsWhiteSpace))
        {
            AddStatus.Text = "Enter a valid server address, for example play.example.net.";
            return;
        }

        var name = NameBox.Text.Trim();
        _state.Settings.Servers.Add(new ServerEntry { Name = name.Length > 0 ? name : address, Address = address, ProfileId = _state.SelectedProfile.Id });
        _state.Save();
        NameBox.Clear();
        AddressBox.Clear();
        AddStatus.Text = "";
        BuildList();
    }
}
