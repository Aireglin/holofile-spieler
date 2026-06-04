using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using UfoEncounter.Audio;
using UfoEncounter.Encounters;
using UfoEncounter.Sim;
using UfoEncounter.Util;

namespace UfoEncounter;

public partial class MainWindow : Window
{
    private SimConnectClient? _sim;
    private ElectricalDisruptor? _disruptor;
    private AudioEngine? _audio;
    private EncounterDirector? _director;
    private Logbook? _logbook;

    private CancellationTokenSource? _effects; // for single-effect test buttons
    private DispatcherTimer? _reconnect;

    public MainWindow() => InitializeComponent();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        _sim = new SimConnectClient(hwnd);
        _audio = new AudioEngine();
        _logbook = new Logbook();
        _disruptor = new ElectricalDisruptor(_sim, Trace);
        _director = new EncounterDirector(_sim, _disruptor, _audio, _logbook, Trace);

        _sim.Connected += () => { SimStatus.Text = " CONNECTED"; SimStatus.Foreground = (System.Windows.Media.Brush)FindResource("Accent"); };
        _sim.Disconnected += () => { SimStatus.Text = " DISCONNECTED"; SimStatus.Foreground = (System.Windows.Media.Brush)FindResource("Warn"); };
        _sim.StateUpdated += s => AglText.Text = $" {s.AltitudeAgl:F0} ft";
        _sim.Log += Trace;

        _director.EncounterStateChanged += (name, active) =>
            EncounterText.Text = active ? $" ▶ {name}" : " idle";

        BuildScenarioButtons();
        PushSettingsToDirector();

        // Try to attach to a running sim, and keep retrying quietly.
        _reconnect = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _reconnect.Tick += (_, _) => { if (_sim is { IsConnected: false }) _sim.TryConnect(); };
        _reconnect.Start();
        _sim.TryConnect();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == SimConnectClient.WM_USER_SIMCONNECT)
        {
            _sim?.HandleWindowMessage();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void BuildScenarioButtons()
    {
        foreach (var sc in EncounterScenario.Catalog)
        {
            var btn = new Button
            {
                Content = sc.Name,
                ToolTip = sc.Description,
                MinWidth = 160,
            };
            btn.Click += async (_, _) =>
            {
                if (_director != null) await _director.TriggerAsync(sc);
            };
            ScenarioPanel.Children.Add(btn);
        }
    }

    // ---- Status / logging ----

    private void Trace(string msg) => Dispatcher.Invoke(() =>
    {
        LogText.Text += $"[{DateTime.Now:HH:mm:ss}] {msg}\n";
        LogScroll.ScrollToEnd();
    });

    // ---- Connection ----

    private void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_sim == null) return;
        if (_sim.IsConnected) _sim.Disconnect();
        else if (!_sim.TryConnect()) Trace("Sim not running? Will keep retrying.");
    }

    // ---- Single-effect tests ----

    private async void TestBlackout_Click(object sender, RoutedEventArgs e) =>
        await RunEffect(new DisruptionPlan { Kind = DisruptionKind.Blackout, Duration = TimeSpan.FromSeconds(6) });

    private async void TestStutter_Click(object sender, RoutedEventArgs e) =>
        await RunEffect(new DisruptionPlan
        {
            Kind = DisruptionKind.Stutter,
            Duration = TimeSpan.FromSeconds(6),
            StutterStep = TimeSpan.FromMilliseconds(450 - 250 * IntensitySlider.Value),
        });

    private async Task RunEffect(DisruptionPlan plan)
    {
        if (_disruptor == null || _sim is not { IsConnected: true }) { Trace("Not connected."); return; }
        _effects = new CancellationTokenSource();
        try { await _disruptor.RunAsync(plan, _effects.Token); }
        catch (OperationCanceledException) { }
    }

    private async void TestHum_Click(object sender, RoutedEventArgs e)
    {
        if (_audio == null) return;
        _effects = new CancellationTokenSource();
        _audio.StartHum(IntensitySlider.Value);
        Trace("Hum test (6s).");
        try { await Task.Delay(TimeSpan.FromSeconds(6), _effects.Token); }
        catch (OperationCanceledException) { }
        finally { _audio.Stop(); }
    }

    private void Panic_Click(object sender, RoutedEventArgs e)
    {
        _effects?.Cancel();
        _director?.PanicStop();
        if (RandomChk.IsChecked == true) RandomChk.IsChecked = false;
    }

    // ---- Settings ----

    private void PushSettingsToDirector()
    {
        if (_director == null) return;
        _director.FrequencyPerMin = FreqSlider.Value;
        _director.MinDurationSec = MinDurSlider.Value;
        _director.MaxDurationSec = MaxDurSlider.Value;
        _director.Intensity = IntensitySlider.Value;
        _director.MinAglFeet = AglSlider.Value;
        _director.RealismLock = RealismChk.IsChecked == true;
    }

    private void RandomChk_Changed(object sender, RoutedEventArgs e) =>
        _director?.SetRandomMode(RandomChk.IsChecked == true);

    private void Realism_Changed(object sender, RoutedEventArgs e)
    {
        if (_director != null) _director.RealismLock = RealismChk.IsChecked == true;
    }

    private void FreqSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FreqVal != null) FreqVal.Text = FreqSlider.Value.ToString("0.0", CultureInfo.InvariantCulture);
        if (_director != null) _director.FrequencyPerMin = FreqSlider.Value;
    }

    private void DurSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MinDurVal != null) MinDurVal.Text = MinDurSlider.Value.ToString("0", CultureInfo.InvariantCulture);
        if (MaxDurVal != null) MaxDurVal.Text = MaxDurSlider.Value.ToString("0", CultureInfo.InvariantCulture);
        if (_director == null) return;
        _director.MinDurationSec = MinDurSlider.Value;
        _director.MaxDurationSec = MaxDurSlider.Value;
    }

    private void IntensitySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IntensityVal != null) IntensityVal.Text = IntensitySlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        if (_director != null) _director.Intensity = IntensitySlider.Value;
    }

    private void AglSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (AglVal != null) AglVal.Text = AglSlider.Value.ToString("0", CultureInfo.InvariantCulture);
        if (_director != null) _director.MinAglFeet = AglSlider.Value;
    }

    private void ApplySeed_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(SeedBox.Text, out var seed)) _director?.SetSeed(seed);
        else Trace("Seed must be an integer.");
    }

    private void RandomSeed_Click(object sender, RoutedEventArgs e)
    {
        SeedBox.Text = "";
        _director?.SetSeed(null);
    }

    private void ExportLog_Click(object sender, RoutedEventArgs e)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "sightings.csv");
        Trace(File.Exists(path) ? $"Sightings: {path}" : "No sightings recorded yet.");
    }

    protected override void OnClosed(EventArgs e)
    {
        // Failsafe on exit: stop everything and restore power.
        _director?.PanicStop();
        _audio?.Dispose();
        _sim?.Dispose();
        base.OnClosed(e);
    }
}
