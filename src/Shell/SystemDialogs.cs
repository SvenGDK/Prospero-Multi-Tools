// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using SharpProspero.Interop;
using SharpProspero.Platform;
using System;

using ImeDialogEndStatus = SharpProspero.Interop.Dialog.ImeDialogEndStatus;
using ImeType = SharpProspero.Interop.Dialog.ImeType;
using MsgDialogButtonId = SharpProspero.Interop.Dialog.MsgDialogButtonId;

namespace ProsperoMultiTools.Shell;

internal sealed class SystemDialogs : IDisposable
{
    private readonly System.Collections.Generic.Queue<(Action Open, Action Abandon)> _queued = new();
    private IDisposable? _open;
    private Func<bool>? _pump;
    private bool _disposed;

    public bool IsBusy => _open is not null;

    public void Tick()
    {
        if (_open is not null)
        {
            bool finished;
            try
            {
                finished = _pump!();
            }
            catch (ProsperoException)
            {
                finished = true;
            }

            if (!finished)
                return;

            _open.Dispose();
            _open = null;
            _pump = null;
        }

        if (_open is null && _queued.Count > 0)
        {
            (Action open, Action abandon) = _queued.Dequeue();
            Start(open, abandon);
        }
    }

    public void Alert(string message, Action? closed = null)
        => Request(() =>
        {
            MessageDialog dialog = MessageDialog.ShowMessage(message, MessageDialogButtons.Ok);
            Begin(dialog, () =>
            {
                if (dialog.Update() == MessageDialogState.Running)
                    return false;
                closed?.Invoke();
                return true;
            });
        }, () => closed?.Invoke());

    public void Confirm(string question, Action<bool> answered)
        => Request(() =>
        {
            MessageDialog dialog = MessageDialog.ShowMessage(question, MessageDialogButtons.YesNo);
            Begin(dialog, () =>
            {
                if (dialog.Update() == MessageDialogState.Running)
                    return false;
                answered(dialog.ChosenButton == MsgDialogButtonId.Ok);
                return true;
            });
        }, () => answered(false));

    public void AskText(
        string title,
        string initialText,
        Action<string?> entered,
        int maxLength = 256,
        ImeType type = ImeType.Default,
        string? placeholder = null)
        => Request(() =>
        {
            TextInputDialog dialog = TextInputDialog.Open(
                title, maxLength, type, placeholder, initialText);
            Begin(dialog, () =>
            {
                if (dialog.Update() == TextInputState.Running)
                    return false;
                entered(dialog.EndStatus == ImeDialogEndStatus.Ok ? dialog.Text : null);
                return true;
            });
        }, () => entered(null));

    public void RunWithProgress(string caption, Func<MessageDialog, bool> step, Action? finished = null)
        => Request(() =>
        {
            MessageDialog dialog = MessageDialog.ShowProgress(caption);
            Begin(dialog, () =>
            {
                bool done;
                try
                {
                    done = step(dialog);
                }
                catch (Exception)
                {
                    dialog.Update();
                    finished?.Invoke();
                    return true;
                }

                dialog.Update();
                if (!done)
                    return false;
                finished?.Invoke();
                return true;
            });
        }, () => finished?.Invoke());

    public void ShowErrorCode(int errorCode, Action? closed = null)
        => Request(() =>
        {
            ErrorDialog dialog = ErrorDialog.Show(errorCode);
            Begin(dialog, () =>
            {
                if (dialog.Update() == ErrorDialogState.Running)
                    return false;
                closed?.Invoke();
                return true;
            });
        }, () => closed?.Invoke());

    private void Request(Action open, Action abandon)
    {
        if (_open is not null)
        {
            _queued.Enqueue((open, abandon));
            return;
        }
        Start(open, abandon);
    }

    private void Start(Action open, Action abandon)
    {
        try
        {
            open();
        }
        catch (Exception)
        {
            _open = null;
            _pump = null;
            try { abandon(); } catch { }
        }
    }

    private void Begin(IDisposable dialog, Func<bool> pump)
    {
        _open = dialog;
        _pump = pump;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _queued.Clear();
        _open?.Dispose();
        _open = null;
        _pump = null;
    }
}
