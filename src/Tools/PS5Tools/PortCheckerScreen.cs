// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Application;
using SharpProspero.Platform;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoMultiTools.Tools.PS5Tools;

internal sealed class PortCheckerScreen : MultiToolsScreen
{
    // Preset list mirrors the sibling desktop toolkit's PS5 port checker so a user who moves
    // between the two sees the same slots labeled the same way. Each row is a service the
    // homebrew ecosystem exposes on a well-known TCP port.
    private static readonly (string Name, int Port)[] Presets =
    [
        ("Payload TCP", 9020),
        ("Payload WS", 9021),
        ("FTP (Web Manager)", 1337),
        ("FTP", 2121),
        ("Klog", 3232),
        ("Kernel exploit", 9081),
        ("Discord RPC", 8000),
        ("Direct PKG installer", 9090),
        ("PKG installer", 12800),
        ("WebSrv", 8080),
        ("SHSrv", 2323),
        ("Lapse", 50000),
    ];

    private readonly List<(string Name, int Port, string Status)> _results = [];
    private readonly Queue<(string Name, int Port)> _checkQueue = new();
    private string _customHost;

    private TcpConnection? _activeConn;
    private SocketPoller? _activePoller;
    private string _activeName = "";
    private int _activePort;
    private double _activeStart = -1;
    private bool _autoChecked;

    private const double CheckTimeoutSec = 3.0;

    public PortCheckerScreen(MultiToolsShell shell) : base(shell)
    {
        // Seed the target from the payload sender setting, which is where the user has already
        // put their console's IP. localhost is only a useful default when the shell is running
        // on the console itself and the payload host is the same machine.
        string configured = shell.Settings.PayloadHost;
        _customHost = string.IsNullOrEmpty(configured) ? "localhost" : configured;
    }

    public override string Title => "Port Checker";

    public override string Hint => "Cross opens, Circle goes back.";

    protected override UiElement BuildRoot()
    {
        var menu = new ScrollMenu { ViewHeight = 500 };

        menu.Add(new Label("Check which TCP ports are open on the target.") { TextColor = Shell.Theme.TextMuted });
        menu.Add(new Separator());

        menu.Add(new Button($"Target: {_customHost}", () =>
        {
            Shell.Dialogs.AskText("Target host", _customHost, value =>
            {
                if (value is not null)
                {
                    _customHost = value;
                    _results.Clear();
                    _autoChecked = false;
                    RebuildScreen();
                }
            });
        }));

        menu.Add(new Separator());
        menu.Add(new Label("Presets") { TextColor = Shell.Theme.Accent });

        foreach (var (name, port) in Presets)
        {
            int p = port;
            string n = name;
            menu.Add(new Button($"Check {n} (port {p})", () => EnqueueCheck(n, p)));
        }

        menu.Add(new Separator());
        menu.Add(new Button("Check custom port", () =>
        {
            Shell.Dialogs.AskText("Port number", "", value =>
            {
                if (value is not null && int.TryParse(value, out int port) && port > 0 && port <= 65535)
                    EnqueueCheck($"Port {port}", port);
            });
        }));

        menu.Add(new Button("Check all presets", CheckAllPresets));

        if (_results.Count > 0)
        {
            menu.Add(new Separator());
            menu.Add(new Label("Results") { TextColor = Shell.Theme.Accent });
            foreach (var (name, port, status) in _results)
                menu.Add(new KeyValueRow($"{name} ({port})", status));
        }

        if (_activeConn is not null)
        {
            menu.Add(new Separator());
            menu.Add(new Label($"Checking {_activeName} ({_activePort})...") { TextColor = Shell.Theme.TextMuted });
        }
        else if (_checkQueue.Count > 0)
        {
            menu.Add(new Separator());
            menu.Add(new Label($"{_checkQueue.Count} check(s) queued...") { TextColor = Shell.Theme.TextMuted });
        }

        menu.Add(new Separator());
        menu.Add(new Button("Clear results", () =>
        {
            _results.Clear();
            RebuildScreen();
        }));

        return menu;
    }

    public override void OnShown()
    {
        // Auto-run every preset the first time the screen appears with a target set, so the
        // user is not asked to press a button to see what's already available on their console.
        if (!_autoChecked && !string.IsNullOrEmpty(_customHost))
        {
            _autoChecked = true;
            CheckAllPresets();
        }
    }

    public override void Tick(FrameContext context)
    {
        if (_activeConn is not null)
            TickActiveCheck(context);
        else if (_checkQueue.Count > 0)
            StartNextCheck();
    }

    private void EnqueueCheck(string name, int port)
    {
        _checkQueue.Enqueue((name, port));
    }

    private void CheckAllPresets()
    {
        _results.Clear();
        foreach (var (name, port) in Presets)
            _checkQueue.Enqueue((name, port));
        RebuildScreen();
    }

    private void StartNextCheck()
    {
        var (name, port) = _checkQueue.Dequeue();

        SocketAddress address;
        try
        {
            address = ResolveAddress(port);
        }
        catch
        {
            RecordResult(name, port, "Could not resolve host");
            return;
        }

        try
        {
            _activeConn = TcpConnection.BeginConnect(address);
            _activePoller = SocketPoller.Create();
            _activePoller.Add(_activeConn.Handle, PollEvents.Write, 0);
            _activeName = name;
            _activePort = port;
            _activeStart = -1;
        }
        catch
        {
            CleanupActiveCheck();
            RecordResult(name, port, "Closed");
        }
    }

    private void TickActiveCheck(FrameContext context)
    {
        if (_activeStart < 0)
            _activeStart = context.TotalSeconds;

        double elapsed = context.TotalSeconds - _activeStart;
        if (elapsed > CheckTimeoutSec)
        {
            string name = _activeName;
            int port = _activePort;
            CleanupActiveCheck();
            RecordResult(name, port, "No response (timed out)");
            return;
        }

        Span<PollReady> ready = stackalloc PollReady[1];
        int count;
        try
        {
            count = _activePoller!.Wait(ready, 0);
        }
        catch
        {
            string name = _activeName;
            int port = _activePort;
            CleanupActiveCheck();
            RecordResult(name, port, "Closed");
            return;
        }

        if (count == 0)
            return;

        string rName = _activeName;
        int rPort = _activePort;

        if (ready[0].IsClosed)
        {
            CleanupActiveCheck();
            RecordResult(rName, rPort, "Closed");
            return;
        }

        if (ready[0].IsWritable)
        {
            int err = _activeConn!.GetSocketError();
            CleanupActiveCheck();
            RecordResult(rName, rPort, err == 0 ? "Open" : "Closed");
        }
    }

    private void RecordResult(string name, int port, string status)
    {
        for (int i = 0; i < _results.Count; i++)
        {
            if (_results[i].Port == port)
            {
                _results[i] = (name, port, status);
                RebuildScreen();
                return;
            }
        }

        _results.Add((name, port, status));
        RebuildScreen();
    }

    private void CleanupActiveCheck()
    {
        _activePoller?.Dispose();
        _activePoller = null;
        _activeConn?.Dispose();
        _activeConn = null;
        _activeName = "";
        _activePort = 0;
        _activeStart = -1;
    }

    protected override void OnDispose()
    {
        CleanupActiveCheck();
        _checkQueue.Clear();
    }

    private SocketAddress ResolveAddress(int port)
    {
        if (_customHost == "localhost" || _customHost == "127.0.0.1")
            return SocketAddress.Loopback(port);

        if (SocketAddress.TryParse(_customHost, port, out SocketAddress address))
            return address;

        try
        {
            using var dns = HostResolver.Create();
            return dns.Resolve(_customHost, port);
        }
        catch
        {
            throw new FormatException($"Cannot resolve '{_customHost}'.");
        }
    }
}
