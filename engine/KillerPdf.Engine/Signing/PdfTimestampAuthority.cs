using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace KillerPdf.Engine.Signing;

/// <summary>
/// Adds RFC 3161 signature timestamps to detached CMS signatures. The timestamp covers the
/// signer's signature value and is stored as the signer's unsigned signature-time-stamp
/// attribute, which is what PAdES baseline-T and Adobe readers look for.
/// </summary>
public static class PdfTimestampAuthority
{
    /// <summary>The id-aa-signatureTimeStampToken attribute identifier.</summary>
    public const string SignatureTimestampTokenOid = "1.2.840.113549.1.9.16.2.14";

    private const string QueryMediaType = "application/timestamp-query";
    private const string ReplyMediaType = "application/timestamp-reply";
    private static readonly HttpClient SharedClient = new();

    /// <summary>
    /// Requests a timestamp over the first signer's signature value through
    /// <paramref name="sendRequest"/>, verifies the returned token against that signature, and
    /// returns the CMS with the token added. A CMS whose signer already carries a signature
    /// timestamp is returned unchanged.
    /// </summary>
    /// <param name="detachedCms">The DER or BER encoded detached CMS signature.</param>
    /// <param name="sendRequest">Sends an encoded TimeStampReq and returns the encoded TimeStampResp.</param>
    /// <param name="hashAlgorithm">The message imprint algorithm; SHA-256 when omitted.</param>
    public static byte[] AddTimestamp(
        ReadOnlyMemory<byte> detachedCms,
        Func<ReadOnlyMemory<byte>, byte[]> sendRequest,
        HashAlgorithmName? hashAlgorithm = null)
    {
        ArgumentNullException.ThrowIfNull(sendRequest);
        if (detachedCms.IsEmpty)
            throw new ArgumentException("The CMS signature is empty.", nameof(detachedCms));

        var cms = new SignedCms();
        cms.Decode(detachedCms.Span);
        if (cms.SignerInfos.Count == 0)
            throw new CryptographicException("The CMS signature has no signer to timestamp.");
        SignerInfo signer = cms.SignerInfos[0];
        foreach (CryptographicAttributeObject attribute in signer.UnsignedAttributes)
        {
            if (attribute.Oid.Value == SignatureTimestampTokenOid)
                return detachedCms.ToArray();
        }

        // A random, positive nonce lets ProcessResponse reject a replayed or mismatched reply.
        byte[] nonce = RandomNumberGenerator.GetBytes(8);
        nonce[0] = (byte)((nonce[0] & 0x7F) | 0x01);
        Rfc3161TimestampRequest request = Rfc3161TimestampRequest.CreateFromSignerInfo(
            signer, hashAlgorithm ?? HashAlgorithmName.SHA256,
            requestSignerCertificates: true, nonce: nonce);

        Rfc3161TimestampToken token;
        try
        {
            byte[] response = sendRequest(request.Encode())
                ?? throw new PdfTimestampException("The timestamp server returned no response.");
            if (response.Length == 0)
                throw new PdfTimestampException("The timestamp server returned an empty response.");
            token = request.ProcessResponse(response, out _);
        }
        catch (Exception ex) when (ex is not PdfTimestampException
            and (CryptographicException or HttpRequestException or OperationCanceledException or IOException))
        {
            throw new PdfTimestampException("The timestamp could not be obtained: " + ex.Message, ex);
        }
        if (!token.VerifySignatureForSignerInfo(signer, out X509Certificate2? authority))
            throw new PdfTimestampException(
                "The timestamp token does not verify against the signature it should cover.");
        authority?.Dispose();

        signer.AddUnsignedAttribute(new AsnEncodedData(
            SignatureTimestampTokenOid, token.AsSignedCms().Encode()));
        return cms.Encode();
    }

    /// <summary>
    /// Returns a transport that posts each request to <paramref name="server"/> as
    /// application/timestamp-query and returns the reply body.
    /// </summary>
    /// <param name="server">An absolute http or https timestamp-server address.</param>
    /// <param name="client">An optional client; a shared client is used when omitted.</param>
    /// <param name="timeout">The time allowed per request; 30 seconds when omitted.</param>
    /// <param name="maximumResponseBytes">The largest reply accepted; <see cref="DefaultMaximumResponseBytes"/> when omitted.</param>
    public static Func<ReadOnlyMemory<byte>, byte[]> HttpTransport(
        Uri server, HttpClient? client = null, TimeSpan? timeout = null, int? maximumResponseBytes = null)
    {
        ValidateServer(server);
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(30);
        if (limit <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "The timeout must be positive.");
        int cap = maximumResponseBytes ?? DefaultMaximumResponseBytes;
        if (cap <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes), "The response limit must be positive.");
        return request =>
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, server)
            {
                Content = new ByteArrayContent(request.ToArray())
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue(QueryMediaType);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ReplyMediaType));
            using var cancellation = new CancellationTokenSource(limit);
            // Headers first, so the status, type and declared size are judged before the body is read.
            using HttpResponseMessage response = (client ?? SharedClient).Send(
                message, HttpCompletionOption.ResponseHeadersRead, cancellation.Token);
            if (!response.IsSuccessStatusCode)
                throw new PdfTimestampException(
                    $"The timestamp server answered HTTP {(int)response.StatusCode}.");
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Equals(ReplyMediaType, StringComparison.OrdinalIgnoreCase))
                throw new PdfTimestampException(
                    $"The timestamp server answered with {mediaType} instead of a timestamp reply.");
            if (response.Content.Headers.ContentLength is long declared && declared > cap)
                throw new PdfTimestampException(TooLarge(cap));
            using Stream body = response.Content.ReadAsStream(cancellation.Token);
            using var copy = new MemoryStream();
            byte[] buffer = new byte[16 * 1024];
            int read;
            while ((read = body.Read(buffer)) > 0)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (copy.Length + read > cap) throw new PdfTimestampException(TooLarge(cap));
                copy.Write(buffer, 0, read);
            }
            return copy.ToArray();
        };
    }

    /// <summary>The largest timestamp reply accepted by default. Real replies are a few kilobytes.</summary>
    public const int DefaultMaximumResponseBytes = 1024 * 1024;

    private static string TooLarge(int cap) =>
        $"The timestamp server's reply is larger than the {cap:N0}-byte limit.";

    /// <summary>Throws when <paramref name="server"/> is not an absolute http or https address.</summary>
    public static void ValidateServer(Uri server)
    {
        ArgumentNullException.ThrowIfNull(server);
        if (!server.IsAbsoluteUri || (server.Scheme != Uri.UriSchemeHttp && server.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException(
                "The timestamp server must be an absolute http or https address.", nameof(server));
    }
}

/// <summary>Raised when a signature timestamp could not be obtained or did not verify.</summary>
public sealed class PdfTimestampException : CryptographicException
{
    /// <summary>Creates the exception with a default message.</summary>
    public PdfTimestampException() : base("The signature timestamp could not be added.") { }

    /// <summary>Creates the exception with a message.</summary>
    public PdfTimestampException(string message) : base(message) { }

    /// <summary>Creates the exception with a message and the failure that caused it.</summary>
    public PdfTimestampException(string message, Exception inner) : base(message, inner) { }
}
