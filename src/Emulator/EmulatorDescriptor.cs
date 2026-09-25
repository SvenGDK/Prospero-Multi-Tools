// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using System.Collections.Generic;

namespace ProsperoMultiTools.Emulator;

/// <summary>
/// Describes one on-device emulator installation, including its identity and the
/// candidate paths where the emulator folder may reside on the console filesystem.
/// </summary>
internal sealed class EmulatorDescriptor
{
    /// <summary>The kind of platform this emulator targets.</summary>
    public EmulatorKind Kind { get; }

    /// <summary>The emulator's own folder name (e.g. <c>"Jakv2"</c>), used as the display name
    /// in menus and as the last path segment under <c>/data/homebrew/emulators/</c>.</summary>
    public string Name { get; }

    /// <summary>The title id assigned to the emulator.</summary>
    public string TitleId { get; }

    /// <summary>The full content id for the emulator's system-level metadata.</summary>
    public string ContentId { get; }

    /// <summary>
    /// Ordered list of on-device paths to probe when resolving this emulator.
    /// The first existing directory wins.
    /// </summary>
    public IReadOnlyList<string> SourceFolderProbe { get; }

    /// <summary>
    /// True when the registry auto-discovered this entry by walking the standard emulator roots
    /// instead of reading it from the shipped manifest. A rescan strips discovered rows before
    /// re-walking so a manifest edit or a removed folder shows the right state.
    /// </summary>
    public bool AutoDiscovered { get; init; }

    public EmulatorDescriptor(EmulatorKind kind, string name,
        string titleId, string contentId, IReadOnlyList<string> sourceFolderProbe)
    {
        Kind = kind;
        Name = name;
        TitleId = titleId;
        ContentId = contentId;
        SourceFolderProbe = sourceFolderProbe;
    }
}
