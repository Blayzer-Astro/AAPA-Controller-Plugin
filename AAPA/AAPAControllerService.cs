using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NINA.Core.Utility;

namespace NINA.Plugins.AAPA {

    public class AAPAControllerService : IDisposable {
        private SerialPort? _serialPort;
        private TcpClient? _tcpClient;
        private StreamReader? _tcpReader;
        private StreamWriter? _tcpWriter;
        private bool _isTcp = false;
        
        private readonly object _portLock = new object();
        private CancellationTokenSource? _pollCts;
        private Task? _pollTask;
        private bool _disposed;

        public bool IsConnected { get; private set; }
        public string? ConnectedPortName { get; private set; }
        public AAPAStatus Status { get; private set; } = new AAPAStatus();

        public event EventHandler<AAPAStatus>? StatusUpdated;
        public event EventHandler<bool>? ConnectionChanged;
        public event EventHandler<string>? LogMessage;

        private static readonly Regex StatusRegex = new Regex(
            @"POSX:(?<x>-?\d+)\s+POSY:(?<y>-?\d+)\s+BUSYX:(?<busyx>\d)\s+BUSYY:(?<busyy>\d)\s+HOMED:(?<homed>\d).*?MINY:(?<miny>-?\d+)\s+MAXY:(?<maxy>-?\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex IdRegex = new Regex(
            @"POSX:|BUSYX:|HOMED:",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public async Task<bool> ConnectAsync(string? portName = null, CancellationToken ct = default) {
            Disconnect();

            // Check if user entered an IP address
            if (!string.IsNullOrWhiteSpace(portName) && IPAddress.TryParse(portName, out _)) {
                return await ConnectTcpAsync(portName, ct);
            }

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
                    Encoding = Encoding.ASCII,
                    DtrEnable = false,
                    RtsEnable = false
                };

                try {
                    sp.Open();
                    await Task.Delay(150, ct);
                    sp.DiscardInBuffer();

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
                        lock (_portLock) { 
                            _serialPort = sp; 
                            _isTcp = false;
                        }
                        ConnectedPortName = port;
                        IsConnected = true;

                        await QueryStatusAsync();
                        ConnectionChanged?.Invoke(this, true);

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

        private async Task<bool> ConnectTcpAsync(string ipAddress, CancellationToken ct) {
            Log($"Connecting via TCP to {ipAddress}...");
            try {
                _tcpClient = new TcpClient();
                _tcpClient.ReceiveTimeout = 500;
                _tcpClient.SendTimeout = 500;
                await _tcpClient.ConnectAsync(ipAddress, 23);
                var stream = _tcpClient.GetStream();
                _tcpReader = new StreamReader(stream, Encoding.ASCII);
                _tcpWriter = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\n", AutoFlush = true };
                
                lock (_portLock) { _isTcp = true; }

                await Task.Delay(150, ct);
                
                // discard in buffer
                while (stream.DataAvailable) { stream.ReadByte(); }

                _tcpWriter.WriteLine(":STATUS");
                await Task.Delay(100, ct);

                string response = string.Empty;
                var deadline = DateTime.UtcNow.AddMilliseconds(600);
                while (DateTime.UtcNow < deadline) {
                    if (stream.DataAvailable) {
                        var line = await _tcpReader.ReadLineAsync();
                        if (line != null) {
                            response += line.Trim() + " ";
                            if (IdRegex.IsMatch(response)) break;
                        }
                    } else {
                        await Task.Delay(10, ct);
                    }
                }

                if (IdRegex.IsMatch(response)) {
                    Log($"AAPA device found on TCP {ipAddress}");
                    ConnectedPortName = ipAddress;
                    IsConnected = true;
                    
                    await QueryStatusAsync();
                    ConnectionChanged?.Invoke(this, true);
                    
                    _pollCts = new CancellationTokenSource();
                    _pollTask = PollStatusLoopAsync(_pollCts.Token);
                    return true;
                }
            } catch (Exception ex) {
                Log($"Error on TCP {ipAddress}: {ex.Message}");
            } finally {
                if (!IsConnected && _tcpClient != null) {
                    try { _tcpClient.Close(); } catch { }
                    _tcpClient = null;
                }
            }
            Log("No AAPA device found on TCP.");
            return false;
        }

        public void Disconnect() {
            if (IsConnected) {
                lock (_portLock) {
                    if (_isTcp && _tcpWriter != null) {
                        try { _tcpWriter.WriteLine(":SAVE"); Thread.Sleep(100); } catch { }
                    } else if (!_isTcp && _serialPort != null && _serialPort.IsOpen) {
                        try { _serialPort.WriteLine(":SAVE"); Thread.Sleep(100); } catch { }
                    }
                }
            }

            _pollCts?.Cancel();
            _pollCts?.Dispose();
            _pollCts = null;
            _pollTask = null;

            lock (_portLock) {
                if (_serialPort != null) {
                    try { if (_serialPort.IsOpen) _serialPort.Close(); } catch { }
                    _serialPort.Dispose();
                    _serialPort = null;
                }
                if (_tcpClient != null) {
                    try { _tcpClient.Close(); } catch { }
                    _tcpClient = null;
                }
                _tcpReader = null;
                _tcpWriter = null;
            }

            IsConnected = false;
            ConnectedPortName = null;
            ConnectionChanged?.Invoke(this, false);
            Log("Disconnected.");
        }

        public async Task<bool> MoveAzimuthAsync(int steps, CancellationToken ct = default) {
            if (steps == 0) return true;
            return await SendCommandAsync($"X{steps}", ct);
        }

        public async Task<bool> MoveAltitudeAsync(int steps, CancellationToken ct = default) {
            if (steps == 0) return true;
            return await SendCommandAsync($"Y{steps}", ct);
        }

        public async Task<bool> MoveBothAxesAsync(int azSteps, int altSteps, CancellationToken ct = default) {
            bool ok = true;
            if (azSteps != 0)  ok &= await MoveAzimuthAsync(azSteps, ct);
            if (altSteps != 0) ok &= await MoveAltitudeAsync(altSteps, ct);
            return ok;
        }

        public async Task<bool> StopAsync(CancellationToken ct = default) =>
            await SendCommandAsync(":STOP", ct);

        public async Task<bool> HomeAltitudeAsync(CancellationToken ct = default) {
            long distance = -Status.PositionY;
            if (distance == 0) return true;
            return await MoveAltitudeAsync((int)distance, ct);
        }

        public async Task<bool> HomeAzimuthAsync(CancellationToken ct = default) {
            long distance = -Status.PositionX;
            if (distance == 0) return true;
            return await MoveAzimuthAsync((int)distance, ct);
        }

        public async Task<bool> ResetYAsync(CancellationToken ct = default) {
            bool ok = await SendCommandAsync(":RESETY", ct);
            if (ok) {
                Status.IsHomed = true;
                Status.PositionY = 0;
            }
            return ok;
        }

        public async Task<bool> ResetXAsync(CancellationToken ct = default) {
            bool ok = await SendCommandAsync(":RESETX", ct);
            if (ok) {
                Status.PositionX = 0;
            }
            return ok;
        }

        public async Task<bool> SaveConfigAsync(CancellationToken ct = default) =>
            await SendCommandAsync(":SAVE", ct);

        public async Task<bool> SetAzimuthSpeedAsync(int stepsPerSec, CancellationToken ct = default) =>
            await SendCommandAsync($":SPDX {stepsPerSec}", ct);

        public async Task<bool> SetAltitudeSpeedAsync(int stepsPerSec, CancellationToken ct = default) =>
            await SendCommandAsync($":SPDY {stepsPerSec}", ct);

        public async Task<bool> SetAzimuthAccelAsync(int stepsPerSecSq, CancellationToken ct = default) =>
            await SendCommandAsync($":ACCX {stepsPerSecSq}", ct);

        public async Task<bool> SetAltitudeAccelAsync(int stepsPerSecSq, CancellationToken ct = default) =>
            await SendCommandAsync($":ACCY {stepsPerSecSq}", ct);

        public async Task<bool> SetMinYAsync(long limit, CancellationToken ct = default) =>
            await SendCommandAsync($":MINY {limit}", ct);

        public async Task<bool> SetMaxYAsync(long limit, CancellationToken ct = default) =>
            await SendCommandAsync($":MAXY {limit}", ct);

        public async Task<bool> QueryStatusAsync(CancellationToken ct = default) =>
            await SendCommandAsync(":STATUS", ct);

        private async Task<bool> SendCommandAsync(string command, CancellationToken ct) {
            if (!IsConnected) return false;

            return await Task.Run(() => {
                lock (_portLock) {
                    if (_isTcp && _tcpClient == null) return false;
                    if (!_isTcp && (_serialPort == null || !_serialPort.IsOpen)) return false;

                    try {
                        if (_isTcp) {
                            _tcpWriter?.WriteLine(command);
                        } else {
                            _serialPort?.WriteLine(command);
                        }

                        var deadline = DateTime.UtcNow.AddMilliseconds(800);
                        var responseLines = new List<string>();

                        while (DateTime.UtcNow < deadline) {
                            ct.ThrowIfCancellationRequested();
                            try {
                                string? line = null;
                                if (_isTcp) {
                                    if (_tcpClient!.GetStream().DataAvailable) {
                                        line = _tcpReader?.ReadLine()?.Trim();
                                    } else {
                                        Thread.Sleep(10);
                                    }
                                } else {
                                    line = _serialPort?.ReadLine().Trim();
                                }

                                if (!string.IsNullOrWhiteSpace(line)) {
                                    responseLines.Add(line);
                                    if (TryParseStatus(line, out var status)) {
                                        Status = status;
                                        StatusUpdated?.Invoke(this, status);
                                    }
                                }
                            } catch (TimeoutException) {
                                break;
                            } catch (IOException) {
                                break;
                            }
                        }
                        return true;
                    } catch (Exception ex) {
                        Log($"Command '{command}' failed: {ex.Message}");
                        if (ex is InvalidOperationException || ex is IOException || ex is SocketException) {
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
