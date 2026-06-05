using System.Runtime.InteropServices;
using Microsoft.FlightSimulator.SimConnect;

namespace UfoEncounter.Sim;

/// <summary>
/// Snapshot of the player aircraft, refreshed roughly once per second.
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
    public double TimeOfDay;        // TIME OF DAY (enum: 1=dawn 2=day 3=dusk 4=night)
}

/// <summary>
/// Position/attitude written to a spawned light SimObject. Field order MUST
/// match the MoveObject data definition.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct ObjectPose
{
    public double Latitude;   // degrees
    public double Longitude;  // degrees
    public double AltitudeMsl;// feet
    public double Pitch;      // degrees
    public double Bank;       // degrees
    public double Heading;    // degrees true
    public double Vx;         // VELOCITY BODY X (ft/s) — kept 0 to stop drift
    public double Vy;         // VELOCITY BODY Y (ft/s)
    public double Vz;         // VELOCITY BODY Z (ft/s)
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

    private enum DEFINITION { PlaneState, MoveObject, Title }
    private enum REQUEST : uint { PlaneState = 0, Title = 1, LightBase = 1000, LightRemoveBase = 2000 }
    private enum GROUP { Priority }

    // Events we transmit to the user aircraft or to spawned objects.
    public enum SimEvent
    {
        TOGGLE_MASTER_BATTERY,
        TOGGLE_ALTERNATOR1,
        TOGGLE_ALTERNATOR2,
        TOGGLE_MASTER_ALTERNATOR,
        TOGGLE_AVIONICS_MASTER,
        // Engine kill / restart (used by the deep blackout's optional engine cut).
        ENGINE_AUTO_SHUTDOWN,
        ENGINE_AUTO_START,
        // Freeze a spawned object so the sim's physics leaves our writes alone.
        FREEZE_LATITUDE_LONGITUDE_SET,
        FREEZE_ALTITUDE_SET,
        FREEZE_ATTITUDE_SET,
    }

    private SimConnect? _sim;
    private readonly IntPtr _hwnd;

    public bool IsConnected => _sim != null;
    public PlaneState State { get; private set; }

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<PlaneState>? StateUpdated;
    public event Action<string>? Log;
    /// <summary>Raised when a spawn request returns its object id (requestId, objectId).</summary>
    public event Action<uint, uint>? ObjectAssigned;
    /// <summary>Raised with the user aircraft's exact SimObject title after RequestAircraftTitle().</summary>
    public event Action<string>? TitleReceived;

    public SimConnectClient(IntPtr hwnd) => _hwnd = hwnd;

    public bool TryConnect()
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
            Log?.Invoke($"Connect failed: {ex.Message}");
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

        // Settable position/attitude for moving spawned light objects.
        void Move(string name, string unit) =>
            _sim!.AddToDataDefinition(DEFINITION.MoveObject, name, unit,
                SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);

        Move("PLANE LATITUDE", "degrees");
        Move("PLANE LONGITUDE", "degrees");
        Move("PLANE ALTITUDE", "feet");
        Move("PLANE PITCH DEGREES", "degrees");
        Move("PLANE BANK DEGREES", "degrees");
        Move("PLANE HEADING DEGREES TRUE", "degrees");
        Move("VELOCITY BODY X", "feet per second");
        Move("VELOCITY BODY Y", "feet per second");
        Move("VELOCITY BODY Z", "feet per second");
        _sim!.RegisterDataDefineStruct<ObjectPose>(DEFINITION.MoveObject);

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

    /// <summary>Fire a (toggle/set) event at the user aircraft.</summary>
    public void Transmit(SimEvent e, uint data = 0) =>
        TransmitTo(SimConnect.SIMCONNECT_OBJECT_ID_USER, e, data);

    /// <summary>Fire a (toggle/set) event at a specific object id.</summary>
    public void TransmitTo(uint objectId, SimEvent e, uint data = 0)
    {
        if (_sim == null) return;
        try
        {
            _sim.TransmitClientEvent(objectId, e, data,
                GROUP.Priority, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        }
        catch (COMException ex)
        {
            Log?.Invoke($"Transmit {e} failed: {ex.Message}");
        }
    }

    // ---- Light SimObjects ----

    /// <summary>Spawn a SimObject. <paramref name="lightIndex"/> keys the request
    /// id so the assigned object id can be matched back.</summary>
    public void SpawnLight(string title, ObjectPose pose, uint lightIndex)
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
        try
        {
            _sim.AICreateSimulatedObject(title, init, (REQUEST)((uint)REQUEST.LightBase + lightIndex));
        }
        catch (COMException ex)
        {
            Log?.Invoke($"Spawn '{title}' failed: {ex.Message}");
        }
    }

    public void MoveLight(uint objectId, ObjectPose pose)
    {
        if (_sim == null) return;
        try
        {
            _sim.SetDataOnSimObject(DEFINITION.MoveObject, objectId,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT, pose);
        }
        catch (COMException) { /* object may have just been removed */ }
    }

    /// <summary>Freeze physics on a spawned object so our position writes stick.</summary>
    public void FreezeLight(uint objectId)
    {
        TransmitTo(objectId, SimEvent.FREEZE_LATITUDE_LONGITUDE_SET, 1);
        TransmitTo(objectId, SimEvent.FREEZE_ALTITUDE_SET, 1);
        TransmitTo(objectId, SimEvent.FREEZE_ATTITUDE_SET, 1);
    }

    public void RemoveLight(uint objectId, uint lightIndex)
    {
        if (_sim == null) return;
        try
        {
            _sim.AIRemoveObject(objectId, (REQUEST)((uint)REQUEST.LightRemoveBase + lightIndex));
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
        Log?.Invoke($"Connected to {data.szApplicationName}.");
        s.RequestDataOnSimObject(REQUEST.PlaneState, DEFINITION.PlaneState,
            SimConnect.SIMCONNECT_OBJECT_ID_USER, SIMCONNECT_PERIOD.SECOND,
            SIMCONNECT_DATA_REQUEST_FLAG.CHANGED, 0, 0, 0);
        Connected?.Invoke();
    }

    private void OnQuit(SimConnect s, SIMCONNECT_RECV data)
    {
        Log?.Invoke("Simulator closed.");
        Disconnect();
    }

    private void OnException(SimConnect s, SIMCONNECT_RECV_EXCEPTION data) =>
        Log?.Invoke($"SimConnect exception: {(SIMCONNECT_EXCEPTION)data.dwException}");

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
                Log?.Invoke($"Current aircraft title: '{title}'");
                TitleReceived?.Invoke(title);
                break;
        }
    }

    private void OnAssignedObjectId(SimConnect s, SIMCONNECT_RECV_ASSIGNED_OBJECT_ID data)
    {
        if (data.dwRequestID >= (uint)REQUEST.LightBase &&
            data.dwRequestID < (uint)REQUEST.LightRemoveBase)
        {
            uint lightIndex = data.dwRequestID - (uint)REQUEST.LightBase;
            ObjectAssigned?.Invoke(lightIndex, data.dwObjectID);
        }
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
