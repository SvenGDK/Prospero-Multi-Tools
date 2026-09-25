// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using SharpProspero.Security;
using System;
using System.Text;

namespace ProsperoMultiTools.Data.SceSysBuilder;

/// <summary>
/// Produces the 96-byte <c>sce_sys/keystone</c> file the shell's savedata subsystem reads to
/// derive per-title savedata encryption keys. Layout:
/// <list type="table">
/// <listheader><term>Offset</term><description>Field</description></listheader>
/// <item><term>0x00..0x08</term><description>ASCII magic <c>"keystone"</c>.</description></item>
/// <item><term>0x08..0x0A</term><description>Little-endian <c>uint16</c> version (<c>2</c>).</description></item>
/// <item><term>0x0A..0x0C</term><description>Little-endian <c>uint16</c> flag (<c>0x0001</c>).</description></item>
/// <item><term>0x0C..0x20</term><description>Zero padding to the 32-byte header size.</description></item>
/// <item><term>0x20..0x40</term><description>HMAC-SHA256 of the passcode with <see cref="CryptoKeys.KeystoneHmacKey"/>.</description></item>
/// <item><term>0x40..0x60</term><description>HMAC-SHA256 of <c>header || fingerprint</c> with <see cref="CryptoKeys.KeystoneMacDataKey"/>.</description></item>
/// </list>
/// </summary>
internal static class KeystoneWriter
{
    /// <summary>Byte length of a keystone file.</summary>
    internal const int Size = 0x60;

    /// <summary>
    /// Serialises a keystone bound to <paramref name="passcode"/>. The passcode is a 32-character
    /// ASCII string; the pkg builder that packages this title, and every retail signer, ties the
    /// keystone's fingerprint block to the same passcode value.
    /// </summary>
    internal static byte[] Create(string passcode)
    {
        if (string.IsNullOrEmpty(passcode) || passcode.Length != 32)
            throw new ArgumentException("Passcode must be exactly 32 ASCII characters.", nameof(passcode));

        byte[] header = new byte[0x20];
        Encoding.ASCII.GetBytes("keystone").CopyTo(header, 0);
        header[8]  = 0x02;
        header[10] = 0x01;

        byte[] passcodeBytes = Encoding.ASCII.GetBytes(passcode);
        byte[] fingerprint = Hmac.Sha256(CryptoKeys.KeystoneHmacKey, passcodeBytes);

        byte[] headerAndFingerprint = new byte[header.Length + fingerprint.Length];
        Buffer.BlockCopy(header, 0, headerAndFingerprint, 0, header.Length);
        Buffer.BlockCopy(fingerprint, 0, headerAndFingerprint, header.Length, fingerprint.Length);

        byte[] outerMac = Hmac.Sha256(CryptoKeys.KeystoneMacDataKey, headerAndFingerprint);

        byte[] result = new byte[Size];
        Buffer.BlockCopy(header, 0, result, 0, header.Length);
        Buffer.BlockCopy(fingerprint, 0, result, 0x20, fingerprint.Length);
        Buffer.BlockCopy(outerMac, 0, result, 0x40, outerMac.Length);
        return result;
    }
}
