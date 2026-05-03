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

    public MainWindow()
    {
        InitializeComponent();
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

        UpdateStepUi();
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
            SelectedHostText.Text = _selectedHost != null
                ? $"Adresă selectată: {_selectedHost} (port {PortBox.Text.Trim()})"
                : "Nicio adresă selectată.";
            RefreshPrinterIdFromNameIfNeeded();
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

        if (_selectedHost == null)
        {
            MessageBox.Show(this, "Lipsește adresa imprimantei.", "Validare", MessageBoxButton.OK, MessageBoxImage.Warning);
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

            var entry = new JsonObject
            {
                ["id"] = pid,
                ["name"] = name,
                ["ipAddress"] = _selectedHost.ToString(),
                ["port"] = port
            };
            MergePreservedPrinterMetadata(replaced, entry);
            printers.Add(entry);

            await Task.Run(() => _store.Save(root)).ConfigureAwait(true);

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
