using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Plugins.AAPA.Alignment;

namespace NINA.Plugins.AAPA {
    /// <summary>
    /// Global headless orchestrator for AAPA logic.
    /// This allows the WebSocket API and the UI to share the same controllers
    /// regardless of whether the UI tab is open.
    /// </summary>
    public class AAPACore {
        public static AAPACore Instance { get; } = new AAPACore();

        public AAPAControllerService Controller { get; }
        public TPPALogMonitor LogMonitor { get; }
        public AlignmentEngine Engine { get; }
        public AutoPilotController AutoPilot { get; }
        public RatioCalibrationController CalibrationController { get; }

        public event EventHandler<string> CoreLogMessage;

        private AAPACore() {
            Controller = AAPAPlugin.ControllerService;
            LogMonitor = new TPPALogMonitor();
            
            Engine = new AlignmentEngine();
            UpdateEngineSettings();

            AutoPilot = new AutoPilotController(Controller, Engine);
            CalibrationController = new RatioCalibrationController(Controller, Engine);

            Controller.LogMessage += (_, msg) => CoreLogMessage?.Invoke(this, msg);
            LogMonitor.LogMessage += (_, msg) => CoreLogMessage?.Invoke(this, msg);
            AutoPilot.LogMessage += (_, msg) => CoreLogMessage?.Invoke(this, msg);
            CalibrationController.LogMessage += (_, msg) => CoreLogMessage?.Invoke(this, msg);

            LogMonitor.Start();
        }

        public void UpdateEngineSettings() {
            Engine.StepsPerRevolution = Properties.Settings.Default.StepsPerRevolution;
            Engine.AzimuthMicrosteps = Properties.Settings.Default.AzimuthMicrosteps;
            Engine.AltitudeMicrosteps = Properties.Settings.Default.AltitudeMicrosteps;
            Engine.AzimuthGearRatio = Properties.Settings.Default.AzimuthGearRatio;
            Engine.AltitudeGearRatio = Properties.Settings.Default.AltitudeGearRatio;
            Engine.ReverseAzimuth = Properties.Settings.Default.ReverseAzimuth;
            Engine.ReverseAltitude = Properties.Settings.Default.ReverseAltitude;
            Engine.AzimuthBacklashSteps = Properties.Settings.Default.AzimuthBacklash;
            Engine.AltitudeBacklashSteps = Properties.Settings.Default.AltitudeBacklash;
            Engine.MaxCorrectionPerIterationDegrees = Properties.Settings.Default.MaxCorrectionDegrees;
        }

        private TaskCompletionSource<PolarAlignmentError?>? _errorTcs;
        private CancellationTokenSource? _autoPilotCts;

        public bool IsAutoPilotRunning { get; private set; }
        public event EventHandler<bool> AutoPilotStateChanged;

        public void StartAutoPilot() {
            if (IsAutoPilotRunning) return;
            
            IsAutoPilotRunning = true;
            AutoPilotStateChanged?.Invoke(this, true);
            
            _autoPilotCts = new CancellationTokenSource();
            _ = Task.Run(() => RunAutoPilotInternalAsync(_autoPilotCts.Token));
        }

        public void StopAutoPilot() {
            if (!IsAutoPilotRunning) return;
            _autoPilotCts?.Cancel();
            _errorTcs?.TrySetResult(null);
            _ = Controller.StopAsync(); // Send stop command to hardware immediately
        }

        private async Task RunAutoPilotInternalAsync(CancellationToken ct) {
            UpdateEngineSettings();
            
            // Set autopilot properties
            AutoPilot.ToleranceDegrees = Properties.Settings.Default.ToleranceDegrees;
            AutoPilot.SettleTimeSeconds = Properties.Settings.Default.SettleTimeSeconds;
            AutoPilot.MaxIterations = Properties.Settings.Default.MaxIterations;
            AutoPilot.MotionTimeoutSeconds = Properties.Settings.Default.MotionTimeoutSeconds;

            LogMonitor.ErrorDetected += OnErrorDetected;
            try {
                var result = await AutoPilot.RunAsync(WaitForTPPAErrorAsync, ct);
                
                string msg = result switch {
                    AutoPilotResult.Success => "Auto-Pilot finished — alignment within tolerance! (Positions saved)",
                    AutoPilotResult.Cancelled => "Auto-Pilot cancelled.",
                    AutoPilotResult.MaxIterationsReached => "Auto-Pilot: max iterations reached.",
                    AutoPilotResult.NoTPPAData => "Auto-Pilot: no TPPA data received. Is TPPA running?",
                    AutoPilotResult.MotionTimeout => "Auto-Pilot: AAPA motion timeout.",
                    _ => $"Auto-Pilot stopped: {result}"
                };
                CoreLogMessage?.Invoke(this, msg);

                if (result == AutoPilotResult.Success) {
                    await Controller.SaveConfigAsync();
                }
            } finally {
                LogMonitor.ErrorDetected -= OnErrorDetected;
                IsAutoPilotRunning = false;
                AutoPilotStateChanged?.Invoke(this, false);
            }
        }

        private void OnErrorDetected(object sender, PolarAlignmentError error) {
            var tcs = Interlocked.Exchange(ref _errorTcs, null);
            tcs?.TrySetResult(error);
        }

        private async Task<PolarAlignmentError?> WaitForTPPAErrorAsync(CancellationToken ct) {
            var tcs = new TaskCompletionSource<PolarAlignmentError?>();
            Interlocked.Exchange(ref _errorTcs, tcs);
            using var reg = ct.Register(() => tcs.TrySetCanceled());
            try {
                return await tcs.Task;
            } catch (TaskCanceledException) {
                return null;
            }
        }

        private CancellationTokenSource? _calibrationCts;

        public bool IsCalibrationRunning { get; private set; }
        public event EventHandler<bool> CalibrationStateChanged;

        public async Task<RatioCalibrationResult> StartCalibrationAsync(CalibrationAxis axis, int steps, CancellationToken ct) {
            if (IsCalibrationRunning) return new RatioCalibrationResult { Success = false, Message = "Calibration is already running." };
            
            IsCalibrationRunning = true;
            CalibrationStateChanged?.Invoke(this, true);
            
            UpdateEngineSettings();
            
            _calibrationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            LogMonitor.ErrorDetected += OnErrorDetected;
            try {
                return await CalibrationController.RunCalibrationAsync(axis, steps, WaitForTPPAErrorAsync, _calibrationCts.Token);
            } finally {
                LogMonitor.ErrorDetected -= OnErrorDetected;
                _calibrationCts?.Dispose();
                _calibrationCts = null;
                IsCalibrationRunning = false;
                CalibrationStateChanged?.Invoke(this, false);
            }
        }

        public void StopCalibration() {
            _calibrationCts?.Cancel();
            _errorTcs?.TrySetResult(null);
            _ = Controller.StopAsync();
        }
    }
}
