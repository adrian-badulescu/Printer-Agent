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
    private static readonly Regex EnrollmentCodeRegex = new("^[A-Za-z0-9]{6,32}$", RegexOptions.Compiled);
    private int _step;
    private readonly AgentConfigurationStore _store = new();
    private readonly Port9100Scanner _scanner = new();
    private readonly TestPrintService _testPrint = new();
    private CancellationTokenSource? _scanCts;
    private bool _printerIdManual;
    private bool _printerIdProgrammaticChange;
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
        NicLabel.Content = UiStrings.Get("Nic_Label");
        ScanConsentText.Text = UiStrings.Get("ScanConsent_Text");
        FoundHostsLabel.Content = UiStrings.Get("FoundHosts_Label");

        Step3TitleText.Text = UiStrings.Get("Step3_Title");
        PrinterNameLabel.Content = UiStrings.Get("PrinterName_Label");
        PrinterIdLabel.Content = UiStrings.Get("PrinterId_Label");
        PortLabel.Content = UiStrings.Get("Port_Label");
        SaveHintText.Text = UiStrings.Get("SaveHintText");

        Step4TitleText.Text = UiStrings.Get("Step4_Title");
        DoneHintText.Text = UiStrings.Get("DoneHintText");
    }

    private void UpdateStepUi()
    {
        StepEnrollmentPanel.Visibility = _step == 0 ? Visibility.Visible : Visibility.Collapsed;
        StepNetworkPanel.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        StepPrinterPanel.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        StepDonePanel.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;

        BackButton.IsEnabled = _step is > 0 and < 3;
        NextButton.IsEnabled = true;
        NextButton.Content = _step == 2 ? UiStrings.Get("SaveButton") : _step == 3 ? UiStrings.Get("CloseButton") : UiStrings.Get("NextButton");
        NextButton.IsDefault = _step != 3;

        FooterStatusText.Text = _step switch
        {
            0 => UiStrings.Get("Footer_Step1"),
            1 => UiStrings.Get("Footer_Step2"),
            2 => UiStrings.Get("Footer_Step3"),
            3 => UiStrings.Get("Footer_Step4"),
            _ => ""
        };

        if (_step == 2)
        {
            if (string.IsNullOrWhiteSpace(IpAddressBox.Text) && _selectedHost != null)
                IpAddressBox.Text = _selectedHost.ToString();

            SelectedHostText.Text = !string.IsNullOrWhiteSpace(IpAddressBox.Text)
                ? $"Adresă: {IpAddressBox.Text.Trim()} (port {PortBox.Text.Trim()})"
                : _selectedHost != null
                    ? $"Adresă selectată: {_selectedHost} (port {PortBox.Text.Trim()})"
                    : "Introduceți adresa IP (ex. 127.0.0.1 pentru FiscalNet).";
            RefreshPrinterIdFromNameIfNeeded();
            ApplyPrinterTypeUi();
        }
    }

    private void PrinterTypeCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ApplyPrinterTypeUi();

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
        var fiscal = IsFiscalNetSelected();
        FiscalSettingsPanel.Visibility = fiscal ? Visibility.Visible : Visibility.Collapsed;
        PortLabel.Content = fiscal ? "Port HTTP" : "Port TCP";

        if (fiscal)
        {
            if (string.IsNullOrWhiteSpace(IpAddressBox.Text))
                IpAddressBox.Text = "127.0.0.1";
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
            if (NicCombo.SelectedItem is not NicSubnetOption)
            {
                MessageBox.Show(this, "Selectați o interfață de rețea.", "Validare", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ScanConsentCheckBox.IsChecked != true)
            {
                MessageBox.Show(
                    this,
                    "Confirmați că doriți scanarea activă a subnetului bifând caseta de mai sus.",
                    "Consimțământ",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (FoundHostsList.SelectedItem is not IPAddress ip)
            {
                MessageBox.Show(
                    this,
                    "Rulați scanul și selectați o adresă din listă.",
                    "Validare",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _selectedHost = ip;
            _step++;
            _printerIdManual = false;
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
            MessageBox.Show(this, "Introduceți numele afișat al imprimantei.", "Validare", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(pid) || !Regex.IsMatch(pid, "^[A-Za-z0-9._-]+$"))
        {
            MessageBox.Show(
                this,
                "PrinterId trebuie să fie nevid și să conțină doar litere, cifre, punct, cratimă sau underscore.",
                "Validare",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        if (_selectedHost == null && !IsFiscalNetSelected())
        {
            MessageBox.Show(this, "Lipsește adresa imprimantei.", "Validare", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ipText = IpAddressBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(ipText))
            ipText = _selectedHost?.ToString() ?? string.Empty;

        if (!IPAddress.TryParse(ipText, out var ipAddress))
        {
            MessageBox.Show(this, "Adresă IP invalidă.", "Validare", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Port TCP invalid (1–65535).", "Validare", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        NextButton.IsEnabled = false;
        try
        {
            var root = _store.LoadOrCreateTemplate();
            root["EnrollmentCode"] = EnrollmentCodeBox.Text.Trim();

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

            var printerType = PrinterTypes.EscPos;
            if (PrinterTypeCombo.SelectedItem is ComboBoxItem typeItem && typeItem.Tag is string typeTag)
                printerType = typeTag;

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
            DoneMessageText.Text =
                $"Configurația a fost salvată în:{Environment.NewLine}{_store.AgentJsonPath}{Environment.NewLine}{Environment.NewLine}" +
                "Reporniți serviciul URSPrinterAgent dacă rulează, ca să reîncarce setările.";
            UpdateStepUi();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Salvare eșuată: {ex.Message}", "Eroare", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            NextButton.IsEnabled = true;
        }
    }

    private void NicCombo_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NicCombo.SelectedItem is NicSubnetOption opt)
        {
            SubnetSummaryText.Text =
                $"Subnet scanat: {opt.CidrDisplay}{Environment.NewLine}" +
                $"Adresa locală: {opt.IPv4} / {opt.PrefixLength}{(opt.HasDefaultGateway ? " (are gateway implicit)" : "")}";
        }
        else
            SubnetSummaryText.Text = "";
    }

    private async void ScanButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (NicCombo.SelectedItem is not NicSubnetOption opt)
        {
            MessageBox.Show(this, "Selectați o interfață de rețea.", "Validare", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (ScanConsentCheckBox.IsChecked != true)
        {
            MessageBox.Show(
                this,
                "Bifați consimțământul pentru scan înainte de a continua.",
                "Consimțământ",
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
                ? "Nicio adresă cu 9100 deschis."
                : $"Găsite: {found.Count}.";
        }
        catch (OperationCanceledException)
        {
            ScanProgressText.Text = "Scan anulat.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Scan eșuat: {ex.Message}", "Eroare", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (_selectedHost == null)
        {
            MessageBox.Show(this, "Lipsește adresa imprimantei.", "Test print", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        {
            MessageBox.Show(this, "Port TCP invalid.", "Test print", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var printer = new Printer
        {
            IpAddress = _selectedHost.ToString(),
            Port = port,
            Name = string.IsNullOrWhiteSpace(PrinterNameBox.Text) ? "Test" : PrinterNameBox.Text.Trim()
        };

        TestPrintButton.IsEnabled = false;
        try
        {
            var ok = await _testPrint.SendTestPageAsync(printer).ConfigureAwait(true);
            MessageBox.Show(
                this,
                ok ? "Pagină de test trimisă (verificați imprimanta)." : "Nu s-a putut conecta sau trimite datele.",
                "Test print",
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
            MessageBox.Show(this, ex.Message, "Explorer", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        _printerIdManual = false;
        _step = 1;
        UpdateStepUi();
    }
}
