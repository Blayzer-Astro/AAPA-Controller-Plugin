using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Fleck;
using Newtonsoft.Json.Linq;
using NINA.Plugins.AAPA.Alignment;

namespace NINA.Plugins.AAPA {
    public static class AAPAWebSocketServer {
        private static WebSocketServer _server;
        private static System.Collections.Concurrent.ConcurrentDictionary<Guid, IWebSocketConnection> _sockets = new System.Collections.Concurrent.ConcurrentDictionary<Guid, IWebSocketConnection>();
        private static bool _eventsWired = false;

        // AutoPilot state
        private static int _autoPilotIteration = 0;
        private static JObject _lastCorrection = null;

        // TPPA state
        private static JObject _lastTppaError = null;

        public static void Start() {
            if (!Properties.Settings.Default.EnableWebsocket) return;
            if (_server != null) return;
            try {
                int port = Properties.Settings.Default.WebsocketPort;
                if (port <= 0 || port > 65535) port = 8081;

                _server = new WebSocketServer($"ws://0.0.0.0:{port}");
                _server.Start(socket => {
                    socket.OnOpen = () => {
                        _sockets.TryAdd(socket.ConnectionInfo.Id, socket);
                        Broadcast("{\"status\": \"CONNECTED\"}");
                        SendState(socket);
                    };
                    socket.OnClose = () => _sockets.TryRemove(socket.ConnectionInfo.Id, out _);
                    socket.OnMessage = message => HandleMessage(socket, message);
                });

                if (!_eventsWired) {
                    _eventsWired = true;
                    AAPACore.Instance.CoreLogMessage += (_, msg) => BroadcastLog(msg);
                    
                    AAPACore.Instance.Controller.StatusUpdated += (_, status) => BroadcastState();
                    AAPACore.Instance.Controller.ConnectionChanged += (_, isConnected) => BroadcastState();

                    // Auto-Pilot hooks
                    AAPACore.Instance.AutoPilotStateChanged += (_, isRunning) => {
                        if (!isRunning) {
                            _autoPilotIteration = 0;
                            _lastCorrection = null;
                        }
                        BroadcastState();
                    };
                    AAPACore.Instance.AutoPilot.ProgressUpdated += (_, e) => {
                        _autoPilotIteration = e.Iteration;
                        if (e.Correction != null) {
                            _lastCorrection = new JObject {
                                ["azSteps"] = e.Correction.AzimuthSteps,
                                ["altSteps"] = e.Correction.AltitudeSteps
                            };
                        }
                        BroadcastState();
                    };

                    // Calibration hooks
                    AAPACore.Instance.CalibrationStateChanged += (_, isRunning) => BroadcastState();

                    // TPPA hooks
                    AAPACore.Instance.LogMonitor.ErrorDetected += (_, error) => {
                        _lastTppaError = new JObject {
                            ["azErrorDeg"] = error.AzimuthErrorDegrees,
                            ["altErrorDeg"] = error.AltitudeErrorDegrees,
                            ["totalErrorArcSec"] = error.TotalErrorArcSec,
                            ["timestamp"] = DateTime.UtcNow.ToString("O")
                        };
                        BroadcastState();
                    };

                    Properties.Settings.Default.PropertyChanged += (_, e) => {
                        if (e.PropertyName == "EnableWebsocket" || e.PropertyName == "WebsocketPort") {
                            Task.Run(() => Restart());
                        }
                        BroadcastState();
                    };
                }
            } catch (Exception ex) {
                Debug.WriteLine($"WebSocket Server failed to start: {ex.Message}");
            }
        }

        public static void Restart() {
            Stop();
            Start();
        }

        public static void Stop() {
            if (_server != null) {
                foreach (var s in _sockets.Values) s.Close();
                _sockets.Clear();
                _server.Dispose();
                _server = null;
            }
        }

        public static void Broadcast(string message) {
            foreach (var socket in _sockets.Values) {
                try {
                    socket.Send(message);
                } catch {
                    // Ignore send errors, OnClose will clean up dead sockets
                }
            }
        }

        public static void BroadcastLog(string message) {
            var line = FormattableString.Invariant($"[{DateTime.Now:HH:mm:ss}] {message}");
            var json = new JObject {
                ["type"] = "log",
                ["message"] = line
            };
            Broadcast(json.ToString());
        }

        public static void BroadcastState() {
            var state = GetStateJson();
            Broadcast(state.ToString());
        }

        private static void SendState(IWebSocketConnection socket) {
            try {
                socket.Send(GetStateJson().ToString());
            } catch { }
        }

        private static JObject GetStateJson() {
            var props = Properties.Settings.Default;
            var ctrl = AAPACore.Instance.Controller;
            var ports = new JArray(System.IO.Ports.SerialPort.GetPortNames());
            return new JObject {
                ["type"] = "state",
                ["protocolVersion"] = 2,
                ["supportedCommands"] = new JArray(
                    "START_AUTOPILOT", "STOP_AUTOPILOT", 
                    "HOME", "SET_HOME", "HOME_AZ", "HOME_ALT", "RESET_X", "RESET_Y", 
                    "STOP", "SEND_SPEED_ACCEL", 
                    "CALIBRATE_AZ", "CALIBRATE_ALT", "CANCEL_CALIBRATION", 
                    "SAVE_SETTINGS", "SEND_TO_AAPA", 
                    "NUDGE_AZ_POS", "NUDGE_AZ_NEG", "NUDGE_ALT_POS", "NUDGE_ALT_NEG"
                ),
                ["isConnected"] = ctrl.IsConnected,
                ["connectedPort"] = ctrl.ConnectedPortName ?? "",
                ["availablePorts"] = ports,
                ["isBusy"] = ctrl.Status.IsBusy,
                ["isHomed"] = ctrl.Status.IsHomed,
                ["positionX"] = ctrl.Status.PositionX,
                ["positionY"] = ctrl.Status.PositionY,
                ["autoPilotRunning"] = AAPACore.Instance.IsAutoPilotRunning,
                ["autoPilotIteration"] = _autoPilotIteration,
                ["lastCorrection"] = _lastCorrection,
                ["tppaError"] = _lastTppaError,
                ["calibrationRunning"] = AAPACore.Instance.IsCalibrationRunning,
                ["settings"] = new JObject {
                    ["ToleranceDegrees"] = props.ToleranceDegrees,
                    ["SettleTimeSeconds"] = props.SettleTimeSeconds,
                    ["MaxIterations"] = props.MaxIterations,
                    ["MaxCorrectionDeg"] = props.MaxCorrectionDegrees,
                    ["MotionTimeoutSeconds"] = props.MotionTimeoutSeconds,
                    ["StepsPerRevolution"] = props.StepsPerRevolution,
                    ["AzimuthMicrosteps"] = props.AzimuthMicrosteps,
                    ["AltitudeMicrosteps"] = props.AltitudeMicrosteps,
                    ["AzimuthGearRatio"] = props.AzimuthGearRatio,
                    ["AltitudeGearRatio"] = props.AltitudeGearRatio,
                    ["ReverseAzimuth"] = props.ReverseAzimuth,
                    ["ReverseAltitude"] = props.ReverseAltitude,
                    ["AzimuthBacklash"] = props.AzimuthBacklash,
                    ["AltitudeBacklash"] = props.AltitudeBacklash,
                    ["AzimuthSpeed"] = props.AzimuthSpeed,
                    ["AltitudeSpeed"] = props.AltitudeSpeed,
                    ["AzimuthAccel"] = props.AzimuthAccel,
                    ["AltitudeAccel"] = props.AltitudeAccel,
                    ["MinYLimit"] = props.MinYLimit,
                    ["MaxYLimit"] = props.MaxYLimit,
                    ["CalibrationSteps"] = props.CalibrationSteps,
                    ["NudgeDegrees"] = props.NudgeDegrees,
                    ["LastIpAddress"] = props.LastIpAddress
                }
            };
        }

        private static async void HandleMessage(IWebSocketConnection socket, string message) {
            try {
                if (!message.Trim().StartsWith("{")) {
                    HandleLegacyMessage(message);
                    return;
                }

                var json = JObject.Parse(message);
                var action = json["action"]?.ToString();

                if (action == "set") {
                    var property = json["property"]?.ToString();
                    var value = json["value"];
                    if (property != null && value != null) {
                        UpdateSetting(property, value);
                    }
                } 
                else if (action == "command") {
                    var cmd = json["command"]?.ToString();
                    if (cmd == "Connect") {
                        var port = json["port"]?.ToString();
                        _ = AAPACore.Instance.Controller.ConnectAsync(port);
                    }
                    else if (cmd == "Disconnect") {
                        AAPACore.Instance.Controller.Disconnect();
                    }
                    else {
                        HandleCommand(cmd, json);
                    }
                }
            } catch (Exception ex) {
                BroadcastLog($"WS Error: {ex.Message}");
            }
        }

        private static void UpdateSetting(string property, JToken value) {
            var props = Properties.Settings.Default;
            try {
                switch (property) {
                    case "ToleranceDegrees": props.ToleranceDegrees = value.Value<double>(); break;
                    case "SettleTimeSeconds": props.SettleTimeSeconds = value.Value<int>(); break;
                    case "MaxIterations": props.MaxIterations = value.Value<int>(); break;
                    case "MaxCorrectionDeg": props.MaxCorrectionDegrees = value.Value<double>(); break;
                    case "MotionTimeoutSeconds": props.MotionTimeoutSeconds = value.Value<int>(); break;
                    case "StepsPerRevolution": props.StepsPerRevolution = value.Value<int>(); break;
                    case "AzimuthMicrosteps": props.AzimuthMicrosteps = value.Value<int>(); break;
                    case "AltitudeMicrosteps": props.AltitudeMicrosteps = value.Value<int>(); break;
                    case "AzimuthGearRatio": props.AzimuthGearRatio = value.Value<double>(); break;
                    case "AltitudeGearRatio": props.AltitudeGearRatio = value.Value<double>(); break;
                    case "ReverseAzimuth": props.ReverseAzimuth = value.Value<bool>(); break;
                    case "ReverseAltitude": props.ReverseAltitude = value.Value<bool>(); break;
                    case "AzimuthBacklash": props.AzimuthBacklash = value.Value<int>(); break;
                    case "AltitudeBacklash": props.AltitudeBacklash = value.Value<int>(); break;
                    case "AzimuthSpeed": props.AzimuthSpeed = value.Value<int>(); break;
                    case "AltitudeSpeed": props.AltitudeSpeed = value.Value<int>(); break;
                    case "AzimuthAccel": props.AzimuthAccel = value.Value<int>(); break;
                    case "AltitudeAccel": props.AltitudeAccel = value.Value<int>(); break;
                    case "MinYLimit": props.MinYLimit = value.Value<long>(); break;
                    case "MaxYLimit": props.MaxYLimit = value.Value<long>(); break;
                    case "CalibrationSteps": props.CalibrationSteps = value.Value<int>(); break;
                    case "NudgeDegrees": props.NudgeDegrees = value.Value<double>(); break;
                    case "LastIpAddress": props.LastIpAddress = value.Value<string>(); break;
                }
                props.Save();
                AAPACore.Instance.UpdateEngineSettings();
            } catch (Exception ex) {
                BroadcastLog($"Error setting {property}: {ex.Message}");
            }
        }

        private static void HandleLegacyMessage(string message) {
            var cmd = message.ToUpperInvariant().Trim();
            HandleCommand(cmd, null);
        }

        private static void HandleCommand(string cmd, JObject? json) {
            var ctrl = AAPACore.Instance.Controller;
            switch (cmd) {
                case "START_AUTOPILOT":
                case "StartAutoPilot":
                    AAPACore.Instance.StartAutoPilot();
                    break;
                case "STOP_AUTOPILOT":
                case "StopAutoPilot":
                    AAPACore.Instance.StopAutoPilot();
                    break;
                case "STOP":
                    _ = ctrl.StopAsync();
                    break;
                case "HOME":
                    _ = ctrl.HomeAzimuthAsync();
                    _ = ctrl.HomeAltitudeAsync();
                    break;
                case "HOME_AZ":
                    _ = ctrl.HomeAzimuthAsync();
                    break;
                case "HOME_ALT":
                    _ = ctrl.HomeAltitudeAsync();
                    break;
                case "SET_HOME":
                    _ = ctrl.ResetXAsync();
                    _ = ctrl.ResetYAsync();
                    break;
                case "RESET_X":
                    _ = ctrl.ResetXAsync();
                    break;
                case "RESET_Y":
                    _ = ctrl.ResetYAsync();
                    break;
                case "SEND_SPEED_ACCEL":
                    _ = Task.Run(async () => {
                        var props = Properties.Settings.Default;
                        await ctrl.SetAzimuthSpeedAsync(props.AzimuthSpeed);
                        await ctrl.SetAltitudeSpeedAsync(props.AltitudeSpeed);
                        await ctrl.SetAzimuthAccelAsync(props.AzimuthAccel);
                        await ctrl.SetAltitudeAccelAsync(props.AltitudeAccel);
                        await ctrl.SetMinYAsync(props.MinYLimit);
                        await ctrl.SetMaxYAsync(props.MaxYLimit);
                        BroadcastLog("Speed, Accel, and Limits sent to AAPA.");
                    });
                    break;
                case "CALIBRATE_AZ":
                    int azSteps = json?["steps"]?.Value<int>() ?? 10000;
                    _ = DoCalibrationAsync(CalibrationAxis.Azimuth, azSteps);
                    break;
                case "CALIBRATE_ALT":
                    int altSteps = json?["steps"]?.Value<int>() ?? 10000;
                    _ = DoCalibrationAsync(CalibrationAxis.Altitude, altSteps);
                    break;
                case "CANCEL_CALIBRATION":
                    AAPACore.Instance.StopCalibration();
                    break;
                case "SAVE_SETTINGS":
                    Properties.Settings.Default.Save();
                    AAPACore.Instance.UpdateEngineSettings();
                    BroadcastLog("Settings saved in NINA plugin.");
                    break;
                case "SEND_TO_AAPA":
                    _ = ctrl.SaveConfigAsync();
                    BroadcastLog("Config sent and saved to AAPA Arduino EEPROM.");
                    break;
                case "NUDGE_AZ_POS": {
                    double deg = json?["degrees"]?.Value<double>() ?? AAPACore.Instance.Engine.MaxCorrectionPerIterationDegrees;
                    _ = NudgeAsync(deg, 0);
                    break;
                }
                case "NUDGE_AZ_NEG": {
                    double deg = json?["degrees"]?.Value<double>() ?? AAPACore.Instance.Engine.MaxCorrectionPerIterationDegrees;
                    _ = NudgeAsync(-deg, 0);
                    break;
                }
                case "NUDGE_ALT_POS": {
                    double deg = json?["degrees"]?.Value<double>() ?? AAPACore.Instance.Engine.MaxCorrectionPerIterationDegrees;
                    _ = NudgeAsync(0, deg);
                    break;
                }
                case "NUDGE_ALT_NEG": {
                    double deg = json?["degrees"]?.Value<double>() ?? AAPACore.Instance.Engine.MaxCorrectionPerIterationDegrees;
                    _ = NudgeAsync(0, -deg);
                    break;
                }
            }
        }

        private static async Task NudgeAsync(double azDegrees, double altDegrees) {
            var engine = AAPACore.Instance.Engine;
            var ctrl = AAPACore.Instance.Controller;
            if (ctrl.Status.IsBusy) return;

            var error = new PolarAlignmentError(azDegrees, altDegrees);
            var correction = engine.Calculate(error);
            await ctrl.MoveBothAxesAsync(correction.AzimuthSteps, correction.AltitudeSteps, CancellationToken.None);
        }

        private static async Task DoCalibrationAsync(CalibrationAxis axis, int steps) {
            try {
                if (AAPACore.Instance.IsCalibrationRunning) return;

                var result = await AAPACore.Instance.StartCalibrationAsync(axis, steps, CancellationToken.None);
                if (result.Success) {
                    var props = Properties.Settings.Default;
                    if (axis == CalibrationAxis.Azimuth) props.AzimuthGearRatio = result.NewGearRatio;
                    else props.AltitudeGearRatio = result.NewGearRatio;
                    props.Save();
                }
                BroadcastLog(result.Message);
            } catch (Exception ex) {
                BroadcastLog($"Calibration Error: {ex.Message}");
            }
        }
    }
}
