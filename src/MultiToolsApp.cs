// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Application;
using SharpProspero.Graphics;
using SharpProspero.Interop;
using SharpProspero.Interop.Font;
using SharpProspero.Interop.Sysmodule;
using SharpProspero.Modules;
using SharpProspero.Platform;
using SharpProspero.Ui;
using System;

namespace ProsperoMultiTools;

internal sealed class MultiToolsApp : ProsperoApp
{
    private MultiToolsShell? _shell;
    private AppSettings? _settings;
    private ITextFont? _font;
    private SystemModule? _pngDecModule;
    private SystemModule? _jpegDecModule;
    private SystemModule? _avPlayerModule;
    private SystemModule? _fontModule;
    private SystemModule? _fontFtModule;
    private SystemModule? _freeTypeModule;
    private long _lastKeepAwakeFrame;

    /// <summary>
    /// How many frames pass between idle-timer resets. At 60 fps this is roughly five seconds, which
    /// is well within the timer's window and avoids calling the service on every frame.
    /// </summary>
    private const long KeepAwakeCadenceFrames = 300;

    protected override void OnLoad()
    {
        // Load the settings first so the font code that follows knows the text scale to open at.
        _settings = AppSettings.Load();

        // Open the outline font BEFORE the escalation daemon widens the file view. The font
        // engine's system-font path derives its resident-set path from a sandbox salt the kernel
        // returns; a widened view drops the sandbox namespace and the salt reads back as empty,
        // so the derivation skips the path buffer and every mode of the font-set open answers
        // with a generic parameter error. Opening the font first keeps the sandbox salt reachable
        // for the derivation and leaves the wider view for every step that follows.
        _font = LoadFont();

        // Now widen the file view for the rest of the shell: the file broker starts, the storage
        // probe reads the daemon-visible partitions, and the settings file lands where the user
        // can read it back on the next launch.
        UnjailResult unjail = UnjailRequest.Request();
        Places.Configure(unjail);
        Places.Refresh();

        try { _pngDecModule = SystemModule.Load(SystemModuleId.PngDec); } catch (ProsperoException) { }
        try { _jpegDecModule = SystemModule.Load(SystemModuleId.JpegDec); } catch (ProsperoException) { }
        try { _avPlayerModule = SystemModule.Load(SystemModuleId.AvPlayer); } catch (ProsperoException) { }

        _shell = new MultiToolsShell(_settings, _font);

        _shell.Push(new HomeScreen(_shell));

        int reachable = Places.Reachable.Count;
        if (reachable > 0)
            _shell.Status($"{reachable} folders can be read.");
        else
            _shell.Status("No folder could be read.");

        if (_fontLoadFailure is not null)
            _shell.Status(_fontLoadFailure);
    }

    protected override void OnFrame(FrameContext context)
    {
        if (_shell is null)
            return;

        if (context.FrameIndex - _lastKeepAwakeFrame >= KeepAwakeCadenceFrames)
        {
            _lastKeepAwakeFrame = context.FrameIndex;
            try { SystemControl.KeepAwake(); } catch (ProsperoException) { }
        }

        _shell.Tick(context);
        _shell.Draw(context);

        if (_shell.ExitRequested)
            context.RequestExit();
    }

    protected override void OnUnload()
    {
        // Every step is guarded so a failing early step cannot cascade past the sysmodule unloads
        // below it: a font-related failure that skipped the FreeTypeOt or FontFt unload would leave
        // libc's own finalizer walking modules that no longer exist, which the system reports as an
        // abnormal exit even after everything above ran cleanly.
        try { _settings?.Save(); } catch { }
        try { _shell?.Dispose(); } catch { } finally { _shell = null; }
        try { (_font as IDisposable)?.Dispose(); } catch { } finally { _font = null; }
        try { _freeTypeModule?.Dispose(); } catch { } finally { _freeTypeModule = null; }
        try { _fontFtModule?.Dispose(); } catch { } finally { _fontFtModule = null; }
        try { _fontModule?.Dispose(); } catch { } finally { _fontModule = null; }
        try { _avPlayerModule?.Dispose(); } catch { } finally { _avPlayerModule = null; }
        try { _jpegDecModule?.Dispose(); } catch { } finally { _jpegDecModule = null; }
        try { _pngDecModule?.Dispose(); } catch { } finally { _pngDecModule = null; }
    }

    private ITextFont LoadFont()
    {
        // The glyph renderer draws through the FreeType OpenType backend, so all three modules are
        // loaded before a font is created; without the backend the renderer reaches an unresolved
        // routine and the process faults. Every step is guarded so an early failure does not
        // cascade past the remaining unloads at teardown.
        try
        {
            _fontModule = SystemModule.Load(SystemModuleId.Font);
            _fontFtModule = SystemModule.Load(SystemModuleId.FontFt);
            _freeTypeModule = SystemModule.Load(SystemModuleId.FreeTypeOt);
            // The pixel size follows the sibling shell's floor-and-scale rule: eleven pixels per
            // text-scale step, no lower than fourteen so a scale-one setting still reads at TV
            // distance. Regular W1G is the same face the system's XMB draws with, so the shell
            // matches its own kerning and stroke weight rather than a heavier Medium or Bold cut.
            float pixelSize = MathF.Max(14f, _settings!.TextScale * 11f);
            return SystemFont.Open(SceFontSet.StdEuropeanW1G, pixelSize);
        }
        catch (Exception e)
        {
            _fontLoadFailure = "System font: " + e.Message;
            _freeTypeModule?.Dispose(); _freeTypeModule = null;
            _fontFtModule?.Dispose(); _fontFtModule = null;
            _fontModule?.Dispose(); _fontModule = null;
            return BitmapFallback();
        }
    }

    private ITextFont BitmapFallback()
    {
        // Match the user's text-scale setting. Scale two on a 1920x1080 framebuffer draws the
        // built-in glyphs at a sixteen-pixel body height, which reads at a normal viewing
        // distance on a television. Scale one, which the previous fix reached for, drew every
        // line at half that size and left the whole shell impossibly small.
        return new BitmapTextFont(_settings!.TextScale);
    }

    private string? _fontLoadFailure;

    private static class Program
    {
        private static void Main()
        {
            try
            {
                using (var app = new MultiToolsApp())
                    app.Run();
            }
            catch
            {
                // A failure that reached this far means one of the framework's own steps threw during
                // teardown. Ending through the C library is still better than returning to the entry
                // point, which the platform reports as an abnormal exit and shows the user as a crash.
            }

            ProcessExit.Exit();
        }
    }
}
