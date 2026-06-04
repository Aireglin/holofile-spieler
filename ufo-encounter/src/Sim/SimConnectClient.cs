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
}

/// <summary>
/// Thin wrapper around the managed SimConnect API. All public methods must be
/// invoked on the UI thread (the thread that owns the window handle), because
/// SimConnect is pumped through that window's message loop.
/// </summary>
public sealed class SimConnectClient : IDisposable
{
    public const uint WM_USER_SIMCONNECT = 0x0402;

    private enum DEFINITION { PlaneState }
    private enum REQUEST { PlaneState }
    private enum GROUP { Priority }

    // Electrical events we transmit to the user aircraft.
    public enum SimEvent
    {
        TOGGLE_MASTER_BATTERY,
        TOGGLE_ALTERNATOR1,
        TOGGLE_ALTERNATOR2,
        TOGGLE_MASTER_ALTERNATOR,
        TOGGLE_AVIONICS_MASTER,
    }

    private SimConnect? _sim;
    private readonly IntPtr _hwnd;

    public bool IsConnected => _sim != null;
    public PlaneState State { get; private set; }

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action<PlaneState>? StateUpdated;
    public event Action<string>? Log;

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
        void Add(string name, string unit) =>
            _sim!.AddToDataDefinition(DEFINITION.PlaneState, name, unit,
                SIMCONNECT_DATATYPE.FLOAT64, 0.0f, SimConnect.SIMCONNECT_UNUSED);

        Add("PLANE ALTITUDE", "feet");
        Add("PLANE ALT ABOVE GROUND", "feet");
        Add("PLANE LATITUDE", "degrees");
        Add("PLANE LONGITUDE", "degrees");
        Add("AIRSPEED INDICATED", "knots");
        Add("SIM ON GROUND", "Bool");
        Add("ELECTRICAL MASTER BATTERY", "Bool");
        Add("GENERAL ENG MASTER ALTERNATOR:1", "Bool");

        _sim!.RegisterDataDefineStruct<PlaneState>(DEFINITION.PlaneState);
    }

    private void RegisterEvents()
    {
        void Map(SimEvent e) => _sim!.MapClientEventToSimEvent(e, e.ToString());
        foreach (SimEvent e in Enum.GetValues<SimEvent>())
            Map(e);
    }

    /// <summary>Fire a (toggle) event at the user aircraft.</summary>
    public void Transmit(SimEvent e, uint data = 0)
    {
        if (_sim == null) return;
        try
        {
            _sim.TransmitClientEvent(SimConnect.SIMCONNECT_OBJECT_ID_USER, e, data,
                GROUP.Priority, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        }
        catch (COMException ex)
        {
            Log?.Invoke($"Transmit {e} failed: {ex.Message}");
        }
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
        if ((REQUEST)data.dwRequestID != REQUEST.PlaneState) return;
        if (data.dwData[0] is PlaneState ps)
        {
            State = ps;
            StateUpdated?.Invoke(ps);
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
