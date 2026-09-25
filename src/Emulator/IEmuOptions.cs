// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using ProsperoMultiTools.Shell;
using SharpProspero.Ui;
using System;
using System.Text;

namespace ProsperoMultiTools.Emulator;

/// <summary>
/// Common interface for per-platform emulator option models.
/// Each implementation owns both the UI presentation and the
/// <c>config-emu-ps4.txt</c> serialization of its options.
/// </summary>
internal interface IEmuOptions
{
    /// <summary>
    /// Appends the <c>--key=value</c> lines that make up the config file for this
    /// emulator session. The caller writes the result to the emulator folder.
    /// </summary>
    void EmitConfig(StringBuilder builder);

    /// <summary>
    /// Populates <paramref name="menu"/> with the interactive controls that let the
    /// user adjust every option in this model. Each option maps to one of the
    /// standard UI elements: <see cref="Stepper"/> for numeric values,
    /// <see cref="Checkbox"/> for booleans, <see cref="OptionSelector"/> for fixed
    /// choices, and <see cref="Button"/> paired with <c>AskText</c> for paths.
    /// </summary>
    /// <param name="menu">The scroll menu to add rows to.</param>
    /// <param name="shell">The application shell, for theming and dialogs.</param>
    /// <param name="rebuild">Called when a button-style option changes and the screen
    /// must rebuild its layout to reflect the new caption.</param>
    void BuildOptionRows(ScrollMenu menu, MultiToolsShell shell, Action rebuild);
}
