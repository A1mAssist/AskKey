using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using AskKey.Integrations;
using AskKey.Vault;

namespace AskKeyApp;

public partial class AgentAccessView : UserControl
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly AgentClientKind[] ClientKinds =
        [AgentClientKind.ClaudeCode, AgentClientKind.Codex, AgentClientKind.Cursor, AgentClientKind.Grok];
    private readonly AppHost _host;
    private readonly Dictionary<AgentClientKind, bool> _connected = new();
    private int _statusGeneration;

    public AgentAccessView(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        var paused = _host.IsAgentAccessPaused;
        StatusTitle.Text = paused ? Loc.T("Agent access is paused") : Loc.T("Agent access is running");
        StatusDetail.Text = paused
            ? Loc.T("The Broker refuses new requests until you resume access.")
            : Loc.T("The local Broker is ready for approved requests.");
        StatusIcon.Foreground = (Brush)Application.Current.Resources[paused ? "WarningBrush" : "AccentBrush"];
        PauseButton.Content = paused ? Loc.T("Resume Agent access") : Loc.T("Pause Agent access");
        PauseButton.Style = (Style)Application.Current.Resources[paused ? "PrimaryButtonStyle" : "SecondaryButtonStyle"];

        var helper = AppPaths.HelperPath;
        HelperPathText.Text = helper;
        McpConfigText.Text = JsonSerializer.Serialize(new
        {
            mcpServers = new { askkey = new { command = helper, args = new[] { "mcp" } } },
        }, JsonOptions);

        BrokerFailurePanel.Visibility = string.IsNullOrWhiteSpace(_host.BrokerFailure) ? Visibility.Collapsed : Visibility.Visible;
        BrokerFailureText.Text = Loc.T(_host.BrokerFailure ?? "");
        _ = RefreshStatusesAsync();
    }

    private async Task RefreshStatusesAsync()
    {
        var generation = ++_statusGeneration;
        SetChecking();
        try
        {
            var connector = Connector();
            // The four checks share no state, so they run together and each row updates
            // as soon as its own client answers instead of waiting for the slowest.
            await Task.WhenAll(ClientKinds.Select(async client =>
            {
                var status = await Task.Run(() => connector.Check(client));
                if (OwnsRow(generation, _statusGeneration)) ApplyStatus(status);
            }));
        }
        catch (Exception error)
        {
            if (generation == _statusGeneration) SetUnavailable(error.Message);
        }
    }

    /// <summary>
    /// A refresh started after a single-client operation began owns the row, so a
    /// slow check or connect that answers late must not overwrite it. Every path
    /// that writes a row result goes through here.
    /// </summary>
    internal static bool OwnsRow(int capturedGeneration, int currentGeneration)
        => capturedGeneration == currentGeneration;

    private async Task CheckOneAsync(AgentClientKind client)
    {
        // A refresh started later owns the row, so a slow check that answers after
        // it must not overwrite the newer result.
        var generation = _statusGeneration;
        var row = Row(client);
        row.Button.IsEnabled = false;
        row.Status.Text = Loc.T("Checking…");
        try
        {
            var status = await Task.Run(() => Connector().Check(client));
            if (OwnsRow(generation, _statusGeneration)) ApplyStatus(status);
        }
        catch (Exception error)
        {
            if (OwnsRow(generation, _statusGeneration))
            {
                ShowRow(client, Loc.T("Not connected"), Loc.T(error.Message));
            }
        }
        finally
        {
            row.Button.IsEnabled = true;
        }
    }

    private async Task ConnectOneAsync(AgentClientKind client)
    {
        if (_connected.GetValueOrDefault(client))
        {
            await CheckOneAsync(client);
            return;
        }
        AgentSetupPreview preview;
        try
        {
            preview = Connector().SetupPreview(client);
        }
        catch (Exception error)
        {
            ShowRow(client, Loc.T("Not connected"), Loc.T(error.Message));
            return;
        }
        if (!_host.ConfirmManagement(SetupReason(client, preview))) return;

        var generation = _statusGeneration;
        var row = Row(client);
        row.Button.IsEnabled = false;
        row.Status.Text = Loc.T("Connecting…");
        row.Detail.Text = Loc.T("Reviewing the client configuration and installing Ask Key.");
        try
        {
            var status = await Task.Run(() => Connector().Connect(client));
            if (OwnsRow(generation, _statusGeneration)) ApplyStatus(status);
        }
        catch (Exception error)
        {
            if (OwnsRow(generation, _statusGeneration))
            {
                ShowRow(client, Loc.T("Not connected"), Loc.T(error.Message));
            }
        }
        finally
        {
            row.Button.IsEnabled = true;
        }
    }

    private void ApplyStatus(AgentConnectionStatus status)
    {
        _connected[status.Client] = status.Connected;
        var title = status.Connected ? Loc.T("Connected") : status.Installed ? Loc.T("Not connected") : Loc.T("Not installed");
        ShowRow(status.Client, title, Loc.T(status.Message));
        var row = Row(status.Client);
        row.Button.Content = Loc.T(status.Connected ? "Check again" : "Connect");
        row.Button.IsEnabled = status.Installed;
    }

    private void SetChecking()
    {
        foreach (var client in ClientKinds)
        {
            var row = Row(client);
            row.Status.Text = Loc.T("Checking…");
            row.Button.IsEnabled = false;
        }
    }

    private void SetUnavailable(string message)
    {
        foreach (var client in ClientKinds)
        {
            ShowRow(client, Loc.T("Not connected"), Loc.T(message));
            Row(client).Button.IsEnabled = false;
        }
    }

    private (TextBlock Status, TextBlock Detail, Button Button) Row(AgentClientKind client) => client switch
    {
        AgentClientKind.ClaudeCode => (ClaudeStatusText, ClaudeDetailText, ClaudeButton),
        AgentClientKind.Codex => (CodexStatusText, CodexDetailText, CodexButton),
        AgentClientKind.Cursor => (CursorStatusText, CursorDetailText, CursorButton),
        _ => (GrokStatusText, GrokDetailText, GrokButton),
    };

    private void ShowRow(AgentClientKind client, string status, string detail)
    {
        var row = Row(client);
        row.Status.Text = status;
        row.Detail.Text = detail;
    }

    private AgentConnector Connector()
    {
        var pipeName = _host.BrokerPipeName;
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new InvalidOperationException("The Ask Key Broker is not running. Start Ask Key and try again.");
        return new AgentConnector(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            VaultConfiguration.Default.ApplicationSupportDirectory,
            AppPaths.HelperPath,
            pipeName,
            AppPaths.BrokerPipeFile);
    }

    private static string SetupReason(AgentClientKind client, AgentSetupPreview preview)
    {
        var lines = new List<string>
        {
            Loc.F("Connect {0} to Ask Key?", ClientName(client)),
            Loc.T("Ask Key will back up existing files, add its MCP server and credential discovery hook, then verify both."),
        };
        if (preview.McpConfiguration is { } mcp)
            lines.Add(Loc.F("MCP configuration: {0} ({1})", mcp.Path, Loc.T(mcp.Exists ? "exists" : "new")));
        else
            lines.Add(Loc.T("MCP configuration: user scope managed by the Claude Code CLI."));
        lines.Add(Loc.F("Discovery hook: {0} ({1})", preview.DiscoveryHook.Path,
            Loc.T(preview.DiscoveryHook.Exists ? "exists" : "new")));
        foreach (var backup in preview.Backups)
            lines.Add(Loc.F("Backup folder: {0} ({1})", backup.Path, Loc.T(backup.Exists ? "exists" : "new")));
        lines.Add(Loc.T("If MCP setup fails, Ask Key restores the discovery hook. Start a new task after connecting."));
        return string.Join(Environment.NewLine, lines);
    }

    private static string ClientName(AgentClientKind client) => client switch
    {
        AgentClientKind.ClaudeCode => Loc.T("Claude Code"),
        AgentClientKind.Codex => Loc.T("Codex"),
        AgentClientKind.Cursor => Loc.T("Cursor"),
        _ => Loc.T("Grok CLI"),
    };

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        _host.ToggleAgentAccess();
        Refresh();
    }

    private void CopyConfig_Click(object sender, RoutedEventArgs e) => ClipboardSafety.Copy(McpConfigText.Text);

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = Path.GetDirectoryName(AppPaths.HelperPath)!;
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception error)
        {
            _host.ShowError(error.Message);
        }
    }

    private void RetryBroker_Click(object sender, RoutedEventArgs e)
    {
        _host.RetryBroker();
        Refresh();
    }

    private void CheckConnections_Click(object sender, RoutedEventArgs e) => Refresh();
    private void ConnectClaude_Click(object sender, RoutedEventArgs e) => _ = ConnectOneAsync(AgentClientKind.ClaudeCode);
    private void ConnectCodex_Click(object sender, RoutedEventArgs e) => _ = ConnectOneAsync(AgentClientKind.Codex);
    private void ConnectCursor_Click(object sender, RoutedEventArgs e) => _ = ConnectOneAsync(AgentClientKind.Cursor);
    private void ConnectGrok_Click(object sender, RoutedEventArgs e) => _ = ConnectOneAsync(AgentClientKind.Grok);

    private async void CheckHelper_Click(object sender, RoutedEventArgs e)
    {
        var helper = AppPaths.HelperPath;
        if (!File.Exists(helper))
        {
            ShowCheckResult(Loc.F("Helper not found at {0}. Package askkey.exe under the Helpers folder beside AskKey.exe.", helper));
            return;
        }
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(helper, "health")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                ShowCheckResult(Loc.T("Helper check timed out after 10 seconds."));
                return;
            }
            var output = (await stdout).Trim();
            var error = (await stderr).Trim();
            ShowCheckResult(string.IsNullOrEmpty(error) ? output : output + Environment.NewLine + error);
        }
        catch (Exception error)
        {
            ShowCheckResult(error.Message);
        }
    }

    private void ShowCheckResult(string text)
    {
        CheckResultText.Text = text;
        CheckResultText.Visibility = Visibility.Visible;
    }
}
