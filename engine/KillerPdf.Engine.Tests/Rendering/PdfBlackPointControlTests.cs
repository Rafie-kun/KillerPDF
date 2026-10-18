using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfBlackPointControlTests
{
    private static readonly double[] IdentityValues = [0.0123, 0.4567, 0.8910];
    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void Mapping_UsesBothProfileBlackPointsAndPreservesWhite(bool gray, int intent)
    {
        var source = new PdfIccProfileTransform(RaisedBlackProfile(gray), intent);
        var destination = new PdfIccProfileTransform(MatrixProfile(!gray, 0.15), intent);
        Assert.True(PdfBlackPointCompensation.TryCreate(source, destination, intent, out var mapping));
        Assert.InRange(mapping.Scale, 0.85 / 0.9 - 0.00003, 0.85 / 0.9 + 0.00003);
        Span<double> device = stackalloc double[3];
        Span<double> xyz = stackalloc double[3];
        for (int step = 0; step <= 256; step++)
        {
            double input = step / 256d;
            device.Fill(input);
            source.ToXyz(device[..source.Components], xyz);
            mapping.Apply(xyz);
            double expectedY = 0.15 + 0.85 * input;
            Assert.InRange(xyz[1], expectedY - 0.00004, expectedY + 0.00004);
            // An ideal D50 neutral isolates the XYZ mapping from matrix-tag rounding.
            double sourceY = 0.1 + 0.9 * input;
            xyz[0] = 0.9642 * sourceY;
            xyz[1] = sourceY;
            xyz[2] = 0.8249 * sourceY;
            mapping.Apply(xyz);
            Assert.InRange(xyz[0], 0.9642 * expectedY - 0.00004, 0.9642 * expectedY + 0.00004);
            Assert.InRange(xyz[1], expectedY - 0.00004, expectedY + 0.00004);
            Assert.InRange(xyz[2], 0.8249 * expectedY - 0.00004, 0.8249 * expectedY + 0.00004);
        }
        Assert.True(PdfBlackPointCompensation.TryCreate(source, null, intent, out mapping));
        Assert.InRange(mapping.Scale, 1 / 0.9 - 0.00003, 1 / 0.9 + 0.00003);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mapping_LeavesAbsoluteIntentAndSameProfileValuesUnchanged(bool gray)
    {
        var profile = new PdfIccProfileTransform(RaisedBlackProfile(gray));
        foreach (int intent in new[] { 0, 1, 2, 3 })
        {
            Assert.True(PdfBlackPointCompensation.TryCreate(profile, profile, intent, out var mapping));
            Assert.True(mapping.IsIdentity);
            Span<double> values = [0.0123, 0.4567, 0.8910];
            mapping.Apply(values);
            Assert.Equal(IdentityValues, values.ToArray());
        }
        Assert.True(PdfBlackPointCompensation.TryCreate(profile, null, 3, out var absolute));
        Assert.True(absolute.IsIdentity);
    }

    [Theory]
    [MemberData(nameof(ObjectPolicies))]
    public void Render_OnMapsSourceBlackToDisplayBlackAndRestoresObjectPolicy(bool image, bool gray,
        string intent, string disabled, bool indirect, string kind)
    {
        bool single = gray || kind != "ICC";
        bool named = kind is "Separation" or "DeviceN";
        string whitePaint = named ? "0" : single ? "1" : "1 1 1";
        string blackPaint = named ? "1" : single ? "0" : "0 0 0";
        string Paint(int column, bool white = false) => image
            ? $"q 1 0 0 1 {column} 0 cm /{(white ? "White" : "Black")} Do Q"
            : (white ? whitePaint + " scn " : "") + $"{column} 0 1 1 re f";
        string content = "/S cs " + blackPaint + " scn "
            + $"/{intent} ri /Off gs " + Paint(0) + " /On gs " + Paint(1)
            + " q /Off gs " + Paint(2) + " Q " + Paint(3) + " " + Paint(4, true);
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(5, 1,
            Encoding.ASCII.GetBytes(content)).Build());
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var pageRef = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(pageRef);
        var update = new PdfIncrementalUpdateBuilder(source);
        var profile = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(gray ? 1 : 3))]),
            RaisedBlackProfile(gray)));
        PdfArray baseSpace = new([Name("ICCBased"), profile]);
        PdfDictionary tint = new([Entry("FunctionType", new PdfInteger(2)),
            Entry("Domain", new PdfArray([new PdfInteger(0), new PdfInteger(1)])),
            Entry("C0", new PdfArray(Enumerable.Repeat<PdfObject>(new PdfInteger(1), gray ? 1 : 3))),
            Entry("C1", new PdfArray(Enumerable.Repeat<PdfObject>(new PdfInteger(0), gray ? 1 : 3))),
            Entry("N", new PdfInteger(1))]);
        PdfArray space = kind switch
        {
            "Indexed" => new([Name("Indexed"), baseSpace, new PdfInteger(1),
                new PdfString(gray ? [0, 255] : [0, 0, 0, 255, 255, 255], PdfStringForm.Hexadecimal)]),
            "Separation" => new([Name("Separation"), Name("Spot"), baseSpace, tint]),
            "DeviceN" => new([Name("DeviceN"), new PdfArray([Name("Spot")]), baseSpace, tint]),
            _ => baseSpace
        };
        PdfObject Policy(string name) => indirect ? update.AddObject(Name(name)) : Name(name);
        PdfObject Image(byte sample)
        {
            if (named) sample = (byte)(255 - sample);
            return update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)), Entry("Height", new PdfInteger(1)),
                Entry("BitsPerComponent", new PdfInteger(8)), Entry("ColorSpace", space)]),
                single ? [sample] : [sample, sample, sample]));
        }
        var resources = new PdfDictionary([
            Entry("ColorSpace", new PdfDictionary([Entry("S", space)])),
            Entry("ExtGState", new PdfDictionary([
                Entry("On", new PdfDictionary([Entry("UseBlackPtComp", Policy("ON"))])),
                Entry("Off", new PdfDictionary([Entry("UseBlackPtComp", Policy(disabled))]))])),
            Entry("XObject", new PdfDictionary([Entry("Black", Image(0)), Entry("White", Image(255))]))]);
        update.ReplaceObject(pageRef.ObjectNumber, new PdfDictionary(page.Where(pair => !pair.Key.Equals(Name("Resources")))
            .Append(Entry("Resources", resources))));
        var result = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(40, 8));
        Assert.Empty(result.Diagnostics);
        byte[] pixels = result.Pixels.ToArray();
        for (int column = 0; column < 5; column++)
        {
            // The profile's neutral source black is Y=0.1 and white is Y=1.
            // Mapping those endpoints to display Y=0 and Y=1 makes ON black zero.
            // Without compensation, linear 0.1 encodes to sRGB approximately 89.
            int expected = column == 4 ? 255 : column is 1 or 3 && intent != "AbsoluteColorimetric" ? 0 : 89;
            int position = (4 * 40 + column * 8 + 4) * 4;
            for (int channel = 0; channel < 3; channel++)
                Assert.InRange((int)pixels[position + channel], Math.Max(0, expected - 1), Math.Min(255, expected + 1));
            Assert.Equal((byte)255, pixels[position + 3]);
        }
    }

    public static TheoryData<bool, bool, string, string, bool, string> ObjectPolicies
    {
        get
        {
            var data = new TheoryData<bool, bool, string, string, bool, string>();
            foreach (bool image in new[] { false, true })
            foreach (bool gray in new[] { false, true })
            foreach (string intent in new[] { "Perceptual", "RelativeColorimetric", "Saturation", "AbsoluteColorimetric" })
            foreach (string disabled in new[] { "OFF", "Default" })
            foreach (bool indirect in new[] { false, true })
            foreach (string kind in new[] { "ICC", "Indexed", "Separation", "DeviceN" })
                data.Add(image, gray, intent, disabled, indirect, kind);
            return data;
        }
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Render_GroupUsesItsDestinationBlackAndInvokingObjectPolicy(bool gray, bool inside, bool outside)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes($"/{(outside ? "On" : "Off")} gs /F Do")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var original = (PdfDictionary)source.Resolve(page.Reference);
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfArray Space(double black) => new([Name("ICCBased"), update.AddObject(new PdfStream(
            new PdfDictionary([Entry("N", new PdfInteger(gray ? 1 : 3))]), MatrixProfile(gray, black)))]);
        PdfDictionary states = new([Entry("On", new PdfDictionary([Entry("UseBlackPtComp", Name("ON"))])),
            Entry("Off", new PdfDictionary([Entry("UseBlackPtComp", Name("OFF"))]))]);
        PdfDictionary resources = new([Entry("ExtGState", states),
            Entry("ColorSpace", new PdfDictionary([Entry("S", Space(0.1))]))]);
        string content = $"/{(inside ? "On" : "Off")} gs /S cs "
            + (gray ? "0.25" : "0.25 0.25 0.25") + " scn 0 0 1 1 re f";
        var form = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", new PdfArray([
                new PdfInteger(0), new PdfInteger(0), new PdfInteger(1), new PdfInteger(1)])),
            Entry("Resources", resources), Entry("Group", new PdfDictionary([
                Entry("S", Name("Transparency")), Entry("I", new PdfBoolean(true)), Entry("CS", Space(0.15))]))]),
            Encoding.ASCII.GetBytes(content)));
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(original
            .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", new PdfDictionary([
                Entry("ExtGState", states), Entry("XObject", new PdfDictionary([Entry("F", form)]))])))));
        var result = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(8, 8));
        Assert.Empty(result.Diagnostics);
        // With compensation, the group's native value is 0.25, quantized to 64.
        // Without it, (0.325 - 0.15) / 0.85 quantizes to 53.
        double native = (inside ? 64 : 53) / 255d;
        double linear = outside ? native : 0.15 + 0.85 * native;
        int expected = (int)Math.Round(255 * (1.055 * Math.Pow(linear, 1 / 2.4) - 0.055));
        byte[] pixels = result.Pixels.ToArray();
        int offset = (4 * 8 + 4) * 4;
        for (int channel = 0; channel < 3; channel++) Assert.InRange((int)pixels[offset + channel], expected - 1, expected + 1);
        Assert.Equal((byte)255, pixels[offset + 3]);
    }

    private static byte[] RaisedBlackProfile(bool gray) => MatrixProfile(gray, 0.1);

    [Theory]
    [InlineData("CalGray", false)]
    [InlineData("CalGray", true)]
    [InlineData("CalRGB", false)]
    [InlineData("CalRGB", true)]
    [InlineData("Lab", false)]
    [InlineData("Lab", true)]
    public void Render_CalibratedAndLabPaintMapIntoRaisedBlackGroup(string kind, bool enabled)
    {
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(1, 1,
            Encoding.ASCII.GetBytes("/F Do")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(source);
        static PdfArray Reals(params double[] values) => new(values.Select(value => (PdfObject)new PdfReal(value)));
        var parameters = new List<KeyValuePair<PdfName, PdfObject>>
        {
            Entry("WhitePoint", Reals(0.9642, 1, 0.8249))
        };
        if (kind == "CalRGB") parameters.Add(Entry("Matrix", Reals(0.9642, 0, 0, 0, 1, 0, 0, 0, 0.8249)));
        var space = new PdfArray([Name(kind), new PdfDictionary(parameters)]);
        var destination = new PdfArray([Name("ICCBased"), update.AddObject(new PdfStream(
            new PdfDictionary([Entry("N", new PdfInteger(1))]), MatrixProfile(true, 0.15)))]);
        string paint = kind == "CalGray" ? "0.25" : kind == "CalRGB" ? "0.25 0.25 0.25" : "57.07542089390265 0 0";
        var form = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", Reals(0, 0, 1, 1)),
            Entry("Resources", new PdfDictionary([
                Entry("ColorSpace", new PdfDictionary([Entry("S", space)])),
                Entry("ExtGState", new PdfDictionary([Entry("P", new PdfDictionary([
                    Entry("UseBlackPtComp", Name(enabled ? "ON" : "OFF"))]))]))])),
            Entry("Group", new PdfDictionary([Entry("S", Name("Transparency")),
                Entry("I", new PdfBoolean(true)), Entry("CS", destination)]))]),
            Encoding.ASCII.GetBytes($"/S cs {paint} scn /P gs 0 0 1 1 re f")));
        ReplaceResources(source, update, page.Reference, new PdfDictionary([
            Entry("XObject", new PdfDictionary([Entry("F", form)]))]));
        var result = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(8, 8));
        Assert.Empty(result.Diagnostics);
        double native = enabled ? 64 / 255d : 30 / 255d;
        double linear = 0.15 + 0.85 * native;
        int expected = (int)Math.Round(255 * (1.055 * Math.Pow(linear, 1 / 2.4) - 0.055));
        AssertNeutral(result, 4, 4, expected);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Render_ImageMaskUsesSavedCompensatedPaint(bool gray, bool inverted)
    {
        string paint = gray ? "0" : "0 0 0";
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            Encoding.ASCII.GetBytes($"/S cs {paint} scn /Off gs /M Do /On gs q 1 0 0 1 1 0 cm /M Do Q")).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(source);
        var profile = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(gray ? 1 : 3))]),
            RaisedBlackProfile(gray)));
        var mask = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)), Entry("Height", new PdfInteger(1)),
            Entry("ImageMask", new PdfBoolean(true)), Entry("BitsPerComponent", new PdfInteger(1)),
            Entry("Decode", new PdfArray([new PdfInteger(inverted ? 1 : 0), new PdfInteger(inverted ? 0 : 1)]))]),
            inverted ? [128] : [0]));
        ReplaceResources(source, update, page.Reference, new PdfDictionary([
            Entry("ColorSpace", new PdfDictionary([Entry("S", new PdfArray([Name("ICCBased"), profile]))])),
            Entry("ExtGState", Policies()), Entry("XObject", new PdfDictionary([Entry("M", mask)]))]));
        var result = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(16, 8));
        Assert.Empty(result.Diagnostics);
        AssertNeutral(result, 4, 4, 89);
        AssertNeutral(result, 12, 4, 0);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Render_ShadingAndPatternUseInvokingPolicy(bool gray, bool radial, bool pattern)
    {
        string Paint(int column) => $"q 1 0 0 1 {column} 0 cm "
            + (pattern ? $"/Pattern cs /P{column} scn 0 0 1 1 re f" : "0 0 1 1 re W n /Sh sh") + " Q";
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1,
            Encoding.ASCII.GetBytes("/Off gs " + Paint(0) + " /On gs " + Paint(1))).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfArray Numbers(params int[] values) => new(values.Select(value => (PdfObject)new PdfInteger(value)));
        var profile = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(gray ? 1 : 3))]),
            RaisedBlackProfile(gray)));
        PdfArray black = gray ? Numbers(0) : Numbers(0, 0, 0);
        var shading = new PdfDictionary([
            Entry("ShadingType", new PdfInteger(radial ? 3 : 2)),
            Entry("ColorSpace", new PdfArray([Name("ICCBased"), profile])),
            Entry("Coords", radial ? Numbers(0, 0, 0, 0, 0, 2) : Numbers(0, 0, 1, 0)),
            Entry("Function", new PdfDictionary([Entry("FunctionType", new PdfInteger(2)),
                Entry("Domain", Numbers(0, 1)), Entry("C0", black), Entry("C1", black), Entry("N", new PdfInteger(1))]))]);
        ReplaceResources(source, update, page.Reference, new PdfDictionary([
            Entry("ExtGState", Policies()), Entry("Shading", new PdfDictionary([Entry("Sh", shading)])),
            Entry("Pattern", new PdfDictionary(Enumerable.Range(0, 2).Select(column => Entry($"P{column}", new PdfDictionary([
                Entry("PatternType", new PdfInteger(2)), Entry("Shading", shading),
                Entry("Matrix", Numbers(1, 0, 0, 1, column, 0))])))))]));
        var result = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(16, 8));
        Assert.Empty(result.Diagnostics);
        AssertNeutral(result, 4, 4, 89);
        AssertNeutral(result, 12, 4, 0);
    }

    private static PdfDictionary Policies() => new([
        Entry("On", new PdfDictionary([Entry("UseBlackPtComp", Name("ON"))])),
        Entry("Off", new PdfDictionary([Entry("UseBlackPtComp", Name("OFF"))]))]);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Render_SavedStrokeAndFillRebindWhenStateIntentOrPolicyChanges(bool gray, bool stroke)
    {
        string paint = gray ? "0" : "0 0 0";
        string select = $"/S {(stroke ? "CS" : "cs")} {paint} {(stroke ? "SCN" : "scn")} ";
        string Paint(int column) => stroke ? $"{column} 0.5 m {column + 1} 0.5 l S" : $"{column} 0 1 1 re f";
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(4, 1, Encoding.ASCII.GetBytes(
            "1 w " + select + "/Absolute gs " + Paint(0) + " /RelativeColorimetric ri " + Paint(1)
            + " /Invalid gs " + Paint(2) + " /On gs " + Paint(3))).Build());
        var page = PdfPageTree.Read(source).Pages[0];
        var update = new PdfIncrementalUpdateBuilder(source);
        var profile = update.AddObject(new PdfStream(new PdfDictionary([Entry("N", new PdfInteger(gray ? 1 : 3))]),
            RaisedBlackProfile(gray)));
        ReplaceResources(source, update, page.Reference, new PdfDictionary([
            Entry("ColorSpace", new PdfDictionary([Entry("S", new PdfArray([Name("ICCBased"), profile]))])),
            Entry("ExtGState", new PdfDictionary([
                Entry("Absolute", new PdfDictionary([Entry("RI", Name("AbsoluteColorimetric")),
                    Entry("UseBlackPtComp", Name("ON"))])),
                Entry("Invalid", new PdfDictionary([Entry("UseBlackPtComp", Name("on"))])),
                Entry("On", new PdfDictionary([Entry("UseBlackPtComp", Name("ON"))]))]))]));
        var result = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(32, 8));
        Assert.Equal(["The black-point compensation policy is invalid; the default policy was used."], result.Diagnostics);
        for (int column = 0; column < 4; column++) AssertNeutral(result, column * 8 + 4, 4, column % 2 == 0 ? 89 : 0);
    }

    private static void ReplaceResources(PdfDocument source, PdfIncrementalUpdateBuilder update,
        PdfIndirectReference page, PdfDictionary resources)
    {
        var original = (PdfDictionary)source.Resolve(page);
        update.ReplaceObject(page.ObjectNumber, new PdfDictionary(original
            .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources))));
    }

    private static void AssertNeutral(PdfRenderedPage result, int x, int y, int expected)
    {
        byte[] pixels = result.Pixels.ToArray();
        int offset = (y * result.Width + x) * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange((int)pixels[offset + channel], Math.Max(0, expected - 1), Math.Min(255, expected + 1));
        Assert.Equal((byte)255, pixels[offset + 3]);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public void Mapping_LutDestinationUsesStraightOrFittedShadowBlack(int intent, bool quadratic)
    {
        var source = new PdfIccProfileTransform(RaisedBlackProfile(false), intent);
        var destination = new PdfIccProfileTransform(LutProfile(quadratic), intent);
        Assert.True(destination.IsLutBased);
        Assert.True(PdfBlackPointCompensation.TryCreate(source, destination, intent, out var mapping));
        // Independent polynomial fitting of the quantized fixture gives this
        // relative-intent shadow root instead of the unquantized root of zero.
        double blackY = quadratic ? intent == 1 ? 0.0006480908340989557 : 0 : Math.Pow(46d / 116, 3);
        double sourceY = Math.Round(0.1 * 65536) / 65536;
        double expected = (1 - blackY) / (1 - sourceY);
        Assert.InRange(mapping.Scale, expected - 0.0000003, expected + 0.0000003);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mapping_CapsProfileBlackLightnessAtFifty(bool gray)
    {
        var source = new PdfIccProfileTransform(MatrixProfile(gray, 0.4));
        Assert.True(PdfBlackPointCompensation.TryCreate(source, null, 1, out var mapping));
        double expected = 1 / (1 - Math.Pow(66d / 116, 3));
        Assert.InRange(mapping.Scale, expected - 1e-12, expected + 1e-12);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Mapping_CmykUsesPerceptualInverseForSourceAndSampledDestination(int intent)
    {
        var cmyk = new PdfIccProfileTransform(CmykProfile(), intent);
        var rgb = new PdfIccProfileTransform(RaisedBlackProfile(false), intent);
        Assert.Equal(4, cmyk.Components);
        double blackY = Math.Pow(46d / 116, 3);
        Assert.True(PdfBlackPointCompensation.TryCreate(cmyk, null, intent, out var source));
        Assert.InRange(source.Scale, 1 / (1 - blackY) - 1e-12, 1 / (1 - blackY) + 1e-12);
        Assert.True(PdfBlackPointCompensation.TryCreate(rgb, cmyk, intent, out var destination));
        double sourceY = Math.Round(0.1 * 65536) / 65536;
        double expected = (1 - blackY) / (1 - sourceY);
        Assert.InRange(destination.Scale, expected - 0.0000003, expected + 0.0000003);
        Assert.True(PdfBlackPointCompensation.TryCreate(cmyk, cmyk, intent, out var native));
        Assert.True(native.IsIdentity);
    }

    internal static byte[] CmykProfile() => LutProfileCore(false, 4);
    private static byte[] LutProfile(bool quadratic) => LutProfileCore(quadratic, 3);

    private static byte[] LutProfileCore(bool quadratic, int components)
    {
        byte[] Table(bool reverse)
        {
            const int entries = 256;
            int inputs = reverse ? 3 : components, outputs = reverse ? components : 3, cells = 1 << inputs;
            byte[] data = new byte[52 + 2 * ((inputs + outputs) * entries + cells * outputs)];
            "mft2"u8.CopyTo(data);
            data[8] = (byte)inputs;
            data[9] = (byte)outputs;
            data[10] = 2;
            for (int row = 0; row < 3; row++) BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12 + row * 16), 65536);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(48), entries);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(50), entries);
            int offset = 52;
            void Write(double value)
            {
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), (ushort)Math.Round(Math.Clamp(value, 0, 1) * 65535));
                offset += 2;
            }
            for (int channel = 0; channel < inputs; channel++)
                for (int index = 0; index < entries; index++)
                {
                    double value = index / 255d;
                    if (reverse && channel == 0)
                        value = quadratic ? value * 65535 / 65280
                            : (value * 65535 / 65280 * 100 - 30) / 70;
                    else if (!reverse && channel == 0 && quadratic) value *= value;
                    Write(value);
                }
            for (int cell = 0; cell < cells; cell++)
                for (int channel = 0; channel < outputs; channel++)
                {
                    double light = components == 4 ? 1 - (cell & 1) : cell >> 2;
                    Write(reverse ? components == 4 ? channel == 3 ? 1 - (cell >> 2) : 0 : cell >> 2
                        : channel == 0 ? (30 + 70 * light) / 100d * 65280 / 65535 : 32768d / 65535);
                }
            for (int channel = 0; channel < outputs; channel++)
                for (int index = 0; index < entries; index++) Write(index / 255d);
            return data;
        }
        byte[] forward = Table(false), reverse = Table(true);
        return BuildProfile(false, "Lab ", [("A2B0", forward), ("B2A0", reverse),
            ("A2B1", forward), ("B2A1", reverse), ("A2B2", forward), ("B2A2", reverse),
            ("wtpt", Xyz(0.9642, 1, 0.8249))], components == 4 ? "CMYK" : null);
    }

    private static byte[] MatrixProfile(bool gray, double black)
    {
        var tags = new List<(string Name, byte[] Data)>();
        byte[] curve = new byte[40];
        "para"u8.CopyTo(curve);
        BinaryPrimitives.WriteUInt16BigEndian(curve.AsSpan(8), 4);
        double[] parameters = [1, 1 - black, black, 1 - black, 0, 0, black];
        for (int i = 0; i < parameters.Length; i++)
            BinaryPrimitives.WriteInt32BigEndian(curve.AsSpan(12 + i * 4), (int)Math.Round(parameters[i] * 65536));
        if (gray) tags.Add(("kTRC", curve));
        else
        {
            foreach (string name in new[] { "rTRC", "gTRC", "bTRC" }) tags.Add((name, curve));
            tags.Add(("rXYZ", Xyz(0.4360747, 0.2225045, 0.0139322)));
            tags.Add(("gXYZ", Xyz(0.3850649, 0.7168786, 0.0971045)));
            tags.Add(("bXYZ", Xyz(0.1430804, 0.0606169, 0.7141733)));
        }
        tags.Add(("wtpt", Xyz(0.9642, 1, 0.8249)));
        return BuildProfile(gray, "XYZ ", tags);
    }

    private static byte[] BuildProfile(bool gray, string pcs, List<(string Name, byte[] Data)> tags, string? deviceSpace = null)
    {
        int size = 132 + tags.Count * 12 + tags.Sum(tag => (tag.Data.Length + 3) & ~3);
        var bytes = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 0x02100000);
        Encoding.ASCII.GetBytes(deviceSpace == "CMYK" ? "prtr" : "mntr").CopyTo(bytes, 12);
        Encoding.ASCII.GetBytes(deviceSpace ?? (gray ? "GRAY" : "RGB ")).CopyTo(bytes, 16);
        Encoding.ASCII.GetBytes(pcs).CopyTo(bytes, 20);
        "acsp"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(64), 1);
        Xyz(0.9642, 1, 0.8249).AsSpan(8).CopyTo(bytes.AsSpan(68));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(128), (uint)tags.Count);
        int offset = 132 + tags.Count * 12;
        for (int index = 0; index < tags.Count; index++)
        {
            int entry = 132 + index * 12;
            Encoding.ASCII.GetBytes(tags[index].Name).CopyTo(bytes, entry);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 8), (uint)tags[index].Data.Length);
            tags[index].Data.CopyTo(bytes, offset);
            offset += (tags[index].Data.Length + 3) & ~3;
        }
        return bytes;
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        var data = new byte[20];
        "XYZ "u8.CopyTo(data);
        double[] values = [x, y, z];
        for (int i = 0; i < 3; i++) BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8 + 4 * i), (int)Math.Round(values[i] * 65536));
        return data;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
}
