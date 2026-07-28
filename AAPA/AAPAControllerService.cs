using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;

namespace NINA.Plugins.AAPA {

    /// <summary>
    /// Manages the serial connection to the AAPA device (FYSETC E4 / ESP32).
    /// Handles auto-discovery, command sending, and status polling.
    ///
    /// AAPA Serial Protocol (115200 Baud, 8N1):
    ///   X[steps]     - Move azimuth axis by N steps (positive or negative)
    ///   Y[steps]     - Move altitude axis by N steps (positive or negative)
    ///   :STATUS      - Query device status (position, busy, limits, homed)
    ///   :STOP        - Emergency stop both axes
    ///   :HOMEY       - Home the Y (altitude) axis using StallGuard
    ///   :RESETY      - Reset Y position to 0 (manual home without stall)
    ///   :SAVE        - Persist configuration to NVS
    ///   :SPDX [n]    - Set X max speed (steps/s)
    ///   :SPDY [n]    - Set Y max speed (steps/s)
    ///   :ACCX [n]    - Set X acceleration (steps/s²)
    ///   :ACCY [n]    - Set Y acceleration (steps/s²)
    ///   ?            - Identification probe (used for auto-detection)
    /// </summary>
    public class AAPAControllerService : IDisposable {
        private SerialPort? _port;
        private readonly object _portLock = new object();
        private CancellationTokenSource? _pollCts;
        private Task? _pollTask;
        private bool _disposed;

        // ── Connection state ──────────────────────────────────────────────────
        public bool IsConnected { get; private set; }
        public string? ConnectedPortName { get; private set; }
        public AAPAStatus Status { get; private set; } = new AAPAStatus();

        // ── Events ────────────────────────────────────────────────────────────
        public event EventHandler<AAPAStatus>? StatusUpdated;
        public event EventHandler<bool>? ConnectionChanged;
        public event EventHandler<string>? LogMessage;

        // ── Regex to parse :STATUS response ──────────────────────────────────
        // Expected format (from firmware):
        // POSX:12345 POSY:-678 BUSYX:0 BUSYY:0 HOMED:1 OFFY:200 MINY:-100000 MAXY:100000 ...
        private static readonly Regex StatusRegex = new Regex(
            @"POSX:(?<x>-?\d+)\s+POSY:(?<y>-?\d+)\s+BUSYX:(?<busyx>\d)\s+BUSYY:(?<busyy>\d)\s+HOMED:(?<homed>\d).*?MINY:(?<miny>-?\d+)\s+MAXY:(?<maxy>-?\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Identification probe response — firmware responds to :STATUS with POSX/POSY
        private static readonly Regex IdRegex = new Regex(
            @"POSX:|BUSYX:|HOMED:",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Attempts to connect to an AAPA device on the specified COM port.
        /// If portName is null or empty, scans all available ports.
        /// </summary>
        public async Task<bool> ConnectAsync(string? portName = null, CancellationToken ct = default) {
            Disconnect();

            var ports = string.IsNullOrWhiteSpace(portName)
                ? SerialPort.GetPortNames()
                : new[] { portName };

            foreach (var port in ports) {
                ct.ThrowIfCancellationRequested();
                Log($"Scanning {port} for AAPA device...");

                var sp = new SerialPort(port, 115200, Parity.None, 8, StopBits.One) {
                    NewLine = "\n",
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    Encoding = Encoding.ASCII
                };

                try {
                    sp.Open();
                    // Wait for device to settle after opening
                    await Task.Delay(150, ct);
                    sp.DiscardInBuffer();

                    // Send identification probe
                    sp.WriteLine(":STATUS");
                    await Task.Delay(100, ct);

                    string response = string.Empty;
                    var deadline = DateTime.UtcNow.AddMilliseconds(600);
                    while (DateTime.UtcNow < deadline) {
                        try {
                            var line = sp.ReadLine().Trim();
                            response += line + " ";
                            if (IdRegex.IsMatch(response)) break;
                        } catch (TimeoutException) {
                            break;
                        }
                    }

                    if (IdRegex.IsMatch(response)) {
                        Log($"AAPA device found on {port}");
                        lock (_portLock) { _port = sp; }
                        ConnectedPortName = port;
                        IsConnected = true;

                        // Initial status read (populates limits etc.) before firing connection event
                        await QueryStatusAsync();

                        ConnectionChanged?.Invoke(this, true);

                        // Start status polling
                        _pollCts = new CancellationTokenSource();
                        _pollTask = PollStatusLoopAsync(_pollCts.Token);

                        return true;
                    }
                } catch (Exception ex) {
                    Log($"Error on {port}: {ex.Message}");
                } finally {
                    if (!IsConnected && sp.IsOpen) {
                        try { sp.Close(); } catch { }
                        sp.Dispose();
                    }
                }
            }

            Log("No AAPA device found.");
            return false;
        }

        /// <summary>Disconnects from the AAPA device.</summary>
        public void Disconnect() {
            if (IsConnected) {
                // Auto-save on disconnect/shutdown
                lock (_portLock) {
                    if (_port != null && _port.IsOpen) {
                        try {
                            _port.WriteLine(":SAVE");
                            Thread.Sleep(100); // Allow time for UART buffer to flush
                        } catch { }
                    }
                }
            }

            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
            _pollTask = null;

            lock (_portLock) {
                if (_port != null) {
                    try { if (_port.IsOpen) _port.Close(); } catch { }
                    _port.Dispose();
                    _port = null;
                }
            }

            IsConnected = false;
            ConnectedPortName = null;
            ConnectionChanged?.Invoke(this, false);
            Log("Disconnected.");
        }

        /// <summary>
        /// Moves the azimuth (X) axis by the given number of steps.
        /// Positive = one direction, negative = other direction.
        /// </summary>
        public async Task<bool> MoveAzimuthAsync(int steps, CancellationToken ct = default) {
            if (steps == 0) return true;
            return await SendCommandAsync($"X{steps}", ct);
        }

        /// <summary>
        /// Moves the altitude (Y) axis by the given number of steps.
        /// </summary>
        public async Task<bool> MoveAltitudeAsync(int steps, CancellationToken ct = default) {
            if (steps == 0) return true;
            return await SendCommandAsync($"Y{steps}", ct);
        }

        /// <summary>
        /// Moves both axes simultaneously.
        /// If steps == 0 the axis is not moved.
        /// </summary>
        public async Task<bool> MoveBothAxesAsync(int azSteps, int altSteps, CancellationToken ct = default) {
            bool ok = true;
            if (azSteps != 0)  ok &= await MoveAzimuthAsync(azSteps, ct);
            if (altSteps != 0) ok &= await MoveAltitudeAsync(altSteps, ct);
            return ok;
        }

        /// <summary>Stops both axes immediately.</summary>
        public async Task<bool> StopAsync(CancellationToken ct = default) =>
            await SendCommandAsync(":STOP", ct);

        /// <summary>Moves the Y (altitude) axis to the 0 position based on the current coordinates.</summary>
        public async Task<bool> HomeAltitudeAsync(CancellationToken ct = default) {
            long distance = -Status.PositionY;
            if (distance == 0) return true;
            return await MoveAltitudeAsync((int)distance, ct);
        }

        /// <summary>Moves the X (azimuth) axis to the 0 position based on the current coordinates.</summary>
        public async Task<bool> HomeAzimuthAsync(CancellationToken ct = default) {
            long distance = -Status.PositionX;
            if (distance == 0) return true;
            return await MoveAzimuthAsync((int)distance, ct);
        }

        /// <summary>Resets Y position to 0 (manual home without stall detection).</summary>
        public async Task<bool> ResetYAsync(CancellationToken ct = default) {
            bool ok = await SendCommandAsync(":RESETY", ct);
            if (ok) {
                Status.IsHomed = true;
                Status.PositionY = 0;
            }
            return ok;
        }

        /// <summary>Resets X position to 0.</summary>
        public async Task<bool> ResetXAsync(CancellationToken ct = default) {
            bool ok = await SendCommandAsync(":RESETX", ct);
            if (ok) {
                Status.PositionX = 0;
            }
            return ok;
        }

        /// <summary>Saves current configuration to NVS flash.</summary>
        public async Task<bool> SaveConfigAsync(CancellationToken ct = default) =>
            await SendCommandAsync(":SAVE", ct);

        /// <summary>Sets maximum speed for azimuth axis in steps/s.</summary>
        public async Task<bool> SetAzimuthSpeedAsync(int stepsPerSec, CancellationToken ct = default) =>
            await SendCommandAsync($":SPDX {stepsPerSec}", ct);

        /// <summary>Sets maximum speed for altitude axis in steps/s.</summary>
        public async Task<bool> SetAltitudeSpeedAsync(int stepsPerSec, CancellationToken ct = default) =>
            await SendCommandAsync($":SPDY {stepsPerSec}", ct);

        /// <summary>Sets acceleration for azimuth axis in steps/s².</summary>
        public async Task<bool> SetAzimuthAccelAsync(int stepsPerSecSq, CancellationToken ct = default) =>
            await SendCommandAsync($":ACCX {stepsPerSecSq}", ct);

        /// <summary>Sets acceleration for altitude axis in steps/s².</summary>
        public async Task<bool> SetAltitudeAccelAsync(int stepsPerSecSq, CancellationToken ct = default) =>
            await SendCommandAsync($":ACCY {stepsPerSecSq}", ct);

        /// <summary>Sets minimum soft limit for altitude (Y) axis.</summary>
        public async Task<bool> SetMinYAsync(long limit, CancellationToken ct = default) =>
            await SendCommandAsync($":MINY {limit}", ct);

        /// <summary>Sets maximum soft limit for altitude (Y) axis.</summary>
        public async Task<bool> SetMaxYAsync(long limit, CancellationToken ct = default) =>
            await SendCommandAsync($":MAXY {limit}", ct);

        /// <summary>Queries the device status and updates the Status property.</summary>
        public async Task<bool> QueryStatusAsync(CancellationToken ct = default) =>
            await SendCommandAsync(":STATUS", ct);

        // ── Private helpers ───────────────────────────────────────────────────

        private async Task<bool> SendCommandAsync(string command, CancellationToken ct) {
            if (!IsConnected) return false;

            return await Task.Run(() => {
                lock (_portLock) {
                    if (_port == null || !_port.IsOpen) return false;
                    try {
                        _port.WriteLine(command);

                        // Read response lines until timeout or we get a useful response
                        var deadline = DateTime.UtcNow.AddMilliseconds(800);
                        var responseLines = new List<string>();

                        while (DateTime.UtcNow < deadline) {
                            ct.ThrowIfCancellationRequested();
                            try {
                                var line = _port.ReadLine().Trim();
                                if (!string.IsNullOrWhiteSpace(line)) {
                                    responseLines.Add(line);
                                    // Parse status if this looks like a STATUS response
                                    if (TryParseStatus(line, out var status)) {
                                        Status = status;
                                        StatusUpdated?.Invoke(this, status);
                                    }
                                }
                            } catch (TimeoutException) {
                                break;
                            }
                        }
                        return true;
                    } catch (Exception ex) {
                        Log($"Command '{command}' failed: {ex.Message}");
                        if (ex is InvalidOperationException || ex is System.IO.IOException) {
                            // Port likely disconnected
                            IsConnected = false;
                            ConnectionChanged?.Invoke(this, false);
                        }
                        return false;
                    }
                }
            }, ct);
        }

        private static bool TryParseStatus(string line, out AAPAStatus status) {
            status = new AAPAStatus();
            var m = StatusRegex.Match(line);
            if (!m.Success) return false;

            status.PositionX = long.Parse(m.Groups["x"].Value);
            status.PositionY = long.Parse(m.Groups["y"].Value);
            status.IsBusy    = m.Groups["busyx"].Value == "1" || m.Groups["busyy"].Value == "1";
            status.IsHomed   = m.Groups["homed"].Value == "1";
            if (m.Groups["miny"].Success) status.MinY = long.Parse(m.Groups["miny"].Value);
            if (m.Groups["maxy"].Success) status.MaxY = long.Parse(m.Groups["maxy"].Value);
            return true;
        }

        private async Task PollStatusLoopAsync(CancellationToken ct) {
            while (!ct.IsCancellationRequested) {
                try {
                    await Task.Delay(1000, ct);
                    if (IsConnected) {
                        await QueryStatusAsync(ct);
                    }
                } catch (OperationCanceledException) {
                    break;
                } catch (Exception ex) {
                    Log($"Status poll error: {ex.Message}");
                }
            }
        }

        private void Log(string message) {
            Logger.Info($"[AAPA] {message}");
            LogMessage?.Invoke(this, message);
        }

        public void Dispose() {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
        }
    }
}
