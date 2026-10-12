using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

namespace UfoEncounter.Sim;

/// <summary>
/// Snapshot of the player aircraft, refreshed every sim frame.
/// Bool SimVars come across the wire as FLOAT64 (0 / 1).
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct PlaneState
{
    public double AltitudeMsl;      // PLANE ALTITUDE (feet)
    public double AltitudeAgl;      // PLANE ALT ABOVE GROUND (feet)
    public double Latitude;         // degrees
    public double Longitude;        // degrees
    public double AirspeedIndicated;// knots
    public double OnGround;         // SIM ON GROUND (bool)
    public double MasterBattery;    // ELECTRICAL MASTER BATTERY (bool)
    public double MasterAlternator; // GENERAL ENG MASTER ALTERNATOR:1 (bool)
    public double HeadingTrue;      // PLANE HEADING DEGREES TRUE (degrees)
    public double TimeOfDay;        // TIME OF DAY (enum: 0=dawn 1=day 2=dusk 3=night)
}

/// <summary>Position/attitude written to a spawned object. Field order MUST match
/// the ObjectPose data definition.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ObjectPose
{
    public double Latitude;   // degrees
    public double Longitude;  // degrees
    public double AltitudeMsl;// feet
    public double Pitch;      // degrees
    public double Bank;       // degrees
    public double Heading;    // degrees true
}

/// <summary>VELOCITY BODY X/Y/Z (ft/s), kept at 0 so frozen aircraft don't drift.
/// A separate definition, because plain SimObjects may reject velocity writes —
/// that must never block the position update.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ObjectVelocity
{
    public double X;
    public double Y;
    public double Z;
}

/// <summary>Holds a single string SimVar (e.g. TITLE).</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
public struct TitleStruct
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string Value;
}

/// <summary>
/// Thin wrapper around the managed SimConnect API. All public methods must be
/// invoked on the UI thread (the thread that owns the window handle), because
/// SimConnect is pumped through that window's message loop.
/// </summary>
public sealed class SimConnectClient : IDisposable
{
    public const uint WM_USER_SIMCONNECT = 0x0402;

    /// <summary>Spawn slots per request-id range. Every spawn gets a fresh slot, so a
    /// late assignment can never be mistaken for a newer object.</summary>
    public const uint SpawnSlots = 0x1000;

    private enum DEFINITION { PlaneState, ObjectPose, ObjectVelocity, Title }
    private enum REQUEST : uint { PlaneState = 0, Title = 1, SpawnBase = 0x1000, RemoveBase = 0x2000, ReleaseBase = 0x3000 }
    private enum GROUP { Priority }

    // Events we transmit to the user aircraft or to spawned objects.
    public enum SimEvent
    {
        TOGGLE_MASTER_BATTERY,
        TOGGLE_ALTERNATOR1,
        TOGGLE_ALTERNATOR2,
        TOGGLE_ALTERNATOR3,
        TOGGLE_ALTERNATOR4,
        TOGGLE_MASTER_ALTERNATOR,
        TOGGLE_AVIONICS_MASTER,
        // Engine kill / restart (deep blackout).
        ENGINE_AUTO_SHUTDOWN,
        ENGINE_AUTO_START,
        // Freeze a spawned object so the sim's physics leaves our writes alone.
        FREEZE_LATITUDE_LONGITUDE_SET,
        FREEZE_ALTITUDE_SET,
        FREEZE_ATTITUDE_SET,
        // Exterior lights of spawned aircraft (sent to the object id).
        NAV_LIGHTS_SET,
        STROBES_SET,
        BEACON_LIGHTS_SET,
        LOGO_LIGHTS_SET,
    }

    private static readonly object ZeroVelocityBoxed = new ObjectVelocity();

    private SimConnect? _sim;
    private readonly IntPtr _hwnd;
    private readonly Dictionary<uint, int> _exceptionCounts = new();

    public bool IsConnected => _sim != null;
    public PlaneState State { get; private set; }

    public event Action? Connected;
    public event Action? Disconnected;
    /// <summary>Raised when MSFS itself reports it is closing (before Disconnected).</summary>
    public event Action? SimQuit;
    /// <summary>Raised every sim frame with the fresh player state.</summary>
    public event Action<PlaneState>? StateUpdated;
    public event Action<string>? Log;
    /// <summary>A spawn returned its object id: (slot, objectId).</summary>
    public event Action<uint, uint>? ObjectAssigned;
    /// <summary>Raised with the user aircraft's exact SimObject title after RequestAircraftTitle().</summary>
    public event Action<string>? TitleReceived;

    public SimConnectClient(IntPtr hwnd) => _hwnd = hwnd;

    /// <summary>Open the connection. <paramref name="quiet"/> suppresses the failure
    /// message (used by the background retry while MSFS is still loading).</summary>
    public bool TryConnect(bool quiet = false)
    {
        if (_sim != null) return true;
        try
        {
            _sim = new SimConnect("UFO Encounter", _hwnd, WM_USER_SIMCONNECT, null, 0);

            _sim.OnRecvOpen += OnOpen;
            _sim.OnRecvQuit += OnQuit;
            _sim.OnRecvException += OnException;
            _sim.OnRecvSimobjectData += OnSimobjectData;
            _sim.OnRecvAssignedObjectId += OnAssignedObjectId;

            RegisterDataDefinition();
            RegisterEvents();
            return true;
        }
        catch (COMException ex)
        {
            if (!quiet) Log?.Invoke($"Keine Verbindung zu MSFS ({ex.Message}). Läuft der Sim und ist ein Flug geladen?");
            _sim = null;
            return false;
        }
    }

    private void RegisterDataDefinition()
    {
        void Read(string name, string unit) =>
            _sim!.AddToDataDefinition(DEFINITION.PlaneState, name, unit,
                SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);

        Read("PLANE ALTITUDE", "feet");
        Read("PLANE ALT ABOVE GROUND", "feet");
        Read("PLANE LATITUDE", "degrees");
        Read("PLANE LONGITUDE", "degrees");
        Read("AIRSPEED INDICATED", "knots");
        Read("SIM ON GROUND", "Bool");
        Read("ELECTRICAL MASTER BATTERY", "Bool");
        Read("GENERAL ENG MASTER ALTERNATOR:1", "Bool");
        Read("PLANE HEADING DEGREES TRUE", "degrees");
        Read("TIME OF DAY", "enum");
        _sim!.RegisterDataDefineStruct<PlaneState>(DEFINITION.PlaneState);

        void Pose(string name, string unit) =>
            _sim!.AddToDataDefinition(DEFINITION.ObjectPose, name, unit,
                SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);

        Pose("PLANE LATITUDE", "degrees");
        Pose("PLANE LONGITUDE", "degrees");
        Pose("PLANE ALTITUDE", "feet");
        Pose("PLANE PITCH DEGREES", "degrees");
        Pose("PLANE BANK DEGREES", "degrees");
        Pose("PLANE HEADING DEGREES TRUE", "degrees");
        _sim!.RegisterDataDefineStruct<ObjectPose>(DEFINITION.ObjectPose);

        void Vel(string name) =>
            _sim!.AddToDataDefinition(DEFINITION.ObjectVelocity, name, "feet per second",
                SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);

        Vel("VELOCITY BODY X");
        Vel("VELOCITY BODY Y");
        Vel("VELOCITY BODY Z");
        _sim!.RegisterDataDefineStruct<ObjectVelocity>(DEFINITION.ObjectVelocity);

        // TITLE is a string SimVar (no unit) — used to auto-detect the loaded aircraft.
        _sim!.AddToDataDefinition(DEFINITION.Title, "TITLE", null,
            SIMCONNECT_DATATYPE.STRING256, 0.0f, SimConnect.SIMCONNECT_UNUSED);
        _sim!.RegisterDataDefineStruct<TitleStruct>(DEFINITION.Title);
    }

    /// <summary>Ask the sim for the exact title of the currently loaded aircraft
    /// (a one-shot request; the answer arrives via <see cref="TitleReceived"/>).</summary>
    public void RequestAircraftTitle()
    {
        if (_sim == null) return;
        _sim.RequestDataOnSimObject(REQUEST.Title, DEFINITION.Title,
            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.ONCE,
            SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
    }

    private void RegisterEvents()
    {
        foreach (SimEvent e in Enum.GetValues<SimEvent>())
            _sim!.MapClientEventToSimEvent(e, e.ToString());
    }

    /// <summary>Fire a (toggle/set) event at the user aircraft. Returns false if
    /// nothing was sent (not connected, or SimConnect refused).</summary>
    public bool Transmit(SimEvent e, uint data = 0) =>
        TransmitTo(SimConnect.SIMCONNECT_OBJECT_ID_USER, e, data);

    /// <summary>Fire a (toggle/set) event at a specific object id.</summary>
    public bool TransmitTo(uint objectId, SimEvent e, uint data = 0)
    {
        if (_sim == null) return false;
        try
        {
            _sim.TransmitClientEvent(objectId, e, data,
                GROUP.Priority, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            return true;
        }
        catch (COMException ex)
        {
            Log?.Invoke($"Event {e} fehlgeschlagen: {ex.Message}");
            return false;
        }
    }

    // ---- Spawned objects ----

    /// <summary>Spawn a SimObject in <paramref name="slot"/> (0..SpawnSlots-1). Aircraft
    /// titles must use AICreateNonATCAircraft; AICreateSimulatedObject is only for
    /// real SimpleObjects (with aircraft titles it can freeze MSFS 2024).</summary>
    public void SpawnObject(string title, ObjectPose pose, uint slot, bool asAircraft)
    {
        if (_sim == null) return;
        var init = new SIMCONNECT_DATA_INITPOSITION
        {
            Latitude = pose.Latitude,
            Longitude = pose.Longitude,
            Altitude = pose.AltitudeMsl,
            Pitch = pose.Pitch,
            Bank = pose.Bank,
            Heading = pose.Heading,
            OnGround = 0,
            Airspeed = 0,
        };
        var req = (REQUEST)((uint)REQUEST.SpawnBase + slot % SpawnSlots);
        try
        {
            if (asAircraft)
                _sim.AICreateNonATCAircraft(title, $"UFO{slot % 1000}", init, req);
            else
                _sim.AICreateSimulatedObject(title, init, req);
        }
        catch (COMException ex)
        {
            Log?.Invoke($"Objekt „{title}“ konnte nicht erzeugt werden: {ex.Message}");
        }
    }

    public void MoveObject(uint objectId, ObjectPose pose)
    {
        if (_sim == null) return;
        try
        {
            _sim.SetDataOnSimObject(DEFINITION.ObjectPose, objectId,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT, pose);
        }
        catch (COMException) { /* object may have just been removed */ }
    }

    public void ZeroVelocity(uint objectId)
    {
        if (_sim == null) return;
        try
        {
            _sim.SetDataOnSimObject(DEFINITION.ObjectVelocity, objectId,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT, ZeroVelocityBoxed);
        }
        catch (COMException) { /* object may have just been removed */ }
    }

    /// <summary>Hand a freshly spawned object from the AI system to us, then
    /// freeze it. AIReleaseControl is essential — otherwise the AI system keeps
    /// fighting our SetData writes and the object snaps back to its spawn point.</summary>
    public void ReleaseAndFreeze(uint objectId, uint slot)
    {
        if (_sim == null) return;
        try { _sim.AIReleaseControl(objectId, (REQUEST)((uint)REQUEST.ReleaseBase + slot % SpawnSlots)); }
        catch (COMException ex) { Log?.Invoke($"AIReleaseControl fehlgeschlagen: {ex.Message}"); }
        FreezeObject(objectId);
    }

    /// <summary>Freeze physics on a spawned object so our position writes stick.</summary>
    public void FreezeObject(uint objectId)
    {
        TransmitTo(objectId, SimEvent.FREEZE_LATITUDE_LONGITUDE_SET, 1);
        TransmitTo(objectId, SimEvent.FREEZE_ALTITUDE_SET, 1);
        TransmitTo(objectId, SimEvent.FREEZE_ATTITUDE_SET, 1);
    }

    /// <summary>Switch the exterior lights of a spawned aircraft on/off. Released,
    /// frozen AI aircraft keep the state; nothing turns them back off.</summary>
    public void SetObjectLights(uint objectId, bool on)
    {
        uint v = on ? 1u : 0u;
        TransmitTo(objectId, SimEvent.NAV_LIGHTS_SET, v);
        TransmitTo(objectId, SimEvent.STROBES_SET, v);
        TransmitTo(objectId, SimEvent.BEACON_LIGHTS_SET, v);
        TransmitTo(objectId, SimEvent.LOGO_LIGHTS_SET, v);
    }

    public void RemoveObject(uint objectId, uint slot)
    {
        if (_sim == null) return;
        try
        {
            _sim.AIRemoveObject(objectId, (REQUEST)((uint)REQUEST.RemoveBase + slot % SpawnSlots));
        }
        catch (COMException) { /* already gone */ }
    }

    /// <summary>Call from the window message hook for WM_USER_SIMCONNECT.</summary>
    public void HandleWindowMessage()
    {
        try { _sim?.ReceiveMessage(); }
        catch (COMException) { Disconnect(); }
    }

    private void OnOpen(SimConnect s, SIMCONNECT_RECV_OPEN data)
    {
        _exceptionCounts.Clear();
        Log?.Invoke($"Verbunden mit {data.szApplicationName}.");
        // Per SIM FRAME: the reference position must be fresh, otherwise spawned
        // objects placed relative to it lag behind and snap forward each update.
        s.RequestDataOnSimObject(REQUEST.PlaneState, DEFINITION.PlaneState,
            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SIM_FRAME,
            SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT, 0, 0, 0);
        Connected?.Invoke();
    }

    private void OnQuit(SimConnect s, SIMCONNECT_RECV data)
    {
        Log?.Invoke("MSFS wurde beendet.");
        SimQuit?.Invoke();
        Disconnect();
    }

    private void OnException(SimConnect s, SIMCONNECT_RECV_EXCEPTION data)
    {
        // The same exception can fire every frame (e.g. writes to a removed object):
        // explain it once in German, then stay quiet.
        _exceptionCounts.TryGetValue(data.dwException, out int n);
        _exceptionCounts[data.dwException] = ++n;
        if (n <= 2)
            Log?.Invoke($"SimConnect-Hinweis: {Explain(data.dwException)}");
        else if (n == 3)
            Log?.Invoke("(weitere gleiche SimConnect-Hinweise werden ausgeblendet)");
    }

    // Numeric values of SIMCONNECT_EXCEPTION (SimConnect.h), so we don't depend on
    // member names of the managed enum.
    private static string Explain(uint code) => code switch
    {
        3 => "Objekt existiert nicht mehr (harmlos, z. B. nach Flugende).",
        7 => "Ein Event- oder Variablenname ist dieser MSFS-Version unbekannt (harmlos).",
        11 => "Zu viele KI-Objekte im Sim.",
        22 => "Objekt konnte nicht erzeugt werden. Stimmt der Titel exakt (Groß-/Kleinschreibung)? "
              + "Ist das Paket im Community-Ordner und wurde MSFS danach neu gestartet?",
        24 => "Diese Aktion ist für diesen Objekttyp nicht möglich (harmlos).",
        33 => "Objekt liegt außerhalb des sichtbaren Bereichs des Sims und wurde nicht erzeugt.",
        _ => $"{(SIMCONNECT_EXCEPTION)code} (Code {code})",
    };

    private void OnSimobjectData(SimConnect s, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        switch ((REQUEST)data.dwRequestID)
        {
            case REQUEST.PlaneState when data.dwData[0] is PlaneState ps:
                State = ps;
                StateUpdated?.Invoke(ps);
                break;
            case REQUEST.Title when data.dwData[0] is TitleStruct t:
                var title = t.Value?.Trim() ?? "";
                Log?.Invoke($"Titel des geladenen Flugzeugs: „{title}“");
                TitleReceived?.Invoke(title);
                break;
        }
    }

    private void OnAssignedObjectId(SimConnect s, SIMCONNECT_RECV_ASSIGNED_OBJECT_ID data)
    {
        uint b = (uint)REQUEST.SpawnBase;
        if (data.dwRequestID >= b && data.dwRequestID < b + SpawnSlots)
            ObjectAssigned?.Invoke(data.dwRequestID - b, data.dwObjectID);
    }

    public void Disconnect()
    {
        if (_sim == null) return;
        try { _sim.Dispose(); } catch { /* ignore */ }
        _sim = null;
        Disconnected?.Invoke();
    }

    public void Dispose() => Disconnect();
}
