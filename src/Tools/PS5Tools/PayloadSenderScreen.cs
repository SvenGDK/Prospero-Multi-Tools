// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Application;
using SharpProspero.Platform;
using SharpProspero.Storage;
using SharpProspero.Ui;
using System;

namespace ProsperoMultiTools.Tools.PS5Tools;

internal sealed class PayloadSenderScreen : MultiToolsScreen
{
    private enum SendPhase { Idle, Connecting, SendingData, WaitingReply }

    private string _host;
    private int _port;
    private string _filePath;
    private readonly KeyValueRow _statusRow = new("Status", "Ready");

    private SendPhase _phase = SendPhase.Idle;
    private TcpConnection? _conn;
    private SocketPoller? _poller;
    private DeviceFileStream? _fileStream;
    private long _fileLength;
    private byte[]? _chunkBuffer;
    private long _bytesSent;
    private double _phaseStart = -1;

    private const double ConnectTimeoutSec = 5.0;
    private const double ReplyTimeoutSec = 2.0;
    private const int MaxBytesPerTick = 256 * 1024;
    private const int ChunkSize = 65536;

    /// <summary>Creates the sender with the default settings from the shell.</summary>
    public PayloadSenderScreen(MultiToolsShell shell) : base(shell)
    {
        _host = shell.Settings.PayloadHost;
        _port = shell.Settings.PayloadPort;
        _filePath = "";
    }

    public override string Title => "Payload Sender";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 500 };

        menu.Add(new Label("Send an ELF payload to the target device.") { TextColor = Shell.Theme.TextMuted });
        menu.Add(new Separator());

        menu.Add(new Button($"Host: {_host}", () =>
        {
            if (_phase != SendPhase.Idle)
                return;
            Shell.Dialogs.AskText("Target host", _host, value =>
            {
                if (value is not null)
                {
                    _host = value;
                    Shell.Settings.PayloadHost = value;
                    RebuildScreen();
                }
            });
        }));

        menu.Add(new Button($"Port: {_port}", () =>
        {
            if (_phase != SendPhase.Idle)
                return;
            Shell.Dialogs.AskText("Target port", _port.ToString(), value =>
            {
                if (value is not null && int.TryParse(value, out int parsed) && parsed > 0 && parsed <= 65535)
                {
                    _port = parsed;
                    Shell.Settings.PayloadPort = parsed;
                    RebuildScreen();
                }
            });
        }));

        menu.Add(new Separator());

        menu.Add(new Button($"File: {(_filePath.Length > 0 ? _filePath : "(not selected)")}", () =>
        {
            if (_phase != SendPhase.Idle)
                return;
            FilePickerScreen.PickFile(Shell, "Pick a payload .elf",
                _filePath.Length > 0 ? _filePath : (Places.StartingPoint(Shell.Settings.StartPath) ?? ""),
                [".elf", ".bin"],
                value =>
                {
                    if (!string.IsNullOrEmpty(value))
                    {
                        _filePath = value;
                        RebuildScreen();
                    }
                });
        }));

        menu.Add(new Separator());
        menu.Add(_statusRow);
        menu.Add(new Separator());

        if (_phase == SendPhase.Idle)
            menu.Add(new Button("Send", () => StartSend()));
        else
            menu.Add(new Button("Cancel", () => CancelSend()));

        return menu;
    }

    public override void Tick(FrameContext context)
    {
        switch (_phase)
        {
            case SendPhase.Connecting:
                TickConnecting(context);
                break;
            case SendPhase.SendingData:
                TickSendingData();
                break;
            case SendPhase.WaitingReply:
                TickWaitingReply(context);
                break;
        }
    }

    private void StartSend()
    {
        if (string.IsNullOrEmpty(_filePath))
        {
            Shell.Notify("Select a file first.");
            return;
        }

        if (!FileSystem.Exists(_filePath))
        {
            Shell.Notify($"File not found: {_filePath}");
            return;
        }

        _statusRow.Value = "Opening file...";

        DeviceFileStream stream;
        long length;
        try
        {
            stream = FileSystem.OpenRead(_filePath);
            length = stream.Length;
        }
        catch (Exception e)
        {
            _statusRow.Value = $"Open failed: {e.Message}";
            return;
        }

        SocketAddress address;
        try
        {
            address = ResolveAddress(_host, _port);
        }
        catch (Exception e)
        {
            stream.Dispose();
            _statusRow.Value = $"Address error: {e.Message}";
            return;
        }

        _statusRow.Value = $"Connecting to {_host}:{_port}...";

        try
        {
            _fileStream = stream;
            _fileLength = length;
            _chunkBuffer = new byte[ChunkSize];
            _bytesSent = 0;
            _phaseStart = -1;
            _conn = TcpConnection.BeginConnect(address);
            _poller = SocketPoller.Create();
            _poller.Add(_conn.Handle, PollEvents.Write, 0);
            _phase = SendPhase.Connecting;
            RebuildScreen();
        }
        catch (Exception e)
        {
            _statusRow.Value = $"Connect failed: {e.Message}";
            CleanupSendResources();
        }
    }

    private void TickConnecting(FrameContext context)
    {
        if (_phaseStart < 0)
            _phaseStart = context.TotalSeconds;

        double elapsed = context.TotalSeconds - _phaseStart;
        if (elapsed > ConnectTimeoutSec)
        {
            _statusRow.Value = "Connection timed out.";
            FinishSend(false);
            return;
        }

        Span<PollReady> ready = stackalloc PollReady[1];
        int count;
        try
        {
            count = _poller!.Wait(ready, 0);
        }
        catch (Exception e)
        {
            _statusRow.Value = $"Poll failed: {e.Message}";
            FinishSend(false);
            return;
        }

        if (count == 0)
            return;

        if (ready[0].IsClosed)
        {
            _statusRow.Value = "Connection refused.";
            FinishSend(false);
            return;
        }

        if (ready[0].IsWritable)
        {
            int err = _conn!.GetSocketError();
            if (err != 0)
            {
                _statusRow.Value = $"Connect error: {err}";
                FinishSend(false);
                return;
            }

            _conn.Blocking = true;
            _phase = SendPhase.SendingData;
            _statusRow.Value = $"Sending {Formatting.FormatSize(_fileLength)}...";
        }
    }

    private void TickSendingData()
    {
        if (_fileStream is null || _chunkBuffer is null || _conn is null)
        {
            FinishSend(false);
            return;
        }

        try
        {
            int budget = MaxBytesPerTick;

            while (budget > 0 && _bytesSent < _fileLength)
            {
                int toRead = (int)Math.Min(_chunkBuffer.Length, Math.Min(_fileLength - _bytesSent, budget));
                int read = _fileStream.Read(_chunkBuffer, 0, toRead);
                if (read <= 0)
                    break;
                _conn.SendAll(_chunkBuffer.AsSpan(0, read));
                _bytesSent += read;
                budget -= read;
            }

            _statusRow.Value = $"Sending... {Formatting.FormatSize(_bytesSent)} / {Formatting.FormatSize(_fileLength)}";

            if (_bytesSent >= _fileLength)
            {
                _phase = SendPhase.WaitingReply;
                _phaseStart = -1;
                _statusRow.Value = "Sent. Checking for response...";
            }
        }
        catch (Exception e)
        {
            _statusRow.Value = $"Send failed: {e.Message}";
            FinishSend(false);
        }
    }

    private void TickWaitingReply(FrameContext context)
    {
        if (_phaseStart < 0)
        {
            _phaseStart = context.TotalSeconds;
            _conn!.Blocking = false;
            try
            {
                _poller!.Modify(_conn.Handle, PollEvents.Read, 0);
            }
            catch
            {
                _statusRow.Value = "Done.";
                FinishSend(true);
                return;
            }
            return;
        }

        double elapsed = context.TotalSeconds - _phaseStart;
        if (elapsed > ReplyTimeoutSec)
        {
            _statusRow.Value = "Done.";
            FinishSend(true);
            return;
        }

        Span<PollReady> ready = stackalloc PollReady[1];
        int count;
        try
        {
            count = _poller!.Wait(ready, 0);
        }
        catch
        {
            _statusRow.Value = "Done.";
            FinishSend(true);
            return;
        }

        if (count > 0 && (ready[0].IsReadable || ready[0].IsClosed))
        {
            Span<byte> reply = stackalloc byte[256];
            int read = 0;
            try
            {
                read = _conn!.Receive(reply);
            }
            catch
            {
                // No response is a valid outcome.
            }

            if (read > 0)
                _statusRow.Value = $"Done. Response: {read} bytes received.";
            else
                _statusRow.Value = "Done.";

            FinishSend(true);
        }
    }

    private void CancelSend()
    {
        _statusRow.Value = "Cancelled.";
        FinishSend(false);
    }

    private void FinishSend(bool success)
    {
        CleanupSendResources();
        _phase = SendPhase.Idle;
        _phaseStart = -1;
        RebuildScreen();
        if (success)
            Shell.Notify("Transfer complete.");
    }

    private void CleanupSendResources()
    {
        _poller?.Dispose();
        _poller = null;
        _conn?.Dispose();
        _conn = null;
        _fileStream?.Dispose();
        _fileStream = null;
        _chunkBuffer = null;
        _fileLength = 0;
        _bytesSent = 0;
    }

    protected override void OnDispose()
    {
        CleanupSendResources();
    }

    private static SocketAddress ResolveAddress(string host, int port)
    {
        if (host == "localhost" || host == "127.0.0.1")
            return SocketAddress.Loopback(port);

        if (SocketAddress.TryParse(host, port, out SocketAddress address))
            return address;

        using var dns = HostResolver.Create();
        return dns.Resolve(host, port);
    }
}
