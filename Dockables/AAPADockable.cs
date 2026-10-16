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



            _controller = AAPACore.Instance.Controller;
            _logMonitor = AAPACore.Instance.LogMonitor;
            _engine     = AAPACore.Instance.Engine;
            _autoPilot  = AAPACore.Instance.AutoPilot;
            _calibrationController = AAPACore.Instance.CalibrationController;

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
                });
            };

            AAPACore.Instance.AutoPilotStateChanged += (_, isRunning) => {
                Application.Current?.Dispatcher.InvokeAsync(() => {
                    if (!AAPACore.Instance.IsCalibrationRunning) AutoPilotRunning = isRunning;
                });
            };

            AAPACore.Instance.CalibrationStateChanged += (_, isRunning) => {
                Application.Current?.Dispatcher.InvokeAsync(() => {
                    if (!AAPACore.Instance.IsAutoPilotRunning) AutoPilotRunning = isRunning;
                });
            };

            Properties.Settings.Default.PropertyChanged += (_, e) => {
                Application.Current?.Dispatcher.InvokeAsync(() => {
                    var s = Properties.Settings.Default;
                    switch (e.PropertyName) {
                        case nameof(s.ToleranceDegrees): if (ToleranceDegrees != s.ToleranceDegrees) ToleranceDegrees = s.ToleranceDegrees; break;
                        case nameof(s.SettleTimeSeconds): if (SettleTimeSeconds != s.SettleTimeSeconds) SettleTimeSeconds = s.SettleTimeSeconds; break;
                        case nameof(s.MaxIterations): if (MaxIterations != s.MaxIterations) MaxIterations = s.MaxIterations; break;
                        case nameof(s.MaxCorrectionDegrees): if (MaxCorrectionDeg != s.MaxCorrectionDegrees) MaxCorrectionDeg = s.MaxCorrectionDegrees; break;
                        case nameof(s.MotionTimeoutSeconds): if (MotionTimeoutSeconds != s.MotionTimeoutSeconds) MotionTimeoutSeconds = s.MotionTimeoutSeconds; break;
                        case nameof(s.StepsPerRevolution): if (StepsPerRevolution != s.StepsPerRevolution) StepsPerRevolution = s.StepsPerRevolution; break;
                        case nameof(s.AzimuthMicrosteps): if (AzimuthMicrosteps != s.AzimuthMicrosteps) AzimuthMicrosteps = s.AzimuthMicrosteps; break;
                        case nameof(s.AltitudeMicrosteps): if (AltitudeMicrosteps != s.AltitudeMicrosteps) AltitudeMicrosteps = s.AltitudeMicrosteps; break;
                        case nameof(s.AzimuthGearRatio): if (AzimuthGearRatio != s.AzimuthGearRatio) AzimuthGearRatio = s.AzimuthGearRatio; break;
                        case nameof(s.AltitudeGearRatio): if (AltitudeGearRatio != s.AltitudeGearRatio) AltitudeGearRatio = s.AltitudeGearRatio; break;
                        case nameof(s.ReverseAzimuth): if (ReverseAzimuth != s.ReverseAzimuth) ReverseAzimuth = s.ReverseAzimuth; break;
                        case nameof(s.ReverseAltitude): if (ReverseAltitude != s.ReverseAltitude) ReverseAltitude = s.ReverseAltitude; break;
                        case nameof(s.AzimuthBacklash): if (AzimuthBacklash != s.AzimuthBacklash) AzimuthBacklash = s.AzimuthBacklash; break;
                        case nameof(s.AltitudeBacklash): if (AltitudeBacklash != s.AltitudeBacklash) AltitudeBacklash = s.AltitudeBacklash; break;
                        
                        case nameof(s.AzimuthSpeed): if (AzimuthSpeed != s.AzimuthSpeed) AzimuthSpeed = s.AzimuthSpeed; break;
                        case nameof(s.AltitudeSpeed): if (AltitudeSpeed != s.AltitudeSpeed) AltitudeSpeed = s.AltitudeSpeed; break;
                        case nameof(s.AzimuthAccel): if (AzimuthAccel != s.AzimuthAccel) AzimuthAccel = s.AzimuthAccel; break;
                        case nameof(s.AltitudeAccel): if (AltitudeAccel != s.AltitudeAccel) AltitudeAccel = s.AltitudeAccel; break;
                        case nameof(s.MinYLimit): if (MinYLimit != s.MinYLimit) MinYLimit = s.MinYLimit; break;
                        case nameof(s.MaxYLimit): if (MaxYLimit != s.MaxYLimit) MaxYLimit = s.MaxYLimit; break;
                        case nameof(s.CalibrationSteps): if (CalibrationSteps != s.CalibrationSteps) CalibrationSteps = s.CalibrationSteps; break;
                        case nameof(s.LastIpAddress): if (IpAddress != s.LastIpAddress) IpAddress = s.LastIpAddress; break;
                        
                        case nameof(s.EnableWebsocket): if (EnableWebsocket != s.EnableWebsocket) EnableWebsocket = s.EnableWebsocket; break;
                        case nameof(s.WebsocketPort): if (WebsocketPort != s.WebsocketPort) WebsocketPort = s.WebsocketPort; break;
                        case nameof(s.NudgeDegrees): if (NudgeDegrees != s.NudgeDegrees) NudgeDegrees = s.NudgeDegrees; break;
                    }
                });
            };

            this.PropertyChanged += (_, e) => {
                var s = Properties.Settings.Default;
                switch (e.PropertyName) {
                    case nameof(ToleranceDegrees): if (s.ToleranceDegrees != ToleranceDegrees) s.ToleranceDegrees = ToleranceDegrees; break;
                    case nameof(SettleTimeSeconds): if (s.SettleTimeSeconds != SettleTimeSeconds) s.SettleTimeSeconds = SettleTimeSeconds; break;
                    case nameof(MaxIterations): if (s.MaxIterations != MaxIterations) s.MaxIterations = MaxIterations; break;
                    case nameof(MaxCorrectionDeg): if (s.MaxCorrectionDegrees != MaxCorrectionDeg) s.MaxCorrectionDegrees = MaxCorrectionDeg; break;
                    case nameof(MotionTimeoutSeconds): if (s.MotionTimeoutSeconds != MotionTimeoutSeconds) s.MotionTimeoutSeconds = MotionTimeoutSeconds; break;
                    case nameof(StepsPerRevolution): if (s.StepsPerRevolution != StepsPerRevolution) s.StepsPerRevolution = StepsPerRevolution; break;
                    case nameof(AzimuthMicrosteps): if (s.AzimuthMicrosteps != AzimuthMicrosteps) s.AzimuthMicrosteps = AzimuthMicrosteps; break;
                    case nameof(AltitudeMicrosteps): if (s.AltitudeMicrosteps != AltitudeMicrosteps) s.AltitudeMicrosteps = AltitudeMicrosteps; break;
                    case nameof(AzimuthGearRatio): if (s.AzimuthGearRatio != AzimuthGearRatio) s.AzimuthGearRatio = AzimuthGearRatio; break;
                    case nameof(AltitudeGearRatio): if (s.AltitudeGearRatio != AltitudeGearRatio) s.AltitudeGearRatio = AltitudeGearRatio; break;
                    case nameof(ReverseAzimuth): if (s.ReverseAzimuth != ReverseAzimuth) s.ReverseAzimuth = ReverseAzimuth; break;
                    case nameof(ReverseAltitude): if (s.ReverseAltitude != ReverseAltitude) s.ReverseAltitude = ReverseAltitude; break;
                    case nameof(AzimuthBacklash): if (s.AzimuthBacklash != AzimuthBacklash) s.AzimuthBacklash = AzimuthBacklash; break;
                    case nameof(AltitudeBacklash): if (s.AltitudeBacklash != AltitudeBacklash) s.AltitudeBacklash = AltitudeBacklash; break;
                    
                    case nameof(AzimuthSpeed): if (s.AzimuthSpeed != AzimuthSpeed) s.AzimuthSpeed = AzimuthSpeed; break;
                    case nameof(AltitudeSpeed): if (s.AltitudeSpeed != AltitudeSpeed) s.AltitudeSpeed = AltitudeSpeed; break;
                    case nameof(AzimuthAccel): if (s.AzimuthAccel != AzimuthAccel) s.AzimuthAccel = AzimuthAccel; break;
                    case nameof(AltitudeAccel): if (s.AltitudeAccel != AltitudeAccel) s.AltitudeAccel = AltitudeAccel; break;
                    case nameof(MinYLimit): if (s.MinYLimit != MinYLimit) s.MinYLimit = MinYLimit; break;
                    case nameof(MaxYLimit): if (s.MaxYLimit != MaxYLimit) s.MaxYLimit = MaxYLimit; break;
                    case nameof(CalibrationSteps): if (s.CalibrationSteps != CalibrationSteps) s.CalibrationSteps = CalibrationSteps; break;
                    
                    case nameof(EnableWebsocket): if (s.EnableWebsocket != EnableWebsocket) s.EnableWebsocket = EnableWebsocket; break;
                    case nameof(WebsocketPort): if (s.WebsocketPort != WebsocketPort) s.WebsocketPort = WebsocketPort; break;
                    case nameof(NudgeDegrees): if (s.NudgeDegrees != NudgeDegrees) s.NudgeDegrees = NudgeDegrees; break;
                }
            };

            RefreshPortsImpl();
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

        private int _calibrationSteps = Properties.Settings.Default.CalibrationSteps;
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

        private int _motionTimeoutSeconds = Properties.Settings.Default.MotionTimeoutSeconds;
        public int MotionTimeoutSeconds { get => _motionTimeoutSeconds; set { _motionTimeoutSeconds = value; RaisePropertyChanged(); } }

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

        private long _minYLimit = Properties.Settings.Default.MinYLimit;
        public long MinYLimit { get => _minYLimit; set { _minYLimit = value; RaisePropertyChanged(); } }

        private long _maxYLimit = Properties.Settings.Default.MaxYLimit;
        public long MaxYLimit { get => _maxYLimit; set { _maxYLimit = value; RaisePropertyChanged(); } }
        
        private bool _enableWebsocket = Properties.Settings.Default.EnableWebsocket;
        public bool EnableWebsocket { get => _enableWebsocket; set { _enableWebsocket = value; RaisePropertyChanged(); } }
        
        private int _websocketPort = Properties.Settings.Default.WebsocketPort;
        public int WebsocketPort { get => _websocketPort; set { _websocketPort = value; RaisePropertyChanged(); } }

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

        private double _nudgeDegrees = Properties.Settings.Default.NudgeDegrees;
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
            SaveSettingsImpl();
            AAPACore.Instance.StartAutoPilot();
        }

        private RelayCommand? _stopAutoPilotCommand;
        public RelayCommand StopAutoPilotCommand => _stopAutoPilotCommand ??= new RelayCommand((object o) => {
            AAPACore.Instance.StopAutoPilot();
            AAPACore.Instance.StopCalibration();
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
            if (s.ConnectionType != SelectedConnectionType) s.ConnectionType = SelectedConnectionType;
            if (s.LastIpAddress != IpAddress) s.LastIpAddress = IpAddress;
            if (s.StepsPerRevolution != StepsPerRevolution) s.StepsPerRevolution = StepsPerRevolution;
            if (s.AzimuthMicrosteps != AzimuthMicrosteps) s.AzimuthMicrosteps = AzimuthMicrosteps;
            if (s.AltitudeMicrosteps != AltitudeMicrosteps) s.AltitudeMicrosteps = AltitudeMicrosteps;
            if (s.AzimuthGearRatio != AzimuthGearRatio) s.AzimuthGearRatio = AzimuthGearRatio;
            if (s.AltitudeGearRatio != AltitudeGearRatio) s.AltitudeGearRatio = AltitudeGearRatio;
            if (s.ReverseAzimuth != ReverseAzimuth) s.ReverseAzimuth = ReverseAzimuth;
            if (s.ReverseAltitude != ReverseAltitude) s.ReverseAltitude = ReverseAltitude;
            if (s.AzimuthBacklash != AzimuthBacklash) s.AzimuthBacklash = AzimuthBacklash;
            if (s.AltitudeBacklash != AltitudeBacklash) s.AltitudeBacklash = AltitudeBacklash;
            if (s.ToleranceDegrees != ToleranceDegrees) s.ToleranceDegrees = ToleranceDegrees;
            if (s.SettleTimeSeconds != SettleTimeSeconds) s.SettleTimeSeconds = SettleTimeSeconds;
            if (s.MaxIterations != MaxIterations) s.MaxIterations = MaxIterations;
            if (s.MotionTimeoutSeconds != MotionTimeoutSeconds) s.MotionTimeoutSeconds = MotionTimeoutSeconds;
            if (s.MaxCorrectionDegrees != MaxCorrectionDeg) s.MaxCorrectionDegrees = MaxCorrectionDeg;
            if (s.AzimuthSpeed != AzimuthSpeed) s.AzimuthSpeed = AzimuthSpeed;
            if (s.AltitudeSpeed != AltitudeSpeed) s.AltitudeSpeed = AltitudeSpeed;
            if (s.AzimuthAccel != AzimuthAccel) s.AzimuthAccel = AzimuthAccel;
            if (s.AltitudeAccel != AltitudeAccel) s.AltitudeAccel = AltitudeAccel;
            if (s.MinYLimit != MinYLimit) s.MinYLimit = MinYLimit;
            if (s.MaxYLimit != MaxYLimit) s.MaxYLimit = MaxYLimit;
            if (s.CalibrationSteps != CalibrationSteps) s.CalibrationSteps = CalibrationSteps;
            if (s.EnableWebsocket != EnableWebsocket) s.EnableWebsocket = EnableWebsocket;
            if (s.WebsocketPort != WebsocketPort) s.WebsocketPort = WebsocketPort;
            if (s.NudgeDegrees != NudgeDegrees) s.NudgeDegrees = NudgeDegrees;
            CoreUtil.SaveSettings(s);
            AAPACore.Instance.UpdateEngineSettings();
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
            AAPACore.Instance.UpdateEngineSettings();
            AutoPilotRunning = true; // Use same flag to disable UI
            
            _ = Task.Run(async () => {
                var result = await AAPACore.Instance.StartCalibrationAsync(axis, CalibrationSteps, CancellationToken.None);
                
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



        private void AppendLog(string message) {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            LogText = (LogText + "\n" + line).TrimStart('\n');
            var lines = LogText.Split('\n');
            if (lines.Length > 200) LogText = string.Join("\n", lines[^200..]);
            AAPAWebSocketServer.Broadcast(line);
        }
    }
}
