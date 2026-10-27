using System.Reflection;
using System.Security;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>Updates follow the publisher, not one certificate: a release signed with the renewed
/// certificate (new thumbprint, same subject) is accepted; a wrong subject or invalid signature is not.</summary>
public sealed class ReleaseIdentityTests
{
    private const string Publisher = "Open Source Developer Stephen Riley";
    private static readonly Version Running = new(1, 9, 0, 0);
    private static readonly AssemblyName Image = new("KillerPDF") { Version = new Version(1, 9, 1, 0) };

    private static void Verify((bool, string) installed, (bool, string) incoming, bool strict = false)
        => ReleaseIdentity.Verify(Image, "v1.9.1", Running, installed, incoming, strict);

    [Fact]
    public void RenewedCertificateOfTheInstalledPublisherIsAccepted()
    {
        // Subjects only: the renewed certificate's thumbprint never enters the decision.
        Verify((true, Publisher), (true, Publisher));
        Verify((true, Publisher), (true, Publisher.ToUpperInvariant()));
        Verify((false, "(not signed)"), (true, Publisher));
    }

    [Fact]
    public void WrongSubjectIsRefused()
    {
        Assert.Throws<SecurityException>(() => Verify((true, Publisher), (true, "Open Source Developer Someone Else")));
        Assert.Throws<SecurityException>(() => Verify((true, Publisher), (true, "Stephen Riley Imitations Ltd")));
        Assert.Throws<SecurityException>(() => Verify((false, "(not signed)"), (true, "Someone Else")));
    }

    [Fact]
    public void InvalidSignatureIsRefused()
    {
        Assert.Throws<SecurityException>(() => Verify((true, Publisher), (false, Publisher)));
        Assert.Throws<SecurityException>(() => Verify((false, "(not signed)"), (true, Publisher), strict: true));
    }

    [Fact]
    public void SamePublisherIgnoresCaseAndNeverMatchesPlaceholders()
    {
        Assert.True(CodeSignature.SamePublisher(Publisher, " " + Publisher.ToLowerInvariant()));
        Assert.False(CodeSignature.SamePublisher("(not signed)", "(not signed)"));
        Assert.False(CodeSignature.SamePublisher("", ""));
        Assert.False(CodeSignature.SamePublisher(Publisher, "Open Source Developer Someone Else"));
    }
}
