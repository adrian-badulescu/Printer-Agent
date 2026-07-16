using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using PrinterAgent.Application.Storage;
using PrinterAgent.Configurator.Services;
using PrinterAgent.Domain;

namespace PrinterAgent.Configurator;

public sealed class PrinterListRow
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; }
    public string Endpoint => string.IsNullOrWhiteSpace(IpAddress) ? "?" : $"{IpAddress}:{Port}";
}

public partial class MainWindow
{
    private static readonly Regex EnrollmentCodeRegex = new("^[A-Za-z0-9]{10}$", RegexOptions.Compiled);
    private static readonly TimeSpan SessionExpirySkew = TimeSpan.FromMinutes(5);
    private int _step;
    private bool _skipEnrollmentMode;
    private readonly AgentConfigurationStore _store = new();
    private readonly AgentSessionProbe _sessionProbe = new();
    private readonly Port9100Scanner _scanner = new();
    private readonly TestPrintService _testPrint = new();
    private CancellationTokenSource? _scanCts;
    private bool _printerIdManual;
    private bool _printerIdProgrammaticChange;
    private bool _printerTypeProgrammaticChange;
    private IPAddress? _selectedHost;

    public ObservableCollection<PrinterListRow> ExistingPrinters { get; } = new();

    public string ExistingPrintersTitleText => UiStrings.Get("Manage_SectionTitle");
    public string DeleteButtonText => UiStrings.Get("Manage_DeleteButton");

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        Loaded += MainWindow_OnLoaded;
    }

    /// <summary>Configurator save replaces the printer JSON object; copy metadata written by the Worker (MAC, recovery notes).</summary>
    private static void MergePreservedPrinterMetadata(JsonObject? previous, JsonObject next)
    {
        if (previous == null)
            return;

        static string? Str(JsonObject o, string camel, string pascal) =>
            o[camel]?.GetValue<string>() ?? o[pascal]?.GetValue<string>();

        var mac = Str(previous, "macAddress", "MacAddress");
        if (!string.IsNullOrWhiteSpace(mac))
            next["macAddress"] = mac.Trim();

        if (previous["fallbackProvisional"] is JsonValue fb && fb.TryGetValue(out bool b))
            next["fallbackProvisional"] = b;
        else if (previous["FallbackProvisional"] is JsonValue fb2 && fb2.TryGetValue(out bool b2))
            next["fallbackProvisional"] = b2;

        var note = Str(previous, "lastDiscoveryNote", "LastDiscoveryNote");
        if (!string.IsNullOrWhiteSpace(note))
            next["lastDiscoveryNote"] = note;
    }

    private void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyUiStrings();

        try
        {
            var root = _store.LoadOrCreateTemplate();
            var code = root["EnrollmentCode"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(code))
                EnrollmentCodeBox.Text = code.Trim();
        }
        catch (Exception ex)
        {
            FooterStatusText.Text = UiStrings.Format("ReadAgentJsonFailed", ex.Message);
        }

        var nics = LocalSubnetService.GetIpv4SubnetOptions();
        NicCombo.ItemsSource = nics;
        var preferred = LocalSubnetService.GetPreferredDefault(nics);
        if (preferred != null)
            NicCombo.SelectedItem = preferred;

        ReloadExistingPrintersList();

        if (_sessionProbe.HasUsableSession(SessionExpirySkew) && ExistingPrinters.Count > 0)
        {
            _skipEnrollmentMode = true;
            _step = 1;
        }

        UpdateStepUi();
    }

    private void ReloadExistingPrintersList()
    {
        ExistingPrinters.Clear();
        try
        {
            var root = _store.LoadOrCreateTemplate();
            if (root["Printers"] is not JsonArray arr)
            {
                UpdateExistingPrintersVisibility();
                return;
            }

            foreach (var p in arr)
            {
                if (p is not JsonObject o)
                    continue;

                var id = o["id"]?.GetValue<string>() ?? o["Id"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var name = o["name"]?.GetValue<string>() ?? o["Name"]?.GetValue<string>() ?? id;
                var ip = o["ipAddress"]?.GetValue<string>() ?? o["IpAddress"]?.GetValue<string>() ?? string.Empty;
                var port = 9100;
                if (o["port"] is JsonValue pv && pv.TryGetValue(out int pi))
                    port = pi;
                else if (o["Port"] is JsonValue pv2 && pv2.TryGetValue(out int pi2))
                    port = pi2;

                ExistingPrinters.Add(new PrinterListRow
                {
                    Id = id,
                    Name = name,
                    IpAddress = ip,
                    Port = port
                });
            }
        }
        catch
        {
            // ignore: empty list is the safe fallback for the UI panel
        }

        UpdateExistingPrintersVisibility();
    }

    private void UpdateExistingPrintersVisibility()
    {
        // Panels are bound by x:Name in XAML; toggle visibility by population, never by step alone.
        var visibility = ExistingPrinters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (ExistingPrintersBorderStep0 != null)
            ExistingPrintersBorderStep0.Visibility = visibility;
        if (ExistingPrintersBorderStep3 != null)
            ExistingPrintersBorderStep3.Visibility = visibility;
    }

    private void DeletePrinter_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string id || string.IsNullOrWhiteSpace(id))
            return;

        var confirm = MessageBox.Show(
            this,
            UiStrings.Format("Manage_DeleteConfirm", id),
            UiStrings.Get("Manage_DeleteTitle"),
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            var root = _store.LoadOrCreateTemplate();
            if (root["Printers"] is JsonArray arr)
            {
                for (var i = arr.Count - 1; i >= 0; i--)
                {
                    var pid = arr[i]?["id"]?.GetValue<string>() ?? arr[i]?["Id"]?.GetValue<string>();
                    if (string.Equals(pid, id, StringComparison.OrdinalIgnoreCase))
                    {
                        arr.RemoveAt(i);
                        break;
                    }
                }

                _store.Save(root);
            }

            ReloadExistingPrintersList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, UiStrings.Get("Manage_DeleteTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ApplyAgentJsonPathText()
    {
        AgentJsonPathText.Text =
            UiStrings.Get("AgentJsonPath_Label") + Environment.NewLine + _store.AgentJsonPath;
    }

    private void ApplyUiStrings()
    {
        Title = UiStrings.Get("WindowTitle");

        BackButton.Content = UiStrings.Get("BackButton");
        ScanButton.Content = UiStrings.Get("ScanButton");
        TestPrintButton.Content = UiStrings.Get("TestPrintButton");
        SuggestIdButton.Content = UiStrings.Get("RegenerateIdButton");
        OpenProgramDataButton.Content = UiStrings.Get("OpenProgramDataButton");
        RefreshIpButton.Content = UiStrings.Get("RefreshIp_Button");
        AddAnotherPrinterButton.Content = UiStrings.Get("AddAnotherPrinterButton");

        Step1TitleText.Text = UiStrings.Get("Step1_Title");
        Step1DescriptionText.Text = UiStrings.Get("Step1_Description");
        EnrollmentCodeLabel.Content = UiStrings.Get("EnrollmentCode_Label");
        EnrollmentHintText.Text = UiStrings.Get("EnrollmentCode_Hint");
        ApplyAgentJsonPathText();

        Step2TitleText.Text = UiStrings.Get("Step2_Title");
        Step2DescriptionText.Text = UiStrings.Get("Step2_Description");
        SetupTypeLabel.Text = UiStrings.Get("SetupType_Label");
        SetupTypeEscPosRadio.Content = UiStrings.Get("SetupType_EscPos");
        SetupTypeFiscalNetRadio.Content = UiStrings.Get("SetupType_FiscalNet");
        NicLabel.Content = UiStrings.Get("Nic_Label");
        ScanConsentText.Text = UiStrings.Get("ScanConsent_Text");
        FoundHostsLabel.Content = UiStrings.Get("FoundHosts_Label");

        Step3TitleText.Text = UiStrings.Get("Step3_Title");
        PrinterNameLabel.Content = UiStrings.Get("PrinterName_Label");
        PrinterTypeLabel.Content = UiStrings.Get("PrinterType_Label");
        PrinterIdHintText.Text = UiStrings.Get("PrinterId_Hint");
        if (FiscalNetHintText != null)
            FiscalNetHintText.Text = UiStrings.Get("FiscalNet_Hint");
        if (IpAddressLabel != null)
            IpAddressLabel.Content = UiStrings.Get("IpAddress_Label");
        if (FiscalVatGroupLabel != null)
            FiscalVatGroupLabel.Content = UiStrings.Get("FiscalVatGroup_Label");
        if (FiscalDepartmentLabel != null)
            FiscalDepartmentLabel.Content = UiStrings.Get("FiscalDepartment_Label");
        if (FiscalTimeoutLabel != null)
            FiscalTimeoutLabel.Content = UiStrings.Get("FiscalTimeout_Label");
        PortLabel.Content = UiStrings.Get("Port_Label");
        SaveHintText.Text = UiStrings.Get("SaveHintText");
        ApplyPrinterTypeComboLabels();

        Step4TitleText.Text = UiStrings.Get("Step4_Title");
        DoneHintText.Text = UiStrings.Get("DoneHintText");
    }

    private void ApplyPrinterTypeComboLabels()
    {
        if (PrinterTypeCombo is null)
            return;

        foreach (var item in PrinterTypeCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag)
            {
                item.Content = string.Equals(tag, PrinterTypes.FiscalNet, StringComparison.OrdinalIgnoreCase)
                    ? UiStrings.Get("PrinterType_FiscalNet")
                    : UiStrings.Get("PrinterType_EscPos");
            }
        }
    }

    private void UpdateStepUi()
    {
        StepEnrollmentPanel.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        StepNetworkPanel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepPrinterPanel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepDonePanel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.IsEnabled = _step is > 0 and < 3 && !(_step == 1 && _skipEnrollmentMode);
        NextButton.IsEnabled = true;
        NextButton.Content = _step == 2 ? UiStrings.Get("SaveButton") : _step == 3 ? UiStrings.Get("CloseButton") : UiStrings.Get("NextButton");
        NextButton.IsDefault = _step != 3;

        FooterStatusText.Text = _step switch
        {
            0 when ExistingPrinters.Count > 0 && !_sessionProbe.HasUsableSession(SessionExpirySkew)
                => UiStrings.Get("Footer_Step1_NotEnrolled"),
            0 => UiStrings.Get("Footer_Step1"),
            1 when _skipEnrollmentMode => IsFiscalNetSetupSelected()
                ? UiStrings.Get("Footer_Step2_FiscalNet_AddPrinter")
                : UiStrings.Get("Footer_Step2_AddPrinter"),
            1 => IsFiscalNetSetupSelected()
                ? UiStrings.Get("Footer_Step2_FiscalNet")
                : UiStrings.Get("Footer_Step2"),
            2 => UiStrings.Get("Footer_Step3"),
            3 => UiStrings.Get("Footer_Step4"),
            _ => ""
        };

        if (_step == 1)
            ApplySetupTypeUi();

        if (_step == 2)
        {
            SyncPrinterTypeComboFromSetup();
            if (string.IsNullOrWhiteSpace(IpAddressBox.Text) && _selectedHost != null)
                IpAddressBox.Text = _selectedHost.ToString();

            SelectedHostText.Text = !string.IsNullOrWhiteSpace(IpAddressBox.Text)
                ? UiStrings.Format("SelectedHost_WithPort", IpAddressBox.Text.Trim(), PortBox.Text.Trim())
                : _selectedHost != null
                    ? UiStrings.Format("SelectedHost_FromScan", _selectedHost.ToString(), PortBox.Text.Trim())
                    : UiStrings.Get("SelectedHost_FiscalNetManual");
            PrinterIdLabel.Content = IsFiscalNetSelected()
                ? UiStrings.Get("PrinterId_Label_FiscalNet")
                : UiStrings.Get("PrinterId_Label_EscPos");
            RefreshPrinterIdFromNameIfNeeded();
            ApplyPrinterTypeUi();
        }
    }

    private void SetupTypeRadio_OnChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        ApplySetupTypeUi();
        SyncPrinterTypeComboFromSetup();
    }

    private bool IsFiscalNetSetupSelected() =>
        SetupTypeFiscalNetRadio?.IsChecked == true;

    private void ApplySetupTypeUi()
    {
        if (EscPosScanPanel is null || SetupTypeHintText is null)
            return;

        var fiscal = IsFiscalNetSetupSelected();
        EscPosScanPanel.Visibility = fiscal ? Visibility.Collapsed : Visibility.Visible;
        SetupTypeHintText.Text = fiscal
            ? UiStrings.Get("SetupType_FiscalNet_Hint")
            : UiStrings.Get("SetupType_EscPos_Hint");
    }

    private NicSubnetOption? GetSelectedNicOption() =>
        NicCombo.SelectedItem as NicSubnetOption;

    private string? GetSelectedNicIpv4String() =>
        GetSelectedNicOption()?.IPv4.ToString();

    private void SyncPrinterTypeComboFromSetup()
    {
        if (PrinterTypeCombo is null || _printerTypeProgrammaticChange)
            return;

        var targetTag = IsFiscalNetSetupSelected() ? PrinterTypes.FiscalNet : PrinterTypes.EscPos;
        foreach (var item in PrinterTypeCombo.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is string tag &&
                string.Equals(tag, targetTag, StringComparison.OrdinalIgnoreCase))
            {
                _printerTypeProgrammaticChange = true;
                try
                {
                    PrinterTypeCombo.SelectedItem = item;
                }
                finally
                {
                    _printerTypeProgrammaticChange = false;
                }
                return;
            }
        }
    }

    private void SyncSetupFromPrinterTypeCombo()
    {
        if (SetupTypeEscPosRadio is null || SetupTypeFiscalNetRadio is null)
            return;

        if (IsFiscalNetSelected())
            SetupTypeFiscalNetRadio.IsChecked = true;
        else
            SetupTypeEscPosRadio.IsChecked = true;
    }

    private void PrefillFiscalNetAddressFromNic()
    {
        var nicIp = GetSelectedNicIpv4String();
        if (!string.IsNullOrWhiteSpace(nicIp))
            IpAddressBox.Text = nicIp;
    }

    private void PrinterTypeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _printerTypeProgrammaticChange)
            return;
        SyncSetupFromPrinterTypeCombo();
        ApplySetupTypeUi();
        ApplyPrinterTypeUi();
    }

    private bool IsFiscalNetSelected()
    {
        if (PrinterTypeCombo.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            string.Equals(tag, PrinterTypes.FiscalNet, StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    private void ApplyPrinterTypeUi()
    {
        // SelectionChanged can fire during InitializeComponent before later named controls exist.
        if (FiscalSettingsPanel is null || PortLabel is null || IpAddressBox is null || PortBox is null)
            return;

        var fiscal = IsFiscalNetSelected();
        FiscalSettingsPanel.Visibility = fiscal ? Visibility.Visible : Visibility.Collapsed;
        PortLabel.Content = fiscal ? UiStrings.Get("PortLabel_Http") : UiStrings.Get("Port_Label");

        if (fiscal)
        {
            if (string.IsNullOrWhiteSpace(IpAddressBox.Text))
            {
                var nicIp = GetSelectedNicIpv4String();
                if (!string.IsNullOrWhiteSpace(nicIp))
                    IpAddressBox.Text = nicIp;
            }
            if (PortBox.Text.Trim() is "" or "9100")
                PortBox.Text = "65400";
        }
        else if (PortBox.Text.Trim() == "65400")
        {
            PortBox.Text = "9100";
        }
    }

    private void BackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_step <= 0)
            return;
        _scanCts?.Cancel();
        _step--;
        UpdateStepUi();
    }

    private async void NextButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_step == 3)
        {
            Close();
            return;
        }

        if (_step == 0)
        {
            var code = EnrollmentCodeBox.Text.Trim();
            if (!EnrollmentCodeRegex.IsMatch(code))
            {
                MessageBox.Show(
                    this,
                    UiStrings.Get("Validation_EnrollmentCode"),
                    UiStrings.Get("Validation_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _step++;
            UpdateStepUi();
            return;
        }

        if (_step == 1)
        {
            if (GetSelectedNicOption() is null)
            {
                MessageBox.Show(this, UiStrings.Get("Validation_SelectNic"), UiStrings.Get("Validation_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (IsFiscalNetSetupSelected())
            {
                _selectedHost = null;
                _step++;
                _printerIdManual = false;
                SyncPrinterTypeComboFromSetup();
                PrefillFiscalNetAddressFromNic();
                UpdateStepUi();
                return;
            }

            if (ScanConsentCheckBox.IsChecked != true)
            {
                MessageBox.Show(
                    this,
                    UiStrings.Get("Validation_ScanConsent"),
                    UiStrings.Get("Validation_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (FoundHostsList.SelectedItem is not IPAddress ip)
            {
                MessageBox.Show(
                    this,
                    UiStrings.Get("Validation_SelectScannedHost"),
                    UiStrings.Get("Validation_Title"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _selectedHost = ip;
            _step++;
            _printerIdManual = false;
            SyncPrinterTypeComboFromSetup();
            UpdateStepUi();
            return;
        }

        if (_step == 2)
            await SaveConfigurationAsync();
    }

    private async Task SaveConfigurationAsync()
    {
        var name = PrinterNameBox.Text.Trim();
        var pid = PrinterIdBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show(this, UiStrings.Get("Validation_PrinterName"), UiStrings.Get("Validation_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(pid) || !Regex.IsMatch(pid, "^[A-Za-z0-9._-]+$"))
        {
            MessageBox.Show(
                this,
                UiStrings.Get("Validation_PrinterId"),
                UiStrings.Get("Validation_Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (_selectedHost == null && !IsFiscalNetSelected())
        {
            MessageBox.Show(this, UiStrings.Get("Validation_MissingAddress"), UiStrings.Get("Validation_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ipText = IpAddressBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(ipText))
            ipText = _selectedHost?.ToString() ?? string.Empty;

        if (!IPAddress.TryParse(ipText, out var ipAddress))
        {
            MessageBox.Show(this, UiStrings.Get("Validation_InvalidIp"), UiStrings.Get("Validation_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, UiStrings.Get("Validation_InvalidTcpPort"), UiStrings.Get("Validation_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        NextButton.IsEnabled = false;
        try
        {
            var root = _store.LoadOrCreateTemplate();
            var enrollmentCode = EnrollmentCodeBox.Text.Trim();
            if (!(_skipEnrollmentMode && string.IsNullOrWhiteSpace(enrollmentCode)))
                root["EnrollmentCode"] = enrollmentCode;

            var printers = root["Printers"] as JsonArray ?? new JsonArray();
            root["Printers"] = printers;

            JsonObject? replaced = null;
            for (var i = printers.Count - 1; i >= 0; i--)
            {
                var id = printers[i]?["id"]?.GetValue<string>() ?? printers[i]?["Id"]?.GetValue<string>();
                if (string.Equals(id, pid, StringComparison.OrdinalIgnoreCase))
                {
                    replaced = printers[i] as JsonObject;
                    printers.RemoveAt(i);
                    break;
                }
            }

            var printerType = ResolvePrinterTypeForSave(port);

            var entry = new JsonObject
            {
                ["id"] = pid,
                ["name"] = name,
                ["ipAddress"] = ipAddress.ToString(),
                ["port"] = port,
                ["type"] = printerType
            };

            if (string.Equals(printerType, PrinterTypes.FiscalNet, StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(FiscalVatGroupBox.Text.Trim(), out var vatGroup);
                _ = int.TryParse(FiscalDepartmentBox.Text.Trim(), out var department);
                _ = int.TryParse(FiscalTimeoutBox.Text.Trim(), out var timeoutMs);
                entry["fiscal"] = new JsonObject
                {
                    ["defaultVatGroup"] = vatGroup is >= 1 and <= 5 ? vatGroup : 1,
                    ["defaultDepartment"] = department > 0 ? department : 1,
                    ["timeoutMs"] = timeoutMs >= 5000 ? timeoutMs : 120_000
                };
            }

            MergePreservedPrinterMetadata(replaced, entry);
            printers.Add(entry);

            await Task.Run(() => _store.Save(root)).ConfigureAwait(true);
            ReloadExistingPrintersList();

            _step = 3;
            DoneMessageText.Text = UiStrings.Format("DoneMessage_Saved", _store.AgentJsonPath);
            UpdateStepUi();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, UiStrings.Format("SaveFailed", ex.Message), UiStrings.Get("Error_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            NextButton.IsEnabled = true;
        }
    }

    private string ResolvePrinterTypeForSave(int port)
    {
        if (IsFiscalNetSelected() || IsFiscalNetSetupSelected())
            return PrinterTypes.FiscalNet;

        if (port == PrinterTypes.DefaultFiscalNetPort)
            return PrinterTypes.FiscalNet;

        return PrinterTypes.EscPos;
    }

    private void NicCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NicCombo.SelectedItem is NicSubnetOption opt)
        {
            SubnetSummaryText.Text = UiStrings.Format(
                "SubnetSummary",
                opt.CidrDisplay,
                opt.IPv4,
                opt.PrefixLength,
                opt.HasDefaultGateway ? UiStrings.Get("SubnetSummary_HasGateway") : "");
        }
        else
            SubnetSummaryText.Text = "";
    }

    private async void ScanButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (NicCombo.SelectedItem is not NicSubnetOption opt)
        {
            MessageBox.Show(this, UiStrings.Get("Validation_SelectNic"), UiStrings.Get("Validation_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (ScanConsentCheckBox.IsChecked != true)
        {
            MessageBox.Show(
                this,
                UiStrings.Get("Validation_ScanConsent"),
                UiStrings.Get("ScanConsent_Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        ScanButton.IsEnabled = false;
        FoundHostsList.ItemsSource = null;
        ScanProgressText.Text = "";

        try
        {
            var progress = new Progress<string>(s => ScanProgressText.Text = s);
            var found = await _scanner.ScanAsync(opt.IPv4, opt.PrefixLength, progress, token).ConfigureAwait(true);
            FoundHostsList.ItemsSource = new ObservableCollection<IPAddress>(found);
            ScanProgressText.Text = found.Count == 0
                ? UiStrings.Get("Scan_NoneFound")
                : UiStrings.Format("Scan_FoundCount", found.Count);
        }
        catch (OperationCanceledException)
        {
            ScanProgressText.Text = UiStrings.Get("Scan_Cancelled");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, UiStrings.Format("Scan_Failed", ex.Message), UiStrings.Get("Error_Title"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            ScanButton.IsEnabled = true;
        }
    }

    private void FoundHostsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // list selection only; Next validates
    }

    private void PrinterNameBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_step != 2 || _printerIdManual)
            return;
        RefreshPrinterIdFromNameIfNeeded();
    }

    private void PrinterIdBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_step != 2 || _printerIdProgrammaticChange)
            return;
        _printerIdManual = true;
    }

    private void SuggestIdButton_OnClick(object sender, RoutedEventArgs e)
    {
        _printerIdManual = false;
        RefreshPrinterIdFromNameIfNeeded(force: true);
        _printerIdManual = false;
    }

    private void RefreshPrinterIdFromNameIfNeeded(bool force = false)
    {
        if (_step != 2)
            return;
        if (_printerIdManual && !force)
            return;

        try
        {
            var root = _store.LoadOrCreateTemplate();
            var existing = new List<string>();
            if (root["Printers"] is JsonArray arr)
            {
                foreach (var p in arr)
                {
                    var id = p?["id"]?.GetValue<string>() ?? p?["Id"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(id))
                        existing.Add(id);
                }
            }

            var name = PrinterNameBox.Text.Trim();
            if (string.IsNullOrEmpty(name))
                return;

            var slug = AgentConfigurationStore.ToPrinterIdSlug(name, existing);
            _printerIdProgrammaticChange = true;
            try
            {
                PrinterIdBox.Text = slug;
            }
            finally
            {
                _printerIdProgrammaticChange = false;
            }
        }
        catch
        {
            // ignore preview errors
        }
    }

    private async void TestPrintButton_OnClick(object sender, RoutedEventArgs e)
    {
        var ipText = IpAddressBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(ipText) && _selectedHost != null)
            ipText = _selectedHost.ToString();

        if (string.IsNullOrWhiteSpace(ipText) || !IPAddress.TryParse(ipText, out var testIp))
        {
            MessageBox.Show(this, UiStrings.Get("Validation_MissingPrinterAddress"), UiStrings.Get("TestPrint_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, UiStrings.Get("Validation_InvalidPort"), UiStrings.Get("TestPrint_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (IsFiscalNetSelected())
        {
            MessageBox.Show(this, UiStrings.Get("TestPrint_FiscalNetUnavailable"), UiStrings.Get("TestPrint_Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var printer = new Printer
        {
            IpAddress = testIp.ToString(),
            Port = port,
            Name = string.IsNullOrWhiteSpace(PrinterNameBox.Text) ? "Test" : PrinterNameBox.Text.Trim()
        };

        TestPrintButton.IsEnabled = false;
        try
        {
            var ok = await _testPrint.SendTestPageAsync(printer).ConfigureAwait(true);
            MessageBox.Show(
                this,
                ok ? UiStrings.Get("TestPrint_Sent") : UiStrings.Get("TestPrint_Failed"),
                UiStrings.Get("TestPrint_Title"),
                MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            TestPrintButton.IsEnabled = true;
        }
    }

    private async void RefreshIpButton_OnClick(object sender, RoutedEventArgs e)
    {
        RefreshIpButton.IsEnabled = false;
        FooterStatusText.Text = UiStrings.Get("RefreshIp_Running");
        try
        {
            var (ok, msg) = await PrinterIpRefreshService.TryRefreshAllPrintersAsync().ConfigureAwait(true);
            MessageBox.Show(
                this,
                msg,
                UiStrings.Get("RefreshIp_Title"),
                MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            ApplyAgentJsonPathText();
            ReloadExistingPrintersList();
        }
        finally
        {
            RefreshIpButton.IsEnabled = true;
            UpdateStepUi();
        }
    }

    private void OpenProgramDataFolder_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = AgentProgramData.Root,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, UiStrings.Get("Explorer_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddAnotherPrinterButton_OnClick(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        _selectedHost = null;
        FoundHostsList.ItemsSource = null;
        FoundHostsList.SelectedItem = null;
        ScanProgressText.Text = "";
        PrinterNameBox.Text = "";
        PrinterIdBox.Text = "";
        PortBox.Text = "9100";
        IpAddressBox.Text = "";
        SetupTypeEscPosRadio.IsChecked = true;
        _printerIdManual = false;
        _skipEnrollmentMode = true;
        _step = 1;
        ApplySetupTypeUi();
        SyncPrinterTypeComboFromSetup();
        UpdateStepUi();
    }
}
