using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Plugins.AAPA.Alignment;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.Plugins.AAPA.Instructions {

    /// <summary>
    /// NINA Sequence Instruction: "AAPA Auto-Align".
    ///
    /// When added to a NINA sequence this instruction:
    ///   1. Ensures the AAPA device is connected (auto-scan if not)
    ///   2. Starts monitoring TPPA log files for polar alignment errors
    ///   3. Runs the AutoPilotController until error is within tolerance
    ///      or max iterations is reached
    ///   4. Reports progress through the NINA sequencer status bar
    ///
    /// Place this instruction AFTER a TPPA measurement step in the sequence.
    /// </summary>
    [ExportMetadata("Name",        "AAPA Auto-Align")]
    [ExportMetadata("Description", "Automatically corrects polar alignment using the AAPA hardware, driven by TPPA error measurements. Place this instruction after a TPPA measurement step.")]
    [ExportMetadata("Icon",        "TwoWayArrow_SVG")]
    [ExportMetadata("Category",    "AAPA")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class AAPAAlignInstruction : SequenceItem, IValidatable {

        private readonly AAPAControllerService _controller;
        private readonly AlignmentEngine _engine;
        private readonly AutoPilotController _autoPilot;
        private readonly TPPALogMonitor _logMonitor;
        private TaskCompletionSource<PolarAlignmentError?>? _errorTcs;

        [ImportingConstructor]
        public AAPAAlignInstruction() {
            _controller = AAPAPlugin.ControllerService;
            _engine = new AlignmentEngine {
                StepsPerRevolution               = Properties.Settings.Default.StepsPerRevolution,
                AzimuthMicrosteps                = Properties.Settings.Default.AzimuthMicrosteps,
                AltitudeMicrosteps               = Properties.Settings.Default.AltitudeMicrosteps,
                AzimuthGearRatio                 = Properties.Settings.Default.AzimuthGearRatio,
                AltitudeGearRatio                = Properties.Settings.Default.AltitudeGearRatio,
                ReverseAzimuth                   = Properties.Settings.Default.ReverseAzimuth,
                ReverseAltitude                  = Properties.Settings.Default.ReverseAltitude,
                AzimuthBacklashSteps             = Properties.Settings.Default.AzimuthBacklash,
                AltitudeBacklashSteps            = Properties.Settings.Default.AltitudeBacklash,
                MaxCorrectionPerIterationDegrees = Properties.Settings.Default.MaxCorrectionDegrees,
            };
            _autoPilot  = new AutoPilotController(_controller, _engine);
            _logMonitor = new TPPALogMonitor();

            _autoPilot.LogMessage += (_, msg) => Logger.Info($"[AAPA Seq] {msg}");
        }

        // Clone constructor
        private AAPAAlignInstruction(AAPAAlignInstruction original) : this() {
            ToleranceDegrees  = original.ToleranceDegrees;
            MaxIterations     = original.MaxIterations;
            SettleTimeSeconds = original.SettleTimeSeconds;
        }

        // ── Serialised properties ─────────────────────────────────────────────

        [JsonProperty]
        public double ToleranceDegrees {
            get => _toleranceDegrees;
            set { _toleranceDegrees = value; RaisePropertyChanged(); }
        }
        private double _toleranceDegrees = Properties.Settings.Default.ToleranceDegrees;

        [JsonProperty]
        public int MaxIterations {
            get => _maxIterations;
            set { _maxIterations = value; RaisePropertyChanged(); }
        }
        private int _maxIterations = Properties.Settings.Default.MaxIterations;

        [JsonProperty]
        public double SettleTimeSeconds {
            get => _settleTimeSeconds;
            set { _settleTimeSeconds = value; RaisePropertyChanged(); }
        }
        private double _settleTimeSeconds = Properties.Settings.Default.SettleTimeSeconds;

        // ── ISequenceItem ─────────────────────────────────────────────────────

        public override object Clone() => new AAPAAlignInstruction(this);

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken ct) {
            progress?.Report(new ApplicationStatus { Status = "AAPA: Connecting to device..." });

            if (!_controller.IsConnected) {
                var ok = await _controller.ConnectAsync(ct: ct);
                if (!ok) throw new SequenceEntityFailedException(
                    "AAPA: Could not connect to any AAPA device. Check COM port and firmware.");
            }

            _logMonitor.ErrorDetected += OnErrorDetected;
            _logMonitor.Start();

            try {
                _autoPilot.ToleranceDegrees  = ToleranceDegrees;
                _autoPilot.MaxIterations     = MaxIterations;
                _autoPilot.SettleTimeSeconds = SettleTimeSeconds;

                _autoPilot.ProgressUpdated += (_, args) => {
                    progress?.Report(new ApplicationStatus {
                        Status = $"AAPA [{args.Iteration}]: " +
                                 $"Az {args.Error.AzimuthErrorDegrees:+0.0000;-0.0000}°  " +
                                 $"Alt {args.Error.AltitudeErrorDegrees:+0.0000;-0.0000}°" +
                                 (args.IsDone ? " ✓ Done" : "")
                    });
                };

                var result = await _autoPilot.RunAsync(WaitForTPPAErrorAsync, ct);

                progress?.Report(new ApplicationStatus { Status = $"AAPA: {result}" });

                if (result is AutoPilotResult.Error or AutoPilotResult.NotConnected) {
                    throw new SequenceEntityFailedException($"AAPA alignment failed: {result}");
                }
            } finally {
                _logMonitor.ErrorDetected -= OnErrorDetected;
                _logMonitor.Stop();
            }
        }

        // ── IValidatable ──────────────────────────────────────────────────────

        public IList<string> Issues { get; private set; } = new List<string>();

        public bool Validate() {
            var issues = new List<string>();
            if (ToleranceDegrees <= 0)
                issues.Add("Tolerance must be > 0 degrees.");
            if (MaxIterations < 0)
                issues.Add("Max iterations must be >= 0 (0 = unlimited).");
            if (SettleTimeSeconds < 0)
                issues.Add("Settle time cannot be negative.");
            Issues = issues;
            RaisePropertyChanged(nameof(Issues));
            return issues.Count == 0;
        }

        // ── Private ───────────────────────────────────────────────────────────

        private Task<PolarAlignmentError?> WaitForTPPAErrorAsync(CancellationToken ct) {
            _errorTcs = new TaskCompletionSource<PolarAlignmentError?>();
            ct.Register(() => _errorTcs?.TrySetResult(null));

            // Timeout after 2 minutes
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            cts.Token.Register(() => _errorTcs?.TrySetResult(null));

            return _errorTcs.Task;
        }

        private void OnErrorDetected(object? sender, PolarAlignmentError error) {
            _errorTcs?.TrySetResult(error);
            _errorTcs = null;
        }
    }
}
