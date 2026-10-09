using System.Reflection;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

[Collection(nameof(SoftMaskAllocationCollection))]
public sealed class PdfGraphicsSoftMaskRowStorageTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int Left = -31, Top = 17, Width = 2048, Height = 1536;

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 4)]
    [InlineData(true, 1)]
    [InlineData(true, 4)]
    public void Render_NonIsolatedRepeatedRowMaskPreservesBackdrop(bool transparent, int parallelism)
    {
        var page = new PdfPageRenderer(CreateDocument(transparent)).Render(0,
            new PdfRenderOptions(Width, Height, transparentBackground: transparent)
            { MaximumParallelism = parallelism });
        Assert.Empty(page.Diagnostics);
        int[] columns = [512, 1536];
        int[] rows = [0, 512, 1023, 1024, 1535];
        foreach (int y in rows)
            foreach (int x in columns)
            {
                byte alpha = x < 1024 ? (byte)64 : (byte)191;
                byte[] expected = transparent ? [0, 0, 255, alpha]
                    : x < 1024 ? [96, 96, 160, 255] : [32, 32, 223, 255];
                Assert.Equal(expected, page.Pixels.Slice((y * Width + x) * 4, 4).ToArray());
            }
    }

    [Fact]
    public void Mask_RepeatedRowsAvoidDuplicateFullPlaneAllocation()
    {
        static byte Value(int x, int y) => x < Left + 13 || x >= Left + Width - 19
            || y < Top + 23 || y >= Top + Height - 29 ? (byte)0
            : x < Left + Width / 2 ? (byte)64 : (byte)191;
        var mask = CreateMask((l, t, r, b) => (Samples(l, t, r, b, Value), 0));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Ensure(mask);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 4 * 1024 * 1024, $"Repeated-row mask allocated {allocated} bytes.");
        var at = Reader(mask);
        int[] columns = [Left - 1, Left, Left + 13, Left + Width / 2, Left + Width - 20, Left + Width];
        for (int y = Top; y < Top + Height; y++)
            foreach (int x in columns) Assert.Equal(Value(x, y), at(x, y));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mask_DenseFallbackPreservesEarlierRows(bool late)
    {
        byte Value(int x, int y) => late && y - Top < 1100 ? (byte)0
            : (byte)(((y - Top) * 37 + (x - Left) * 17) % 256);
        var mask = CreateMask((l, t, r, b) => (Samples(l, t, r, b, Value), 0));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Ensure(mask);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 8 * 1024 * 1024, $"Dense fallback allocated {allocated} bytes.");
        var at = Reader(mask);
        int[] columns = [Left, Left + 13, Left + 1024, Left + Width - 1];
        for (int y = Top; y < Top + Height; y++)
            foreach (int x in columns) Assert.Equal(Value(x, y), at(x, y));
    }

    [Fact]
    public void Mask_CanceledTileDoesNotPublishIncompleteRows()
    {
        bool cancel = true;
        var mask = CreateMask((l, t, r, b) =>
        {
            if (t > Top && cancel) throw new OperationCanceledException();
            return (null, t == Top ? (byte)64 : (byte)192);
        });
        var exception = Assert.Throws<TargetInvocationException>(() => Ensure(mask));
        Assert.IsType<OperationCanceledException>(exception.InnerException);
        var at = Reader(mask);
        Assert.Throws<OperationCanceledException>(() => at(Left, Top));
        cancel = false;
        Ensure(mask);
        Assert.Equal(64, at(Left, Top));
        Assert.Equal(192, at(Left, Top + 1024));
        Assert.Equal(192, at(Left + Width - 1, Top + Height - 1));
        Assert.Equal(0, at(Left - 1, Top));
    }

    [Fact]
    public void Mask_ConcurrentPartialRequestsPromoteWithoutChangingSamples()
    {
        static byte Value(int x, int y) => y - Top < 1024 ? (byte)64 : (byte)192;
        var mask = CreateMask((l, t, r, b) => (Samples(l, t, r, b, Value), 0));
        Parallel.Invoke(() => Ensure(mask, Top, Top + 768),
            () => Ensure(mask, Top + 768, Top + Height));
        var at = Reader(mask);
        for (int y = Top; y < Top + Height; y++)
        {
            Assert.Equal(Value(Left, y), at(Left, y));
            Assert.Equal(Value(Left + Width - 1, y), at(Left + Width - 1, y));
        }
    }

    private static object CreateMask(Func<int, int, int, int, (byte[]? Samples, byte Constant)> render)
    {
        Type type = typeof(PdfPageRenderer).GetNestedType("GraphicsSoftMask", BindingFlags.NonPublic)!;
        return Activator.CreateInstance(type, Flags, null,
            [Left, Top, Left + Width, Top + Height, (byte)0, render], null)!;
    }

    private static void Ensure(object mask, int top = Top, int bottom = Top + Height) =>
        mask.GetType().GetMethod("ForBounds", Flags)!.Invoke(mask, [Left, top, Left + Width, bottom]);

    private static Func<int, int, byte> Reader(object mask) =>
        mask.GetType().GetMethod("At", Flags)!.CreateDelegate<Func<int, int, byte>>(mask);

    private static byte[] Samples(int left, int top, int right, int bottom, Func<int, int, byte> value)
    {
        byte[] samples = new byte[(right - left) * (bottom - top)];
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++) samples[(y - top) * (right - left) + x - left] = value(x, y);
        return samples;
    }

    private static PdfDocument CreateDocument(bool transparent)
    {
        string backdrop = transparent ? "" : "0.5 g 0 0 10 10 re f ";
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10,
            Encoding.ASCII.GetBytes(backdrop + "q /Mask gs /Paint Do Q")).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var reference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var group = new PdfDictionary([Entry("S", Name("Transparency")), Entry("CS", Name("DeviceRGB"))]);
        var mask = new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", Bounds()), Entry("Group", group),
            Entry("Resources", new PdfDictionary([]))]),
            Encoding.ASCII.GetBytes("0.25 g 0 0 5 10 re f 0.75 g 5 0 5 10 re f"));
        var form = new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", Bounds()), Entry("Group", group),
            Entry("Resources", new PdfDictionary([]))]), Encoding.ASCII.GetBytes("1 0 0 rg 0 0 10 10 re f"));
        var resources = new PdfDictionary([
            Entry("ExtGState", new PdfDictionary([Entry("Mask", new PdfDictionary([
                Entry("SMask", new PdfDictionary([Entry("S", Name("Luminosity")),
                    Entry("BC", new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)])),
                    Entry("G", update.AddObject(mask))]))]))])),
            Entry("XObject", new PdfDictionary([Entry("Paint", update.AddObject(form))]))]);
        return PdfDocument.Open(update.ReplaceObject(reference.ObjectNumber,
            new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
                .Append(Entry("Resources", resources)))).Build());
    }

    private static PdfArray Bounds() => new([new PdfInteger(0), new PdfInteger(0), new PdfInteger(10), new PdfInteger(10)]);
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
