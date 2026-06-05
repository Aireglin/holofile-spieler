# UFO Encounter — MSFS 2024

Ein kleines Windows-Tool, das über **SimConnect** zufällige „UFO-Begegnungen"
im Microsoft Flight Simulator 2024 inszeniert — angelehnt an wiederkehrende
Pilot-/UAP-Berichte.

> **Status: V2.** Enthalten sind **Elektronikstörungen** (Instrumentenausfall
> über den „Generatoren-Trick", inkl. **Total-Blackout** mit zeitlich
> entkoppeltem Wiedereinschalten), ein synthetisiertes **dumpfes Summen**, die
> **Licht-Choreografie** (tanzende Licht-SimObjects, *experimentell*) sowie die
> komplette **UI** mit Test-Buttons, Slidern, Zufallsmodus, Realismus-Lock,
> Seed und Sichtungs-Logbuch.

## Was es kann

- **Szenarien-Presets** nach echten Reports: Tic-Tac (Nimitz), Verfolger
  (Wingman), Fly-by, Pulk-Formation, Pop-up & Verschwinden, RB-47 (E-Störung).
- **Test-Buttons** für jedes Szenario *und* für Einzel-Effekte
  (Blackout / Stutter / Hum) — alles auf Abruf.
- **Instrumentenausfall** über `TOGGLE_MASTER_BATTERY`, `TOGGLE_ALTERNATOR1/2`,
  `TOGGLE_AVIONICS_MASTER`. Modi: **Blackout**, **Stutter** (an/aus/an/aus) und
  **Total-Blackout** (alles aus; Strom kommt **zeitlich entkoppelt** vom
  Encounter zurück, eigene Zufallsdauer via Slider). Strom wird **garantiert**
  wieder eingeschaltet (auch bei Stop/Absturz/Beenden → Failsafe).
- **Licht-Choreografie** *(experimentell)*: spawnt Licht-SimObjects und lässt
  sie relativ zum Flugzeug tanzen — Muster **Tic-Tac, Wingman, Fly-by,
  Formation, Pop-up**. Jede Begegnung wird **zufällig variiert** (Tempo,
  Distanz, Amplitude, Sprungrhythmus, gelegentliche „Darts") — das gleiche
  Szenario läuft nie zweimal identisch ab.
- **Mutterschiff**: zwei Szenarien mit einem **massiven Objekt** (eigenes
  Titel-Feld), das weit entfernt lauert, sich langsam nähert und gelegentlich
  „unmöglich" umspringt — auch **bei Tag** sichtbar.
- **Mehrschichtiger Sound** (prozedural synthetisiert, keine Audiodateien
  nötig): Drone/Summen, pulsierender **Sub-Bass**, **Static/Knistern** bei
  Stromausfall und ein **Anflug-Whoosh**. **Lautstärke nach Distanz** – der Mix
  wird lauter, je näher das Objekt kommt.
- **Mehrphasige Begegnungen**: Annäherung → Beobachtung → Eskalation → Abgang,
  als kleine „Geschichte" statt eines flachen Effekts (abschaltbar).
- **Dread-Regler** (Anspannung): steuert, wie nah/lang/aggressiv die Eskalation
  wird – und die Wahrscheinlichkeit für …
- **Signature-Events**: seltene (≈1,5–6,5 %, mit Dread steigend) Ausnahme-
  Begegnungen – riesiges Mutterschiff, sehr nah, langer Deep-Blackout.
- **Zufalls- oder Manuell-Modus**: Häufigkeit, Dauer (min/max) und Intensität
  per Slider; oder Encounter gezielt per Knopfdruck.
- **Realismus-Lock**: keine Encounters am Boden oder unter einer einstellbaren
  Min-AGL (kein Crash beim Start/Landung).
- **Seed** für reproduzierbare Läufe (Demos/Streaming).
- **Logbuch**: jede Begegnung landet in `sightings.csv` (Zeit, Position, AGL).

> ⚠️ Ob der Instrumentenausfall sichtbar wird, hängt vom **Flugzeugmodell** ab.
> Glascockpit-/Study-Flieger (z. B. **Vision Jet**) halten essentielle Busse mit
> Batterie-Backup am Leben und reagieren kaum — das ist eine Sim-Grenze, kein
> Bug. Der **Total-Blackout** maximiert den Versuch (Batterie + beide
> Generatoren + Avionik). Am deutlichsten wirkt es bei einfachen Fliegern
> (z. B. Cessna 172). Der Strom kommt in jedem Fall garantiert wieder.

## Lichter: SimObject-Titel finden (experimentell)

Die Lichter werden als **SimObjects** in den Sim gespawnt. SimConnect braucht
dafür den **exakten Titel** eines vorhandenen Objekts — der variiert je nach
Installation, deshalb ist das Feld in der UI leer (= Lichter aus), bis du einen
Titel einträgst.

**Einfachster Weg (auch für die Xbox/Store-Version): Auto-Erkennung.**
Lade in MSFS das Flugzeug, das als „Licht" bzw. „Mutterschiff" dienen soll,
und klick im Panel auf **„Aktuelles Flugzeug übernehmen"** neben dem jeweiligen
Feld. Das Tool liest den exakten `TITLE` per SimConnect aus und füllt das Feld.
Du musst keine Dateien suchen.

> Hinweis: Bei der **Store/Xbox-Version** liegen die Aircraft-Dateien in einem
> geschützten Windows-Ordner und sind kaum manuell durchsuchbar — die
> Auto-Erkennung ist dort der praktikable Weg.

Alternativ manuell:

1. In MSFS **Developer Mode** an → Menü **Windows → Behaviors** bzw.
   **AI/Traffic**; oder (nur Steam-Version komfortabel) die `aircraft.cfg`
   ansehen, Eintrag `title = ...` unter `[FLTSIM.x]`.
2. Titel **exakt** (Groß/Klein, Leerzeichen) ins Feld eintragen.
3. Auf **„Test: Pop-up"** (bzw. **„Test: Mutterschiff"**) klicken. Erscheint
   nichts, steht im Log unten eine `SimConnect exception` (meist falscher
   Titel) → anderen Titel probieren.

> Idee für mehr Dynamik: Trage als Licht-Titel ein **kleines** Objekt und als
> Mutterschiff-Titel ein **großes** (z. B. einen Airliner) ein — so orientieren
> sich die Encounters nicht am eigenen Flugzeugtyp.

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
   │  ├─ ElectricalDisruptor.cs  # Blackout/Stutter/Deep-Blackout + garant. Restore
   │  └─ LightChoreographer.cs   # spawnt & animiert Licht-SimObjects (experimentell)
   ├─ Audio/
   │  ├─ AudioEngine.cs          # mehrschichtiger Mix (MediaPlayer, Echtzeit-Volume)
   │  └─ WavSynth.cs             # erzeugt Drone/Sub-Bass/Static/Whoosh als WAV
   └─ Util/Logbook.cs            # Sichtungs-CSV
```

## Roadmap

- **Lichter kalibrieren**: sinnvollen Default-SimObject-Titel + ggf. ein
  eigenes, lichtemittierendes Modell mitliefern; Lichtfarbe/Helligkeit.
- **Sichtungsbericht-Export** als formatiertes Markdown/PDF (geplant).
- Joystick-/Hotkey-Bindung für „Encounter jetzt" und Panik-Stop.
- Tag/Nacht-Logik (nachts Licht-lastig, tags Mutterschiff bevorzugen).
