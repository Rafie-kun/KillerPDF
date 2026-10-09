using System.Reflection;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfNativeProfileImageTests
{
    private static readonly double[] ForwardDecode = [0d, 1d];
    private static readonly double[] ReverseDecode = [1d, 0d];
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NativeProfileSamplesRetainBytesAndReduction(bool reverse, bool matte)
    {
        const int width = 37, height = 29;
        byte[] samples = new byte[width * height * 4];
        new Random(17934).NextBytes(samples);
        // Include every channel endpoint as well as arbitrary fractional native values.
        samples[0] = 0; samples[1] = 255; samples[2] = 1; samples[3] = 254;
        byte[] wide = [.. samples.SelectMany(value => new byte[] { value, value })];
        PdfColorTransform profile = new NativeOnlyProfile();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Type spaceType = typeof(PdfPageRenderer).GetNestedType("ImageColorSpace", BindingFlags.NonPublic)!;
        ConstructorInfo spaceConstructor = spaceType.GetConstructors().Single();
        object?[] spaceArguments = [.. spaceConstructor.GetParameters()
            .Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue : null)];
        spaceArguments[0] = 4;
        spaceArguments[Array.FindIndex(spaceConstructor.GetParameters(), p => p.Name == "Profile")] = profile;
        object space = spaceConstructor.Invoke(spaceArguments);
        Type converterType = typeof(PdfPageRenderer).GetNestedType("ImageSampleConverter", BindingFlags.NonPublic)!;
        ConstructorInfo constructor = converterType.GetConstructors(flags).Single();
        double[] decode = [.. Enumerable.Range(0, 4)
            .SelectMany(_ => reverse ? ReverseDecode : ForwardDecode)];
        object Converter(byte[] bytes, int bits)
        {
            object?[] arguments = [.. constructor.GetParameters()
                .Select(parameter => parameter.HasDefaultValue ? parameter.DefaultValue : null)];
            object?[] supplied = [bytes, width * 4 * bits / 8, 4, bits, decode, space, true, profile, matte];
            supplied.CopyTo(arguments, 0);
            return constructor.Invoke(arguments);
        }
        object narrow = Converter(samples, 8), reference = Converter(wide, 16);
        var convert = converterType.GetMethod("Convert", flags)!;
        var sample = convert.CreateDelegate<Func<int, int, uint>>(narrow);
        var wideSample = convert.CreateDelegate<Func<int, int, uint>>(reference);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            Assert.Equal(wideSample(x, y), sample(x, y));
        MethodInfo reduce = converterType.GetMethod("ConvertArea", flags)!;
        for (int y = 0; y < 11; y++)
        for (int x = 0; x < 13; x++)
        {
            object[] arguments = [x, y, 13, 11, width, height, CancellationToken.None];
            Assert.Equal((uint)reduce.Invoke(reference, arguments)!, (uint)reduce.Invoke(narrow, arguments)!);
        }
    }

    private sealed class NativeOnlyProfile : PdfColorTransform
    {
        internal override int Components => 4;
        internal override bool CanConvertFromXyz => true;
        internal override void ToXyz(ReadOnlySpan<double> device, Span<double> xyz) =>
            throw new InvalidOperationException("Native identity samples must retain their original ink.");
        internal override void FromXyz(ReadOnlySpan<double> xyz, Span<double> device) =>
            throw new InvalidOperationException("Native identity samples must retain their original ink.");
    }
}
