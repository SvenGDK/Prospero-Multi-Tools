// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using SharpProspero.Application;
using SharpProspero.Graphics;
using SharpProspero.Interop;
using SharpProspero.Platform;
using SharpProspero.Ui;
using System;
using System.Collections.Generic;

namespace ProsperoMultiTools.Shell;

internal sealed class MultiToolsShell : IDisposable
{
    private readonly List<MultiToolsScreen> _stack = [];
    private readonly Toast _toast = new();
    private readonly UiRepeater _inputRepeater = new();
    private bool _disposed;
    private bool _dialogWasBusy;

    public MultiToolsShell(AppSettings settings, ITextFont font)
    {
        Settings = settings;
        Theme = BuildTheme(settings, font);
    }

    public AppSettings Settings { get; }
    public SystemDialogs Dialogs { get; } = new();
    public UiTheme Theme { get; }
    public MultiToolsScreen? Top => _stack.Count > 0 ? _stack[^1] : null;
    public int Depth => _stack.Count;
    public bool ExitRequested { get; private set; }

    public void Push(MultiToolsScreen screen)
    {
        ArgumentNullException.ThrowIfNull(screen);
        Top?.OnHidden();
        _stack.Add(screen);
        screen.OnShown();
    }

    public void Pop()
    {
        if (_stack.Count == 0)
            return;
        if (_stack.Count == 1)
        {
            ExitRequested = true;
            return;
        }

        MultiToolsScreen leaving = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        leaving.OnHidden();
        leaving.Dispose();
        Top?.OnShown();
    }

    public void PopToRoot()
    {
        while (_stack.Count > 1)
            Pop();
    }

    public void RequestExit() => ExitRequested = true;

    public void Notify(string message)
    {
        _toast.Show(message, Settings.ToastSeconds);
        if (!Settings.SystemNotifications)
            return;
        try
        {
            Notification.Show(message);
        }
        catch (Exception)
        {
        }
    }

    public void Status(string message) => _toast.Show(message, Settings.ToastSeconds);

    public void ReportFailure(string what, Exception error, bool important = false)
    {
        string reason = error.Message;
        if (important)
            Dialogs.Alert($"{what}\n\n{reason}");
        else
            _toast.Show($"{what}: {reason}", Math.Max(Settings.ToastSeconds, 4f));
    }

    public void Tick(FrameContext context)
    {
        try { Dialogs.Tick(); } catch (Exception e) { _toast.Show($"Overlay failed: {e.Message}", Math.Max(Settings.ToastSeconds, 4f)); }

        // Snapshot background pages: TickPage may remove a failed page from _stack mid-iteration.
        int bgCount = _stack.Count - 1;
        if (bgCount > 0)
        {
            var bgPages = new List<MultiToolsScreen>(bgCount);
            for (int i = 0; i < bgCount; i++)
                bgPages.Add(_stack[i]);
            foreach (MultiToolsScreen bg in bgPages)
            {
                if (bg.TicksInBackground)
                    TickPage(bg, context);
            }
        }

        MultiToolsScreen? top = Top;
        if (top is not null)
            TickPage(top, context);

        // Route the frame's input to the top screen from Tick, not from Draw. Update may Push or Pop
        // (Cross opens a new page, Circle leaves the current one); if that ran during Draw, the header
        // has already been drawn for the outgoing screen and the body-draw is guarded out — the frame
        // ships with the old header on top of a body region that was cleared and never redrawn.
        // Running Update here settles the stack before Draw reads Top, so header and body always
        // come from the same page.
        Surface tickSurface = context.Surface;
        MultiToolsScreen? afterTick = Top;
        if (afterTick is not null && !afterTick.IsDisposed)
        {
            try
            {
                int headerHeightTick = HeaderHeight;
                var area = new UiRect(
                    Margin,
                    headerHeightTick,
                    tickSurface.Width - (2 * Margin),
                    tickSurface.Height - headerHeightTick - Margin);
                UiScreen screen = afterTick.Screen;
                screen.Layout(area);
                bool busyNow = Dialogs.IsBusy;
                UiInput input;
                if (busyNow || _dialogWasBusy)
                {
                    input = UiInput.None;
                    // Reset the repeater on the boundary of a dialog so a direction the user was
                    // holding before the dialog opened does not fire a delayed repeat once it closes.
                    _inputRepeater.Reset();
                }
                else
                {
                    // The repeater carries hold timers between frames, so the d-pad and left stick
                    // repeat while held after the initial delay, and the cross/circle buttons still
                    // fire once per press. Every screen answers to either input in the same way.
                    input = _inputRepeater.Update(context.Input, (float)context.DeltaSeconds);
                }
                _dialogWasBusy = busyNow;
                screen.Update(input);
            }
            catch (Exception error)
            {
                ClosePageAfterFailure(afterTick, error);
            }
        }

        _toast.Update((float)context.DeltaSeconds);
    }

    private void TickPage(MultiToolsScreen page, FrameContext context)
    {
        try
        {
            page.Tick(context);
        }
        catch (Exception error)
        {
            ClosePageAfterFailure(page, error);
        }
    }

    private void ClosePageAfterFailure(MultiToolsScreen page, Exception error)
    {
        string title = page.Title;
        int at = _stack.IndexOf(page);
        if (at >= 0)
        {
            _stack.RemoveAt(at);
            page.Dispose();
            if (at == _stack.Count)
                Top?.OnShown();
        }
        // Show the screen name plainly; the raw exception message is often a syscall + errno
        // that gives the user no next step and reads as diagnostics rather than status.
        _toast.Show($"{title} closed unexpectedly.", Math.Max(Settings.ToastSeconds, 5f));
        if (_stack.Count == 0)
            ExitRequested = true;
    }

    public void Draw(FrameContext context)
    {
        Surface surface = context.Surface;
        surface.Clear(Theme.Background);

        MultiToolsScreen? top = Top;
        if (top is null || top.IsDisposed)
        {
            _toast.Draw(surface, Theme);
            return;
        }

        try
        {
            DrawHeader(surface, top);

            int headerHeight = HeaderHeight;
            var area = new UiRect(Margin, headerHeight, surface.Width - (2 * Margin), surface.Height - headerHeight - Margin);
            UiScreen screen = top.Screen;
            // Layout runs again in Draw. Tick already laid the screen out and routed the frame's
            // input; a second Layout here is cheap and keeps the draw self-contained when the
            // surface size changes between the two calls.
            screen.Layout(area);
            screen.Draw(surface);
        }
        catch (Exception error)
        {
            ClosePageAfterFailure(top, error);
        }

        _toast.Draw(surface, Theme);
    }

    private void DrawHeader(Surface surface, MultiToolsScreen top)
    {
        int bar = HeaderHeight - 12;
        surface.FillRect(0, 0, surface.Width, bar, Theme.Panel);
        surface.HLine(0, bar, surface.Width, Theme.Border);

        int line = Theme.LineHeight;
        int titleY = 16;

        string hint = top.Hint;
        int hintWidth = Theme.MeasureText(hint);
        int hintX = surface.Width - Margin - hintWidth;
        Theme.DrawText(surface, hint, hintX, titleY, Theme.TextMuted);
        Theme.DrawClipped(surface, top.Title, Margin, titleY, Theme.Text, hintX - Theme.Spacing - Margin);

        if (_stack.Count > 1)
            Theme.DrawClipped(surface, Breadcrumb(), Margin, titleY + line + 6, Theme.Accent, surface.Width - (2 * Margin));
    }

    private string Breadcrumb()
    {
        var trail = new System.Text.StringBuilder();
        for (int i = 0; i < _stack.Count; i++)
        {
            if (i > 0)
                trail.Append("  >  ");
            trail.Append(_stack[i].Title);
        }
        return trail.ToString();
    }

    private const int Margin = 48;

    private int HeaderHeight => (Theme.LineHeight * 2) + 48;

    private static UiTheme BuildTheme(AppSettings settings, ITextFont font) => new()
    {
        Background = Color.FromRgb(0x0A, 0x0E, 0x14),
        Panel = Color.FromRgb(0x14, 0x1A, 0x24),
        PanelFocused = Color.FromRgb(0x20, 0x2A, 0x38),
        Accent = Color.FromRgb(0x00, 0x88, 0xFF),
        Text = Color.FromRgb(0xEC, 0xF0, 0xF4),
        TextMuted = Color.FromRgb(0x8A, 0x94, 0xA0),
        Border = Color.FromRgb(0x28, 0x32, 0x40),
        Font = font,
        TextScale = settings.TextScale,
    };

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (int i = _stack.Count - 1; i >= 0; i--)
            _stack[i].Dispose();
        _stack.Clear();
        Dialogs.Dispose();
    }
}
