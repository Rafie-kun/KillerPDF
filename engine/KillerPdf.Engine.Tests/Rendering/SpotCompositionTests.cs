using System.Numerics;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class SpotCompositionTests
{
    [Theory]
    [InlineData(2)]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(32)]
    public void SpotAllocationOrderCannotChangeExactComplementComposition(int count)
    {
        var random = new Random(7319);
        for (int example = 0; example < 200; example++)
        {
            uint process = (uint)random.NextInt64(1L << 32);
            uint[] spots = Enumerable.Range(0, count).Select(_ => (uint)random.NextInt64(1L << 32)).ToArray();
            uint expected = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                // Independent rational calculation of 255 * (1 - product(1 - ink / 255)).
                BigInteger denominator = BigInteger.Pow(255, count);
                BigInteger numerator = 255 - (byte)(process >> (channel * 8));
                foreach (uint spot in spots) numerator *= 255 - (byte)(spot >> (channel * 8));
                BigInteger quotient = BigInteger.DivRem(255 * denominator - numerator, denominator, out var remainder);
                if (remainder * 2 > denominator) quotient++;
                expected |= (uint)quotient << (channel * 8);
            }
            Assert.Equal(expected, PdfPageRenderer.ComposeSpotInks(process, spots));
            Array.Reverse(spots);
            Assert.Equal(expected, PdfPageRenderer.ComposeSpotInks(process, spots));
        }
    }
}
