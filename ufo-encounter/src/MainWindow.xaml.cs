using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using UfoEncounter.Audio;
using UfoEncounter.Encounters;
using UfoEncounter.Sim;
using UfoEncounter.Util;

namespace UfoEncounter;

/// <summary>One log entry. A class (reference identity) rather than a string, so
/// ScrollIntoView finds the newest line even when the same text repeats.</summary>
public sealed class LogLine
{
    public LogLine(string text) => Text = text;
    public string Text { get; }
    public override string ToString() => Text;
}

public partial class MainWindow : Window
{
    private const int MaxLogLines = 400;

    private SimConnectClient? _sim;
    private ElectricalDisruptor? _disruptor;
    private LightChoreographer? _lights;
    private AudioEngine? _audio;
    private EncounterDirector? _director;
    private Logbook? _logbook;
    private GlobalHotkeys? _hotkeys;

    private CancellationTokenSource? _effects;  // single-effect test buttons
    private CancellationTokenSource? _humCts;   // hum test
    private DispatcherTimer? _reconnect;
    private DispatcherTimer? _uiTimer;           // countdown + autosave
    private readonly ObservableCollection<LogLine> _logLines = new();
    private readonly Stopwatch _uiClock = Stopwatch.StartNew();
    private long _lastStatusMs = -1000;
    private bool _todLogged;
    private int _autosaveTicks;
    private bool _uiReady;
    private Rect _lastNormalBounds = Rect.Empty;   // RestoreBounds is Rect.Empty once the window is closed
    private string _communityFolder = "";          // manual pick, used only when auto-detection fails

    // Which field a pending "Übernehmen" (current aircraft title) should fill.
    private TextBox? _titleTarget;

    public MainWindow()
    {
        InitializeComponent();
        _uiReady = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        LogList.ItemsSource = _logLines;

        _sim = new SimConnectClient(hwnd);
        _audio = new AudioEngine();
        _logbook = new Logbook();
        _disruptor = new ElectricalDisruptor(_sim, Trace);
        _lights = new LightChoreographer(_sim, Trace);
        _director = new EncounterDirector(_sim, _disruptor, _lights, _audio, _logbook, Trace);

        _sim.Connected += OnSimConnected;
        _sim.Disconnected += OnSimDisconnected;
        _sim.StateUpdated += OnPlaneState;
        _sim.Log += Trace;
        _sim.TitleReceived += title =>
        {
            var target = _titleTarget;
            _titleTarget = null;
            if (target != null && !string.IsNullOrEmpty(title)) target.Text = title;
        };

        _director.StatusChanged += s => EncounterText.Text = s.Length == 0 ? "Bereit." : "▶ " + s;
        _director.RandomModeChanged += on => { if (RandomChk.IsChecked != on) RandomChk.IsChecked = on; };

        BuildScenarioButtons();
        BuildPatternCombo();
        LoadSettings();          // apply persisted settings to the controls...
        UpdateSliderLabels();
        PushSettingsToDirector();// ...and sync them to the director
        DataPathText.Text = $"Speicherort: {AppPaths.DataDir}";
        UpdatePackageInfo();

        _hotkeys = new GlobalHotkeys(hwnd);
        RegisterHotkey(ModifierKeys.Control | ModifierKeys.Alt, Key.P, "Strg+Alt+P", Panic);
        RegisterHotkey(ModifierKeys.Control | ModifierKeys.Alt, Key.U, "Strg+Alt+U", () => _ = EncounterNowAsync());
        RegisterHotkey(ModifierKeys.Control | ModifierKeys.Alt, Key.Z, "Strg+Alt+Z",
            () => RandomChk.IsChecked = RandomChk.IsChecked != true);

        // Attach to a running sim and keep retrying quietly (MSFS can take minutes to load).
        _reconnect = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _reconnect.Tick += (_, _) => { if (_sim is { IsConnected: false }) _sim.TryConnect(quiet: true); };
        _reconnect.Start();
        _sim.TryConnect(quiet: true);

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += OnUiTick;
        _uiTimer.Start();

        Trace("UFO Encounter bereit. Tastenkürzel: Strg+Alt+P = PANIK-STOP · Strg+Alt+U = Begegnung jetzt · Strg+Alt+Z = Zufallsmodus.");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == SimConnectClient.WM_USER_SIMCONNECT)
        {
            _sim?.HandleWindowMessage();
            handled = true;
        }
        else if (_hotkeys != null && _hotkeys.Handle(msg, wParam))
        {
            handled = true;
        }
        return IntPtr.Zero;
    }

    private void RegisterHotkey(ModifierKeys mods, Key key, string name, Action action)
    {
        if (_hotkeys != null && !_hotkeys.Register(mods, key, action))
            Trace($"Tastenkürzel {name} ist von einem anderen Programm belegt – bitte den Button benutzen.");
    }

    private void BuildScenarioButtons()
    {
        foreach (var sc in EncounterScenario.Catalog)
        {
            var btn = new Button { Content = sc.Name, ToolTip = sc.Description, MinWidth = 170 };
            btn.Click += async (_, _) =>
            {
                if (_director != null) await _director.TriggerAsync(sc, manual: true);
            };
            ScenarioPanel.Children.Add(btn);
        }
    }

    private static readonly LightPattern[] TestablePatterns =
    {
        LightPattern.PopUp, LightPattern.TicTac, LightPattern.Wingman, LightPattern.PaceAhead,
        LightPattern.Abeam, LightPattern.Above, LightPattern.Below, LightPattern.Formation,
        LightPattern.Inspect, LightPattern.Orbit, LightPattern.Approach, LightPattern.BrakeHover,
        LightPattern.FlyBy,
    };

    private void BuildPatternCombo()
    {
        foreach (var p in TestablePatterns)
            PatternCombo.Items.Add(new ComboBoxItem { Content = PatternNames.German(p), Tag = p });
        PatternCombo.SelectedIndex = 0;
    }

    // ---- Status / logging ----

    private void Trace(string msg)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Trace(msg)); return; }
        _logLines.Add(new LogLine($"[{DateTime.Now:HH:mm:ss}] {msg}"));
        while (_logLines.Count > MaxLogLines) _logLines.RemoveAt(0);
        LogList.ScrollIntoView(_logLines[^1]);
    }

    private static string TodName(double code) => (int)Math.Round(code) switch
    {
        0 => "Morgendämmerung",
        1 => "Tag",
        2 => "Abenddämmerung",
        3 => "Nacht",
        _ => "?",
    };

    private void OnPlaneState(PlaneState s)
    {
        // State arrives every sim frame; the labels only need a few updates per second.
        long now = _uiClock.ElapsedMilliseconds;
        if (now - _lastStatusMs < 250) return;
        _lastStatusMs = now;
        AglText.Text = $"{s.AltitudeAgl:F0} ft";
        TodText.Text = TodName(s.TimeOfDay);
        if (!_todLogged)
        {
            _todLogged = true;
            Trace($"Tageszeit erkannt: {TodName(s.TimeOfDay)} (Code {s.TimeOfDay:0}).");
        }
    }

    private void OnSimConnected()
    {
        SimStatus.Text = "verbunden";
        SimStatus.Foreground = (Brush)FindResource("Accent");
        ConnectBtn.Content = "Verbunden";
        ConnectBtn.IsEnabled = false;
        _todLogged = false;
    }

    private void OnSimDisconnected()
    {
        SimStatus.Text = "getrennt – warte auf MSFS …";
        SimStatus.Foreground = (Brush)FindResource("Warn");
        ConnectBtn.Content = "Verbinden";
        ConnectBtn.IsEnabled = true;
        AglText.Text = "—";
        TodText.Text = "—";
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        if (_director is { IsRandomMode: true, NextRandomEncounterUtc: DateTime next })
        {
            double s = (next - DateTime.UtcNow).TotalSeconds;
            NextText.Text = _director.IsBusy
                ? "Zufallsmodus pausiert, solange eine Begegnung/ein Test läuft"
                : s > 1 ? $"nächste Zufalls-Begegnung in ca. {s:0} s" : "nächste Zufalls-Begegnung gleich";
        }
        else NextText.Text = "";

        if (++_autosaveTicks >= 20) { _autosaveTicks = 0; SaveSettings(); }
    }

    // ---- Connection ----

    private void ConnectBtn_Click(object sender, RoutedEventArgs e)
    {
        // Connect only — a manual disconnect mid-blackout would strand the restore.
        if (_sim is { IsConnected: false }) _sim.TryConnect();
    }

    // ---- Encounters ----

    private async Task EncounterNowAsync()
    {
        if (_director != null) await _director.RunRandomEncounterAsync();
    }

    private async void EncounterNow_Click(object sender, RoutedEventArgs e) => await EncounterNowAsync();

    private void RandomChk_Changed(object sender, RoutedEventArgs e) =>
        _director?.SetRandomMode(RandomChk.IsChecked == true);

    private async void TestHolyGrail_Click(object sender, RoutedEventArgs e)
    {
        if (_director != null) await _director.RunHolyGrailAsync();
    }

    private async void TestJetChase_Click(object sender, RoutedEventArgs e)
    {
        if (_director != null) await _director.RunJetChaseAsync();
    }

    private async void TestFullEncounter_Click(object sender, RoutedEventArgs e)
    {
        if (_director != null)
            await _director.TriggerAsync(EncounterScenario.ByName("Verfolger (Wingman)"), manual: true, forceMultiPhase: true);
    }

    private void Panic_Click(object sender, RoutedEventArgs e) => Panic();

    private void Panic()
    {
        _effects?.Cancel();
        _humCts?.Cancel();
        _director?.PanicStop();
    }

    // ---- Objects & lights ----

    private void UseCurrentForLight_Click(object sender, RoutedEventArgs e) => RequestTitleInto(LightTitleBox);
    private void UseCurrentForMothership_Click(object sender, RoutedEventArgs e) => RequestTitleInto(MothershipTitleBox);
    private void UseCurrentForBeam_Click(object sender, RoutedEventArgs e) => RequestTitleInto(BeamTitleBox);
    private void UseCurrentForJet_Click(object sender, RoutedEventArgs e) => RequestTitleInto(JetTitleBox);

    private void RequestTitleInto(TextBox target)
    {
        if (_sim is not { IsConnected: true }) { Trace("Keine Verbindung – der Titel kann nicht gelesen werden."); return; }
        _titleTarget = target;
        _sim.RequestAircraftTitle();
    }

    private static double TestDuration(LightPattern p) => p switch
    {
        LightPattern.Inspect or LightPattern.Orbit => 24,
        LightPattern.Approach or LightPattern.FlyBy => 12,
        LightPattern.BrakeHover => 10,
        _ => 14,
    };

    private void TestPattern_Click(object sender, RoutedEventArgs e)
    {
        if (PatternCombo.SelectedItem is ComboBoxItem { Tag: LightPattern p })
            _director?.TestLights(p, TestDuration(p));
    }

    private void TestMothership_Click(object sender, RoutedEventArgs e) =>
        _director?.TestLights(LightPattern.Mothership, 20, mothership: true);

    private async void RunDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (_director == null) return;
        UpdatePackageInfo();   // pick up a package generated meanwhile
        await _director.RunDiagnosticsAsync();
    }

    private string? ResolveCommunityFolder() =>
        LightPackageGenerator.DetectCommunityFolder()
        ?? (_communityFolder.Length > 0 && Directory.Exists(_communityFolder) ? _communityFolder : null);

    // A title that only packages with the new light styles (Glimmen/Funkeln, Kern, Funke) contain.
    private const string NewStylesMarker = LightPackageGenerator.TitlePrefix + "Licht_Weiss_Glimmen";

    /// <summary>Show the package state and hand its titles to the director (the mix
    /// only uses package titles that are installed).</summary>
    private void UpdatePackageInfo()
    {
        string? community = ResolveCommunityFolder();
        var installed = community == null ? null : LightPackageGenerator.ReadInstalledTitles(community);
        if (_director != null) _director.InstalledTitles = installed;
        if (community == null)
        {
            PackageInfo.Text = "Community-Ordner nicht automatisch gefunden – beim Erzeugen wirst du danach gefragt.";
            return;
        }
        string pkg = Path.Combine(community, LightPackageGenerator.PackageFolderName);
        if (!Directory.Exists(pkg)) PackageInfo.Text = $"Noch nicht erzeugt. Ziel: {community}";
        else if (installed == null) PackageInfo.Text = $"Installiert: {pkg} – nicht lesbar, bitte neu erzeugen";
        else if (installed.Contains(NewStylesMarker)) PackageInfo.Text = $"Installiert: {pkg} – neue Lichtstile vorhanden";
        else PackageInfo.Text = $"Installiert: {pkg} – bitte neu erzeugen (ältere Version)";
    }

    private void GeneratePackage_Click(object sender, RoutedEventArgs e)
    {
        string? community = ResolveCommunityFolder();
        bool picked = false;
        if (community == null)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Community-Ordner von MSFS 2024 auswählen" };
            if (dlg.ShowDialog(this) != true) return;
            community = dlg.FolderName;
            picked = true;
        }
        try
        {
            var r = LightPackageGenerator.Generate(community);
            // Remember the picked folder, so the next update does not ask again.
            if (picked)
            {
                _communityFolder = community;
                SaveSettings();
            }
            UpdatePackageInfo();
            Trace($"Licht-Paket erzeugt: {r.PackagePath} ({r.LightTitles.Count} Lichter, {r.OrbTitles.Count} Kugeln, "
                  + $"{r.CoreTitles.Count} Kerne, {r.SparkTitles.Count} Funken).");
            var answer = MessageBox.Show(this,
                "Das Licht-Paket wurde erzeugt:\n" + r.PackagePath +
                "\n\nWICHTIG: MSFS jetzt neu starten – Pakete werden nur beim Start geladen.\n\n" +
                "Soll der Titel für „Lichter / kleine UFOs“ auf die neuen Lichtobjekte gesetzt werden?\n" +
                "(ein Titel pro Farbe – das Programm mischt ruhige, glimmende, funkelnde und blinkende Lichter, " +
                "Kerne und Funken automatisch; „Lichtstile automatisch mischen“ wird eingeschaltet)",
                "Licht-Paket erzeugt", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
            {
                // One steady title per colour: the colours are the user's choice, the styles come from the mix.
                LightTitleBox.Text = string.Join(", ", LightPackageGenerator.TitlesOf(LightPackageGenerator.LightKind.Steady));
                AutoMixChk.IsChecked = true;
            }
            Trace("Test nach dem MSFS-Neustart: Tab „Objekte & Lichter“ → Muster testen (nachts sieht man es am besten). "
                  + "Bitte auch einmal „▶ Diagnose (Test-Objekte)“ starten und melden, was du siehst. "
                  + $"Erscheint gar nichts: „Lichtstile automatisch mischen“ ausschalten und einzeln „{r.OrbTitles[0]}“ (leuchtende Kugel) eintragen.");
        }
        catch (Exception ex)
        {
            string msg = ex.Message;
            if (ex is IOException or UnauthorizedAccessException)
                msg += "\n\nLäuft MSFS noch oder ist der Ordner im Explorer geöffnet? Dann beides schließen und erneut „Licht-Paket erzeugen“ klicken.";
            Trace($"Licht-Paket konnte nicht erzeugt werden: {ex.Message}");
            MessageBox.Show(this, msg, "Licht-Paket", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---- Electrical & sound tests ----

    private async void TestBlackout_Click(object sender, RoutedEventArgs e) => await RunEffect(DisruptionKind.DeepBlackout);
    private async void TestStutter_Click(object sender, RoutedEventArgs e) => await RunEffect(DisruptionKind.Stutter);
    private async void TestStutterToBlackout_Click(object sender, RoutedEventArgs e) => await RunEffect(DisruptionKind.StutterToBlackout);

    private async Task RunEffect(DisruptionKind kind)
    {
        if (_disruptor == null || _director == null || _sim is not { IsConnected: true }) { Trace("Keine Verbindung zum Sim."); return; }
        if (_director.IsEncounterActive) { Trace("Während einer Begegnung nicht möglich."); return; }
        if (_disruptor.IsActive) { Trace("Es läuft bereits ein Ausfall."); return; }
        _effects?.Dispose();
        var cts = _effects = new CancellationTokenSource();
        try { await _disruptor.RunAsync(_director.TestPlan(kind), cts.Token); }
        catch (OperationCanceledException) { }
    }

    private async void TestHum_Click(object sender, RoutedEventArgs e)
    {
        if (_audio == null || _director == null) return;
        if (_director.IsEncounterActive) { Trace("Während einer Begegnung nicht möglich."); return; }
        _humCts?.Cancel();
        var cts = _humCts = new CancellationTokenSource();
        _audio.StartHum(IntensitySlider.Value);
        Trace("Summen-Test (6 s).");
        try { await Task.Delay(TimeSpan.FromSeconds(6), cts.Token); }
        catch (OperationCanceledException) { }
        finally
        {
            if (_humCts == cts && !_director.IsEncounterActive) _audio.Stop();
        }
    }

    // ---- Settings ----

    private void Slider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_uiReady) return;
        UpdateSliderLabels();
        PushSettingsToDirector();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_uiReady) PushSettingsToDirector();
    }

    private void Setting_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_uiReady) PushSettingsToDirector();
    }

    private void ElecMode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_uiReady) PushSettingsToDirector();
    }

    private static string Pct(double v) => $"{v * 100:0} %";

    private void UpdateSliderLabels()
    {
        FreqVal.Text = $"{FreqSlider.Value:0.0}";
        LightCountVal.Text = $"{(int)LightCountSlider.Value}";
        MinDistVal.Text = MinDistSlider.Value < 1 ? "aus" : $"{MinDistSlider.Value:0} m";
        SpeedVal.Text = $"{SpeedSlider.Value:0.00}×";
        BeamCountVal.Text = $"{(int)BeamCountSlider.Value}";
        BeamHeightVal.Text = $"{BeamHeightSlider.Value:0} m";
        DisruptChanceVal.Text = Pct(DisruptChanceSlider.Value);
        BlackMinVal.Text = $"{BlackMinSlider.Value:0} s";
        BlackMaxVal.Text = $"{BlackMaxSlider.Value:0} s";
        IntensityVal.Text = Pct(IntensitySlider.Value);
        WhooshVolVal.Text = Pct(WhooshVolSlider.Value);
        MinDurVal.Text = $"{MinDurSlider.Value:0} s";
        MaxDurVal.Text = $"{MaxDurSlider.Value:0} s";
        DreadVal.Text = Pct(DreadSlider.Value);
        AglVal.Text = $"{AglSlider.Value:0} ft";
        BlinkShareVal.Text = Pct(BlinkShareSlider.Value);
        SatChanceVal.Text = Pct(SatChanceSlider.Value);
    }

    private void PushSettingsToDirector()
    {
        Topmost = TopmostChk.IsChecked == true;
        if (_director == null || _lights == null) return;
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
        _director.Mode = (ElectricalMode)Math.Clamp(ElecModeCombo.SelectedIndex, 0, 2);
        _director.LightsEnabled = LightsChk.IsChecked == true;
        _director.LightObjectTitle = LightTitleBox.Text;
        _director.MothershipTitle = MothershipTitleBox.Text;
        _director.LightCount = (int)LightCountSlider.Value;
        _director.SpeedScale = SpeedSlider.Value;
        _director.MinDistanceMeters = MinDistSlider.Value;
        _director.BlinkShare = BlinkShareSlider.Value;
        _director.SatelliteChance = SatChanceSlider.Value;
        _director.AutoMix = AutoMixChk.IsChecked == true;
        _director.TestWithSatellites = TestSatChk.IsChecked == true;
        _director.ApplyLiveTestTuning();
        _director.BeamTitle = BeamTitleBox.Text;
        _director.BeamCount = (int)BeamCountSlider.Value;
        _director.BeamHeight = BeamHeightSlider.Value;
        _director.JetTitle = JetTitleBox.Text;
        _lights.ForceAircraftLights = AircraftLightsChk.IsChecked == true;
    }

    private void ApplyPreset(double freq, double minDur, double maxDur, double intensity, double dread,
        double disrupt, int count, double minDist, double speed, double blink)
    {
        FreqSlider.Value = freq;
        MinDurSlider.Value = minDur;
        MaxDurSlider.Value = maxDur;
        IntensitySlider.Value = intensity;
        DreadSlider.Value = dread;
        DisruptChanceSlider.Value = disrupt;
        LightCountSlider.Value = count;
        MinDistSlider.Value = minDist;
        SpeedSlider.Value = speed;
        BlinkShareSlider.Value = blink;
        MultiPhaseChk.IsChecked = true;
        Trace("Voreinstellung übernommen. Häufigkeit und Ausfallchance gelten auch für den Zufallsmodus; "
              + "Anzahl, Abstand, Tempo, Dread und Blink-Anteil gelten für Buttons und Tests (der Zufallsmodus würfelt sie je Begegnung neu). "
              + "Mehrphasen-Modus ist an.");
    }

    private void PresetGentle_Click(object sender, RoutedEventArgs e) => ApplyPreset(0.5, 6, 14, 0.45, 0.2, 0.3, 2, 400, 0.5, 0.15);
    private void PresetStandard_Click(object sender, RoutedEventArgs e) => ApplyPreset(1.0, 4, 12, 0.6, 0.4, 0.75, 2, 200, 0.6, 0.3);
    private void PresetMax_Click(object sender, RoutedEventArgs e) => ApplyPreset(2.0, 6, 20, 0.85, 0.85, 0.9, 4, 120, 0.75, 0.5);

    private void ApplySeed_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(SeedBox.Text, out var seed)) _director?.SetSeed(seed);
        else Trace("Der Seed muss eine ganze Zahl sein.");
    }

    private void RandomSeed_Click(object sender, RoutedEventArgs e)
    {
        SeedBox.Text = "";
        _director?.SetSeed(null);
    }

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.DataDir}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Trace($"Ordner konnte nicht geöffnet werden: {ex.Message}"); }
    }

    // ---- Settings persistence ----

    private void LoadSettings()
    {
        var s = SettingsStore.Load();
        _communityFolder = s.CommunityFolder ?? "";
        FreqSlider.Value = s.Frequency;
        MinDurSlider.Value = s.MinDuration;
        MaxDurSlider.Value = s.MaxDuration;
        IntensitySlider.Value = s.Intensity;
        AglSlider.Value = s.MinAgl;
        BlackMinSlider.Value = s.BlackoutMin;
        BlackMaxSlider.Value = s.BlackoutMax;
        DreadSlider.Value = s.Dread;
        WhooshVolSlider.Value = s.WhooshVolume;
        DisruptChanceSlider.Value = s.DisruptionChance;
        LightCountSlider.Value = s.LightCount;
        MinDistSlider.Value = s.MinDistance;
        SpeedSlider.Value = s.SpeedScale;
        BeamCountSlider.Value = s.BeamCount;
        BeamHeightSlider.Value = s.BeamHeight;
        BlinkShareSlider.Value = s.BlinkShare;
        SatChanceSlider.Value = s.SatelliteChance;

        MultiPhaseChk.IsChecked = s.MultiPhase;
        DayNightChk.IsChecked = s.DayNight;
        WhooshChk.IsChecked = s.Whoosh;
        RealismChk.IsChecked = s.Realism;
        LightsChk.IsChecked = s.LightsEnabled;
        AircraftLightsChk.IsChecked = s.AircraftLights;
        TopmostChk.IsChecked = s.Topmost;
        AutoMixChk.IsChecked = s.AutoMix;
        TestSatChk.IsChecked = s.TestWithSatellites;
        ElecModeCombo.SelectedIndex = s.ElecMode >= 0 ? Math.Min(s.ElecMode, 2) : (s.FullShutdown ? 2 : s.EngineCut ? 1 : 0);

        LightTitleBox.Text = s.LightTitle;
        MothershipTitleBox.Text = s.MothershipTitle;
        BeamTitleBox.Text = s.BeamTitle;
        JetTitleBox.Text = string.IsNullOrWhiteSpace(s.JetTitle) ? EncounterDirector.DefaultJetTitle : s.JetTitle;

        Tabs.SelectedIndex = Math.Clamp(s.SelectedTab, 0, Tabs.Items.Count - 1);
        RestoreWindowPlacement(s);
    }

    private void RestoreWindowPlacement(AppSettings s)
    {
        if (s.WindowWidth is double w && s.WindowHeight is double h && w >= MinWidth && h >= MinHeight)
        {
            Width = w;
            Height = h;
        }
        if (s.WindowLeft is double left && s.WindowTop is double top &&
            left >= SystemParameters.VirtualScreenLeft - 50 && top >= SystemParameters.VirtualScreenTop - 50 &&
            left + 200 <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth &&
            top + 100 <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
        {
            Left = left;
            Top = top;
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;   // never restore "minimised"
    }

    private void SaveSettings()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty) bounds = _lastNormalBounds;   // window already closed: reuse the last good value
        else _lastNormalBounds = bounds;
        SettingsStore.Save(new AppSettings
        {
            Frequency = FreqSlider.Value,
            MinDuration = MinDurSlider.Value,
            MaxDuration = MaxDurSlider.Value,
            Intensity = IntensitySlider.Value,
            MinAgl = AglSlider.Value,
            BlackoutMin = BlackMinSlider.Value,
            BlackoutMax = BlackMaxSlider.Value,
            Dread = DreadSlider.Value,
            WhooshVolume = WhooshVolSlider.Value,
            DisruptionChance = DisruptChanceSlider.Value,
            LightCount = (int)LightCountSlider.Value,
            MinDistance = MinDistSlider.Value,
            SpeedScale = SpeedSlider.Value,
            BeamCount = (int)BeamCountSlider.Value,
            BeamHeight = BeamHeightSlider.Value,
            BlinkShare = BlinkShareSlider.Value,
            SatelliteChance = SatChanceSlider.Value,
            AutoMix = AutoMixChk.IsChecked == true,
            TestWithSatellites = TestSatChk.IsChecked == true,
            MultiPhase = MultiPhaseChk.IsChecked == true,
            DayNight = DayNightChk.IsChecked == true,
            Whoosh = WhooshChk.IsChecked == true,
            Realism = RealismChk.IsChecked == true,
            LightsEnabled = LightsChk.IsChecked == true,
            AircraftLights = AircraftLightsChk.IsChecked == true,
            Topmost = TopmostChk.IsChecked == true,
            ElecMode = Math.Clamp(ElecModeCombo.SelectedIndex, 0, 2),
            LightTitle = LightTitleBox.Text,
            MothershipTitle = MothershipTitleBox.Text,
            BeamTitle = BeamTitleBox.Text,
            JetTitle = JetTitleBox.Text,
            CommunityFolder = _communityFolder,
            SelectedTab = Tabs.SelectedIndex,
            WindowLeft = bounds.IsEmpty ? null : bounds.Left,
            WindowTop = bounds.IsEmpty ? null : bounds.Top,
            WindowWidth = bounds.IsEmpty ? null : bounds.Width,
            WindowHeight = bounds.IsEmpty ? null : bounds.Height,
            WindowMaximized = WindowState == WindowState.Maximized,
        });
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        // RestoreBounds is still valid here; in OnClosed it is Rect.Empty.
        if (!e.Cancel) SaveSettings();
    }

    protected override void OnClosed(EventArgs e)
    {
        _reconnect?.Stop();
        _uiTimer?.Stop();
        _effects?.Cancel();
        _humCts?.Cancel();
        SaveSettings();
        // Failsafe on exit: PanicStop restores power/engine synchronously,
        // i.e. while SimConnect is still open.
        _director?.PanicStop();
        _hotkeys?.Dispose();
        _audio?.Dispose();
        _sim?.Dispose();
        base.OnClosed(e);
    }
}
