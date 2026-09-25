// Prospero Multi Tools
// Copyright (C) 2026 SvenGDK

using SharpProspero.Security;
using System;
using System.Text;

namespace ProsperoMultiTools.Data.SceSysBuilder;

/// <summary>
/// PS4/PS5 content type flag used inside <c>license.info</c> and <c>license.dat</c>.
/// <c>Gd</c> is the "game data" (main title) content the shell registers a folder install as.
/// </summary>
internal enum SceContentType : int
{
    /// <summary>Main title (game data). The value the shell reads for a folder-install title.</summary>
    Gd = 0x1A,

    /// <summary>Additional content.</summary>
    Ac = 0x2,

    /// <summary>Application patch.</summary>
    Dp = 0x18,

    /// <summary>Add-on (theme, avatar).</summary>
    Al = 0x1B,
}

/// <summary>
/// Produces the 512-byte <c>sce_sys/license.info</c> file the shell reads at install time to
/// bind the title to its content id. The file has a fixed on-disk shape:
/// <list type="table">
/// <item><term>0x00..0x24</term><description>ASCII content id, exactly 36 bytes.</description></item>
/// <item><term>0x24..0x30</term><description>Zero padding.</description></item>
/// <item><term>0x30..0x40</term><description>Entitlement key (16 bytes, zeros when the title carries none).</description></item>
/// <item><term>0x40..0x44</term><description>Big-endian <c>int32</c> add-on flag (1 for AL, 0 otherwise).</description></item>
/// <item><term>0x44..0x48</term><description>Big-endian <c>int32</c> content type.</description></item>
/// <item><term>0x48..0x4C</term><description>Big-endian <c>int32</c> zero.</description></item>
/// <item><term>0x4C..0x50</term><description>Big-endian <c>int32</c> value <c>1</c>.</description></item>
/// <item><term>0x50..0x200</term><description>Zero padding to the 512-byte total.</description></item>
/// </list>
/// </summary>
internal static class LicenseInfoWriter
{
    /// <summary>Byte length of a license.info file.</summary>
    internal const int Size = 0x200;

    /// <summary>
    /// Serialises <c>license.info</c> for the given content id and content type. When the caller
    /// has an entitlement key (a 16-byte value used to unlock additional-content downloads), it
    /// is written into the entitlement slot; otherwise the slot is zero-filled.
    /// </summary>
    internal static byte[] Create(string contentId, SceContentType type, byte[]? entitlementKey = null)
    {
        if (string.IsNullOrEmpty(contentId))
            throw new ArgumentException("Content id is required.", nameof(contentId));
        if (contentId.Length > 36)
            throw new ArgumentException("Content id must be at most 36 characters.", nameof(contentId));

        byte[] result = new byte[Size];
        Encoding.ASCII.GetBytes(contentId).CopyTo(result, 0);
        if (entitlementKey is not null && entitlementKey.Length == 16)
            Buffer.BlockCopy(entitlementKey, 0, result, 0x30, 16);

        int addonFlag = type == SceContentType.Al ? 1 : 0;
        WriteBigEndianInt32(result, 0x40, addonFlag);
        WriteBigEndianInt32(result, 0x44, (int)type);
        WriteBigEndianInt32(result, 0x48, 0);
        WriteBigEndianInt32(result, 0x4C, 1);
        return result;
    }

    private static void WriteBigEndianInt32(byte[] dest, int offset, int value)
    {
        dest[offset]     = (byte)((value >> 24) & 0xFF);
        dest[offset + 1] = (byte)((value >> 16) & 0xFF);
        dest[offset + 2] = (byte)((value >> 8)  & 0xFF);
        dest[offset + 3] = (byte)(value & 0xFF);
    }
}

/// <summary>
/// Produces the 1024-byte <c>sce_sys/license.dat</c> file. The file's payload is signed by an
/// RSA-2048 signature over its first 0x300 bytes; the Secret block is encrypted with AES-128-CBC
/// using an IV derived from the content id.
/// </summary>
internal static class LicenseDatWriter
{
    /// <summary>Byte length of a license.dat file.</summary>
    internal const int Size = 0x400;

    /// <summary>
    /// Serialises <c>license.dat</c> for the given content id and content type. Signature and
    /// secret cipher use the debug keyset so the shell's debug-license verification path accepts
    /// the file. When the caller has an entitlement key it is folded into the Secret block; the
    /// AL flag is set for AL-type content.
    /// </summary>
    internal static byte[] Create(string contentId, SceContentType type, byte[]? entitlementKey = null)
    {
        if (string.IsNullOrEmpty(contentId))
            throw new ArgumentException("Content id is required.", nameof(contentId));
        if (contentId.Length > 36)
            throw new ArgumentException("Content id must be at most 36 characters.", nameof(contentId));

        // Derive the 16-byte IV and the leading 16 bytes of Secret from SHA-256(contentIdPadded).
        byte[] contentIdPadded = new byte[48];
        Encoding.ASCII.GetBytes(contentId).CopyTo(contentIdPadded, 0);
        byte[] contentIdHash = Sha256.Hash(contentIdPadded);
        byte[] secretIv = new byte[16];
        byte[] secret   = new byte[144];
        Buffer.BlockCopy(contentIdHash, 0, secretIv, 0, 16);
        Buffer.BlockCopy(contentIdHash, 16, secret, 0, 16);
        if (entitlementKey is not null && entitlementKey.Length == 16)
            Buffer.BlockCopy(entitlementKey, 0, secret, 0x70, 16);

        // AES-128-CBC encrypt with the debug rif key using the derived IV. The Secret block is
        // a whole number of 16-byte blocks (144 = 9 * 16), so no padding is used.
        AesCbcEncryptInPlace(secret, CryptoKeys.RifDebugKey, secretIv);

        byte[] result = new byte[Size];

        // Header block (0x00..0x6C).
        result[0] = (byte)'R'; result[1] = (byte)'I'; result[2] = (byte)'F'; result[3] = 0;
        WriteBigEndianInt16(result, 0x04, 1);        // Version
        WriteBigEndianInt16(result, 0x06, unchecked((short)0xFFFF)); // Unknown
        WriteBigEndianInt64(result, 0x08, 0);        // PsnAccountId
        WriteBigEndianInt64(result, 0x10, 1364222275L); // StartTime
        WriteBigEndianInt64(result, 0x18, long.MaxValue); // EndTime
        Encoding.ASCII.GetBytes(contentId).CopyTo(result, 0x20);
        // 0x44..0x50 stays zero.
        WriteBigEndianInt16(result, 0x50, 0);        // LicenseType = Debug_0
        WriteBigEndianInt16(result, 0x52, 1);        // DrmType = PS4
        WriteBigEndianInt16(result, 0x54, (short)(int)type); // ContentType
        WriteBigEndianInt16(result, 0x56, type == SceContentType.Gd ? (short)3 : (short)0); // SkuFlag
        WriteBigEndianInt32(result, 0x58, 0);        // Flags
        WriteBigEndianInt32(result, 0x5C, 0);        // Unk_5C
        WriteBigEndianInt32(result, 0x60, 0);        // Unk_60
        WriteBigEndianInt32(result, 0x64, 1);        // Unk_64
        WriteBigEndianInt32(result, 0x68, 0);        // Unk_Flag

        // 0x6C..0x240 stays zero.

        // DiscKey (0x240..0x260) zero-filled.
        // Encrypted Secret and its IV (0x260..0x300).
        Buffer.BlockCopy(secretIv, 0, result, 0x260, 16);
        Buffer.BlockCopy(secret,   0, result, 0x270, 144);

        // RSA-2048 signature over the first 0x300 bytes.
        byte[] head = new byte[0x300];
        Buffer.BlockCopy(result, 0, head, 0, 0x300);
        byte[] hash = Sha256.Hash(head);

        // PKCS#1 v1.5 signature block: 0x00 0x01 <FF...> 0x00 <sha256-digest-info> <32-byte-hash>.
        byte[] paddedForSigning = BuildPkcs1V15SignatureBlock(hash);

        byte[] signature = new byte[256];
        var rsa = new Rsa2048(
            CryptoKeys.DebugRifModulus,
            CryptoKeys.DebugRifPrime1,
            CryptoKeys.DebugRifPrime2,
            CryptoKeys.DebugRifExponent1,
            CryptoKeys.DebugRifExponent2,
            CryptoKeys.DebugRifCoefficient);
        rsa.PrivateOp(paddedForSigning, signature);
        Buffer.BlockCopy(signature, 0, result, 0x300, signature.Length);
        return result;
    }

    // ASN.1 DigestInfo prefix for SHA-256: RFC 8017 A.2.4 encoding of the SHA-256 algorithm OID
    // followed by a 32-byte octet-string tag. Every valid PKCS#1 v1.5 SHA-256 signature block
    // ends with this exact 19-byte header before the 32-byte hash.
    private static readonly byte[] Sha256DigestInfoPrefix = new byte[19]
    {
        0x30, 0x31, 0x30, 0x0D, 0x06, 0x09, 0x60, 0x86, 0x48, 0x01, 0x65, 0x03, 0x04, 0x02, 0x01,
        0x05, 0x00, 0x04, 0x20,
    };

    // Builds the 256-byte PKCS#1 v1.5 signature block for a SHA-256 hash: leading 0x00 0x01,
    // then 0xFF filler padding, then a single 0x00 separator, then the SHA-256 DigestInfo
    // prefix, then the 32-byte hash. RFC 8017 section 9.2.
    private static byte[] BuildPkcs1V15SignatureBlock(byte[] sha256Hash)
    {
        if (sha256Hash is null || sha256Hash.Length != 32)
            throw new ArgumentException("SHA-256 digest must be 32 bytes.", nameof(sha256Hash));

        const int keyLength = 256;
        int tagLength = Sha256DigestInfoPrefix.Length + sha256Hash.Length; // 19 + 32 = 51
        int paddingLength = keyLength - 3 - tagLength; // 202 filler bytes

        byte[] block = new byte[keyLength];
        block[0] = 0x00;
        block[1] = 0x01;
        for (int i = 0; i < paddingLength; i++)
            block[2 + i] = 0xFF;
        block[2 + paddingLength] = 0x00;
        Buffer.BlockCopy(Sha256DigestInfoPrefix, 0, block, 3 + paddingLength, Sha256DigestInfoPrefix.Length);
        Buffer.BlockCopy(sha256Hash, 0, block, 3 + paddingLength + Sha256DigestInfoPrefix.Length, sha256Hash.Length);
        return block;
    }

    // AES-128-CBC encrypt <paramref name="data"/> in place using <paramref name="key"/> and
    // <paramref name="iv"/>. The data length must be a multiple of the 16-byte block size.
    private static void AesCbcEncryptInPlace(byte[] data, byte[] key, byte[] iv)
    {
        if (data.Length % 16 != 0)
            throw new ArgumentException("Data length must be a multiple of 16.", nameof(data));

        var aes = new Aes128(key);
        Span<byte> prev = stackalloc byte[16];
        iv.AsSpan(0, 16).CopyTo(prev);
        Span<byte> block = stackalloc byte[16];
        for (int i = 0; i < data.Length; i += 16)
        {
            for (int j = 0; j < 16; j++)
                block[j] = (byte)(data[i + j] ^ prev[j]);
            aes.EncryptBlock(block);
            block.CopyTo(prev);
            for (int j = 0; j < 16; j++)
                data[i + j] = block[j];
        }
    }

    private static void WriteBigEndianInt16(byte[] dest, int offset, short value)
    {
        dest[offset]     = (byte)((value >> 8) & 0xFF);
        dest[offset + 1] = (byte)(value & 0xFF);
    }

    private static void WriteBigEndianInt32(byte[] dest, int offset, int value)
    {
        dest[offset]     = (byte)((value >> 24) & 0xFF);
        dest[offset + 1] = (byte)((value >> 16) & 0xFF);
        dest[offset + 2] = (byte)((value >> 8)  & 0xFF);
        dest[offset + 3] = (byte)(value & 0xFF);
    }

    private static void WriteBigEndianInt64(byte[] dest, int offset, long value)
    {
        dest[offset]     = (byte)((value >> 56) & 0xFF);
        dest[offset + 1] = (byte)((value >> 48) & 0xFF);
        dest[offset + 2] = (byte)((value >> 40) & 0xFF);
        dest[offset + 3] = (byte)((value >> 32) & 0xFF);
        dest[offset + 4] = (byte)((value >> 24) & 0xFF);
        dest[offset + 5] = (byte)((value >> 16) & 0xFF);
        dest[offset + 6] = (byte)((value >> 8)  & 0xFF);
        dest[offset + 7] = (byte)(value & 0xFF);
    }
}
