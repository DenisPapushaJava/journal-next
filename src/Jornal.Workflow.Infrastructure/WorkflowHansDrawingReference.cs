using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Jornal.Workflow.Infrastructure;

internal static class WorkflowHansDrawingReference
{
    private const int MaximumDrawingBytes = 8 * 1024 * 1024;
    internal sealed record Label(string Text, int Escapement, uint Color);

    internal static IReadOnlyList<IReadOnlyList<Label>> Read(string docPath)
    {
        // HansLaser DOC stores the drawings as zlib-compressed OfficeArt EMF.
        // Only validate intact, recognisable drawing records in the temporary DOC copy.
        if (new FileInfo(docPath).Length > 24 * 1024 * 1024) return [];
        var bytes = File.ReadAllBytes(docPath);
        var header = new byte[52];
        var drawings = new List<IReadOnlyList<Label>>();
        var remaining = 2 * MaximumDrawingBytes;
        for (var offset = 0; offset + 2 < bytes.Length; offset++)
        {
            if (bytes[offset] != 0x78 || bytes[offset + 1] is not (0x01 or 0x5e or 0x9c or 0xda)) continue;
            try
            {
                using var source = new MemoryStream(bytes, offset, bytes.Length - offset, writable: false);
                using var expanded = new ZLibStream(source, CompressionMode.Decompress);
                if (expanded.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length ||
                    UInt(header, 0) != 1 || !header.AsSpan(40, 4).SequenceEqual(" EMF"u8))
                    continue;
                var length = UInt(header, 48);
                if (length is < 88 or > MaximumDrawingBytes || length > remaining) return [];
                remaining -= (int)length;
                var drawing = new byte[length];
                header.CopyTo(drawing, 0);
                expanded.ReadExactly(drawing.AsSpan(header.Length));
                if (expanded.ReadByte() == -1 && ReadLabels(drawing) is { Count: >= 2 } labels)
                    drawings.Add(labels);
            }
            catch (Exception exception) when (exception is InvalidDataException or EndOfStreamException)
            {
                // Other byte sequences in the compound document can resemble a zlib header.
            }
        }
        return drawings;
    }

    private static IReadOnlyList<Label> ReadLabels(ReadOnlySpan<byte> drawing)
    {
        var fonts = new Dictionary<uint, int>();
        var labels = new List<Label>();
        var saved = new List<(int Escapement, uint Color)>();
        var escapement = 0;
        var color = 0u;
        for (var offset = 0; offset + 8 <= drawing.Length;)
        {
            var length = UInt(drawing, offset + 4);
            if (length < 8 || length > drawing.Length - offset) return [];
            var record = drawing.Slice(offset, (int)length);
            switch (UInt(record, 0))
            {
                case 14: return labels; // EMR_EOF
                case 33: // EMR_SAVEDC
                    saved.Add((escapement, color));
                    break;
                case 34 when record.Length >= 12: // EMR_RESTOREDC
                    var level = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
                    var index = level < 0 ? saved.Count + level : level - 1;
                    if (index < 0 || index >= saved.Count) return [];
                    (escapement, color) = saved[index];
                    saved.RemoveRange(index, saved.Count - index);
                    break;
                case 82 when record.Length >= 32: // EMR_EXTCREATEFONTINDIRECTW
                    fonts[UInt(record, 8)] = BinaryPrimitives.ReadInt32LittleEndian(record[20..]);
                    break;
                case 37 when record.Length >= 12: // EMR_SELECTOBJECT
                    if (fonts.TryGetValue(UInt(record, 8), out var angle)) escapement = angle;
                    break;
                case 40 when record.Length >= 12: // EMR_DELETEOBJECT
                    fonts.Remove(UInt(record, 8));
                    break;
                case 24 when record.Length >= 12: // EMR_SETTEXTCOLOR
                    color = UInt(record, 8);
                    break;
                case 84 when record.Length >= 76: // EMR_EXTTEXTOUTW
                    var count = UInt(record, 44);
                    var start = UInt(record, 48);
                    if (count == 0) break;
                    if (start > record.Length || count > (record.Length - start) / 2) return [];
                    var text = Encoding.Unicode.GetString(record.Slice((int)start, (int)count * 2));
                    labels.Add(new(text, escapement, color));
                    break;
            }
            offset += (int)length;
        }
        return [];
    }

    private static uint UInt(ReadOnlySpan<byte> bytes, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[index..]);
}
