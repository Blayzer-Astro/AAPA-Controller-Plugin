# NINA AAPA Plugin

Ein NINA-Plugin das das **AAPA** (Astrophilos Automated Polar Alignment) Gerät mit dem **TPPA** (Three Point Polar Alignment) Plugin verbindet.

## Übersicht

| | |
|--|--|
| **Hardware** | AAPA — FYSETC E4 (ESP32) mit 2 Schrittmotoren (Azimuth + Altitude) |
| **Software** | NINA 3.x + TPPA Plugin von isbeorn |
| **Kommunikation** | Seriell (USB/COM), 115200 Baud |

Das Plugin ersetzt das separate `platedual.py` / `platedual.exe` Tool und integriert die AAPA-Steuerung direkt in NINA.

## Funktionen

- 🔌 **Auto-Verbindung** — Erkennt das AAPA automatisch auf allen COM-Ports
- 📡 **TPPA-Integration** — Liest Polausrichtungsfehler aus NINA-Logs in Echtzeit
- 🤖 **Auto-Pilot** — Berechnet und sendet automatisch Korrekturbewegungen bis zur Toleranz
- 🕹️ **Manuelle Steuerung** — Azimuth/Altitude per Klick in konfigurierbaren Schritten
- 🏠 **Homing** — StallGuard-Homing der Altitude-Achse (Y)
- 📋 **Sequence Instruction** — „AAPA Auto-Align" für vollautomatische Sequenzen
- ⚙️ **Konfigurierbar** — Getriebe-Ratio, Mikroschritte, Backlash, Speed/Accel

## Installation

### Voraussetzungen
- NINA 3.0+
- TPPA Plugin (von isbeorn, aus dem NINA Plugin-Manager)
- .NET 8 SDK (zum Kompilieren)
- Visual Studio 2022 oder `dotnet build`

### Kompilieren & Installieren

```powershell
cd nina.plugin.aapa

# Debug-Build
dotnet build

# Release-Build (kopiert DLL automatisch nach %LOCALAPPDATA%\NINA\Plugins\)
dotnet build -c Release
```

NINA starten → Plugins → AAPA Controller erscheint in der Plugin-Liste.

### Manuell
Die `NINA.Plugin.AAPA.dll` in folgendes Verzeichnis kopieren:
```
%LOCALAPPDATA%\NINA\Plugins\NINA.Plugin.AAPA\
```

## Verwendung

### 1. Verbindung herstellen

Im Dockable-Panel:
1. COM-Port auswählen (oder „Auto" für automatische Erkennung)
2. **Connect** klicken
3. LED grün → verbunden

### 2. Polausrichtung mit Auto-Pilot

1. TPPA-Messung starten (in TPPA Plugin „Measure" klicken)
2. Im AAPA-Panel: **▶ Start Auto-Pilot** klicken
3. Der Auto-Pilot liest die TPPA-Fehler und sendet Korrekturen bis zur Toleranz

### 3. Automatisierung (Sequence)

1. Sequence Instruction „**AAPA Auto-Align**" nach einem TPPA-Schritt einfügen
2. Toleranz, Max. Iterationen und Settle-Zeit konfigurieren
3. Sequenz starten → vollautomatisch!

## Konfiguration

| Parameter | Standard | Beschreibung |
|-----------|---------|--------------|
| Steps/Rev | 200 | Vollschritte pro Motorumdrehung (NEMA 17 = 200) |
| Microsteps | 16 | Mikroschrittmodus (wie in AAPA-Firmware eingestellt) |
| Gear Ratio | 1.0 | Getriebeübersetzung Motor → Teleskop-Achse |
| Tolerance | 0.01° | Auto-Pilot stoppt wenn Fehler unter diesem Wert |
| Settle Time | 3 s | Wartezeit nach jeder Bewegung vor nächster Messung |
| Max Iterations | 20 | Maximale Korrekturschritte (0 = unbegrenzt) |
| Max Correction | 0.5° | Maximale Korrektur pro Iteration |

## AAPA Serielles Protokoll

| Befehl | Funktion |
|--------|---------|
| `X<n>` | Azimuth N Schritte bewegen |
| `Y<n>` | Altitude N Schritte bewegen |
| `:STATUS` | Status abfragen (Pos, Busy, Homed) |
| `:STOP` | Notfall-Stop |
| `:HOMEY` | Y-Achse homen (StallGuard) |
| `:SPDX <n>` | Azimuth Geschwindigkeit (step/s) |
| `:SPDY <n>` | Altitude Geschwindigkeit (step/s) |
| `:ACCX <n>` | Azimuth Beschleunigung (step/s²) |
| `:ACCY <n>` | Altitude Beschleunigung (step/s²) |
| `:SAVE` | Konfiguration in Flash speichern |

## Schrittberechnung

```
steps = round((grad / 360) × steps_per_rev × microsteps × gear_ratio)
```

Identisch mit der Formel in `platedual.py`.

## Projektstruktur

```
nina.plugin.aapa/
├── AAPAPlugin.cs                          # Plugin-Manifest (MEF Export)
├── NINA.Plugin.AAPA.csproj
│
├── AAPA/
│   ├── AAPAControllerService.cs           # Serielle Kommunikation + Auto-Discovery
│   └── AAPAStatus.cs                      # Status-Datenmodell
│
├── Alignment/
│   ├── TPPALogMonitor.cs                  # TPPA-Log Dateiüberwachung
│   ├── AlignmentEngine.cs                 # Fehler → Schritte Berechnung
│   └── AutoPilotController.cs             # Automatische Korrekturschleife
│
├── Dockables/
│   ├── AAPADockable.cs                    # IDockableVM MEF Export
│   ├── AAPADockableVM.cs                  # ViewModel
│   ├── AAPADockableTemplates.xaml         # UI
│   └── AAPADockableTemplates.xaml.cs
│
├── Instructions/
│   ├── AAPAAlignInstruction.cs            # Sequence Instruction
│   ├── AAPAInstructionTemplates.xaml      # Instruction UI
│   └── AAPAInstructionTemplates.xaml.cs
│
└── Properties/
    ├── AssemblyInfo.cs
    ├── Settings.Designer.cs               # Typisierte Settings
    └── Settings.settings
```

## Lizenz

MIT — Basierend auf dem NINA Plugin Template von isbeorn.

## Links

- [AAPA Hardware](https://astrophiloslab.com/aapa)
- [AAPA GitHub](https://github.com/AstrophilosLab/AAPA)
- [TPPA Plugin](https://github.com/isbeorn/nina.plugin.polaralignment)
- [NINA Plugin Template](https://github.com/isbeorn/nina.plugin.template)
