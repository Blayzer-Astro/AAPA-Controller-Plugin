using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Equipment.Interfaces.ViewModel;
using RelayCommand = NINA.Core.Utility.RelayCommand;
using NINA.Plugins.AAPA.Alignment;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NINA.Plugins.AAPA.Dockables {

    /// <summary>
    /// AAPA Controller dockable panel in NINA.
    /// Combines MEF export (IDockableVM) with all ViewModel logic.
    ///
    /// Features:
    ///  - Serial connection management (auto-scan + manual port selection)
    ///  - Live status display (position, busy, homed)
    ///  - TPPA log monitoring with live Az/Alt error display
    ///  - Manual nudge controls (degrees or steps)
    ///  - Auto-Pilot start / stop
    ///  - Motor configuration (speed, accel, gear ratio, microsteps)
    /// </summary>
    [Export(typeof(IDockableVM))]
    public partial class AAPADockable : DockableVM {

        private readonly AAPAControllerService _controller;
        private readonly TPPALogMonitor _logMonitor;
        private readonly AlignmentEngine _engine;
        private readonly AutoPilotController _autoPilot;
        private readonly RatioCalibrationController _calibrationController;

        private CancellationTokenSource? _autoPilotCts;
        private CancellationTokenSource? _calibrationCts;
        private TaskCompletionSource<PolarAlignmentError?>? _errorTcs;

        [ImportingConstructor]
        public AAPADockable(IProfileService profileService) : base(profileService) {

            Title = "AAPA Controller";



            _controller = AAPAPlugin.ControllerService;
            _logMonitor = new TPPALogMonitor();
            _engine     = BuildEngine();
            _autoPilot  = new AutoPilotController(_controller, _engine);
            _calibrationController = new RatioCalibrationController(_controller, _engine);

            // Wire events
            _controller.ConnectionChanged += (_, connected) => {
                Application.Current?.Dispatcher.InvokeAsync(() => {
                    IsConnected = connected;
                    ConnectedPort = connected ? _controller.ConnectedPortName ?? "" : "";
                    if (connected) {
                        MinYLimit = _controller.Status.MinY;
                        MaxYLimit = _controller.Status.MaxY;
                    }
                    RaisePropertyChanged(nameof(ConnectButtonLabel));
                });
            };

            _controller.StatusUpdated += (_, status) => {
                Application.Current?.Dispatcher.InvokeAsync(() => {
                    PositionX = status.PositionX;
                    PositionY = status.PositionY;
                    IsBusy    = status.IsBusy;
                    IsHomed   = status.IsHomed;
                });
            };

            _controller.LogMessage += (_, msg) => Application.Current?.Dispatcher.InvokeAsync(() => AppendLog(msg));

            _logMonitor.ErrorDetected += (_, error) => {
                Application.Current?.Dispatcher.InvokeAsync(() => {
                    AzimuthErrorDeg  = error.AzimuthErrorDegrees;
                    AltitudeErrorDeg = error.AltitudeErrorDegrees;
                    TotalErrorArcSec = error.TotalErrorArcSec;
                    LastTPPAUpdate   = DateTime.Now.ToString("HH:mm:ss");
                });
                _errorTcs?.TrySetResult(error);
                _errorTcs = null;
            };

            _logMonitor.LogMessage  += (_, msg) => Application.Current?.Dispatcher.InvokeAsync(() => AppendLog(msg));
            _autoPilot.LogMessage   += (_, msg) => Application.Current?.Dispatcher.InvokeAsync(() => AppendLog(msg));
            _calibrationController.LogMessage += (_, msg) => Application.Current?.Dispatcher.InvokeAsync(() => AppendLog(msg));

            _autoPilot.ProgressUpdated += (_, args) => {
                Application.Current?.Dispatcher.InvokeAsync(() => {
                    AutoPilotIteration = args.Iteration;
                    if (args.Correction != null) {
                        LastCorrectionAz  = args.Correction.AzimuthSteps;
                        LastCorrectionAlt = args.Correction.AltitudeSteps;
                    }
                    if (args.IsDone) AutoPilotRunning = false;
                });
            };

            RefreshPortsImpl();
            _logMonitor.Start();
        }

        // ── Observable properties ─────────────────────────────────────────────

        private bool _isConnected;
        public bool IsConnected { get => _isConnected; set { _isConnected = value; RaisePropertyChanged(); } }

        private string _connectedPort = "";
        public string ConnectedPort { get => _connectedPort; set { _connectedPort = value; RaisePropertyChanged(); } }

        private long _positionX;
        public long PositionX { get => _positionX; set { _positionX = value; RaisePropertyChanged(); } }

        private long _positionY;
        public long PositionY { get => _positionY; set { _positionY = value; RaisePropertyChanged(); } }

        private bool _isBusy;
        public bool IsBusy { get => _isBusy; set { _isBusy = value; RaisePropertyChanged(); } }

        private bool _isHomed;
        public bool IsHomed { get => _isHomed; set { _isHomed = value; RaisePropertyChanged(); } }

        private double _azimuthErrorDeg;
        public double AzimuthErrorDeg { get => _azimuthErrorDeg; set { _azimuthErrorDeg = value; RaisePropertyChanged(); } }

        private double _altitudeErrorDeg;
        public double AltitudeErrorDeg { get => _altitudeErrorDeg; set { _altitudeErrorDeg = value; RaisePropertyChanged(); } }

        private double _totalErrorArcSec;
        public double TotalErrorArcSec { get => _totalErrorArcSec; set { _totalErrorArcSec = value; RaisePropertyChanged(); } }

        private string _lastTPPAUpdate = "—";
        public string LastTPPAUpdate { get => _lastTPPAUpdate; set { _lastTPPAUpdate = value; RaisePropertyChanged(); } }

        private bool _autoPilotRunning;
        public bool AutoPilotRunning { get => _autoPilotRunning; set { _autoPilotRunning = value; RaisePropertyChanged(); } }

        private int _autoPilotIteration;
        public int AutoPilotIteration { get => _autoPilotIteration; set { _autoPilotIteration = value; RaisePropertyChanged(); } }

        private int _calibrationSteps = 10000;
        public int CalibrationSteps { get => _calibrationSteps; set { _calibrationSteps = value; RaisePropertyChanged(); } }

        private int _lastCorrectionAz;
        public int LastCorrectionAz { get => _lastCorrectionAz; set { _lastCorrectionAz = value; RaisePropertyChanged(); } }

        private int _lastCorrectionAlt;
        public int LastCorrectionAlt { get => _lastCorrectionAlt; set { _lastCorrectionAlt = value; RaisePropertyChanged(); } }

        // Motor settings
        private int _stepsPerRevolution = Properties.Settings.Default.StepsPerRevolution;
        public int StepsPerRevolution { get => _stepsPerRevolution; set { _stepsPerRevolution = value; RaisePropertyChanged(); } }

        private int _azimuthMicrosteps = Properties.Settings.Default.AzimuthMicrosteps;
        public int AzimuthMicrosteps { get => _azimuthMicrosteps; set { _azimuthMicrosteps = value; RaisePropertyChanged(); } }

        private int _altitudeMicrosteps = Properties.Settings.Default.AltitudeMicrosteps;
        public int AltitudeMicrosteps { get => _altitudeMicrosteps; set { _altitudeMicrosteps = value; RaisePropertyChanged(); } }

        private double _azimuthGearRatio = Properties.Settings.Default.AzimuthGearRatio;
        public double AzimuthGearRatio { get => _azimuthGearRatio; set { _azimuthGearRatio = value; RaisePropertyChanged(); } }

        private double _altitudeGearRatio = Properties.Settings.Default.AltitudeGearRatio;
        public double AltitudeGearRatio { get => _altitudeGearRatio; set { _altitudeGearRatio = value; RaisePropertyChanged(); } }

        private bool _reverseAzimuth = Properties.Settings.Default.ReverseAzimuth;
        public bool ReverseAzimuth { get => _reverseAzimuth; set { _reverseAzimuth = value; RaisePropertyChanged(); } }

        private bool _reverseAltitude = Properties.Settings.Default.ReverseAltitude;
        public bool ReverseAltitude { get => _reverseAltitude; set { _reverseAltitude = value; RaisePropertyChanged(); } }

        private int _azimuthBacklash = Properties.Settings.Default.AzimuthBacklash;
        public int AzimuthBacklash { get => _azimuthBacklash; set { _azimuthBacklash = value; RaisePropertyChanged(); } }

        private int _altitudeBacklash = Properties.Settings.Default.AltitudeBacklash;
        public int AltitudeBacklash { get => _altitudeBacklash; set { _altitudeBacklash = value; RaisePropertyChanged(); } }

        private double _toleranceDegrees = Properties.Settings.Default.ToleranceDegrees;
        public double ToleranceDegrees { get => _toleranceDegrees; set { _toleranceDegrees = value; RaisePropertyChanged(); } }

        private double _settleTimeSeconds = Properties.Settings.Default.SettleTimeSeconds;
        public double SettleTimeSeconds { get => _settleTimeSeconds; set { _settleTimeSeconds = value; RaisePropertyChanged(); } }

        private int _maxIterations = Properties.Settings.Default.MaxIterations;
        public int MaxIterations { get => _maxIterations; set { _maxIterations = value; RaisePropertyChanged(); } }

        private double _maxCorrectionDeg = Properties.Settings.Default.MaxCorrectionDegrees;
        public double MaxCorrectionDeg { get => _maxCorrectionDeg; set { _maxCorrectionDeg = value; RaisePropertyChanged(); } }

        private int _azimuthSpeed = Properties.Settings.Default.AzimuthSpeed;
        public int AzimuthSpeed { get => _azimuthSpeed; set { _azimuthSpeed = value; RaisePropertyChanged(); } }

        private int _altitudeSpeed = Properties.Settings.Default.AltitudeSpeed;
        public int AltitudeSpeed { get => _altitudeSpeed; set { _altitudeSpeed = value; RaisePropertyChanged(); } }

        private int _azimuthAccel = Properties.Settings.Default.AzimuthAccel;
        public int AzimuthAccel { get => _azimuthAccel; set { _azimuthAccel = value; RaisePropertyChanged(); } }

        private int _altitudeAccel = Properties.Settings.Default.AltitudeAccel;
        public int AltitudeAccel { get => _altitudeAccel; set { _altitudeAccel = value; RaisePropertyChanged(); } }

        private long _minYLimit = -100000;
        public long MinYLimit { get => _minYLimit; set { _minYLimit = value; RaisePropertyChanged(); } }

        private long _maxYLimit = 100000;
        public long MaxYLimit { get => _maxYLimit; set { _maxYLimit = value; RaisePropertyChanged(); } }

        private ObservableCollection<string> _availablePorts = new();
        public ObservableCollection<string> AvailablePorts { get => _availablePorts; set { _availablePorts = value; RaisePropertyChanged(); } }

        private string _selectedPort = "";
        public string SelectedPort { get => _selectedPort; set { _selectedPort = value; RaisePropertyChanged(); } }

        public List<string> ConnectionTypes { get; } = new List<string> { "COM", "IP" };

        private string _selectedConnectionType = Properties.Settings.Default.ConnectionType;
        public string SelectedConnectionType { 
            get => _selectedConnectionType; 
            set { 
                _selectedConnectionType = value; 
                RaisePropertyChanged(); 
                SaveSettingsImpl();
            } 
        }

        private string _ipAddress = Properties.Settings.Default.LastIpAddress;
        public string IpAddress { 
            get => _ipAddress; 
            set { 
                _ipAddress = value; 
                RaisePropertyChanged();
                SaveSettingsImpl();
            } 
        }

        private string _logText = "";
        public string LogText { get => _logText; set { _logText = value; RaisePropertyChanged(); } }

        private double _nudgeDegrees = 0.1;
        public double NudgeDegrees { get => _nudgeDegrees; set { _nudgeDegrees = value; RaisePropertyChanged(); } }

        public string ConnectButtonLabel => IsConnected ? "Disconnect" : "Connect";

        // ── Commands ──────────────────────────────────────────────────────────

        private RelayCommand? _connectToggleCommand;
        public RelayCommand ConnectToggleCommand => _connectToggleCommand ??= new RelayCommand((object o) => {
            _ = Task.Run(async () => {
                if (IsConnected) {
                    _controller.Disconnect();
                } else {
                    string? connectionTarget = SelectedConnectionType == "IP" 
                        ? IpAddress 
                        : (SelectedPort == "Auto" ? null : SelectedPort);
                    
                    var ok = await _controller.ConnectAsync(connectionTarget);
                    if (!ok) {
                        string errorMsg = SelectedConnectionType == "IP" 
                            ? $"AAPA: IP connection to {IpAddress} failed." 
                            : "AAPA: COM port connection failed.";
                        Application.Current?.Dispatcher.InvokeAsync(() => Notification.ShowError(errorMsg));
                    } else {
                        string successMsg = SelectedConnectionType == "IP"
                            ? $"AAPA connected via IP ({IpAddress})!"
                            : "AAPA connected via COM port!";
                        Application.Current?.Dispatcher.InvokeAsync(() => Notification.ShowSuccess(successMsg));
                    }
                }
            });
        });

        private RelayCommand? _refreshPortsCommand;
        public RelayCommand RefreshPortsCommand => _refreshPortsCommand ??= new RelayCommand((object o) => RefreshPortsImpl());

        private RelayCommand? _homeAltitudeCommand;
        public RelayCommand HomeAltitudeCommand => _homeAltitudeCommand ??= new RelayCommand((object o) => _ = _controller.HomeAltitudeAsync());

        private RelayCommand? _homeAzimuthCommand;
        public RelayCommand HomeAzimuthCommand => _homeAzimuthCommand ??= new RelayCommand((object o) => _ = _controller.HomeAzimuthAsync());

        private RelayCommand? _resetYCommand;
        public RelayCommand ResetYCommand => _resetYCommand ??= new RelayCommand((object o) => _ = _controller.ResetYAsync());

        private RelayCommand? _resetXCommand;
        public RelayCommand ResetXCommand => _resetXCommand ??= new RelayCommand((object o) => _ = _controller.ResetXAsync());

        private RelayCommand? _stopCommand;
        public RelayCommand StopCommand => _stopCommand ??= new RelayCommand((object o) => _ = _controller.StopAsync());

        private RelayCommand? _nudgeAzimuthPosCommand;
        public RelayCommand NudgeAzimuthPosCommand => _nudgeAzimuthPosCommand ??= new RelayCommand((object o) => _ = NudgeAzimuth(+NudgeDegrees));

        private RelayCommand? _nudgeAzimuthNegCommand;
        public RelayCommand NudgeAzimuthNegCommand => _nudgeAzimuthNegCommand ??= new RelayCommand((object o) => _ = NudgeAzimuth(-NudgeDegrees));

        private RelayCommand? _nudgeAltitudePosCommand;
        public RelayCommand NudgeAltitudePosCommand => _nudgeAltitudePosCommand ??= new RelayCommand((object o) => _ = NudgeAltitude(+NudgeDegrees));

        private RelayCommand? _nudgeAltitudeNegCommand;
        public RelayCommand NudgeAltitudeNegCommand => _nudgeAltitudeNegCommand ??= new RelayCommand((object o) => _ = NudgeAltitude(-NudgeDegrees));

        private RelayCommand? _startAutoPilotCommand;
        public RelayCommand StartAutoPilotCommand => _startAutoPilotCommand ??= new RelayCommand((object o) => {
            StartAutoPilot();
        });

        private void StartAutoPilot() {
            if (AutoPilotRunning) return;
            SaveSettingsImpl();
            UpdateEngine();

            AutoPilotRunning = true;
            AutoPilotIteration = 0;
            _autoPilotCts = new CancellationTokenSource();

            _autoPilot.ToleranceDegrees = ToleranceDegrees;
            _autoPilot.MaxIterations    = MaxIterations;
            _autoPilot.SettleTimeSeconds = SettleTimeSeconds;

            _ = Task.Run(async () => {
                var result = await _autoPilot.RunAsync(WaitForTPPAErrorAsync, _autoPilotCts.Token);
                
                if (result == AutoPilotResult.Success) {
                    await _controller.SaveConfigAsync();
                }

                Application.Current?.Dispatcher.InvokeAsync(() => {
                    AutoPilotRunning = false;
                    var msg = result switch {
                        AutoPilotResult.Success => "Auto-Pilot finished — alignment within tolerance! (Positions saved)",
                        AutoPilotResult.Cancelled => "Auto-Pilot cancelled.",
                        AutoPilotResult.MaxIterationsReached => "Auto-Pilot: max iterations reached.",
                        AutoPilotResult.NoTPPAData => "Auto-Pilot: no TPPA data received. Is TPPA running?",
                        AutoPilotResult.MotionTimeout => "Auto-Pilot: AAPA motion timeout.",
                        _ => $"Auto-Pilot stopped: {result}"
                    };
                    AppendLog(msg);
                });
            });
        }

        private RelayCommand? _stopAutoPilotCommand;
        public RelayCommand StopAutoPilotCommand => _stopAutoPilotCommand ??= new RelayCommand((object o) => {
            _autoPilotCts?.Cancel();
            _errorTcs?.TrySetResult(null);
        });

        private RelayCommand? _sendSpeedAccelCommand;
        public RelayCommand SendSpeedAccelCommand => _sendSpeedAccelCommand ??= new RelayCommand((object o) => {
            _ = Task.Run(async () => {
                await _controller.SetAzimuthSpeedAsync(AzimuthSpeed);
                await _controller.SetAltitudeSpeedAsync(AltitudeSpeed);
                await _controller.SetAzimuthAccelAsync(AzimuthAccel);
                await _controller.SetAltitudeAccelAsync(AltitudeAccel);
                await _controller.SetMinYAsync(MinYLimit);
                await _controller.SetMaxYAsync(MaxYLimit);
                await _controller.SaveConfigAsync();
                Application.Current?.Dispatcher.InvokeAsync(() => AppendLog("Motor parameters and limits sent and saved."));
            });
        });

        private RelayCommand? _calibrateAzimuthCommand;
        public RelayCommand CalibrateAzimuthCommand => _calibrateAzimuthCommand ??= new RelayCommand((object o) => _ = RunCalibrationAsync(CalibrationAxis.Azimuth));

        private RelayCommand? _calibrateAltitudeCommand;
        public RelayCommand CalibrateAltitudeCommand => _calibrateAltitudeCommand ??= new RelayCommand((object o) => _ = RunCalibrationAsync(CalibrationAxis.Altitude));

        private RelayCommand? _saveSettingsCommand;
        public RelayCommand SaveSettingsCommand => _saveSettingsCommand ??= new RelayCommand((object o) => SaveSettingsImpl());

        // ── Private helpers ───────────────────────────────────────────────────

        private void RefreshPortsImpl() {
            AvailablePorts.Clear();
            AvailablePorts.Add("Auto");
            foreach (var p in SerialPort.GetPortNames()) AvailablePorts.Add(p);
            if (string.IsNullOrEmpty(SelectedPort)) SelectedPort = "Auto";
        }

        private void SaveSettingsImpl() {
            var s = Properties.Settings.Default;
            s.ConnectionType         = SelectedConnectionType;
            s.LastIpAddress          = IpAddress;
            s.StepsPerRevolution     = StepsPerRevolution;
            s.AzimuthMicrosteps      = AzimuthMicrosteps;
            s.AltitudeMicrosteps     = AltitudeMicrosteps;
            s.AzimuthGearRatio       = AzimuthGearRatio;
            s.AltitudeGearRatio      = AltitudeGearRatio;
            s.ReverseAzimuth         = ReverseAzimuth;
            s.ReverseAltitude        = ReverseAltitude;
            s.AzimuthBacklash        = AzimuthBacklash;
            s.AltitudeBacklash       = AltitudeBacklash;
            s.ToleranceDegrees       = ToleranceDegrees;
            s.SettleTimeSeconds      = SettleTimeSeconds;
            s.MaxIterations          = MaxIterations;
            s.MaxCorrectionDegrees   = MaxCorrectionDeg;
            s.AzimuthSpeed           = AzimuthSpeed;
            s.AltitudeSpeed          = AltitudeSpeed;
            s.AzimuthAccel           = AzimuthAccel;
            s.AltitudeAccel          = AltitudeAccel;
            CoreUtil.SaveSettings(s);
            UpdateEngine();
            AppendLog("Settings saved.");
        }

        private async Task NudgeAzimuth(double degrees) {
            var steps = _engine.DegreesToSteps(degrees, AzimuthMicrosteps, AzimuthGearRatio);
            if (ReverseAzimuth) steps = -steps;
            await _controller.MoveAzimuthAsync(steps);
        }

        private async Task NudgeAltitude(double degrees) {
            var steps = _engine.DegreesToSteps(degrees, AltitudeMicrosteps, AltitudeGearRatio);
            if (ReverseAltitude) steps = -steps;
            await _controller.MoveAltitudeAsync(steps);
        }

        private async Task RunCalibrationAsync(CalibrationAxis axis) {
            if (AutoPilotRunning) return;
            SaveSettingsImpl();
            UpdateEngine();
            AutoPilotRunning = true; // Use same flag to disable UI
            _calibrationCts = new CancellationTokenSource();

            _ = Task.Run(async () => {
                var result = await _calibrationController.RunCalibrationAsync(
                    axis, CalibrationSteps, WaitForTPPAErrorAsync, _calibrationCts.Token);
                
                if (result.Success) {
                    if (axis == CalibrationAxis.Azimuth) AzimuthGearRatio = result.NewGearRatio;
                    else AltitudeGearRatio = result.NewGearRatio;
                    SaveSettingsImpl();
                }

                Application.Current?.Dispatcher.InvokeAsync(() => {
                    AutoPilotRunning = false;
                    AppendLog(result.Message);
                });
            });
        }

        private Task<PolarAlignmentError?> WaitForTPPAErrorAsync(CancellationToken ct) {
            var tcs = new TaskCompletionSource<PolarAlignmentError?>();

            // Link cancellation token and a 120s timeout
            var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));

            EventHandler<PolarAlignmentError> handler = null!;
            handler = (sender, err) => {
                _logMonitor.ErrorDetected -= handler;
                tcs.TrySetResult(err);
            };

            _logMonitor.ErrorDetected += handler;

            timeoutCts.Token.Register(() => {
                _logMonitor.ErrorDetected -= handler;
                tcs.TrySetResult(null);
            });

            return tcs.Task;
        }

        private AlignmentEngine BuildEngine() => new AlignmentEngine {
            StepsPerRevolution = Properties.Settings.Default.StepsPerRevolution,
            AzimuthMicrosteps  = Properties.Settings.Default.AzimuthMicrosteps,
            AltitudeMicrosteps = Properties.Settings.Default.AltitudeMicrosteps,
            AzimuthGearRatio   = Properties.Settings.Default.AzimuthGearRatio,
            AltitudeGearRatio  = Properties.Settings.Default.AltitudeGearRatio,
            ReverseAzimuth     = Properties.Settings.Default.ReverseAzimuth,
            ReverseAltitude    = Properties.Settings.Default.ReverseAltitude,
            AzimuthBacklashSteps  = Properties.Settings.Default.AzimuthBacklash,
            AltitudeBacklashSteps = Properties.Settings.Default.AltitudeBacklash,
            MaxCorrectionPerIterationDegrees = Properties.Settings.Default.MaxCorrectionDegrees,
        };

        private void UpdateEngine() {
            _engine.StepsPerRevolution = StepsPerRevolution;
            _engine.AzimuthMicrosteps  = AzimuthMicrosteps;
            _engine.AltitudeMicrosteps = AltitudeMicrosteps;
            _engine.AzimuthGearRatio   = AzimuthGearRatio;
            _engine.AltitudeGearRatio  = AltitudeGearRatio;
            _engine.ReverseAzimuth     = ReverseAzimuth;
            _engine.ReverseAltitude    = ReverseAltitude;
            _engine.AzimuthBacklashSteps  = AzimuthBacklash;
            _engine.AltitudeBacklashSteps = AltitudeBacklash;
            _engine.MaxCorrectionPerIterationDegrees = MaxCorrectionDeg;
        }

        private void AppendLog(string message) {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            LogText = (LogText + "\n" + line).TrimStart('\n');
            var lines = LogText.Split('\n');
            if (lines.Length > 200) LogText = string.Join("\n", lines[^200..]);
        }
    }
}
