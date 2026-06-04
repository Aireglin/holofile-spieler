# UFO Encounter — MSFS 2024

Ein kleines Windows-Tool, das über **SimConnect** zufällige „UFO-Begegnungen"
im Microsoft Flight Simulator 2024 inszeniert — angelehnt an wiederkehrende
Pilot-/UAP-Berichte.

> **Status: MVP (Schritt 1).** Enthalten sind **Elektronikstörungen**
> (Instrumentenausfall über den „Generatoren-Trick"), ein synthetisiertes
> **dumpfes Summen**, sowie die komplette **UI** mit Test-Buttons, Slidern,
> Zufallsmodus, Realismus-Lock, Seed und Sichtungs-Logbuch.
> Die **Licht-Choreografie** (tanzende Lichter via gespawnten SimObjects)
> kommt in Schritt 2 und dockt an dieselben Szenario-Presets an.

## Was es kann

- **Szenarien-Presets** nach echten Reports: Tic-Tac (Nimitz), Verfolger
  (Wingman), Fly-by, Pulk-Formation, Pop-up & Verschwinden, RB-47 (E-Störung).
- **Test-Buttons** für jedes Szenario *und* für Einzel-Effekte
  (Blackout / Stutter / Hum) — alles auf Abruf.
- **Instrumentenausfall** über `TOGGLE_MASTER_BATTERY`, `TOGGLE_ALTERNATOR1`,
  `TOGGLE_AVIONICS_MASTER`. Wahlweise durchgehender **Blackout** oder
  **Stutter** (an/aus/an/aus). Strom wird **garantiert** wieder eingeschaltet
  (auch bei Stop/Absturz/Beenden → Failsafe).
- **Dumpfes Summen** (in-memory synthetisiert, keine Audiodateien nötig).
- **Zufalls- oder Manuell-Modus**: Häufigkeit, Dauer (min/max) und Intensität
  per Slider; oder Encounter gezielt per Knopfdruck.
- **Realismus-Lock**: keine Encounters am Boden oder unter einer einstellbaren
  Min-AGL (kein Crash beim Start/Landung).
- **Seed** für reproduzierbare Läufe (Demos/Streaming).
- **Logbuch**: jede Begegnung landet in `sightings.csv` (Zeit, Position, AGL).

> ⚠️ Ob der Instrumentenausfall sichtbar wird, hängt vom **Flugzeugmodell** ab.
> MSFS 2024 stellt zuverlässig nur `TOGGLE_*`-Events bereit, und manche
> (Study-Level-)Flieger ignorieren sie. Das Tool toleriert das und stellt den
> Strom in jedem Fall wieder her.

## Voraussetzungen

- Windows 10/11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- **MSFS 2024 SDK** (für `Microsoft.FlightSimulator.SimConnect.dll` +
  natives `SimConnect.dll`). Im Sim aktivieren: *Optionen → Allgemein →
  Entwicklermodus*, dann SDK installieren.

## Bauen

Das Projekt findet das SDK automatisch über die Umgebungsvariable
`MSFS2024_SDK` (bzw. `MSFS_SDK`) oder den Standardpfad `C:\MSFS 2024 SDK`.
Anderer Pfad? Per `-p:MsfsSdkPath=...` überschreiben:

```powershell
cd ufo-encounter

# Standard (SDK über Env-Var / Default-Pfad gefunden)
dotnet build -c Release

# Oder SDK-Pfad explizit angeben
dotnet build -c Release -p:MsfsSdkPath="D:\MSFS 2024 SDK"
```

### Einzelne .exe erzeugen

```powershell
dotnet publish src/UfoEncounter.csproj -c Release -r win-x64 ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Ergebnis: `src/bin/Release/net8.0-windows/win-x64/publish/UfoEncounter.exe`.
Das native `SimConnect.dll` wird mitkopiert/gebündelt.

## Benutzen

1. MSFS 2024 starten, in einen Flug gehen.
2. `UfoEncounter.exe` starten → Status oben sollte **CONNECTED** zeigen
   (sonst „Connect" klicken; es wird ohnehin automatisch wiederholt).
3. Ein **Szenario** oder einen **Einzel-Effekt** per Button testen, oder
   **Zufallsmodus** aktivieren und Häufigkeit/Dauer/Intensität per Slider regeln.
4. **PANIK-STOP** beendet alles sofort und stellt den Strom wieder her.

## Projektstruktur

```
ufo-encounter/
├─ UfoEncounter.sln
└─ src/
   ├─ UfoEncounter.csproj        # net8.0-windows, x64, SimConnect-Referenz
   ├─ App.xaml(.cs)              # WPF-App + dunkles Theme
   ├─ MainWindow.xaml(.cs)       # UI + Verdrahtung, SimConnect-Message-Hook
   ├─ Sim/SimConnectClient.cs    # Verbindung, SimVars lesen, Events senden
   ├─ Encounters/
   │  ├─ EncounterScenario.cs    # Preset-Katalog (Reports)
   │  ├─ EncounterDirector.cs    # Dirigent: Auswahl, Timing, Random, Realism-Lock
   │  └─ ElectricalDisruptor.cs  # Blackout/Stutter + garantierter Restore
   ├─ Audio/AudioEngine.cs       # synthetisiertes Summen (WAV in-memory)
   └─ Util/Logbook.cs            # Sichtungs-CSV
```

## Roadmap (Schritt 2+)

- **LightChoreographer**: Licht-SimObjects spawnen und relativ zum Flugzeug
  „tanzen" lassen (Tic-Tac-Sprünge, Wingman-Halten, Fly-by, Pulk-Formation),
  Tag & Nacht.
- Joystick-/Hotkey-Bindung für „Encounter jetzt" und Panik-Stop.
- Sichtungsbericht-Export als formatiertes PDF/Markdown.
