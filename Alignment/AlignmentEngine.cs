using System;

namespace NINA.Plugins.AAPA.Alignment {

    /// <summary>
    /// Converts TPPA polar alignment errors (in degrees) into AAPA stepper motor steps.
    ///
    /// Step calculation (same formula as platedual.py and OAPA):
    ///   steps = round((degrees / 360) * steps_per_rev * microsteps * gear_ratio)
    ///
    /// Sign convention:
    ///   Positive azimuth error  → move X in positive direction
    ///   Positive altitude error → move Y in positive direction
    ///   Negative values reverse direction; axis reversal flags flip the sign.
    /// </summary>
    public class AlignmentEngine {

        // ── Motor / gearing parameters ────────────────────────────────────────
        /// <summary>Full steps per motor revolution (typically 200 for 1.8° NEMA 17).</summary>
        public int StepsPerRevolution { get; set; } = 200;

        /// <summary>Microstep setting for azimuth axis (e.g. 16 = 1/16 step).</summary>
        public int AzimuthMicrosteps { get; set; } = 16;

        /// <summary>Microstep setting for altitude axis.</summary>
        public int AltitudeMicrosteps { get; set; } = 16;

        /// <summary>Gear ratio for azimuth axis (motor revolutions per output revolution).</summary>
        public double AzimuthGearRatio { get; set; } = 1.0;

        /// <summary>Gear ratio for altitude axis.</summary>
        public double AltitudeGearRatio { get; set; } = 1.0;

        /// <summary>Reverses the azimuth correction direction if true.</summary>
        public bool ReverseAzimuth { get; set; }

        /// <summary>Reverses the altitude correction direction if true.</summary>
        public bool ReverseAltitude { get; set; }

        /// <summary>
        /// Maximum correction applied per iteration (degrees).
        /// Prevents over-correction on large errors.
        /// </summary>
        public double MaxCorrectionPerIterationDegrees { get; set; } = 0.5;

        /// <summary>
        /// Backlash compensation for azimuth axis (extra steps added when reversing direction).
        /// </summary>
        public int AzimuthBacklashSteps { get; set; } = 0;

        /// <summary>Backlash compensation for altitude axis.</summary>
        public int AltitudeBacklashSteps { get; set; } = 0;

        // Track last movement direction for backlash compensation
        private int _lastAzimuthDirection = 0;
        private int _lastAltitudeDirection = 0;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Calculates the stepper motor correction for a given polar alignment error.
        /// Returns the number of steps for each axis.
        /// </summary>
        public CorrectionResult Calculate(PolarAlignmentError error) {
            // Clamp correction to max per iteration
            var azDeg  = Clamp(error.AzimuthErrorDegrees,  -MaxCorrectionPerIterationDegrees, MaxCorrectionPerIterationDegrees);
            var altDeg = Clamp(error.AltitudeErrorDegrees, -MaxCorrectionPerIterationDegrees, MaxCorrectionPerIterationDegrees);

            // Convert degrees to steps
            int azSteps  = DegreesToSteps(azDeg,  AzimuthMicrosteps,  AzimuthGearRatio);
            int altSteps = DegreesToSteps(altDeg, AltitudeMicrosteps, AltitudeGearRatio);

            // Apply axis reversal
            if (ReverseAzimuth)  azSteps  = -azSteps;
            if (ReverseAltitude) altSteps = -altSteps;

            // Apply backlash compensation
            azSteps  = ApplyBacklash(azSteps,  AzimuthBacklashSteps,  ref _lastAzimuthDirection);
            altSteps = ApplyBacklash(altSteps, AltitudeBacklashSteps, ref _lastAltitudeDirection);

            return new CorrectionResult(azSteps, altSteps, azDeg, altDeg);
        }

        /// <summary>Converts arc-degrees to stepper motor steps.</summary>
        public int DegreesToSteps(double degrees, int microsteps, double gearRatio) {
            return (int)Math.Round((degrees / 360.0) * StepsPerRevolution * microsteps * gearRatio);
        }

        /// <summary>Converts stepper motor steps back to arc-degrees (for display).</summary>
        public double StepsToDegrees(int steps, int microsteps, double gearRatio) {
            if (StepsPerRevolution == 0 || microsteps == 0 || gearRatio == 0) return 0;
            return (steps / (double)(StepsPerRevolution * microsteps * gearRatio)) * 360.0;
        }

        /// <summary>Resets backlash tracking (call after homing).</summary>
        public void ResetBacklash() {
            _lastAzimuthDirection = 0;
            _lastAltitudeDirection = 0;
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static double Clamp(double value, double min, double max) =>
            Math.Max(min, Math.Min(max, value));

        private static int ApplyBacklash(int steps, int backlash, ref int lastDirection) {
            if (steps == 0) return 0;

            int direction = Math.Sign(steps);
            int compensated = steps;

            // Direction reversal detected — add backlash steps
            if (lastDirection != 0 && direction != lastDirection && backlash > 0) {
                compensated += direction * backlash;
            }

            lastDirection = direction;
            return compensated;
        }
    }

    /// <summary>The calculated correction move for both axes.</summary>
    public record CorrectionResult(
        int AzimuthSteps,
        int AltitudeSteps,
        double AzimuthCorrectionDegrees,
        double AltitudeCorrectionDegrees) {

        public bool IsZero => AzimuthSteps == 0 && AltitudeSteps == 0;

        public override string ToString() =>
            $"Az: {AzimuthSteps:+0;-0} steps ({AzimuthCorrectionDegrees:+0.0000;-0.0000}°)  " +
            $"Alt: {AltitudeSteps:+0;-0} steps ({AltitudeCorrectionDegrees:+0.0000;-0.0000}°)";
    }
}
