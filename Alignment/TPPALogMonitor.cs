using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;

namespace NINA.Plugins.AAPA.Alignment {

    /// <summary>
    /// Monitors the NINA / TPPA log files for polar alignment error values.
    ///
    /// TPPA writes alignment errors to the NINA log in lines that look like:
    ///   AltError: -0.0500°  AzError: 0.0300°
    ///   Altitude Error: -0.0500  Azimuth Error: 0.0300
    ///
    /// This class uses a FileSystemWatcher to detect log file changes and
    /// exposes the most recently seen azimuth and altitude error via events.
    /// </summary>
    public class TPPALogMonitor : IDisposable {
        private FileSystemWatcher? _watcher;
        private string? _watchedFilePath;
        private long _lastFilePosition;
        private bool _disposed;

        // Log directory: %LOCALAPPDATA%\NINA\Logs\
        private static readonly string NINALogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "Logs");

        // Patterns that TPPA uses when logging alignment errors
        // Pattern 1:  "AltError: -0.0500°  AzError: 0.0300°"
        // Pattern 2:  "Altitude Error: -0.0500  Azimuth Error: 0.0300"
        // Pattern 3:  "[PolarAlignment] Alt: -0.0500 Az: 0.0300"
        private static readonly Regex[] ErrorPatterns = {
            new Regex(@"AltError[:\s]+(?<alt>-?\d+\.?\d*)[°\s]*AzError[:\s]+(?<az>-?\d+\.?\d*)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"Altitude\s+Error[:\s]+(?<alt>-?\d+\.?\d*)\s+Azimuth\s+Error[:\s]+(?<az>-?\d+\.?\d*)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            new Regex(@"Alt[:\s]+(?<alt>-?\d+\.?\d*)[°]?\s+Az[:\s]+(?<az>-?\d+\.?\d*)[°]?",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // Pattern for DMS: Az: 03° 32' 51", Alt: -00° 20' 00"
            new Regex(@"Az:\s+(?<az_sign>[-+]?)(?<az_deg>\d+)°\s+(?<az_min>\d+)'\s+(?<az_sec>\d+(?:\.\d+)?)"".*?Alt:\s+(?<alt_sign>[-+]?)(?<alt_deg>\d+)°\s+(?<alt_min>\d+)'\s+(?<alt_sec>\d+(?:\.\d+)?)""",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
            // Fallback: look for any pair of signed decimals after common keywords
            new Regex(@"(?:polar|alignment|error|tppa).*?(?<alt>-?\d+\.\d+)[°\s,]*(?<az>-?\d+\.\d+)",
                RegexOptions.IgnoreCase | RegexOptions.Compiled),
        };

        /// <summary>Raised when a new polar alignment error is read from the TPPA log.</summary>
        public event EventHandler<PolarAlignmentError>? ErrorDetected;

        /// <summary>Raised for informational log messages.</summary>
        public event EventHandler<string>? LogMessage;

        /// <summary>The last detected polar alignment error.</summary>
        public PolarAlignmentError? LastError { get; private set; }

        /// <summary>
        /// Starts monitoring the NINA log directory.
        /// Automatically picks the most recently modified .log file.
        /// </summary>
        public void Start() {
            if (!Directory.Exists(NINALogDirectory)) {
                Log($"NINA log directory not found: {NINALogDirectory}");
                return;
            }

            // Find the most recent log file
            var logFiles = Directory.GetFiles(NINALogDirectory, "*.log");
            if (logFiles.Length == 0) {
                Log("No NINA log files found. Start NINA first.");
            } else {
                Array.Sort(logFiles, (a, b) =>
                    File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
                SetWatchedFile(logFiles[0]);
            }

            // Watch for new log files appearing (e.g., NINA restart)
            _watcher = new FileSystemWatcher(NINALogDirectory, "*.log") {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileChanged;
            _watcher.Created += OnFileCreated;
            Log("TPPA log monitor started.");
        }

        /// <summary>Stops monitoring.</summary>
        public void Stop() {
            if (_watcher != null) {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
            Log("TPPA log monitor stopped.");
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private void SetWatchedFile(string filePath) {
            _watchedFilePath = filePath;
            // Jump to end of file — we only want new lines from now on
            try {
                _lastFilePosition = new FileInfo(filePath).Length;
            } catch {
                _lastFilePosition = 0;
            }
            Log($"Watching log file: {Path.GetFileName(filePath)}");
        }

        private void OnFileCreated(object sender, FileSystemEventArgs e) {
            // A new NINA session started — switch to the new log file
            SetWatchedFile(e.FullPath);
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e) {
            if (_watchedFilePath == null) {
                SetWatchedFile(e.FullPath);
            }
            if (!string.Equals(e.FullPath, _watchedFilePath, StringComparison.OrdinalIgnoreCase)) return;

            try {
                ReadNewLines();
            } catch (Exception ex) {
                Log($"Error reading log: {ex.Message}");
            }
        }

        private void ReadNewLines() {
            if (_watchedFilePath == null) return;

            using var fs = new FileStream(_watchedFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            fs.Seek(_lastFilePosition, SeekOrigin.Begin);

            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) != null) {
                TryParseErrorLine(line);
            }

            _lastFilePosition = fs.Position;
        }

        private void TryParseErrorLine(string line) {
            foreach (var pattern in ErrorPatterns) {
                var m = pattern.Match(line);
                if (m.Success) {
                    double az = 0, alt = 0;
                    bool parsed = false;

                    // Check if it's the DMS format
                    if (m.Groups["az_deg"].Success && m.Groups["alt_deg"].Success) {
                        double azDeg = double.Parse(m.Groups["az_deg"].Value, System.Globalization.CultureInfo.InvariantCulture);
                        double azMin = double.Parse(m.Groups["az_min"].Value, System.Globalization.CultureInfo.InvariantCulture);
                        double azSec = double.Parse(m.Groups["az_sec"].Value, System.Globalization.CultureInfo.InvariantCulture);
                        az = azDeg + (azMin / 60.0) + (azSec / 3600.0);
                        if (m.Groups["az_sign"].Value == "-") az = -az;

                        double altDeg = double.Parse(m.Groups["alt_deg"].Value, System.Globalization.CultureInfo.InvariantCulture);
                        double altMin = double.Parse(m.Groups["alt_min"].Value, System.Globalization.CultureInfo.InvariantCulture);
                        double altSec = double.Parse(m.Groups["alt_sec"].Value, System.Globalization.CultureInfo.InvariantCulture);
                        alt = altDeg + (altMin / 60.0) + (altSec / 3600.0);
                        if (m.Groups["alt_sign"].Value == "-") alt = -alt;
                        
                        parsed = true;
                    } 
                    // Otherwise it's the decimal format
                    else if (m.Groups["az"].Success && m.Groups["alt"].Success) {
                        if (double.TryParse(m.Groups["alt"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out alt) &&
                            double.TryParse(m.Groups["az"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out az)) {
                            parsed = true;
                        }
                    }

                    if (parsed) {
                        var error = new PolarAlignmentError(az, alt);
                        LastError = error;
                        Logger.Info($"[AAPA] TPPA error read — Az: {az:+0.0000;-0.0000}° Alt: {alt:+0.0000;-0.0000}°");
                        ErrorDetected?.Invoke(this, error);
                        return;
                    }
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
            Stop();
        }
    }

    /// <summary>A polar alignment error measurement from TPPA.</summary>
    public record PolarAlignmentError(double AzimuthErrorDegrees, double AltitudeErrorDegrees) {
        public double TotalErrorArcSec =>
            Math.Sqrt(AzimuthErrorDegrees * AzimuthErrorDegrees +
                      AltitudeErrorDegrees * AltitudeErrorDegrees) * 3600.0;

        public bool IsWithinTolerance(double toleranceDegrees) =>
            Math.Abs(AzimuthErrorDegrees) <= toleranceDegrees &&
            Math.Abs(AltitudeErrorDegrees) <= toleranceDegrees;

        public override string ToString() =>
            $"Az: {AzimuthErrorDegrees:+0.0000;-0.0000}°  Alt: {AltitudeErrorDegrees:+0.0000;-0.0000}°";
    }
}
