// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using SharpProspero.Application;
using SharpProspero.Ui;
using System;

namespace ProsperoMultiTools.Shell;

internal abstract class MultiToolsScreen : IDisposable
{
    private UiScreen? _screen;
    private UiElement? _rememberedFocus;
    private bool _disposed;

    protected MultiToolsScreen(MultiToolsShell shell) => Shell = shell;

    /// <summary>Whether this screen has been disposed.</summary>
    public bool IsDisposed => _disposed;

    protected MultiToolsShell Shell { get; }

    public abstract string Title { get; }

    public virtual string Hint => "Cross selects, Circle goes back.";

    /// <summary>
    /// The lazily-built UI screen for this page. A rebuild carries the focused control across when
    /// the rebuilt tree still holds it: the arrows-and-cross navigation the user was drawing on the
    /// old tree keeps working on the new one instead of jumping back to the first focusable, and a
    /// long-lived list on the page (a menu, a browser row) does not lose its selection every time a
    /// side panel refreshes.
    /// </summary>
    public UiScreen Screen
    {
        get
        {
            if (_screen is null)
            {
                _screen = new UiScreen(BuildRoot(), Shell.Theme) { Cancelled = HandleCancel };
                if (_rememberedFocus is not null)
                    _screen.RestoreFocus(_rememberedFocus);
                _rememberedFocus = null;
            }
            return _screen;
        }
    }

    /// <summary>
    /// Discards the built screen so the next access to <see cref="Screen"/> re-runs
    /// <see cref="BuildRoot"/>. The focused control (if any) is remembered so a rebuilt tree that
    /// still holds it keeps the focus there.
    /// </summary>
    protected void RebuildScreen()
    {
        _rememberedFocus = _screen?.Focused;
        _screen = null;
    }

    protected abstract UiElement BuildRoot();

    public virtual void Tick(FrameContext context)
    {
    }

    public virtual void OnShown()
    {
    }

    public virtual void OnHidden()
    {
    }

    protected virtual bool OnCancel() => false;

    public virtual bool TicksInBackground => false;

    private void HandleCancel()
    {
        if (!OnCancel())
            Shell.Pop();
    }

    protected virtual void OnDispose()
    {
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        OnDispose();
    }
}
