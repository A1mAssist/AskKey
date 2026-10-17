using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using AskKey.Broker.Approval;
using AskKey.Broker.FileWrite;
using AskKey.Broker.Protocol;
using AskKey.Vault;

namespace AskKeyApp;

public partial class ApprovalWindow : Window
{
    private readonly AppHost _host;
    private readonly BrokerPendingApproval _pending;
    private readonly BrokerCredentialWriteSummary? _write;
    private readonly BrokerOrganizationSummary? _organization;
    private readonly BrokerFileWriteSummary? _file;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _decided;
    private bool _detailsExpanded;

    public ApprovalWindow(AppHost host, BrokerPendingApproval pending)
    {
        _host = host;
        _pending = pending;
        InitializeComponent();
        Title = Loc.T("Ask Key approval");
        WindowHelpers.Configure(this, 640, 760, 600, 520, showInSwitchers: false);
        ThemeManager.Apply(this);
        RootGrid.KeyDown += Window_PreviewKeyDown;

        _write = _host.FrozenWriteSummary(pending);
        _organization = _host.FrozenOrganizationSummary(pending);
        _file = _host.FileWriteSummary(pending.RequestId);

        var request = pending.Request;
        RequesterText.Text = ApprovalDetailsContent.Title(pending, _write);
        ActionText.Text = request.Operation == BrokerApprovalOperation.Read
            ? "" : ApprovalDetailsContent.Subtitle(pending, _write, _organization) ?? OperationDescription(request.Operation);
        ActionText.Visibility = request.Operation == BrokerApprovalOperation.Read ? Visibility.Collapsed : Visibility.Visible;

        if (request.Operation == BrokerApprovalOperation.Read && request.DisplayValue is { } display)
        {
            CommandCard.Visibility = Visibility.Visible;
            CommandText.Text = display.CommandSummary;
            WorkingDirectoryText.Text = string.IsNullOrWhiteSpace(display.WorkingDirectory)
                ? "" : Loc.F("Working directory: {0}", display.WorkingDirectory);
            WorkingDirectoryText.Visibility = string.IsNullOrWhiteSpace(display.WorkingDirectory)
                ? Visibility.Collapsed : Visibility.Visible;
            MappingsText.Text = MappingSummary(display);
            MappingsText.Visibility = Visibility.Visible;
        }
        else
        {
            CommandCard.Visibility = Visibility.Collapsed;
            WorkingDirectoryText.Visibility = Visibility.Collapsed;
            MappingsText.Visibility = Visibility.Collapsed;
        }

        TimedButton.Visibility = request.Operation == BrokerApprovalOperation.Read
                                 && _host.Preferences.TimedAllowanceEnabled ? Visibility.Visible : Visibility.Collapsed;
        TimedButton.Content = Loc.F("Allow for {0} minutes", _host.Preferences.DefaultTimedAllowanceMinutes);
        SetApprovalButton(request.Operation, _write);
        PopulateFileCard();
        PopulateDetails();

        _timer = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => UpdateExpiry();
        _timer.Start();
        UpdateExpiry();
        Closed += (_, _) =>
        {
            _timer.Stop();
            ClearRevealedMaterial();
            _completion.TrySetResult(true);
        };
    }

    public Task<bool> ShowAsync()
        => WindowHelpers.ShowDialogAsync(this, App.CurrentWindow, _completion.Task);

    public void DismissForPrivacy()
    {
        ClearRevealedMaterial();
        Close();
    }

    private void PopulateFileCard()
    {
        if (_file is null) return;
        FileCard.Visibility = Visibility.Visible;
        FileSummaryText.Text = Loc.F("{0} · {1} · {2}", _file.OriginalFilename,
            FormatBytes(_file.ByteCount), OperationLabel(_file.Operation));
        FileDigestText.Text = _file.PreviousDigest is { } previous
            ? Loc.F("New {0}\nPrevious {1}", Short(_file.Digest), Short(previous))
            : Loc.F("Digest {0}", Short(_file.Digest));
        FileRevealHint.Text = Loc.T("The file stays hidden until you authenticate.");
        RevealFileButton.Content = Loc.T("View file");
    }

    private void PopulateDetails()
    {
        var rows = ApprovalDetailsContent.Build(_pending, _write, _organization, _file,
            _host.Preferences.TimedAllowanceEnabled, _host.Preferences.DefaultTimedAllowanceMinutes);
        foreach (var row in rows) DetailsRows.Children.Add(DetailRow(row));
        ValueRevealCard.Visibility = ApprovalDetailsContent.HasRevealableValue(_write) ? Visibility.Visible : Visibility.Collapsed;
        DetailsButton.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static FrameworkElement DetailRow(ApprovalDetailRow row)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = new TextBlock
        {
            Text = row.Label,
            Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
        };
        var value = new TextBlock
        {
            Text = row.Text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextPrimaryBrush"],
            VerticalAlignment = VerticalAlignment.Top,
        };
        if (row.Code)
        {
            value.FontFamily = (FontFamily)Application.Current.Resources["MonoFont"];
            value.FontSize = 11;
        }
        Grid.SetColumn(label, 0);
        Grid.SetColumn(value, 1);
        grid.Children.Add(label);
        grid.Children.Add(value);
        return grid;
    }

    private void SetApprovalButton(BrokerApprovalOperation operation, BrokerCredentialWriteSummary? write)
    {
        AllowButton.Content = operation switch
        {
            BrokerApprovalOperation.Create => Loc.T("Create credential"),
            BrokerApprovalOperation.Modify when ApprovalDetailsContent.IsValueOnlyModify(write) => Loc.T("Replace"),
            BrokerApprovalOperation.Modify => Loc.T("Change"),
            BrokerApprovalOperation.Delete => Loc.T("Delete"),
            BrokerApprovalOperation.Organize => Loc.T("Apply"),
            _ => Loc.T("Allow once"),
        };
        if (ApprovalDetailsContent.IsDestructive(operation, write, _organization))
            AllowButton.Style = (Style)Application.Current.Resources["DangerSolidButtonStyle"];
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

    private static string MappingSummary(BrokerApprovalOperationRequest.Display display)
    {
        if (display.EnvironmentVariables is null || display.TemporaryFileVariables is null)
            return Loc.T("Delivery mapping is unavailable until you approve.");
        var parts = new List<string>();
        if (display.EnvironmentVariables.Count > 0)
            parts.Add(Loc.F("Environment: {0}", string.Join(", ", display.EnvironmentVariables)));
        if (display.TemporaryFileVariables.Count > 0)
            parts.Add(Loc.F("Temporary file path: {0}", string.Join(", ", display.TemporaryFileVariables)));
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
            RootGrid.IsHitTestVisible = false;
            _timer.Stop();
            _decided = true;
            ClearRevealedMaterial();
            Close();
            _host.RefreshPendingCount();
            return;
        }
        ExpiryText.Text = Loc.F("Expires in {0}:{1:00}", (int)remaining.TotalMinutes, remaining.Seconds);
    }

    private void Details_Click(object sender, RoutedEventArgs e)
    {
        _detailsExpanded = !_detailsExpanded;
        DetailsCard.Visibility = _detailsExpanded ? Visibility.Visible : Visibility.Collapsed;
        DetailsChevron.Text = _detailsExpanded ? "⌄" : "›";
    }

    private void RevealFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var file = _host.RevealFileWrite(_pending);
            FileRevealBox.Text = DescribeBytes(file.Bytes);
            FileRevealBox.Visibility = Visibility.Visible;
            RevealFileButton.Visibility = Visibility.Collapsed;
            HideFileButton.Visibility = Visibility.Visible;
            FileRevealHint.Text = Loc.F("{0} · {1} · {2}", file.OriginalFilename,
                FormatBytes(file.ByteCount), Loc.T("contents"));
            FileRevealError.Visibility = Visibility.Collapsed;
        }
        catch
        {
            FileRevealError.Text = Loc.T("Unable to view: authentication was not completed or the request is no longer valid.");
            FileRevealError.Visibility = Visibility.Visible;
        }
    }

    private void HideFile_Click(object sender, RoutedEventArgs e)
    {
        FileRevealBox.Text = "";
        FileRevealBox.Visibility = Visibility.Collapsed;
        RevealFileButton.Visibility = Visibility.Visible;
        HideFileButton.Visibility = Visibility.Collapsed;
        FileRevealError.Visibility = Visibility.Collapsed;
        if (_file is not null) FileRevealHint.Text = Loc.T("The file stays hidden until you authenticate.");
    }

    private void RevealValue_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RevealedValueBox.Text = DescribeWrite(_host.RevealCredentialWrite(_pending));
            RevealedValueBox.Visibility = Visibility.Visible;
            ValueMaskedText.Visibility = Visibility.Collapsed;
            RevealValueButton.Visibility = Visibility.Collapsed;
            HideValueButton.Visibility = Visibility.Visible;
            ValueRevealError.Visibility = Visibility.Collapsed;
        }
        catch
        {
            ValueRevealError.Text = Loc.T("Unable to view: authentication was not completed or the request is no longer valid.");
            ValueRevealError.Visibility = Visibility.Visible;
        }
    }

    private void HideValue_Click(object sender, RoutedEventArgs e)
    {
        RevealedValueBox.Text = "";
        RevealedValueBox.Visibility = Visibility.Collapsed;
        ValueMaskedText.Visibility = Visibility.Visible;
        RevealValueButton.Visibility = Visibility.Visible;
        HideValueButton.Visibility = Visibility.Collapsed;
    }

    private static string DescribeWrite(FrozenCredentialWrite material)
    {
        var before = DescribeInputs(material.Before);
        var after = DescribeInputs(material.After);
        return before.Length == 0 ? after : Loc.T("Before") + "\n" + before + "\n\n" + Loc.T("After") + "\n" + after;
    }

    private static string DescribeInputs(IReadOnlyList<CredentialComponentInput> inputs)
        => string.Join("\n\n", inputs.Select(input => input.Name + "\n" + DescribeValue(input.Value)));

    private static string DescribeValue(CredentialComponentValue value) => value switch
    {
        CredentialComponentValue.Text text => text.Value,
        CredentialComponentValue.File file => file.Filename + " (" + file.Bytes.Length + " B)\n" + DescribeBytes(file.Bytes),
        _ => "",
    };

    private static string DescribeBytes(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Loc.T("Base64") + "\n" + Convert.ToBase64String(bytes);
        }
    }

    private void ClearRevealedMaterial()
    {
        if (FileRevealBox is null) return;
        FileRevealBox.Text = "";
        FileRevealBox.Visibility = Visibility.Collapsed;
        RevealFileButton.Visibility = Visibility.Visible;
        HideFileButton.Visibility = Visibility.Collapsed;
        FileRevealError.Visibility = Visibility.Collapsed;
        if (_file is not null) FileRevealHint.Text = Loc.T("The file stays hidden until you authenticate.");
        RevealedValueBox.Text = "";
        RevealedValueBox.Visibility = Visibility.Collapsed;
        ValueMaskedText.Visibility = Visibility.Visible;
        RevealValueButton.Visibility = Visibility.Visible;
        HideValueButton.Visibility = Visibility.Collapsed;
        ValueRevealError.Visibility = Visibility.Collapsed;
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
        ClearRevealedMaterial();
        _host.Decide(_pending, decision);
        Close();
    }

    private void Window_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || _decided) return;
        ClearRevealedMaterial();
        Close();
        e.Handled = true;
    }
}
