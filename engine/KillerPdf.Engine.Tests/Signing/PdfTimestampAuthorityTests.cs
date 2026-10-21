using System.Formats.Asn1;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;
using Xunit;

namespace KillerPdf.Engine.Tests.Signing;

public sealed class PdfTimestampAuthorityTests : IDisposable
{
    private static readonly Oid Sha256 = new("2.16.840.1.101.3.4.2.1");
    private readonly RSA _signerKey = RSA.Create(2048);
    private readonly X509Certificate2 _signer;
    private readonly RSA _authorityKey = RSA.Create(2048);
    private readonly X509Certificate2 _authority;

    public PdfTimestampAuthorityTests()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _signer = new CertificateRequest("CN=KillerPDF Test Signer", _signerKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
        var authorityRequest = new CertificateRequest("CN=KillerPDF Test Timestamp Authority",
            _authorityKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        authorityRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.8")], critical: true));
        _authority = authorityRequest.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
    }

    public void Dispose()
    {
        _signer.Dispose();
        _signerKey.Dispose();
        _authority.Dispose();
        _authorityKey.Dispose();
    }

    [Fact]
    public void AddTimestamp_EmbedsATokenThatVerifiesAgainstTheSignature()
    {
        byte[] content = "signed byte range"u8.ToArray();
        byte[] stamped = PdfTimestampAuthority.AddTimestamp(SignDetached(content), Authority);

        var cms = new SignedCms(new ContentInfo(content), detached: true);
        cms.Decode(stamped);
        cms.CheckSignature(verifySignatureOnly: true);
        SignerInfo signer = Assert.Single(cms.SignerInfos.Cast<SignerInfo>());
        CryptographicAttributeObject attribute = Assert.Single(signer.UnsignedAttributes
            .Cast<CryptographicAttributeObject>(),
            a => a.Oid.Value == PdfTimestampAuthority.SignatureTimestampTokenOid);
        Assert.True(Rfc3161TimestampToken.TryDecode(attribute.Values[0].RawData,
            out Rfc3161TimestampToken? token, out _));
        Assert.True(token!.VerifySignatureForSignerInfo(signer, out X509Certificate2? tsa));
        Assert.Equal(_authority.Thumbprint, tsa!.Thumbprint);
        tsa.Dispose();
    }

    [Fact]
    public void AddTimestamp_LeavesATimestampedSignatureUnchanged()
    {
        byte[] stamped = PdfTimestampAuthority.AddTimestamp(SignDetached(new byte[] { 1, 2, 3 }), Authority);
        int calls = 0;

        byte[] again = PdfTimestampAuthority.AddTimestamp(stamped, request =>
        {
            calls++;
            return Authority(request);
        });

        Assert.Equal(stamped, again);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void AddTimestamp_RejectsAReplyForADifferentSignature()
    {
        byte[] otherRequest = Rfc3161TimestampRequest.CreateFromHash(
            SHA256.HashData("something else"u8), HashAlgorithmName.SHA256,
            requestSignerCertificates: true, nonce: new byte[] { 1, 2, 3 }).Encode();

        Assert.Throws<PdfTimestampException>(() => PdfTimestampAuthority.AddTimestamp(
            SignDetached(new byte[] { 4, 5, 6 }), _ => Authority(otherRequest)));
    }

    [Fact]
    public void AddTimestamp_RejectsARefusedRequest()
    {
        Assert.Throws<PdfTimestampException>(() => PdfTimestampAuthority.AddTimestamp(
            SignDetached(new byte[] { 7, 8, 9 }), _ => Refusal()));
    }

    [Fact]
    public void Sign_WithRequestedTimestamp_EmbedsTheTokenInTheSignatureContents()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage(300, 300).Build();

        byte[] signed = PdfDetachedSignatureWriter.Sign(PdfDocument.Open(source),
            data => SignDetached(data), new PdfSignatureOptions
            {
                RequestTimestamp = true,
                TimestampTransport = Authority,
                SignerCertificate = _signer.RawDataMemory,
            });

        byte[] contents = ReadSignatureContents(signed);
        var cms = new SignedCms();
        cms.Decode(contents);
        Assert.Contains(cms.SignerInfos[0].UnsignedAttributes.Cast<CryptographicAttributeObject>(),
            a => a.Oid.Value == PdfTimestampAuthority.SignatureTimestampTokenOid);
        // A genuine token bound to this signature is what baseline-T evidence means.
        Assert.Equal(PdfPadesProfile.BaselineT,
            Assert.Single(PdfSignatureInspection.Inspect(PdfDocument.Open(signed)).Entries).PadesProfile);
    }

    [Fact]
    public void Sign_RequestedTimestampNeedsAServerOrATransport()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();

        Assert.Throws<ArgumentException>(() => PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), _ => [1], new PdfSignatureOptions { RequestTimestamp = true }));
        Assert.Throws<ArgumentException>(() => PdfDetachedSignatureWriter.Sign(
            PdfDocument.Open(source), _ => [1], new PdfSignatureOptions
            {
                RequestTimestamp = true,
                TimestampServerUrl = "ftp://timestamp.example.test"
            }));
    }

    [Fact]
    public void HttpTransport_PostsATimestampQueryAndReturnsTheReply()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "application/timestamp-reply", [9, 8, 7]);
        using var client = new HttpClient(handler);
        Func<ReadOnlyMemory<byte>, byte[]> send = PdfTimestampAuthority.HttpTransport(
            new Uri("http://timestamp.example.test/tsa"), client);

        byte[] reply = send(new byte[] { 1, 2, 3 });

        Assert.Equal(new byte[] { 9, 8, 7 }, reply);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("application/timestamp-query", handler.ContentType);
        Assert.Equal(new byte[] { 1, 2, 3 }, handler.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "application/timestamp-reply")]
    [InlineData(HttpStatusCode.OK, "text/html")]
    public void HttpTransport_RejectsAnErrorOrANonTimestampReply(HttpStatusCode status, string mediaType)
    {
        using var client = new HttpClient(new StubHandler(status, mediaType, [1]));
        Func<ReadOnlyMemory<byte>, byte[]> send = PdfTimestampAuthority.HttpTransport(
            new Uri("https://timestamp.example.test"), client);

        Assert.Throws<PdfTimestampException>(() => send(new byte[] { 1 }));
    }

    [Theory]
    [InlineData(false)]   // the reply declares its length
    [InlineData(true)]    // the reply is streamed without one
    public void HttpTransport_RefusesRepliesLargerThanTheLimit(bool undeclaredLength)
    {
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK, "application/timestamp-reply",
            new byte[64], undeclaredLength));
        Func<ReadOnlyMemory<byte>, byte[]> send = PdfTimestampAuthority.HttpTransport(
            new Uri("https://timestamp.example.test"), client, maximumResponseBytes: 63);

        var error = Assert.Throws<PdfTimestampException>(() => send(new byte[] { 1 }));
        Assert.Contains("limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void HttpTransport_AcceptsAReplyExactlyAtTheLimit()
    {
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK, "application/timestamp-reply",
            new byte[64], undeclaredLength: true));
        Func<ReadOnlyMemory<byte>, byte[]> send = PdfTimestampAuthority.HttpTransport(
            new Uri("https://timestamp.example.test"), client, maximumResponseBytes: 64);

        Assert.Equal(64, send(new byte[] { 1 }).Length);
    }

    [Fact]
    public void HttpTransport_RejectsANonHttpAddress() =>
        Assert.Throws<ArgumentException>(() =>
            PdfTimestampAuthority.HttpTransport(new Uri("file:///C:/tsa")));

    private byte[] SignDetached(ReadOnlyMemory<byte> data)
    {
        var cms = new SignedCms(new ContentInfo(data.ToArray()), detached: true);
        cms.ComputeSignature(new CmsSigner(_signer) { DigestAlgorithm = Sha256 }, silent: true);
        return cms.Encode();
    }

    // A minimal RFC 3161 authority: granted status, a TSTInfo echoing the request's imprint and
    // nonce, signed by a certificate with the critical timeStamping usage and the ESS
    // signing-certificate-v2 attribute that RFC 5816 requires.
    private byte[] Authority(ReadOnlyMemory<byte> requestBytes)
    {
        Assert.True(Rfc3161TimestampRequest.TryDecode(requestBytes, out Rfc3161TimestampRequest? request, out _));
        var info = new Rfc3161TimestampTokenInfo(new Oid("1.3.6.1.4.1.32473.1"),
            request!.HashAlgorithmId, request.GetMessageHash(), "*"u8.ToArray(),
            DateTimeOffset.UtcNow, nonce: request.GetNonce());
        var token = new SignedCms(new ContentInfo(new Oid("1.2.840.113549.1.9.16.1.4"), info.Encode()));
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, _authority)
        {
            DigestAlgorithm = Sha256,
            IncludeOption = X509IncludeOption.EndCertOnly,
        };
        signer.SignedAttributes.Add(new AsnEncodedData("1.2.840.113549.1.9.16.2.47",
            SigningCertificateV2(_authority)));
        token.ComputeSignature(signer, silent: true);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.PushSequence();
        writer.WriteInteger(0);
        writer.PopSequence();
        writer.WriteEncodedValue(token.Encode());
        writer.PopSequence();
        return writer.Encode();
    }

    private static byte[] Refusal()
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.PushSequence();
        writer.WriteInteger(2);
        writer.PopSequence();
        writer.PopSequence();
        return writer.Encode();
    }

    private static byte[] SigningCertificateV2(X509Certificate2 certificate)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        writer.PushSequence();
        writer.PushSequence();
        writer.WriteOctetString(SHA256.HashData(certificate.RawData));
        writer.PopSequence();
        writer.PopSequence();
        writer.PopSequence();
        return writer.Encode();
    }

    private static byte[] ReadSignatureContents(byte[] pdf)
    {
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        int start = text.LastIndexOf("/Contents <", StringComparison.Ordinal) + "/Contents <".Length;
        int end = text.IndexOf('>', start);
        byte[] reserved = Convert.FromHexString(text[start..end]);
        int length = new AsnReader(reserved, AsnEncodingRules.BER).ReadEncodedValue().Length;
        return reserved[..length];
    }

    private sealed class StubHandler(HttpStatusCode status, string mediaType, byte[] reply,
        bool undeclaredLength = false) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? ContentType { get; private set; }
        public byte[]? Body { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            Body = request.Content?.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();
            HttpContent content = undeclaredLength
                ? new StreamContent(new UnseekableStream(reply)) : new ByteArrayContent(reply);
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return new HttpResponseMessage(status) { Content = content };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    // A body with no known length, the way a chunked reply arrives.
    private sealed class UnseekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
    }
}
