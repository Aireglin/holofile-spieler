# Licht-only SimObject (Plan B) — Gerüst

Ziel: ein **eigenes, licht-only SimObject** (`UFO_Light`), das nur einen
Leuchtpunkt zeigt – **kein Flugzeugmodell**. Damit zeigen die kleinen
Encounter-Lichter nur Licht, auch bei Tag.

> **Status: Gerüst / Work in Progress.** Die Config-Dateien stehen; der **eine
> noch fehlende Baustein ist das 3D-Modell** (`UFO_Light.gltf`). Ohne den Sim
> zum Testen lässt sich das Modell nicht blind fertig erzeugen – das machen wir
> gemeinsam im **Dev Mode** (du lädst, ich reagiere auf die Konsolen-Meldungen).

## Ordnerstruktur

```
ufo-light/
├─ manifest.json
├─ layout.json                      ← per build-layout.ps1 erzeugen
└─ SimObjects/Misc/UFO_Light/
   ├─ sim.cfg                       ← title = UFO_Light, category = SimpleObject
   └─ model/
      ├─ model.cfg                  ← verweist auf UFO_Light.gltf
      └─ UFO_Light.gltf  (+ .bin)   ← FEHLT NOCH (das Modell)
```

## Der fehlende Baustein: das Modell

Drei Wege, das `UFO_Light.gltf` zu bekommen — vom einfachsten zum saubersten:

1. **Fertiges Objekt nutzen (empfohlen für den Start):** Ein „orb/light/UFO"
   SimObject von flightsim.to installieren und im Tool einfach dessen **Titel**
   eintragen. Dann braucht es dieses Paket gar nicht.
2. **Blender + Asobo glTF-Exporter:** ein winziges (z. B. 5 cm), transparentes
   Mesh mit **emissivem Material** (hell) bauen und als `UFO_Light.gltf`
   exportieren. Optional eine MSFS-Lichtquelle/Effekt an einen Node hängen.
3. **Aus einem bestehenden Licht-Objekt ableiten:** Modell + `model.cfg` eines
   vorhandenen Licht-SimObjects übernehmen und Titel/Pfade anpassen.

## Installieren & testen (Steam, Dev Mode)

1. `build-layout.ps1` ausführen, um `layout.json` zu erzeugen:
   ```powershell
   cd ufo-encounter/SimObjectPackage
   .\build-layout.ps1 -PackageDir .\ufo-light
   ```
2. Den Ordner `ufo-light` in deinen **Community-Ordner** kopieren.
3. MSFS starten (oder im **Dev Mode**: *Tools → Virtual File System →
   Watch Bases / Reload*), damit das Paket geladen wird.
4. Im Tool als **Licht-Titel** `UFO_Light` eintragen, „Objekte sind Flugzeuge"
   **aus**, und **Test: Pop-up** drücken.
5. Erscheint nichts oder ein Fehler: die **Dev-Mode-Konsole** (Fehler/Warnungen)
   kopieren – damit justieren wir `sim.cfg`/`model.cfg`/Modell.

## Was ich von dir brauche, um es fertigzustellen

- Den **Community-Ordner-Pfad** (aus `UserCfg.opt` → `InstalledPackagesPath`).
- Beim ersten Laden die **Konsolen-Ausgabe** aus dem Dev Mode.
- Kurz, **was sichtbar ist** (nichts / Punkt / falsche Größe/Farbe).
