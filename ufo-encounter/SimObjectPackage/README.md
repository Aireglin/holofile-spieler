# Licht-Paket (nur Lichter, ohne 3D-Modell)

Das Paket wird **direkt vom Programm erzeugt**. Du brauchst kein Blender und
keine Dateien aus diesem Ordner:

**Tab „Objekte & Lichter“ → „Licht-Paket erzeugen / aktualisieren“**

Das Programm findet den Community-Ordner von MSFS 2024 automatisch (über
`UserCfg.opt` → `InstalledPackagesPath`, Steam- und Store-Version). Wird er
nicht gefunden, fragt es nach dem Ordner. Danach **MSFS einmal neu starten**,
weil Pakete nur beim Start geladen werden.

## Was erzeugt wird

`Community/ufo-encounter-lights/`. Alle Objekte sind einfache SimObjects
(`category = SimpleObject`), Titel beginnen mit `UFOE_`:

Farben: Weiss, Rot, Gruen, Blau, Bernstein, Violett, Cyan.

| Titel | Inhalt |
|---|---|
| `UFOE_Licht_<Farbe>_Ruhig` | ruhiges Licht mit Lens-Flare, **kein Mesh** |
| `UFOE_Licht_<Farbe>_Glimmen` | glimmt ruhig, alle ~2 s ein heller, weißlicher Schimmer – geht nie aus |
| `UFOE_Licht_<Farbe>_Funkeln` | funkelt unregelmäßig wie ein heller Stern – geht nie aus |
| `UFOE_Licht_<Farbe>_Puls` / `_Blitz` | pulsiert bzw. blitzt (geht dazwischen ganz aus) |
| `UFOE_Kugel_<Farbe>` | kleine leuchtende Kugel (emissives Material) plus Licht – gut bei Tageslicht (Weiss, Rot, Gruen, Blau) |
| `UFOE_Kern_<Farbe>` | großer, sehr heller Kern (Kugel plus starkes Licht) – Hauptlicht für Trabanten und den Holy-Grail-Strahl |
| `UFOE_Funke_<Farbe>_Glimmen` / `_Funkeln` / `_Blitz` | kleine, schwache Funken – die Trabanten und der Funkenkranz im Lichtstrahl |
| `UFOE_Test_Macro_Weiss` | Diagnose: älteres Licht-Format (MSFS 2020) |
| `UFOE_Test_Versatz`, `UFOE_Test_Phase_Sek`, `UFOE_Test_Phase_Anteil`, `UFOE_Test_FlareOnly` | Diagnose für einen Nachttest (Knoten-Versatz, Einheit der Blink-Phase, Sichtbarkeit von Flare-only) |

Im Feld „Lichter / kleine UFOs“ genügt **ein Titel pro Farbe** (z. B.
`UFOE_Licht_Rot_Ruhig`). Mit „Lichtstile automatisch mischen“ wählt das
Programm daraus Hauptlicht, glimmende/funkelnde/blinkende Begleiter, Kerne und
Funken, und zwar nur Titel, die im installierten Paket stehen. Die Test-Titel
mischt es nie ein.

Technik: Jede Variante ist ein handgeschriebenes glTF. Die Lichter sind Knoten
mit der Asobo-Licht-Extension `ASOBO_street_light` (Schlüssel wie im
Asobo-Exporter). Glimmen, Funkeln, Kern und Funken setzen mehrere Lichter an
denselben Punkt: ein ruhiges Grundlicht plus weißliche `flare_only`-Lichter,
die mit zufälliger Phase und krummen Frequenzen aufblitzen, damit sie nie im
Takt laufen. `model.cfg` verweist auf eine `ModelInfo`/`LODS`-XML. Das ist
derselbe Aufbau wie bei Mesh-losen glTFs in ausgelieferten Paketen (z. B.
FlyByWire). `layout.json` und `manifest.json` schreibt das Programm selbst.
Alle glTF-Dateien bestehen den Khronos glTF-Validator ohne Fehler.

## Testen

1. Paket erzeugen, MSFS neu starten, in einen Flug gehen (nachts sieht man es am besten).
2. Bei der Nachfrage „Licht-Titel setzen?“ **Ja** wählen (ein Titel pro
   Farbe), oder Titel selbst eintragen.
3. Tab „Objekte & Lichter“ → **Muster testen** (für die Funken: Haken bei
   „mit Trabanten“).
4. Einmal **„▶ Diagnose (Test-Objekte)“** starten: Die fünf Test-Objekte
   erscheinen nacheinander je ca. 20 s voraus, das Protokoll sagt, worauf du
   achten sollst. Danach kurz melden, was sichtbar war. Damit lässt sich der
   Generator gezielt erweitern.
5. Erscheint gar nichts: Haken „Lichtstile automatisch mischen“ aus, dann
   einzeln `UFOE_Kugel_Weiss` eintragen und melden, ob eine Kugel, ein Punkt
   oder nichts zu sehen ist.

Unter „Licht-Paket“ zeigt das Programm, ob das installierte Paket schon die
neuen Lichtstile enthält. Steht dort „bitte neu erzeugen (ältere Version)“,
einfach erneut auf „Licht-Paket erzeugen / aktualisieren“ klicken und MSFS neu
starten. Bis dahin nimmt die Mischung nur die vorhandenen Titel.

Generierte Objekte werden automatisch als SimObject gespawnt. Alle anderen
Titel, etwa ein übernommenes Flugzeug, werden als Flugzeug gespawnt. Andere
einfache SimObjects, die keine Flugzeuge sind (z. B. ein Orb von flightsim.to),
mit `obj:` vor dem Titel eintragen, z. B. `obj:Mein Orb`.
