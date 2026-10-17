using System.Windows;
using System.Windows.Threading;
using AskKey.Broker.Approval;
using AskKey.Broker.Protocol;

namespace AskKeyApp;

public partial class ApprovalWindow : Window
{
    private readonly AppHost _host;
    private readonly BrokerPendingApproval _pending;
    private readonly DispatcherTimer _timer;
    private bool _decided;

    public ApprovalWindow(AppHost host, BrokerPendingApproval pending)
    {
        _host = host;
        _pending = pending;
        InitializeComponent();

        var request = pending.Request;
        RequesterText.Text = Loc.F("“{0}” is requesting access", NonEmpty(request.CallerName) ?? Loc.T("Local Agent"));
        CredentialText.Text = Loc.F("Credential: “{0}”", pending.DisplayCredentialName);
        ActionText.Text = request.Operation switch
        {
            BrokerApprovalOperation.Read when !string.IsNullOrWhiteSpace(request.DisplayValue?.CommandSummary)
                => Loc.T("An Agent wants to use this credential to run a command."),
            BrokerApprovalOperation.Read => Loc.T("An Agent wants to read this credential."),
            BrokerApprovalOperation.Create => Loc.T("An Agent wants to create a credential."),
            BrokerApprovalOperation.Modify => Loc.T("An Agent wants to change this credential."),
            BrokerApprovalOperation.Delete => Loc.T("An Agent wants to delete this credential."),
            _ => Loc.T("An Agent wants to reorganize credentials."),
        };

        var command = request.DisplayValue?.CommandSummary;
        CommandText.Text = string.IsNullOrWhiteSpace(command)
            ? OperationDescription(request.Operation)
            : command;
        WorkingDirectoryText.Text = request.DisplayValue?.WorkingDirectory is { } directory
            ? Loc.F("Working directory: {0}", directory)
            : "";
        MappingsText.Text = MappingSummary(request.DisplayValue);
        TimedButton.Visibility = request.Operation == BrokerApprovalOperation.Read
            ? Visibility.Visible
            : Visibility.Collapsed;
        TimedButton.Content = Loc.F("Allow for {0} minutes", _host.Preferences.DefaultTimedAllowanceMinutes);

        if (_host.FileWriteSummary(pending.RequestId) is { } summary)
        {
            FileCard.Visibility = Visibility.Visible;
            FileSummaryText.Text = Loc.F("{0} · {1} · {2}", summary.OriginalFilename,
                FormatBytes(summary.ByteCount), OperationLabel(summary.Operation));
            FileDigestText.Text = summary.PreviousDigest is { } previous
                ? Loc.F("New {0}\nPrevious {1}", Short(summary.Digest), Short(previous))
                : Loc.F("Digest {0}", Short(summary.Digest));
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateExpiry();
        _timer.Start();
        UpdateExpiry();
    }

    private static string OperationDescription(BrokerApprovalOperation operation) => operation switch
    {
        BrokerApprovalOperation.Read => Loc.T("Read the credential"),
        BrokerApprovalOperation.Create => Loc.T("Create a new credential"),
        BrokerApprovalOperation.Modify => Loc.T("Modify the credential"),
        BrokerApprovalOperation.Delete => Loc.T("Delete the credential"),
        _ => Loc.T("Reorganize credentials"),
    };

    private static string OperationLabel(BrokerApprovalOperation operation) => operation switch
    {
        BrokerApprovalOperation.Read => Loc.T("Read"),
        BrokerApprovalOperation.Create => Loc.T("Create"),
        BrokerApprovalOperation.Modify => Loc.T("Modify"),
        BrokerApprovalOperation.Delete => Loc.T("Delete"),
        _ => Loc.T("Organize"),
    };

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string MappingSummary(BrokerApprovalOperationRequest.Display? display)
    {
        if (display is null) return Loc.T("Delivery mapping is unavailable until you approve.");
        var parts = new List<string>();
        if (display.EnvironmentVariables is { Count: > 0 } environment)
            parts.Add(Loc.F("Environment: {0}", string.Join(", ", environment)));
        if (display.TemporaryFileVariables is { Count: > 0 } files)
            parts.Add(Loc.F("Temporary file path: {0}", string.Join(", ", files)));
        return parts.Count == 0 ? Loc.T("Kept inside Ask Key; no values are passed to the Agent.") : string.Join(" · ", parts);
    }

    private static string FormatBytes(int bytes) => bytes < 1024
        ? $"{bytes} B"
        : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes / (1024.0 * 1024.0):0.#} MB";

    private static string Short(string digest) => digest.Length <= 24 ? digest : digest[..24] + "…";

    private void UpdateExpiry()
    {
        if (_pending.ExpiresAt is not { } expiresAt)
        {
            ExpiryText.Text = Loc.T("No deadline");
            return;
        }
        var remaining = expiresAt - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            ExpiryText.Text = Loc.T("Expired");
            TimedButton.IsEnabled = false;
            IsEnabled = false;
            _timer.Stop();
            _decided = true;
            Close();
            _host.RefreshPendingCount();
            return;
        }
        ExpiryText.Text = Loc.F("Expires in {0}:{1:00}", (int)remaining.TotalMinutes, remaining.Seconds);
    }

    private void Allow_Click(object sender, RoutedEventArgs e) => Decide(BrokerApprovalDecision.Once);

    private void Timed_Click(object sender, RoutedEventArgs e)
        => Decide(BrokerApprovalDecision.TimedAllow(TimeSpan.FromMinutes(_host.Preferences.DefaultTimedAllowanceMinutes)));

    private void Deny_Click(object sender, RoutedEventArgs e) => Decide(BrokerApprovalDecision.Deny);

    private void Decide(BrokerApprovalDecision decision)
    {
        if (_decided) return;
        _decided = true;
        _timer.Stop();
        _host.Decide(_pending, decision);
        Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        if (_decided) return;
        _decided = true;
        _host.Decide(_pending, BrokerApprovalDecision.Deny);
    }
}
