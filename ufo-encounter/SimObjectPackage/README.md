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

| Titel | Inhalt |
|---|---|
| `UFOE_Licht_<Farbe>_Ruhig` / `_Puls` / `_Blitz` | nur eine Lichtquelle mit Lens-Flare, **kein Mesh** – 7 Farben (Weiss, Rot, Gruen, Blau, Bernstein, Violett, Cyan) |
| `UFOE_Kugel_<Farbe>` | kleine leuchtende Kugel (emissives Material) plus Licht – gut bei Tageslicht |
| `UFOE_Test_Macro_Weiss` | Diagnose: älteres Licht-Format (MSFS 2020) |

Technik: Jede Variante ist ein handgeschriebenes glTF mit einem Knoten, der die
Asobo-Licht-Extension `ASOBO_street_light` trägt (Schlüssel wie im
Asobo-Exporter). `model.cfg` verweist auf eine `ModelInfo`/`LODS`-XML. Das
ist derselbe Aufbau wie bei Mesh-losen glTFs in ausgelieferten Paketen
(z. B. FlyByWire). `layout.json` und `manifest.json` schreibt das Programm
selbst. Alle glTF-Dateien bestehen den Khronos glTF-Validator ohne Fehler.

## Testen

1. Paket erzeugen, MSFS neu starten, in einen Flug gehen (nachts sieht man es am besten).
2. Bei der Nachfrage „Licht-Titel setzen?“ **Ja** wählen, oder Titel selbst eintragen.
3. Tab „Objekte & Lichter“ → **Muster testen**.
4. Erscheint nichts: einzeln `UFOE_Kugel_Weiss` und `UFOE_Test_Macro_Weiss`
   probieren und kurz melden, was sichtbar ist (Punkt / Kugel / nichts).
   Danach lässt sich der Generator gezielt anpassen.

Generierte Objekte werden automatisch als SimObject gespawnt. Alle anderen
Titel, etwa ein übernommenes Flugzeug, werden als Flugzeug gespawnt. Andere
einfache SimObjects, die keine Flugzeuge sind (z. B. ein Orb von flightsim.to),
mit `obj:` vor dem Titel eintragen, z. B. `obj:Mein Orb`.
