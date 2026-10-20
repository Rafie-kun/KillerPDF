using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Documents;

internal static class PdfOfficeImage
{
    internal static byte[] Encode(PdfRenderedPage bitmap, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, bitmap.Width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), bitmap.Height);
        header[8] = 8;
        header[9] = 6;
        Chunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var row = new byte[checked(bitmap.Width * 4 + 1)];
            ReadOnlySpan<byte> pixels = bitmap.Pixels.Span;
            for (int y = 0; y < bitmap.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int source = (y * bitmap.Width + x) * 4, target = x * 4 + 1;
                    row[target] = pixels[source + 2];
                    row[target + 1] = pixels[source + 1];
                    row[target + 2] = pixels[source];
                    row[target + 3] = pixels[source + 3];
                }
                zlib.Write(row);
            }
        }
        Chunk(output, "IDAT", compressed.ToArray());
        Chunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void Chunk(Stream output, string name, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        byte[] type = Encoding.ASCII.GetBytes(name);
        output.Write(type);
        output.Write(data);
        uint crc = uint.MaxValue;
        foreach (byte value in type.Concat(data))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
        output.Write(number);
    }
}
