# NINA AAPA Controller Plugin

A NINA plugin that connects the **AAPA** (Astrophilos Automated Polar Alignment) device to the **TPPA** (Three Point Polar Alignment) plugin.

## Overview

| | |
|--|--|
| **Hardware** | AAPA — FYSETC E4 (ESP32) with 2 stepper motors (Azimuth + Altitude) |
| **Software** | Tested with: NINA 3.2 + TPPA 2.2.7.0 |
| **Communication** | Serial (USB/COM) |

This plugin replaces the standalone `platedual.py` / `platedual.exe` tool and integrates AAPA control directly into NINA.

## Features

- 🔌 **Auto-Connection** — Automatically detects the AAPA on all COM ports
- 📡 **TPPA Integration** — Reads polar alignment errors from NINA logs in real-time
- 🤖 **Auto-Pilot** — Calculates and automatically sends correction movements until within tolerance
- 🕹️ **Manual Control** — Azimuth/Altitude click controls with configurable step sizes
- 🏠 **Homing** — Setting home position and returning to home
- 📋 **Sequence Instruction** — "AAPA Auto-Align" for fully automated sequences
- ⚙️ **Configurable** — Gear ratio, microstepping, backlash, speed/accel

## Requirements

1. AAPA with custom firmware 
2. NINA
3. TPPA plugin
4. AAPA Controller Plugin

## Usage

**Enable "Log polar alignment error adjustments?" in the TPPA settings**

### 1. Connecting

In the dockable panel:
1. Select the COM port (or "Auto" for automatic detection)
2. Click **Connect**

### 2. Calibration

1. Start TPPA
2. In the AAPA panel: click **▶ Calibrate Azimuth (X)** 
3. Wait until the value is calculated
3. Repeat the same for the Altitude (Y) axis

### 3. Polar Alignment with Auto-Pilot

1. Start TPPA
2. In the AAPA panel: click **▶ Start Auto-Pilot**
3. The Auto-Pilot reads the TPPA errors and sends corrections until within tolerance

### 4. Automation (Sequence)

1. Add the sequence instruction "**AAPA Auto-Align**" after a TPPA step
2. Configure tolerance, max iterations, and settle time
3. Start the sequence → fully automated!

## Configuration

| Parameter | Default | Description |
|-----------|---------|--------------|
| Steps/Rev | 200 | Full steps per motor revolution (NEMA 17 = 200) |
| Microsteps | 16 | Microstep mode (as configured in the AAPA firmware) |
| Gear Ratio | 1.0 | Gear ratio from motor to telescope axis |
| Tolerance | 0.01° | Auto-Pilot stops when the error is below this value |
| Settle Time | 3 s | Wait time after each movement before the next measurement |
| Max Iterations | 20 | Maximum correction steps (0 = unlimited) |
| Max Correction | 0.5° | Maximum correction per iteration |

You may need to edit these settings.

## Step Calculation

```
steps = round((degrees / 360) × steps_per_rev × microsteps × gear_ratio)
```

Identical to the formula in `platedual.py`.


## License

MIT — Based on the NINA Plugin Template by isbeorn.

## Links

- [AAPA Hardware](https://astrophiloslab.com/aapa)
- [AAPA GitHub](https://github.com/AstrophilosLab/AAPA)
- [NINA](https://github.com/isbeorn/nina)
- [TPPA Plugin](https://github.com/isbeorn/nina.plugin.polaralignment)
- [NINA Plugin Template](https://github.com/isbeorn/nina.plugin.template)
