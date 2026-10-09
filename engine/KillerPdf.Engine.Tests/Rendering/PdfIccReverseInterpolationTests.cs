using System.Buffers.Binary;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfIccReverseInterpolationTests
{
    [Theory]
    [InlineData(.2, .5, .8)]
    [InlineData(.2, .8, .5)]
    [InlineData(.5, .2, .8)]
    [InlineData(.5, .8, .2)]
    [InlineData(.8, .2, .5)]
    [InlineData(.8, .5, .2)]
    [InlineData(.2, .2, .8)]
    [InlineData(.8, .2, .2)]
    [InlineData(.8, .8, .2)]
    [InlineData(.5, .5, .5)]
    [InlineData(0, .5, .8)]
    [InlineData(.5, 0, .8)]
    [InlineData(.5, .8, 0)]
    [InlineData(1, 1, 1)]
    public void FromXyz_PreservesPiecewiseLinearCubeDiagonal(double first, double second, double third)
    {
        var profile = new PdfIccProfileTransform(Profile());
        double[] result = new double[4];
        profile.FromXyz(Xyz(first, second, third), result);
        // Only the all-high vertex has ink. Its piecewise linear extension is
        // the minimum coordinate, including all six tetrahedra and their ties.
        double expected = Math.Min(first, Math.Min(second, third));
        double[] vertex = [1, 32768d / 65535, 16384d / 65535, 49151d / 65535];
        for (int channel = 0; channel < 4; channel++)
            Assert.Equal(expected * vertex[channel], result[channel], 11);
    }

    [Fact]
    public void FromXyz_RetainsLegacyLabWhiteEncoding()
    {
        var profile = new PdfIccProfileTransform(Profile());
        double[] result = new double[4];
        profile.FromXyz([.9642, 1, .8249], result);
        Assert.Equal(32768d / 65535, result[0], 12);
    }

    [Fact]
    public void ToXyz_RetainsFourDimensionalForwardInterpolation()
    {
        var profile = new PdfIccProfileTransform(Profile());
        double[] xyz = new double[3];
        profile.ToXyz([.2, .4, .6, .8], xyz);
        double lightness = .2 * .4 * .6 * .8 * 100;
        double y = (lightness + 16) / 116;
        Assert.Equal(108d / 841 * (y - 4d / 29), xyz[1], 12);
    }

    private static double[] Xyz(double first, double second, double third)
    {
        double lightness = first * 65535 / 65280 * 100;
        double a = second * 65535 / 256 - 128;
        double b = third * 65535 / 256 - 128;
        double y = (lightness + 16) / 116;
        static double Inverse(double v) => v >= 6d / 29 ? v * v * v : 108d / 841 * (v - 4d / 29);
        return [.9642 * Inverse(y + a / 500), Inverse(y), .8249 * Inverse(y - b / 200)];
    }

    private static byte[] Profile()
    {
        byte[] forward = Table(4, 3), reverse = Table(3, 4);
        byte[] bytes = new byte[156 + forward.Length + reverse.Length];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)bytes.Length);
        "prtr"u8.CopyTo(bytes.AsSpan(12));
        "CMYK"u8.CopyTo(bytes.AsSpan(16));
        "Lab "u8.CopyTo(bytes.AsSpan(20));
        "acsp"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(128), 2);
        "A2B1"u8.CopyTo(bytes.AsSpan(132));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(136), 156);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(140), (uint)forward.Length);
        "B2A1"u8.CopyTo(bytes.AsSpan(144));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(148), (uint)(156 + forward.Length));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(152), (uint)reverse.Length);
        forward.CopyTo(bytes, 156);
        reverse.CopyTo(bytes, 156 + forward.Length);
        return bytes;
    }

    private static byte[] Table(int inputs, int outputs)
    {
        int cells = 1 << inputs;
        byte[] bytes = new byte[52 + (inputs * 2 + cells * outputs + outputs * 2) * 2];
        "mft2"u8.CopyTo(bytes);
        bytes[8] = (byte)inputs;
        bytes[9] = (byte)outputs;
        bytes[10] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(50), 2);
        int offset = 52;
        void Sample(ushort value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), value);
            offset += 2;
        }
        for (int channel = 0; channel < inputs; channel++) { Sample(0); Sample(65535); }
        ushort[] vertex = [65535, 32768, 16384, 49151];
        for (int cell = 0; cell < cells; cell++)
            for (int channel = 0; channel < outputs; channel++)
                Sample(inputs == 4 ? channel == 0 ? cell == cells - 1 ? (ushort)65280 : (ushort)0 : (ushort)32768
                    : cell == cells - 1 ? vertex[channel] : (ushort)0);
        for (int channel = 0; channel < outputs; channel++) { Sample(0); Sample(65535); }
        return bytes;
    }
}
