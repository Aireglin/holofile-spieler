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
    private LightChoreographer? _lights;
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
        _lights = new LightChoreographer(_sim, Trace);
        _director = new EncounterDirector(_sim, _disruptor, _lights, _audio, _logbook, Trace);

        _sim.Connected += () => { SimStatus.Text = " CONNECTED"; SimStatus.Foreground = (System.Windows.Media.Brush)FindResource("Accent"); };
        _sim.Disconnected += () => { SimStatus.Text = " DISCONNECTED"; SimStatus.Foreground = (System.Windows.Media.Brush)FindResource("Warn"); };
        _sim.StateUpdated += s => AglText.Text = $" {s.AltitudeAgl:F0} ft";
        _sim.Log += Trace;
        _sim.TitleReceived += title =>
        {
            var target = _titleTarget ?? LightTitleBox;
            if (!string.IsNullOrEmpty(title)) target.Text = title;
            _titleTarget = null;
        };

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
        _director.BlackoutMinSec = BlackMinSlider.Value;
        _director.BlackoutMaxSec = BlackMaxSlider.Value;
        _director.LightsEnabled = LightsChk.IsChecked == true;
        _director.LightObjectTitle = LightTitleBox.Text;
        _director.MothershipTitle = MothershipTitleBox.Text;
        _director.LightCount = (int)LightCountSlider.Value;
    }

    // Which field a pending "use current aircraft" request should fill.
    private TextBox? _titleTarget;

    private void UseCurrentForLight_Click(object sender, RoutedEventArgs e) => RequestTitleInto(LightTitleBox);
    private void UseCurrentForMothership_Click(object sender, RoutedEventArgs e) => RequestTitleInto(MothershipTitleBox);

    private void RequestTitleInto(TextBox target)
    {
        if (_sim is not { IsConnected: true }) { Trace("Not connected — can't read aircraft title."); return; }
        _titleTarget = target;
        _sim.RequestAircraftTitle();
    }

    private void MothershipTitle_Changed(object sender, TextChangedEventArgs e)
    {
        if (_director != null) _director.MothershipTitle = MothershipTitleBox.Text;
    }

    private void TestMothership_Click(object sender, RoutedEventArgs e) =>
        _director?.TestLights(LightPattern.Mothership, 20, mothership: true);

    private void BlackSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BlackMinVal != null) BlackMinVal.Text = BlackMinSlider.Value.ToString("0", CultureInfo.InvariantCulture);
        if (BlackMaxVal != null) BlackMaxVal.Text = BlackMaxSlider.Value.ToString("0", CultureInfo.InvariantCulture);
        if (_director == null) return;
        _director.BlackoutMinSec = BlackMinSlider.Value;
        _director.BlackoutMaxSec = BlackMaxSlider.Value;
    }

    private void Lights_Changed(object sender, RoutedEventArgs e)
    {
        if (_director != null) _director.LightsEnabled = LightsChk.IsChecked == true;
    }

    private void LightTitle_Changed(object sender, TextChangedEventArgs e)
    {
        if (_director != null) _director.LightObjectTitle = LightTitleBox.Text;
    }

    private void LightCount_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LightCountVal != null) LightCountVal.Text = ((int)LightCountSlider.Value).ToString();
        if (_director != null) _director.LightCount = (int)LightCountSlider.Value;
    }

    private void TestLightTicTac_Click(object sender, RoutedEventArgs e) => _director?.TestLights(LightPattern.TicTac, 12);
    private void TestLightWingman_Click(object sender, RoutedEventArgs e) => _director?.TestLights(LightPattern.Wingman, 12);
    private void TestLightPopup_Click(object sender, RoutedEventArgs e) => _director?.TestLights(LightPattern.PopUp, 12);

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
