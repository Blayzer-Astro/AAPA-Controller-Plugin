using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;

namespace NINA.Plugins.AAPA.Alignment {

    /// <summary>
    /// Orchestrates the automatic polar alignment loop:
    ///   1. Wait for a new TPPA error reading
    ///   2. Calculate correction via AlignmentEngine
    ///   3. Send move commands to AAPA
    ///   4. Wait for motion to complete + settle time
    ///   5. Repeat until within tolerance or cancelled
    /// </summary>
    public class AutoPilotController {

        private readonly AAPAControllerService _aapa;
        private readonly AlignmentEngine _engine;

        // ── Configuration ─────────────────────────────────────────────────────
        /// <summary>Stop when both axes are within this tolerance (degrees).</summary>
        public double ToleranceDegrees { get; set; } = 0.01;   // ~36 arcsec

        /// <summary>Maximum number of correction iterations (0 = unlimited).</summary>
        public int MaxIterations { get; set; } = 20;

        /// <summary>Seconds to wait after each move before the next TPPA measurement.</summary>
        public double SettleTimeSeconds { get; set; } = 3.0;

        // ── Events ────────────────────────────────────────────────────────────
        public event EventHandler<AutoPilotProgressEventArgs>? ProgressUpdated;
        public event EventHandler<string>? LogMessage;

        private volatile bool _isRunning;
        public bool IsRunning => _isRunning;

        public AutoPilotController(AAPAControllerService aapa, AlignmentEngine engine) {
            _aapa   = aapa   ?? throw new ArgumentNullException(nameof(aapa));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        /// <summary>
        /// Runs the auto-pilot alignment loop.
        /// Provide a <paramref name="getLatestError"/> delegate that blocks until a new
        /// TPPA error reading is available (or returns null to abort).
        /// </summary>
        public async Task<AutoPilotResult> RunAsync(
            Func<CancellationToken, Task<PolarAlignmentError?>> getLatestError,
            CancellationToken ct = default) {

            if (_isRunning) return AutoPilotResult.AlreadyRunning;
            if (!_aapa.IsConnected) return AutoPilotResult.NotConnected;

            _isRunning = true;
            int iteration = 0;
            int toleranceHits = 0; // Tracks consecutive measurements within tolerance

            try {
                Log("Auto-Pilot started.");

                while (!ct.IsCancellationRequested) {
                    iteration++;

                    // ── Step 1: Get current TPPA error ────────────────────────
                    Log($"Iteration {iteration}: waiting for TPPA measurement...");
                    var error = await getLatestError(ct);

                    if (error == null) {
                        Log("No TPPA reading received — aborting.");
                        return AutoPilotResult.NoTPPAData;
                    }

                    Log($"Iteration {iteration}: {error}");
                    RaiseProgress(iteration, error, null);

                    // ── Step 2: Check tolerance ───────────────────────────────
                    if (error.IsWithinTolerance(ToleranceDegrees)) {
                        toleranceHits++;
                        if (toleranceHits >= 3) {
                            Log($"Within tolerance ({ToleranceDegrees * 3600:0} arcsec) for 3 consecutive measurements. Done!");
                            RaiseProgress(iteration, error, null, done: true);
                            return AutoPilotResult.Success;
                        } else {
                            int remaining = 3 - toleranceHits;
                            Log($"Within tolerance! Waiting {remaining} more iteration(s) to confirm...");
                            continue; // Skip movement, wait for next TPPA reading
                        }
                    } else {
                        if (toleranceHits > 0) {
                            Log("Error drifted out of tolerance. Resetting verification counter.");
                            toleranceHits = 0;
                        }
                    }

                    // ── Step 3: Calculate correction ──────────────────────────
                    var correction = _engine.Calculate(error);
                    Log($"Correction: {correction}");
                    RaiseProgress(iteration, error, correction);

                    if (correction.IsZero) {
                        Log("Correction is zero — check gear ratio / motor settings.");
                        return AutoPilotResult.ZeroCorrection;
                    }

                    // ── Step 4: Send move commands ────────────────────────────
                    await _aapa.MoveBothAxesAsync(correction.AzimuthSteps, correction.AltitudeSteps, ct);

                    // ── Step 5: Wait for AAPA motion + settle ─────────────────
                    // Poll busy flag until motion complete
                    var motionTimeout = DateTime.UtcNow.AddSeconds(30);
                    while (_aapa.Status.IsBusy && DateTime.UtcNow < motionTimeout) {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(200, ct);
                        await _aapa.QueryStatusAsync(ct);
                    }

                    if (_aapa.Status.IsBusy) {
                        Log("Motion timeout — AAPA still busy after 30s.");
                        await _aapa.StopAsync(ct);
                        return AutoPilotResult.MotionTimeout;
                    }

                    // Settle time
                    Log($"Motion complete. Settling {SettleTimeSeconds}s...");
                    await Task.Delay(TimeSpan.FromSeconds(SettleTimeSeconds), ct);

                    // ── Step 6: Check max iterations ──────────────────────────
                    if (MaxIterations > 0 && iteration >= MaxIterations) {
                        Log($"Max iterations ({MaxIterations}) reached.");
                        return AutoPilotResult.MaxIterationsReached;
                    }
                }

                return AutoPilotResult.Cancelled;
            } catch (OperationCanceledException) {
                Log("Auto-Pilot cancelled.");
                return AutoPilotResult.Cancelled;
            } catch (Exception ex) {
                Log($"Auto-Pilot error: {ex.Message}");
                return AutoPilotResult.Error;
            } finally {
                _isRunning = false;
                Log("Auto-Pilot stopped.");
            }
        }

        private void RaiseProgress(int iteration, PolarAlignmentError error,
                                   CorrectionResult? correction, bool done = false) {
            ProgressUpdated?.Invoke(this, new AutoPilotProgressEventArgs(
                iteration, error, correction, done));
        }

        private void Log(string message) {
            Logger.Info($"[AAPA AutoPilot] {message}");
            LogMessage?.Invoke(this, message);
        }
    }

    // ── Supporting types ──────────────────────────────────────────────────────

    public class AutoPilotProgressEventArgs : EventArgs {
        public int Iteration { get; }
        public PolarAlignmentError Error { get; }
        public CorrectionResult? Correction { get; }
        public bool IsDone { get; }

        public AutoPilotProgressEventArgs(int iteration, PolarAlignmentError error,
                                          CorrectionResult? correction, bool isDone = false) {
            Iteration  = iteration;
            Error      = error;
            Correction = correction;
            IsDone     = isDone;
        }
    }

    public enum AutoPilotResult {
        Success,
        Cancelled,
        AlreadyRunning,
        NotConnected,
        NoTPPAData,
        ZeroCorrection,
        MotionTimeout,
        MaxIterationsReached,
        Error
    }
}
