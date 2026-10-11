# UFO Encounter — MSFS 2024

Ein kleines Windows-Tool, das über **SimConnect** zufällige „UFO-Begegnungen“
im Microsoft Flight Simulator 2024 inszeniert, angelehnt an wiederkehrende
Pilot- und UAP-Berichte.

## Was es kann

- **Szenarien nach echten Reports**: Tic-Tac (Nimitz), Verfolger (Wingman),
  Fly-by, Pulk-Formation, Pop-up, RB-47, Totaler Blackout, Umkreisung,
  JAL1628 (Riese), Mutterschiff (Tag / E-Störung). Ein Szenario-Button liefert
  immer genau dieses Szenario, inklusive seines Ausfalls.
- **Zufällige Muster-Ketten**: Jede Begegnung beginnt mit einem optionalen
  Anflug (Anschießen & Bremsen oder langsame Annäherung). Danach folgen 2–4
  wechselnde Verhaltensmuster, und am Ende fliegen die Objekte davon. Die
  Muster gehen fließend ineinander über. Ausfälle kommen zufällig dazu, so dass
  alles mit allem kombiniert werden kann.
- **18 Bewegungsmuster**: Tic-Tac-Sprünge, Wingman, Begleiter vorn / seitlich /
  oben / unten, Inspektion (lang), Umkreisung, Formation, Pop-up, Vorbeiflug,
  Mutterschiff (langsam, massiv), Lichtstrahl, Überholen u. a. Jedes Muster hat
  bei jeder Begegnung eine eigene Zufallsvarianz.
- **Spezial**: **Holy Grail** (Tempus Fugit: Lichtstrahl über dem Flugzeug in
  totaler Stille) und **Jet-Verfolgung** (UFO schießt davon, ein Kampfjet
  überholt dich knapp).
- **Instrumentenausfall**: Flackern (unregelmäßig), Flackern → Blackout und
  Total-Blackout. Per Auswahlfeld für **Kolben-Flugzeuge** (Batterie,
  Generatoren 1–4, Avionik; der Motor bleibt wahlweise an) oder für
  **Jets/Turbinen** (Komplett-Abschaltung). Strom und Motor werden **immer**
  wiederhergestellt: nach jedem Ausfall, bei PANIK-STOP, beim Schließen des
  Programms und nach einem Verbindungsabriss.
- **Licht-Paket ohne 3D-Modell**: Ein Klick erzeugt ein Community-Paket mit
  reinen Lichtobjekten (7 Farben × ruhig / pulsierend / blitzend) und
  leuchtenden Kugeln für den Tag. Details: [`SimObjectPackage/README.md`](SimObjectPackage/README.md).
- **Sound**: tiefes Summen und pulsierender Sub-Bass, nahtlos geloopt. Die
  Lautstärke richtet sich nach dem Abstand. Ein Jumpscare beim Holy Grail ist
  per eigener Datei austauschbar (`jumpscare.mp3` neben die .exe legen).
- **Zufallsmodus**: würfelt pro Begegnung Szenario, Anzahl, Muster, Abstand,
  Tempo und Dread. Die eigenen Regler-Werte bleiben dabei unverändert. Dazu
  gibt es Tag/Nacht-Logik, den Realismus-Lock (nicht am Boden oder in zu
  geringer Höhe) und Voreinstellungen (Sanft / Standard / Maximum).
- **Globale Tastenkürzel** (funktionieren auch, wenn MSFS im Vollbild den
  Fokus hat): **Strg+Alt+P** PANIK-STOP · **Strg+Alt+U** Begegnung jetzt ·
  **Strg+Alt+Z** Zufallsmodus.
- **Flüssige Bewegung**: Die Objekte werden im Takt der Sim-Frames bewegt, mit
  zeitbasierter Glättung, damit sie auch bei Jet-Reisegeschwindigkeit ruhig
  laufen.
- **Logbuch** (`sightings.csv`), Seed für wiederholbare Läufe, gespeicherte
  Einstellungen inklusive Fenstergröße.

> ⚠️ Ob der Instrumentenausfall sichtbar wird, hängt vom **Flugzeugmodell** ab.
> Glascockpits mit Batterie-Backup (z. B. **Vision Jet**) reagieren teils kaum.
> Das ist eine Grenze des Sims, kein Fehler. Am deutlichsten wirkt es bei
> einfachen Fliegern (C172, DA62).

## Bedienung

Das Fenster hat oben eine **immer sichtbare Statusleiste**: Sim-Status, Höhe,
Tageszeit, laufende Begegnung mit aktueller Phase und Countdown bis zur
nächsten Zufalls-Begegnung. Dort sitzen auch die Buttons **„▶ Begegnung jetzt“**,
**„Zufallsmodus“** und **„PANIK-STOP“**. Darunter liegen vier Tabs:

| Tab | Inhalt |
|---|---|
| **Begegnungen** | Szenario-Buttons, Spezial (Holy Grail, Jet-Verfolgung), Zufallsmodus + Voreinstellungen |
| **Objekte & Lichter** | Objekt-Titel (mit „Übernehmen“), Licht-Paket, Anzahl/Abstand/Tempo, **Muster testen** (alle Muster in einer Liste), Lichtstrahl |
| **Ausfälle & Sound** | Flugzeugtyp/Ausfall-Art, Wahrscheinlichkeit, Blackout-Dauer, Test-Buttons, Sound |
| **Einstellungen** | Ablauf, Dread, Min-Höhe, Seed, „immer im Vordergrund“, Datenordner |

**Objekt-Titel**: Das gewünschte Flugzeug in MSFS laden und **„Übernehmen“**
klicken, dann steht der exakte Titel im Feld. Mehrere Titel mit Komma ergeben
einen Zufallsmix. Titel aus dem Licht-Paket (`UFOE_…`) werden automatisch als
einfaches SimObject gespawnt, alle anderen als Flugzeug.

## Voraussetzungen

- Windows 10/11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- **MSFS 2024 SDK** (für `Microsoft.FlightSimulator.SimConnect.dll` +
  natives `SimConnect.dll`). Im Sim aktivieren: *Optionen → Allgemein →
  Entwicklermodus*, dann SDK installieren.

## Bauen

Das Projekt findet das SDK automatisch über die Umgebungsvariable
`MSFS2024_SDK` (bzw. `MSFS_SDK`) oder den Standardpfad `C:\MSFS 2024 SDK`.

```powershell
cd ufo-encounter
dotnet publish src/UfoEncounter.csproj -c Release -r win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Ergebnis: `src/bin/Release/net8.0-windows/win-x64/publish/UfoEncounter.exe`.
Anderer SDK-Pfad: `-p:MsfsSdkPath="D:\MSFS 2024 SDK"` anhängen.

## Projektstruktur

```
ufo-encounter/
├─ UfoEncounter.sln
├─ SimObjectPackage/README.md    # Infos zum (vom Programm erzeugten) Licht-Paket
└─ src/
   ├─ UfoEncounter.csproj        # net8.0-windows, x64, SimConnect-Referenz
   ├─ App.xaml(.cs)              # WPF-App + dunkles Theme
   ├─ MainWindow.xaml(.cs)       # Statusleiste, Tabs, Log, Tastenkürzel
   ├─ Sim/SimConnectClient.cs    # Verbindung, SimVars (pro Sim-Frame), Events, Objekte
   ├─ Encounters/
   │  ├─ EncounterScenario.cs    # Szenario-Katalog
   │  ├─ EncounterDirector.cs    # Dirigent: Ketten, Zufallsmodus, Spezial, Audio-Mix
   │  ├─ ElectricalDisruptor.cs  # Flackern/Blackout + garantierter Restore
   │  └─ LightChoreographer.cs   # Bewegungsmuster im Sim-Frame-Takt
   ├─ Audio/
   │  ├─ AudioEngine.cs          # mehrschichtiger Mix (MediaPlayer)
   │  └─ WavSynth.cs             # erzeugt Summen/Sub-Bass/Jumpscare als WAV
   └─ Util/
      ├─ LightPackageGenerator.cs# erzeugt das Licht-Paket (glTF ohne Mesh)
      ├─ GlobalHotkeys.cs        # globale Tastenkürzel
      ├─ AppPaths.cs, SettingsStore.cs, Logbook.cs
```
