#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LibreLancer.Graphics;
using LibreLancer.ImageLib;

namespace LibreLancer.ContentEdit.AssetDeduplication;

/// <summary>
/// Low-level, format-preserving operations used by the ship/solar asset
/// deduplication editor script.
/// </summary>
public static class UtfAssetRewriter
{
    private static readonly HashSet<string> TextureReferenceNodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "dt_name", "et_name", "bt_name", "nt_name", "dm_name", "dm0_name",
        "dm1_name", "mt_name", "rt_name", "nm_name"
    };

    public static string HashTextureNode(LUtfNode node)
    {
        try
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            WriteTextureNode(writer, node, includeName: false);
            writer.Flush();
            return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
        }
        catch
        {
            // Keep malformed or unsupported image payloads safely deduplicable only
            // when their complete stored UTF payload is identical.
            return HashNode(node);
        }
    }

    public static string HashMaterialNode(
        LUtfNode node,
        IReadOnlyDictionary<string, string>? textureNames = null) =>
        HashNode(node, textureNames, TextureReferenceNodes);

    public static string HashNode(
        LUtfNode node,
        IReadOnlyDictionary<string, string>? stringMap = null,
        ISet<string>? mappedNodeNames = null)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        WriteNode(writer, node, stringMap, mappedNodeNames, includeName: false);
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }

    private static void WriteNode(
        BinaryWriter writer,
        LUtfNode node,
        IReadOnlyDictionary<string, string>? stringMap,
        ISet<string>? mappedNodeNames,
        bool includeName)
    {
        if (includeName)
            writer.Write(node.Name.ToLowerInvariant());

        if (node.Children == null)
        {
            var data = node.Data ?? [];
            if (stringMap != null &&
                mappedNodeNames != null &&
                mappedNodeNames.Contains(node.Name) &&
                TryGetString(node, out var value) &&
                stringMap.TryGetValue(value, out var replacement))
            {
                var replacementBytes = Encoding.ASCII.GetBytes(replacement);
                writer.Write(replacementBytes.Length + 1);
                writer.Write(replacementBytes);
                writer.Write((byte)0);
            }
            else
            {
                writer.Write(data.Length);
                writer.Write(data);
            }

            return;
        }

        foreach (var child in node.Children
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Data == null ? 1 : 0))
        {
            WriteNode(writer, child, stringMap, mappedNodeNames, includeName: true);
        }
    }

    private static void WriteTextureNode(BinaryWriter writer, LUtfNode node, bool includeName)
    {
        if (includeName)
            writer.Write(node.Name.ToLowerInvariant());

        if (node.Children == null)
        {
            if (node.Data != null && IsImageNode(node.Name) && TryWriteDecodedImage(writer, node.Data))
                return;

            var data = node.Data ?? [];
            writer.Write(data.Length);
            writer.Write(data);
            return;
        }

        foreach (var child in node.Children
                     .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Data == null ? 1 : 0))
        {
            WriteTextureNode(writer, child, includeName: true);
        }
    }

    private static bool IsImageNode(string name) =>
        name.Equals("mips", StringComparison.OrdinalIgnoreCase) ||
        (name.StartsWith("mip", StringComparison.OrdinalIgnoreCase) &&
         int.TryParse(name[3..], out _));

    private static bool TryWriteDecodedImage(BinaryWriter writer, byte[] data)
    {
        try
        {
            Image? image;
            using (var stream = new MemoryStream(data, writable: false))
            {
                // DDS payloads are kept byte-exact here. Several mod DDS files
                // have headers/blocks that the editor decoder cannot safely
                // normalize; refusing to decode is conservative and prevents
                // false merges.
                if (DDS.StreamIsDDS(stream))
                    return false;
            }

            using (var stream = new MemoryStream(data, writable: false))
                image = TGA.ImageFromStream(stream);
            if (image == null)
                return false;

            using var normalized = new MemoryStream();
            using (var normalizedWriter = new BinaryWriter(normalized, Encoding.UTF8, leaveOpen: true))
            {
                normalizedWriter.Write(0x54455831); // TEX1: decoded pixel/mipmap payload
                normalizedWriter.Write(1);
                if (!TryWriteDecodedPixels(normalizedWriter, image))
                    return false;

                normalizedWriter.Flush();
            }

            writer.Write(normalized.ToArray());
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWriteDecodedPixels(BinaryWriter writer, Image image)
    {
        var pixelCount = checked(image.Width * image.Height);
        byte[] pixels;
        switch (image.Format)
        {
            case SurfaceFormat.Bgra8:
                if (image.Data.Length != checked(pixelCount * 4))
                    return false;
                pixels = image.Data;
                break;
            case SurfaceFormat.Dxt1:
            case SurfaceFormat.Dxt3:
            case SurfaceFormat.Dxt5:
                pixels = ToBytes(S3TC.Decompress(image.Format, image.Width, image.Height, image.Data));
                break;
            case SurfaceFormat.Bgra5551:
                pixels = DecodeBgra5551(image.Data, pixelCount);
                break;
            case SurfaceFormat.Bgr565:
                pixels = DecodeBgr565(image.Data, pixelCount);
                break;
            case SurfaceFormat.Bgra4444:
                pixels = DecodeBgra4444(image.Data, pixelCount);
                break;
            default:
                return false;
        }

        writer.Write(image.Width);
        writer.Write(image.Height);
        writer.Write(pixels.Length);
        writer.Write(pixels);
        return true;
    }

    private static byte[] DecodeBgra5551(byte[] data, int pixelCount)
    {
        if (data.Length != checked(pixelCount * 2))
            throw new InvalidDataException("Invalid Bgra5551 image payload");

        var pixels = new Bgra8[pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2, 2));
            pixels[i] = new Bgra8(
                Expand5((byte)((value >> 10) & 0x1F)),
                Expand5((byte)((value >> 5) & 0x1F)),
                Expand5((byte)(value & 0x1F)),
                (value & 0x8000) == 0 ? (byte)0 : (byte)255);
        }

        return ToBytes(pixels);
    }

    private static byte[] DecodeBgr565(byte[] data, int pixelCount)
    {
        if (data.Length != checked(pixelCount * 2))
            throw new InvalidDataException("Invalid Bgr565 image payload");

        var pixels = new Bgra8[pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2, 2));
            pixels[i] = new Bgra8(
                Expand5((byte)((value >> 11) & 0x1F)),
                Expand6((byte)((value >> 5) & 0x3F)),
                Expand5((byte)(value & 0x1F)),
                255);
        }

        return ToBytes(pixels);
    }

    private static byte[] DecodeBgra4444(byte[] data, int pixelCount)
    {
        if (data.Length != checked(pixelCount * 2))
            throw new InvalidDataException("Invalid Bgra4444 image payload");

        var pixels = new Bgra8[pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2, 2));
            pixels[i] = new Bgra8(
                Expand4((byte)((value >> 8) & 0x0F)),
                Expand4((byte)((value >> 4) & 0x0F)),
                Expand4((byte)(value & 0x0F)),
                Expand4((byte)((value >> 12) & 0x0F)));
        }

        return ToBytes(pixels);
    }

    private static byte Expand4(byte value) => (byte)((value << 4) | value);
    private static byte Expand5(byte value) => (byte)((value << 3) | (value >> 2));
    private static byte Expand6(byte value) => (byte)((value << 2) | (value >> 4));

    private static byte[] ToBytes(Bgra8[] pixels) =>
        MemoryMarshal.AsBytes(pixels.AsSpan()).ToArray();

    public static bool TryGetString(LUtfNode node, out string value)
    {
        value = node.StringData ?? string.Empty;
        return node.Data != null;
    }

    public static int RewriteVmeshMaterialCrcs(
        LUtfNode root,
        IReadOnlyDictionary<uint, uint> crcMap)
    {
        var changed = 0;
        foreach (var node in root.IterateAll())
        {
            if (!node.Name.Equals("VMeshData", StringComparison.OrdinalIgnoreCase) ||
                node.Data == null)
            {
                continue;
            }

            changed += RewriteVmeshMaterialCrcs(node.Data, crcMap);
        }

        return changed;
    }

    public static int RewriteVmeshMaterialCrcs(
        byte[] data,
        IReadOnlyDictionary<uint, uint> crcMap)
    {
        if (data.Length < 16)
        {
            throw new InvalidDataException("VMeshData is shorter than its header");
        }

        var meshCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8, 2));
        var headersEnd = checked(16 + meshCount * 12);
        if (headersEnd > data.Length)
        {
            throw new InvalidDataException("VMeshData has truncated mesh headers");
        }

        var changed = 0;
        for (var i = 0; i < meshCount; i++)
        {
            var offset = 16 + i * 12;
            var oldCrc = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
            if (crcMap.TryGetValue(oldCrc, out var newCrc) && oldCrc != newCrc)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), newCrc);
                changed++;
            }
        }

        return changed;
    }

    public static IEnumerable<uint> EnumerateVmeshMaterialCrcs(LUtfNode root)
    {
        foreach (var node in root.IterateAll())
        {
            if (!node.Name.Equals("VMeshData", StringComparison.OrdinalIgnoreCase) ||
                node.Data == null)
            {
                continue;
            }

            foreach (var crc in EnumerateVmeshMaterialCrcs(node.Data))
            {
                yield return crc;
            }
        }
    }

    public static IEnumerable<uint> EnumerateVmeshMaterialCrcs(byte[] data)
    {
        if (data.Length < 16)
        {
            throw new InvalidDataException("VMeshData is shorter than its header");
        }

        var meshCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8, 2));
        var headersEnd = checked(16 + meshCount * 12);
        if (headersEnd > data.Length)
        {
            throw new InvalidDataException("VMeshData has truncated mesh headers");
        }

        for (var i = 0; i < meshCount; i++)
        {
            yield return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16 + i * 12, 4));
        }
    }

    public static int RewriteMaterialNames(
        LUtfNode root,
        IReadOnlyDictionary<string, string> materialNames)
    {
        var changed = 0;
        foreach (var node in root.IterateAll())
        {
            if (node.Children != null &&
                node.Parent?.Name.Equals("MaterialAnim", StringComparison.OrdinalIgnoreCase) == true &&
                materialNames.TryGetValue(node.Name, out var newAnimationName) &&
                !node.Name.Equals(newAnimationName, StringComparison.Ordinal))
            {
                node.Name = newAnimationName;
                changed++;
                continue;
            }

            if (node.Children != null || node.Data == null)
            {
                continue;
            }

            var isMaterialName = node.Name.Equals("material_name", StringComparison.OrdinalIgnoreCase);
            var isSphereSide = node.Parent?.Name.Equals("sphere", StringComparison.OrdinalIgnoreCase) == true &&
                               node.Name.StartsWith("m", StringComparison.OrdinalIgnoreCase);
            if (!isMaterialName && !isSphereSide)
            {
                continue;
            }

            var oldName = node.StringData;
            if (oldName != null && materialNames.TryGetValue(oldName, out var newName) &&
                !oldName.Equals(newName, StringComparison.Ordinal))
            {
                node.StringData = newName;
                changed++;
            }
        }

        return changed;
    }

    public static int RemoveEmbeddedLibraries(LUtfNode root)
    {
        var removed = 0;
        RemoveEmbeddedLibraries(root, ref removed);
        return removed;
    }

    private static void RemoveEmbeddedLibraries(LUtfNode node, ref int removed)
    {
        if (node.Children == null)
        {
            return;
        }

        foreach (var child in node.Children.ToArray())
        {
            if (child.Name.Equals("material library", StringComparison.OrdinalIgnoreCase) ||
                child.Name.Equals("texture library", StringComparison.OrdinalIgnoreCase))
            {
                node.Children.Remove(child);
                removed++;
                continue;
            }

            RemoveEmbeddedLibraries(child, ref removed);
        }
    }
}
