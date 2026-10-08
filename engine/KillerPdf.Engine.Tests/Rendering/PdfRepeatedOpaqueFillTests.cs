using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfRepeatedOpaqueFillTests
{
    private const string Background = ".002 .002 .002 .1 k 0 0 32 32 re f ";
    private const string Shape = "4.25 16.1 m 15.8 4.35 l 27.3 15.8 l 16.35 27.2 l h f ";
    private const string ShiftedShape = "4.75 16.1 m 16.3 4.35 l 27.8 15.8 l 16.85 27.2 l h f ";
    private const string Red = "0 1 1 0 k " + Shape;
    private const string Final = ".002 .002 .002 0 k " + Shape;
    private const string Barrier = "q 0 0 m 0 0 l S Q ";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NativeMagentaSeparation_CoincidentFillMatchesCombinedProcessPaint(
        bool withSpot, bool nestedFractionalForms)
    {
        string prefix = Background + (withSpot
            ? "q /SpotOn gs /Spot cs .5 scn 0 0 32 32 re f Q " : "");
        string first = "q /Paint gs .5 .8 .5 .1 k " + Shape + "Q ";
        string second = "q /Paint gs /Magenta cs .3 scn " + Shape + "Q ";
        string combined = "q /Paint gs .5 .3 .5 .1 k " + Shape + "Q ";
        PdfRenderedPage expected = RenderContent(prefix + combined, 1,
            nestedFractionalForms: nestedFractionalForms);
        PdfRenderedPage actual = RenderContent(prefix + first + second, 1,
            nestedFractionalForms: nestedFractionalForms);

        AssertSame(expected, actual);
    }

    [Theory]
    [InlineData("Cyan", "0 .8 .8 .8", "0")]
    [InlineData("Magenta", ".8 0 .8 .8", "0")]
    [InlineData("Yellow", ".8 .8 0 .8", "0")]
    [InlineData("Black", ".8 .8 .8 0", "0")]
    [InlineData("Cyan", ".3 .8 .8 .8", ".3")]
    [InlineData("Magenta", ".8 .3 .8 .8", ".3")]
    [InlineData("Yellow", ".8 .8 .3 .8", ".3")]
    [InlineData("Black", ".8 .8 .8 .3", ".3")]
    [InlineData("Cyan", "1 .8 .8 .8", "1")]
    [InlineData("Magenta", ".8 1 .8 .8", "1")]
    [InlineData("Yellow", ".8 .8 1 .8", "1")]
    [InlineData("Black", ".8 .8 .8 1", "1")]
    public void NativeProcessSeparation_ReplacementMatchesExplicitCombinedDeviceN(
        string colorant, string combinedValues, string tint)
    {
        string prefix = Background + "q /SpotOn gs /Spot cs .5 scn 0 0 32 32 re f Q ";
        string first = "q /Paint gs .8 .8 .8 .8 k " + Shape + "Q ";
        string second = "q /Paint gs /" + colorant + " cs " + tint + " scn " + Shape + "Q ";
        string combined = "q /Paint gs /Process cs " + combinedValues + " scn " + Shape + "Q ";
        AssertSame(RenderContent(prefix + combined, 1), RenderContent(prefix + first + second, 1));
    }

    [Fact]
    public void NativeProcessDeviceN_ZeroChannelsReplaceEarlierCoincidentPaint()
    {
        string prefix = Background + "q /SpotOn gs /Spot cs .5 scn 0 0 32 32 re f Q ";
        string first = "q /Paint gs .8 .8 .8 .8 k " + Shape + "Q ";
        string final = "q /Paint gs /Process cs .5 0 .3 0 scn " + Shape + "Q ";
        AssertSame(RenderContent(prefix + final, 1), RenderContent(prefix + first + final, 1));
    }

    [Theory]
    [InlineData("path")]
    [InlineData("clip")]
    [InlineData("intent")]
    [InlineData("alpha")]
    [InlineData("policy")]
    public void NativeProcessSeparation_DifferentEffectivePaintKeepsSequentialBehavior(string difference)
    {
        string prefix = Background + "q /SpotOn gs /Spot cs .5 scn 0 0 32 32 re f Q ";
        string first = "q /Paint gs " + (difference == "intent" ? "/RelativeColorimetric ri " : "")
            + ".5 .8 .5 .1 k " + Shape + "Q ";
        string second = "q /Paint gs " + (difference switch
        {
            "clip" => "0 0 18 32 re W n ",
            "intent" => "/Perceptual ri ",
            "alpha" => "/Half gs ",
            "policy" => "/Off gs ",
            _ => ""
        }) + "/Magenta cs .3 scn " + (difference == "path" ? ShiftedShape : Shape) + "Q ";
        AssertSame(RenderContent(prefix + first + Barrier + second, 1),
            RenderContent(prefix + first + second, 1));
    }

    [Fact]
    public void NativeProcessDeviceN_ZeroInkOverWhiteLeavesEveryChannelWhite()
    {
        string first = "q /Paint gs .8 .8 .8 .8 k " + Shape + "Q ";
        string clear = "q /Paint gs /Process cs 0 0 0 0 scn " + Shape + "Q ";
        PdfRenderedPage actual = RenderContent(first + clear, 1);
        Assert.Empty(actual.Diagnostics);
        foreach (byte channel in actual.Pixels.Span) Assert.Equal(255, channel);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void IdenticalOpaqueProcessFill_ReplacesEarlierPaintAtAntialiasedEdges(
        int overprintMode, bool zeroTintSpot)
    {
        PdfRenderedPage expected = Render(overprintMode, zeroTintSpot, firstPaint: false);
        PdfRenderedPage actual = Render(overprintMode, zeroTintSpot, firstPaint: true);

        AssertSame(expected, actual);
    }

    [Theory]
    [InlineData(-1, "")]
    [InlineData(0, "")]
    [InlineData(1, "")]
    [InlineData(1, "q /SpotOn gs /Spot cs .5 scn 0 0 32 32 re f Q ")]
    public void IdenticalOpaqueProcessFill_AcrossGraphicsStateScopesMatchesFinalOnly(
        int overprintMode, string spot)
    {
        string prefix = Background + spot;
        PdfRenderedPage expected = RenderContent(prefix + "q /Paint gs " + Final + "Q ", overprintMode);
        PdfRenderedPage actual = RenderContent(prefix
            + "q /Paint gs " + Red + "Q q /Paint gs " + Final + "Q ", overprintMode);

        AssertSame(expected, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Opm1SecondFill_PreservesProcessChannelPaintedOnlyByFirst(bool withSpot)
    {
        string prefix = Background + (withSpot
            ? "q /SpotOn gs /Spot cs .5 scn 0 0 32 32 re f Q " : "");
        string cyan = ".5 0 0 0 k " + Shape;
        string magenta = "0 .5 0 0 k " + Shape;
        string combined = ".5 .5 0 0 k " + Shape;
        PdfRenderedPage expected = RenderContent(prefix + "q /Paint gs " + combined + "Q ", 1);
        PdfRenderedPage actual = RenderContent(prefix
            + "q /Paint gs " + cyan + "Q q /Paint gs " + magenta + "Q ", 1);

        AssertSame(expected, actual);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void IdenticalOpaqueProcessFill_WithDifferentFinalColorMatchesFinalOnly(int overprintMode)
    {
        string final = ".4 .2 .1 0 k " + Shape;
        string prefix = Background + "q /SpotOn gs /Spot cs .5 scn 0 0 32 32 re f Q ";
        PdfRenderedPage expected = RenderContent(prefix + "q /Paint gs " + final + "Q ", overprintMode);
        PdfRenderedPage actual = RenderContent(prefix
            + "q /Paint gs " + Red + "Q q /Paint gs " + final + "Q ", overprintMode);

        AssertSame(expected, actual);
    }

    [Fact]
    public void NestedFractionalFormClip_IdenticalOpaqueFillsMatchFinalOnly()
    {
        string prefix = Background + "q /SpotOn gs /Spot cs 0 scn 0 0 32 32 re f Q ";
        string final = "q /Paint gs " + Final + "Q ";
        PdfRenderedPage expected = RenderContent(prefix + final, 1,
            nestedFractionalForms: true);
        PdfRenderedPage actual = RenderContent(prefix
            + "q /Paint gs " + Red + "Q " + final, 1,
            nestedFractionalForms: true);

        AssertSame(expected, actual);
    }

    [Fact]
    public void HarmlessStrokeBarrier_DoesNotChangeReferencePixels()
    {
        PdfRenderedPage expected = RenderContent(Background + Final, 1);
        PdfRenderedPage actual = RenderContent(Background + Barrier + Final, 1);

        AssertSame(expected, actual);
    }

    [Fact]
    public void ShiftedSecondPath_KeepsExposedFirstPaint()
    {
        string first = "q /Paint gs " + Red + "Q ";
        string second = "q /Paint gs .002 .002 .002 0 k " + ShiftedShape + "Q ";
        PdfRenderedPage expected = RenderContent(Background + first + Barrier + second, 1);
        PdfRenderedPage actual = RenderContent(Background + first + second, 1);
        PdfRenderedPage finalOnly = RenderContent(Background + second, 1);

        AssertSame(expected, actual);
        Assert.False(actual.Pixels.Span.SequenceEqual(finalOnly.Pixels.Span));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TranslucentFill_DoesNotMergeWithOpaqueFill(bool firstIsTranslucent)
    {
        string first = "q /Paint gs " + (firstIsTranslucent ? "/Half gs " : "") + Red + "Q ";
        string second = "q /Paint gs " + (firstIsTranslucent ? "" : "/Half gs ") + Final + "Q ";
        PdfRenderedPage expected = RenderContent(Background + first + Barrier + second, 1);
        PdfRenderedPage actual = RenderContent(Background + first + second, 1);

        AssertSame(expected, actual);
    }

    [Theory]
    [InlineData("clip")]
    [InlineData("transform")]
    [InlineData("intent")]
    public void ChangedFillState_DoesNotMergeAcrossDifferentEffectivePaint(string difference)
    {
        string first = difference switch
        {
            "clip" => "q /Paint gs 0 0 18 32 re W n " + Red + "Q ",
            "transform" => "q /Paint gs 1 0 0 1 .5 0 cm " + Red + "Q ",
            "intent" => "q /Paint gs /RelativeColorimetric ri " + Red + "Q ",
            _ => throw new ArgumentOutOfRangeException(nameof(difference))
        };
        string second = "q /Paint gs "
            + (difference == "intent" ? "/Perceptual ri " : "") + Final + "Q ";
        PdfRenderedPage expected = RenderContent(Background + first + Barrier + second, 1);
        PdfRenderedPage actual = RenderContent(Background + first + second, 1);

        AssertSame(expected, actual);
    }

    [Fact]
    public void NonrectangularClip_DoesNotMergeWithUnclippedFill()
    {
        string first = "q /Paint gs " + Red + "Q ";
        string second = "q /Paint gs 4 4 m 28 4 l 16 28 l h W n " + Final + "Q ";
        PdfRenderedPage expected = RenderContent(Background + first + Barrier + second, 1);
        PdfRenderedPage actual = RenderContent(Background + first + second, 1);

        AssertSame(expected, actual);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedSpotOverprintPolicies_KeepSequentialPaintBehavior(bool firstOverprints)
    {
        string spot = "q /SpotOn gs /Spot cs .5 scn 0 0 32 32 re f Q ";
        string first = "q /" + (firstOverprints ? "Paint" : "Off") + " gs " + Red + "Q ";
        string second = "q /" + (firstOverprints ? "Off" : "Paint") + " gs " + Final + "Q ";
        PdfRenderedPage expected = RenderContent(Background + spot + first + Barrier + second, 1);
        PdfRenderedPage actual = RenderContent(Background + spot + first + second, 1);

        AssertSame(expected, actual);
    }

    [Fact]
    public void DefaultCmykReplacement_DoesNotUseNativeFillCoalescing()
    {
        string first = "q /Paint gs " + Red + "Q ";
        string second = "q /Paint gs " + Final + "Q ";
        PdfRenderedPage expected = RenderContent(Background + first + Barrier + second, 1,
            defaultCmykReplacement: true);
        PdfRenderedPage actual = RenderContent(Background + first + second, 1,
            defaultCmykReplacement: true);
        PdfRenderedPage finalOnly = RenderContent(Background + second, 1,
            defaultCmykReplacement: true);

        AssertSame(expected, actual);
        Assert.False(actual.Pixels.Span.SequenceEqual(finalOnly.Pixels.Span));
    }

    [Theory]
    [InlineData("stroke")]
    [InlineData("image")]
    [InlineData("form")]
    public void InterveningPaint_FlushesEarlierFill(string kind)
    {
        string middle = kind switch
        {
            "stroke" => "q 0 0 0 1 k 3 14 m 8 18 l S Q ",
            "image" => "q 4 0 0 4 3 14 cm /Im Do Q ",
            "form" => "q 1 0 0 1 3 14 cm /Fm Do Q ",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        string first = "q /Paint gs " + Red + "Q ";
        string second = "q /Paint gs " + Final + "Q ";
        PdfRenderedPage expected = RenderContent(Background + first + Barrier + middle + second, 1);
        PdfRenderedPage actual = RenderContent(Background + first + middle + second, 1);

        AssertSame(expected, actual);
    }

    [Fact]
    public void PendingFillAtEndOfStream_IsPainted()
    {
        string first = Background + "q /Paint gs " + Red + "Q ";
        PdfRenderedPage expected = RenderContent(first + Barrier, 1);
        PdfRenderedPage actual = RenderContent(first, 1);

        AssertSame(expected, actual);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void SingleFractionalForm_IdenticalProcessFillsMatchFinalOnly(int mode, bool withSpot)
    {
        string prefix = Background + (withSpot
            ? "q /SpotOn gs /Spot cs .5 scn 0 0 32 32 re f Q " : "");
        string final = "q /Paint gs " + Final + "Q ";
        AssertSame(RenderContent(prefix + final, mode, singleFractionalForm: true),
            RenderContent(prefix + "q /Paint gs " + Red + "Q " + final, mode,
                singleFractionalForm: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleFractionalForm_NativeReplacementMatchesCombinedProcessPaint(bool withSpot)
    {
        string prefix = Background + (withSpot
            ? "q /SpotOn gs /Spot cs .5 scn 0 0 32 32 re f Q " : "");
        string first = "q /Paint gs .5 .8 .5 .1 k " + Shape + "Q ";
        string second = "q /Paint gs /Magenta cs .3 scn " + Shape + "Q ";
        string combined = "q /Paint gs .5 .3 .5 .1 k " + Shape + "Q ";
        AssertSame(RenderContent(prefix + combined, 1, singleFractionalForm: true),
            RenderContent(prefix + first + second, 1, singleFractionalForm: true));
    }

    [Fact]
    public void SingleFractionalForm_ZeroReplacementLeavesEveryChannelWhite()
    {
        string first = "q /Paint gs .8 .8 .8 .8 k " + Shape + "Q ";
        string clear = "q /Paint gs /Process cs 0 0 0 0 scn " + Shape + "Q ";
        PdfRenderedPage actual = RenderContent(first + clear, 1, singleFractionalForm: true);
        Assert.Empty(actual.Diagnostics);
        foreach (byte channel in actual.Pixels.Span) Assert.Equal(255, channel);
    }

    [Theory]
    [InlineData("4.25 4.25 22 22 re W n ")]
    [InlineData("4.25 4.25 m 27.3 4.25 l 16.35 27.2 l h W n ")]
    public void LocalFractionalClip_RestorePreservesSequentialPainting(string clip)
    {
        string first = "q /Paint gs " + clip + Red + "Q ";
        string second = "q /Paint gs " + Final + "Q ";
        AssertSame(RenderContent(Background + first + Barrier + second, 1, singleFractionalForm: true),
            RenderContent(Background + first + second, 1, singleFractionalForm: true));
    }

    [Theory]
    [InlineData(-1, false, false)]
    [InlineData(0, false, false)]
    [InlineData(1, false, false)]
    [InlineData(-1, true, false)]
    [InlineData(0, true, false)]
    [InlineData(1, true, false)]
    [InlineData(-1, false, true)]
    [InlineData(0, false, true)]
    [InlineData(1, false, true)]
    [InlineData(-1, true, true)]
    [InlineData(0, true, true)]
    [InlineData(1, true, true)]
    public void TemporaryFractionalClip_IdenticalProcessFillsMatchFinalOnly(
        int mode, bool withSpot, bool larger)
    {
        string prefix = "q .1 .1 31.8 31.8 re W n " + Background + (withSpot
            ? "q /SpotOn gs /Spot cs .5 scn 0 0 32 32 re f Q " : "");
        string final = "q /Paint gs " + Final + "Q Q ";
        AssertSame(RenderContent(prefix + final, mode, singleFractionalForm: larger),
            RenderContent(prefix + "q /Paint gs " + Red + "Q " + final, mode,
                singleFractionalForm: larger));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporaryFractionalClip_NativeZeroReplacementIsWhiteAfterRestore(bool larger)
    {
        string content = "q .1 .1 31.8 31.8 re W n q /Paint gs .8 .8 .8 .8 k "
            + Shape + "Q q /Paint gs /Process cs 0 0 0 0 scn " + Shape + "Q Q ";
        PdfRenderedPage actual = RenderContent(content, 1, singleFractionalForm: larger);
        Assert.Empty(actual.Diagnostics);
        foreach (byte channel in actual.Pixels.Span) Assert.Equal(255, channel);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporaryFractionalClip_PendingPaintFinishesBeforeReturnedBufferReuse(bool larger)
    {
        string first = "q 8.1 8.1 15.8 15.8 re W n /Paint gs " + Red;
        string second = "Q q 8.8 8.8 14.4 14.4 re W n /Paint gs .3 .2 .1 0 k "
            + ShiftedShape + "Q ";
        AssertSame(RenderContent(Background + first + Barrier + second, 1,
                singleFractionalForm: larger),
            RenderContent(Background + first + second, 1, singleFractionalForm: larger));
    }

    [Fact]
    public void TemporaryLargeClip_IdenticalProcessFillsMatchFinalOnly()
    {
        string prefix = "q .25 .25 m 31.5 .25 l 16 31.5 l h W n " + Background;
        string first = "q /Paint gs " + Red + "Q ";
        string final = "q /Paint gs " + Final + "Q Q ";
        AssertSame(RenderContent(prefix + final, 1, singleFractionalForm: true),
            RenderContent(prefix + first + final, 1, singleFractionalForm: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TemporaryFractionalClip_PendingPaintFinishesBeforeClipReplacement(bool larger)
    {
        string first = "q 8.1 8.1 15.8 15.8 re W n /Paint gs " + Red;
        string second = "8.8 8.8 14.4 14.4 re W n .3 .2 .1 0 k " + ShiftedShape + "Q ";
        AssertSame(RenderContent(Background + first + Barrier + second, 1,
                singleFractionalForm: larger),
            RenderContent(Background + first + second, 1, singleFractionalForm: larger));
    }

    [Theory]
    [InlineData(false, 0, false, false)]
    [InlineData(false, 1, false, false)]
    [InlineData(true, 0, false, false)]
    [InlineData(true, 1, false, false)]
    [InlineData(false, 0, true, false)]
    [InlineData(false, 1, true, false)]
    [InlineData(true, 0, true, false)]
    [InlineData(true, 1, true, false)]
    [InlineData(false, 0, false, true)]
    [InlineData(false, 1, false, true)]
    [InlineData(true, 0, false, true)]
    [InlineData(true, 1, false, true)]
    [InlineData(false, 0, true, true)]
    [InlineData(false, 1, true, true)]
    [InlineData(true, 0, true, true)]
    [InlineData(true, 1, true, true)]
    public void PureProcessPolicyChange_MatchesExplicitCombinedPaint(
        bool firstOverprint, int mode, bool native, bool clipped)
    {
        string prefix = (clipped ? "q .1 .1 31.8 31.8 re W n " : "") + Background;
        string suffix = clipped ? "Q " : "";
        string first = "q " + (firstOverprint ? "/Paint" : "/Off")
            + " gs .5 .8 .5 .1 k " + Shape + "Q ";
        string final = "q " + (firstOverprint ? "/Off" : "/Paint") + " gs "
            + (native ? "/Magenta cs .3 scn " : "0 .3 0 0 k ") + Shape + "Q ";
        string values = !firstOverprint && (native || mode == 1)
            ? ".5 .3 .5 .1" : "0 .3 0 0";
        string combined = "q /Off gs " + values + " k " + Shape + "Q ";
        AssertSame(RenderContent(prefix + combined + suffix, mode, singleFractionalForm: clipped),
            RenderContent(prefix + first + final + suffix, mode, singleFractionalForm: clipped));
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, 1, false)]
    [InlineData(true, 0, false)]
    [InlineData(true, 1, false)]
    [InlineData(false, 0, true)]
    [InlineData(false, 1, true)]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, true)]
    public void ProcessPolicyChange_WithSpotRetainsSequentialPainting(
        bool firstOverprint, int mode, bool native)
    {
        string prefix = "q .1 .1 31.8 31.8 re W n " + Background
            + "q /SpotOn gs /Spot cs .5 scn 0 0 32 32 re f Q ";
        string first = "q " + (firstOverprint ? "/Paint" : "/Off")
            + " gs .5 .8 .5 .1 k " + Shape + "Q ";
        string final = "q " + (firstOverprint ? "/Off" : "/Paint") + " gs "
            + (native ? "/Magenta cs .3 scn " : "0 .3 0 0 k ") + Shape + "Q Q ";
        AssertSame(RenderContent(prefix + first + Barrier + final, mode, singleFractionalForm: true),
            RenderContent(prefix + first + final, mode, singleFractionalForm: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PureProcessPolicyChange_NativeZeroReplacementLeavesEveryChannelWhite(bool clipped)
    {
        string prefix = clipped ? "q .1 .1 31.8 31.8 re W n " : "";
        string suffix = clipped ? "Q " : "";
        string content = prefix + "q /Off gs .8 .8 .8 .8 k " + Shape
            + "Q q /Paint gs /Process cs 0 0 0 0 scn " + Shape + "Q " + suffix;
        PdfRenderedPage actual = RenderContent(content, 1, singleFractionalForm: clipped);
        Assert.Empty(actual.Diagnostics);
        foreach (byte channel in actual.Pixels.Span) Assert.Equal(255, channel);
    }

    [Theory]
    [InlineData(63, true)]
    [InlineData(64, true)]
    [InlineData(256, true)]
    [InlineData(64, false)]
    [InlineData(256, false)]
    public void LocalClipPointBoundary_PreservesComplexFallbackAndStableClipJoining(
        int vertices, bool localClip)
    {
        string polygon = string.Join(" ", Enumerable.Range(0, vertices).Select(index =>
        {
            double angle = index * 2 * Math.PI / vertices;
            string x = (16 + 11 * Math.Cos(angle)).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            string y = (16 + 11 * Math.Sin(angle)).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            return x + " " + y + (index == 0 ? " m" : " l");
        })) + " h f ";
        string prefix = (localClip ? "q .1 .1 31.8 31.8 re W n " : "") + Background;
        string suffix = localClip ? "Q " : "";
        string first = "q /Paint gs .8 .6 .4 .2 k " + polygon + "Q ";
        string final = "q /Paint gs .2 .3 .4 .5 k " + polygon + "Q ";
        PdfRenderedPage actual = RenderContent(prefix + first + final + suffix, 1,
            singleFractionalForm: true);
        PdfRenderedPage single = RenderContent(prefix + final + suffix, 1,
            singleFractionalForm: true);
        if (localClip && vertices >= 64)
        {
            AssertSame(RenderContent(prefix + first + Barrier + final + suffix, 1,
                singleFractionalForm: true), actual);
            Assert.False(actual.Pixels.Span.SequenceEqual(single.Pixels.Span));
        }
        else AssertSame(single, actual);
    }

    private static PdfRenderedPage Render(int overprintMode, bool zeroTintSpot, bool firstPaint)
    {
        string content = Background
            + (zeroTintSpot ? "q /SpotOn gs /Spot cs 0 scn 0 0 32 32 re f Q " : "")
            + "q /Paint gs " + (firstPaint ? Red : "") + Final + "Q ";
        return RenderContent(content, overprintMode);
    }

    private static PdfRenderedPage RenderContent(string content, int overprintMode,
        bool defaultCmykReplacement = false, bool nestedFractionalForms = false, bool singleFractionalForm = false)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(32, 32, Encoding.ASCII.GetBytes(
                (nestedFractionalForms || singleFractionalForm) ? "/Outer Do" : content)).Build());
        PdfPageTreeEntry page = PdfPageTree.Read(source).Pages[0];
        PdfDictionary original = Assert.IsType<PdfDictionary>(source.Resolve(page.Reference));
        var update = new PdfIncrementalUpdateBuilder(source);
        PdfDictionary tint = new([
            Entry("FunctionType", new PdfInteger(2)), Entry("Domain", Numbers(0, 1)),
            Entry("C0", Numbers(0, 0, 0, 0)), Entry("C1", Numbers(1, 0, 1, 0)),
            Entry("N", new PdfInteger(1))
        ]);
        static PdfDictionary Overprint(bool enabled, int mode) => new([
            Entry("OP", new PdfBoolean(enabled)), Entry("op", new PdfBoolean(enabled)),
            Entry("OPM", new PdfInteger(mode))
        ]);
        PdfIndirectReference image = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(1)),
            Entry("Height", new PdfInteger(1)), Entry("BitsPerComponent", new PdfInteger(8)),
            Entry("ColorSpace", Name("DeviceCMYK"))
        ]), [0, 0, 0, 255]));
        PdfIndirectReference form = update.AddObject(new PdfStream(new PdfDictionary([
            Entry("Subtype", Name("Form")), Entry("BBox", Numbers(0, 0, 4, 4)),
            Entry("Resources", new PdfDictionary([]))
        ]), Encoding.ASCII.GetBytes("0 0 0 1 k 0 0 4 4 re f")));
        var colorSpaces = new List<KeyValuePair<PdfName, PdfObject>>
        {
            Entry("Spot", new PdfArray([
                Name("Separation"), Name("ProbeSpot"), Name("DeviceCMYK"), tint
            ])),
            Entry("Magenta", new PdfArray([
                Name("Separation"), Name("Magenta"), Name("DeviceCMYK"), tint
            ]))
        };
        foreach (string colorant in new[] { "Cyan", "Yellow", "Black" })
            colorSpaces.Add(Entry(colorant, new PdfArray([
                Name("Separation"), Name(colorant), Name("DeviceCMYK"), tint
            ])));
        PdfStream processTint = new(new PdfDictionary([
            Entry("FunctionType", new PdfInteger(4)),
            Entry("Domain", Numbers(0, 1, 0, 1, 0, 1, 0, 1)),
            Entry("Range", Numbers(0, 1, 0, 1, 0, 1, 0, 1))
        ]), Encoding.ASCII.GetBytes("{ }"));
        colorSpaces.Add(Entry("Process", new PdfArray([
            Name("DeviceN"), new PdfArray([Name("Cyan"), Name("Magenta"), Name("Yellow"), Name("Black")]),
            Name("DeviceCMYK"), update.AddObject(processTint)
        ])));
        if (defaultCmykReplacement)
            colorSpaces.Add(Entry("DefaultCMYK", Name("DeviceCMYK")));
        PdfDictionary resources = new([
            Entry("ColorSpace", new PdfDictionary(colorSpaces)),
            Entry("ExtGState", new PdfDictionary([
                Entry("Paint", Overprint(overprintMode >= 0, Math.Max(overprintMode, 0))),
                Entry("Off", Overprint(false, 0)),
                Entry("SpotOn", Overprint(true, 1)),
                Entry("Half", new PdfDictionary([Entry("ca", new PdfReal(0.5))]))
            ])),
            Entry("XObject", new PdfDictionary([Entry("Im", image), Entry("Fm", form)]))
        ]);
        if (nestedFractionalForms || singleFractionalForm)
        {
            PdfArray bounds = new([
                new PdfReal(0.1), new PdfReal(0.1), new PdfReal(31.9), new PdfReal(31.9)
            ]);
            PdfIndirectReference inner = update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Form")), Entry("BBox", bounds),
                Entry("Resources", resources)
            ]), Encoding.ASCII.GetBytes(content)));
            PdfIndirectReference outer = update.AddObject(new PdfStream(new PdfDictionary([
                Entry("Subtype", Name("Form")), Entry("BBox", bounds),
                Entry("Resources", new PdfDictionary([
                    Entry("XObject", new PdfDictionary([Entry("Inner", inner)]))
                ]))
            ]), Encoding.ASCII.GetBytes("/Inner Do")));
            resources = new PdfDictionary([
                Entry("XObject", new PdfDictionary([Entry("Outer", singleFractionalForm ? inner : outer)]))
            ]);
        }
        PdfDictionary group = new([
            Entry("S", Name("Transparency")), Entry("CS", Name("DeviceCMYK"))
        ]);
        update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(original
            .Where(entry => !entry.Key.Equals(Name("Resources")) && !entry.Key.Equals(Name("Group")))
            .Append(Entry("Resources", resources)).Append(Entry("Group", group))));
        return new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0,
            new PdfRenderOptions((nestedFractionalForms || singleFractionalForm) ? 512 : 32,
                (nestedFractionalForms || singleFractionalForm) ? 512 : 32,
                includeAnnotations: false, includeFormFields: false));
    }

    private static void AssertSame(PdfRenderedPage expected, PdfRenderedPage actual)
    {
        Assert.Empty(expected.Diagnostics);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    private static PdfArray Numbers(params int[] values) =>
        new(values.Select(value => (PdfObject)new PdfInteger(value)));

    private static KeyValuePair<PdfName, PdfObject> Entry(string key, PdfObject value) =>
        new(Name(key), value);

    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
}
