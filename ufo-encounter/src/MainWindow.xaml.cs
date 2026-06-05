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
        await RunEffect(new DisruptionPlan
        {
            Kind = DisruptionKind.DeepBlackout,
            Duration = TimeSpan.FromSeconds(8),
            CutEngine = _director?.DeepBlackoutCutsEngine ?? true,
        });

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
        _director.Dread = DreadSlider.Value;
        _director.MultiPhase = MultiPhaseChk.IsChecked == true;
        _director.DayNightBias = DayNightChk.IsChecked == true;
        _director.WhooshEnabled = WhooshChk.IsChecked == true;
        _director.WhooshVolume = WhooshVolSlider.Value;
        _director.DisruptionChance = DisruptChanceSlider.Value;
        _director.DeepBlackoutCutsEngine = EngineCutChk.IsChecked == true;
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

    private void MultiPhase_Changed(object sender, RoutedEventArgs e)
    {
        if (_director != null) _director.MultiPhase = MultiPhaseChk.IsChecked == true;
    }

    private void DayNight_Changed(object sender, RoutedEventArgs e)
    {
        if (_director != null) _director.DayNightBias = DayNightChk.IsChecked == true;
    }

    private void Whoosh_Changed(object sender, RoutedEventArgs e)
    {
        if (_director != null) _director.WhooshEnabled = WhooshChk.IsChecked == true;
    }

    private void WhooshVol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (WhooshVolVal != null) WhooshVolVal.Text = WhooshVolSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        if (_director != null) _director.WhooshVolume = WhooshVolSlider.Value;
    }

    private void EngineCut_Changed(object sender, RoutedEventArgs e)
    {
        if (_director != null) _director.DeepBlackoutCutsEngine = EngineCutChk.IsChecked == true;
    }

    private void DisruptChance_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DisruptChanceVal != null) DisruptChanceVal.Text = DisruptChanceSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        if (_director != null) _director.DisruptionChance = DisruptChanceSlider.Value;
    }

    private void DreadSlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DreadVal != null) DreadVal.Text = DreadSlider.Value.ToString("0.00", CultureInfo.InvariantCulture);
        if (_director != null) _director.Dread = DreadSlider.Value;
    }

    private async void TestFullEncounter_Click(object sender, RoutedEventArgs e)
    {
        if (_director == null) return;
        // Force a multi-phase run for the test regardless of the toggle.
        bool prev = _director.MultiPhase;
        _director.MultiPhase = true;
        try { await _director.TriggerAsync(EncounterScenario.ByName("Verfolger (Wingman)")); }
        finally { _director.MultiPhase = prev; }
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
