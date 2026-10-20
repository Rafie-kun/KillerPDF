using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class UpdateTrustPolicyTests
{
    private static readonly (bool, string, string) Ours = (true, "Open Source Developer", "AA11");
    private static readonly (bool, string, string) Unsigned = (false, "(not signed)", "");

    [Fact]
    public void RejectsAnUnsignedUpdate() =>
        Assert.False(UpdateTrustPolicy.Accepts(Unsigned, Ours));

    [Fact]
    public void AcceptsTheSameCertificate() =>
        Assert.True(UpdateTrustPolicy.Accepts((true, "Open Source Developer", "aa11"), Ours));

    [Fact]
    public void AcceptsARenewedCertificateForTheSameSubject() =>
        Assert.True(UpdateTrustPolicy.Accepts((true, "Open Source Developer", "BB22"), Ours));

    [Fact]
    public void RejectsAnotherPublishersValidSignature() =>
        Assert.False(UpdateTrustPolicy.Accepts((true, "Someone Else", "CC33"), Ours));

    [Fact]
    public void AnUnsignedDevelopmentBuildAcceptsAnyValidSignature() =>
        Assert.True(UpdateTrustPolicy.Accepts((true, "Someone Else", "CC33"), Unsigned));

    [Fact]
    public void ASignedBuildWithAnEmptySubjectDoesNotAcceptEveryEmptySubject() =>
        Assert.False(UpdateTrustPolicy.Accepts((true, "", "CC33"), (true, "", "AA11")));
}
