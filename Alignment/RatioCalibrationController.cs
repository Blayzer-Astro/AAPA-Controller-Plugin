using System;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;

namespace NINA.Plugins.AAPA.Alignment {

    public enum CalibrationAxis {
        Azimuth,
        Altitude
    }

    public class RatioCalibrationResult {
        public bool Success { get; set; }
        public double NewGearRatio { get; set; }
        public string Message { get; set; } = string.Empty;
    }

    public class RatioCalibrationController {

        private readonly AAPAControllerService _aapa;
        private readonly AlignmentEngine _engine;
        
        public event EventHandler<string>? LogMessage;
        
        public double SettleTimeSeconds { get; set; } = 3.0;

        public RatioCalibrationController(AAPAControllerService aapa, AlignmentEngine engine) {
            _aapa   = aapa   ?? throw new ArgumentNullException(nameof(aapa));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public async Task<RatioCalibrationResult> RunCalibrationAsync(
            CalibrationAxis axis,
            int stepsToMove,
            Func<CancellationToken, Task<PolarAlignmentError?>> getNextError,
            CancellationToken ct) {

            if (!_aapa.IsConnected) {
                return new RatioCalibrationResult { Success = false, Message = "AAPA is not connected." };
            }

            if (stepsToMove == 0) {
                return new RatioCalibrationResult { Success = false, Message = "Steps to move cannot be 0." };
            }

            try {
                Log($"Starting {axis} ratio calibration. Moving {stepsToMove} steps.");

                // 1. Get initial error
                Log("Waiting for initial TPPA error reading...");
                var error1 = await getNextError(ct);
                if (error1 == null) return new RatioCalibrationResult { Success = false, Message = "Timeout waiting for initial TPPA error." };
                Log($"Initial error -> Az: {error1.AzimuthErrorDegrees:0.0000}°, Alt: {error1.AltitudeErrorDegrees:0.0000}°");

                // 2. Move motor
                Log($"Moving {axis} by {stepsToMove} steps...");
                if (axis == CalibrationAxis.Azimuth) {
                    await _aapa.MoveAzimuthAsync(stepsToMove, ct);
                } else {
                    await _aapa.MoveAltitudeAsync(stepsToMove, ct);
                }

                // 3. Wait for motion and settle
                var motionTimeout = DateTime.UtcNow.AddSeconds(45);
                while (_aapa.Status.IsBusy && DateTime.UtcNow < motionTimeout) {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(200, ct);
                    await _aapa.QueryStatusAsync(ct);
                }

                if (_aapa.Status.IsBusy) {
                    await _aapa.StopAsync(ct);
                    return new RatioCalibrationResult { Success = false, Message = "Motion timeout." };
                }

                Log($"Motion complete. Settling for {SettleTimeSeconds}s...");
                await Task.Delay(TimeSpan.FromSeconds(SettleTimeSeconds), ct);

                // 4. Get final error
                Log("Waiting for fresh TPPA error reading (skipping 1 old frame)...");
                await getNextError(ct); // Skip first frame (might have been taken while moving)
                Log("Waiting for the next completely fresh TPPA frame...");
                var error2 = await getNextError(ct); // Get second frame
                if (error2 == null) return new RatioCalibrationResult { Success = false, Message = "Timeout waiting for final TPPA error." };
                Log($"Final error -> Az: {error2.AzimuthErrorDegrees:0.0000}°, Alt: {error2.AltitudeErrorDegrees:0.0000}°");

                // 5. Calculate new ratio
                double deltaDegrees = 0;
                if (axis == CalibrationAxis.Azimuth) {
                    deltaDegrees = Math.Abs(error1.AzimuthErrorDegrees - error2.AzimuthErrorDegrees);
                } else {
                    deltaDegrees = Math.Abs(error1.AltitudeErrorDegrees - error2.AltitudeErrorDegrees);
                }

                if (deltaDegrees < 0.0001) {
                    return new RatioCalibrationResult { Success = false, Message = "Error delta too small (< 0.0001°). Try moving more steps." };
                }

                Log($"Total measured movement on {axis}: {deltaDegrees:0.0000}°");

                // Formula: GearRatio = steps / ( (degrees / 360) * StepsPerRev * Microsteps )
                double microsteps = axis == CalibrationAxis.Azimuth ? _engine.AzimuthMicrosteps : _engine.AltitudeMicrosteps;
                double revs = deltaDegrees / 360.0;
                double motorSteps = revs * _engine.StepsPerRevolution * microsteps;
                
                double newRatio = Math.Abs(stepsToMove) / motorSteps;

                Log($"Calculated new gear ratio: {newRatio:0.000}");
                return new RatioCalibrationResult { Success = true, NewGearRatio = newRatio, Message = "Calibration successful!" };

            } catch (OperationCanceledException) {
                return new RatioCalibrationResult { Success = false, Message = "Calibration cancelled." };
            } catch (Exception ex) {
                return new RatioCalibrationResult { Success = false, Message = $"Error: {ex.Message}" };
            }
        }

        private void Log(string message) {
            Logger.Info($"[AAPA Calibration] {message}");
            LogMessage?.Invoke(this, message);
        }
    }
}
