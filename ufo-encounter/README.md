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
- **Spezial**: **Holy Grail** (Tempus Fugit: mit Titeln aus dem Licht-Paket
  ein heller Kern über dem Flugzeug, um den ein Kranz kleiner Funken kreist, in
  totaler Stille; ab und zu taucht ein Funke Richtung Cockpit ab) und **Jet-Verfolgung** (UFO schießt
  davon, ein Kampfjet überholt dich knapp; Standard-Jet ist die mitgelieferte
  F/A-18, Titel `FA18E SuperHornet`).
- **Instrumentenausfall**: Flackern (unregelmäßig), Flackern → Blackout und
  Total-Blackout. Per Auswahlfeld für **Kolben-Flugzeuge** (Batterie,
  Generatoren 1–4, Avionik; der Motor bleibt wahlweise an) oder für
  **Jets/Turbinen** (Komplett-Abschaltung). Strom und Motor werden **immer**
  wiederhergestellt: nach jedem Ausfall, bei PANIK-STOP, beim Schließen des
  Programms und nach einem Verbindungsabriss.
- **Licht-Paket ohne 3D-Modell**: Ein Klick erzeugt ein Community-Paket mit
  reinen Lichtobjekten (7 Farben × ruhig / glimmend / funkelnd / pulsierend /
  blitzend), leuchtenden Kugeln für den Tag, hellen **Kernen** und kleinen
  **Funken**. Glimmen und Funkeln gehen nie ganz aus. Details:
  [`SimObjectPackage/README.md`](SimObjectPackage/README.md).
- **Lichtstile automatisch mischen**: Pro Farbe genügt ein Titel. Das
  Programm wählt je Begegnung eine Hauptfarbe, ein ruhiges Hauptlicht und
  Begleiter, von denen nur ein Teil blinkt (Regler **Blink-Anteil**, meist
  sanft glimmend oder funkelnd statt hartem An/Aus). Es nimmt nur Titel, die im
  installierten Paket stehen.
- **Trabanten**: Manchmal (Regler **Trabanten-Häufigkeit**) kreisen 3–6
  kleine Funken auf schrägen Bahnen um ein großes Licht. Sie lösen sich beim
  Erscheinen aus ihm und fliegen am Ende mit ihm davon.
- **Sound**: tiefes Summen und pulsierender Sub-Bass, nahtlos geloopt. Die
  Lautstärke richtet sich nach dem Abstand. Der Jumpscare beim Holy Grail ist
  ein eingebauter Clip, wenn beim Bauen `src/Assets/jumpscare.mp3` vorhanden
  war (sonst ein erzeugter Klang). Eine eigene `jumpscare.mp3` (oder `.wav`,
  `.ogg`) neben der .exe hat Vorrang.
- **Zufallsmodus**: würfelt pro Begegnung Szenario, Anzahl, Muster, Abstand,
  Tempo, Dread und Blink-Anteil. Die eigenen Regler-Werte bleiben dabei
  unverändert. Dazu gibt es Tag/Nacht-Logik, den Realismus-Lock (nicht am
  Boden oder in zu geringer Höhe) und Voreinstellungen (Sanft / Standard /
  Maximum).
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
| **Objekte & Lichter** | Objekt-Titel (mit „Übernehmen“), Licht-Paket, Anzahl/Abstand/Tempo, Blink-Anteil, Trabanten, automatisches Mischen, **Muster testen** (alle Muster in einer Liste, wahlweise mit Trabanten), **Diagnose**, Lichtstrahl |
| **Ausfälle & Sound** | Flugzeugtyp/Ausfall-Art, Wahrscheinlichkeit, Blackout-Dauer, Test-Buttons, Sound |
| **Einstellungen** | Ablauf, Dread, Min-Höhe, Seed, „immer im Vordergrund“, Datenordner |

**Objekt-Titel**: Für das Licht-Paket reicht ein Titel pro gewünschter Farbe,
z. B. `UFOE_Licht_Rot_Ruhig, UFOE_Licht_Blau_Ruhig`. Nach dem Erzeugen des
Pakets trägt „Ja“ alle sieben Farben so ein. Mit **„Lichtstile automatisch
mischen“** (Standard) bestimmt der Titel nur die Farbe; Stil, Kern und Funken
wählt das Programm. Ist der Haken aus, erscheinen die Titel genau wie
eingetragen (zufällig gemischt, ohne Trabanten). Für ein Flugzeug: in MSFS
laden und **„Übernehmen“** klicken, dann steht der exakte Titel im Feld.
Mehrere Titel mit Komma ergeben einen Zufallsmix; Flugzeuge und Paket-Titel
lassen sich mischen. Titel aus dem Licht-Paket (`UFOE_…`) werden automatisch
als einfaches SimObject gespawnt, alle anderen als Flugzeug. Ein fremdes
einfaches SimObject, das kein Flugzeug ist, bekommt `obj:` vor den Titel
(z. B. `obj:Mein Orb`).

**Blink-Anteil**: wie viele Lichter neben dem ruhigen Hauptlicht blinken
(Standard 30 %). Die meisten davon glimmen oder funkeln nur, ein Drittel
pulsiert oder blitzt richtig. Bei Tic-Tac-Sprüngen halbiert sich der Anteil,
im Zufallsmodus wird er je Begegnung neu gewürfelt (meist eher ruhig).

**Muster testen**: Während ein Test läuft, wirken die Regler für Tempo und
Mindestabstand sofort. Mit **„mit Trabanten“** zeigt der Test ein großes Licht
mit kreisenden Funken. Die Elektrik-Tests laufen nur ohne laufende Begegnung,
und eine Begegnung wartet, bis ein Elektrik-Test vorbei ist.

**Diagnose (Test-Objekte)**: zeigt die fünf Test-Objekte des Licht-Pakets
nacheinander je ca. 20 s vor dem Flugzeug (am besten nachts, in der Luft).
Das Protokoll sagt jeweils, worauf du achten sollst. Danach bitte kurz melden,
was zu sehen war; damit lassen sich weitere Lichteffekte gezielt bauen.
PANIK-STOP bricht die Diagnose ab.

## Voraussetzungen

- Windows 10/11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (x64, das
  **SDK**, nicht nur die Runtime)
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

Jumpscare: Liegt beim Bauen `src/Assets/jumpscare.mp3` vor, wird der Clip in
die .exe eingebaut. Die Datei ist nicht im Repository; ohne sie baut das
Projekt trotzdem und nimmt den erzeugten Klang.

**Fehler „No .NET SDKs were found“**: Das .NET 8 **SDK** (x64) installieren,
nicht nur die Runtime, danach ein **neues** CMD-Fenster öffnen. Zeigt
`where dotnet` zuerst einen Pfad unter `Program Files (x86)`, die x64-Version
direkt aufrufen: `"C:\Program Files\dotnet\dotnet.exe" publish …`.

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
   ├─ Assets/jumpscare.mp3       # optional, nicht im Repository (wird eingebaut)
   ├─ Encounters/
   │  ├─ EncounterScenario.cs    # Szenario-Katalog
   │  ├─ EncounterDirector.cs    # Dirigent: Ketten, Zufallsmodus, Spezial, Diagnose, Audio-Mix
   │  ├─ ElectricalDisruptor.cs  # Flackern/Blackout + garantierter Restore
   │  ├─ LightChoreographer.cs   # Bewegungsmuster im Sim-Frame-Takt, Trabanten
   │  └─ LightMix.cs             # Lichtstile je Rolle: Hauptlicht, Begleiter, Funken
   ├─ Audio/
   │  ├─ AudioEngine.cs          # mehrschichtiger Mix (MediaPlayer)
   │  └─ WavSynth.cs             # erzeugt Summen/Sub-Bass/Ersatz-Jumpscare als WAV
   └─ Util/
      ├─ LightPackageGenerator.cs# erzeugt das Licht-Paket (glTF ohne Mesh)
      ├─ GlobalHotkeys.cs        # globale Tastenkürzel
      ├─ AppPaths.cs, SettingsStore.cs, Logbook.cs
```
