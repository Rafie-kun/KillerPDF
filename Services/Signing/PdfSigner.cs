using System;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;

namespace KillerPDF.Services.Signing
{
    /// <summary>Creates detached-CMS approval signatures through The KillerPDF.Engine.</summary>
    internal sealed class PdfSigner
    {
        public sealed record SignInfo(string Reason, string Location, string Contact,
            VisibleSignatureInfo? VisibleAppearance = null, string? TimestampServer = null);

        public sealed record VisibleSignatureInfo(
            int PageIndex, double Left, double Bottom, double Width, double Height,
            double FontSize, string Text);

        // Keep signing behind the injected service instance used by the application and tests.
#pragma warning disable CA1822
        public void Sign(string inputPath, string outputPath, X509Certificate2 cert, SignInfo info)
#pragma warning restore CA1822
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
            ArgumentNullException.ThrowIfNull(cert);
            ArgumentNullException.ThrowIfNull(info);

            if (!cert.HasPrivateKey)
                throw new InvalidOperationException(
                    "The selected certificate has no private key, so it cannot sign.");

            PdfDocument document = PdfDocument.Open(File.ReadAllBytes(inputPath));
            var cmsSigner = new KillerCmsSigner(cert);
            // A visible signature goes where the signer placed it, so an uncertified document gets a
            // new field there. Otherwise a prepared field the document carries is signed first.
            string newField = PdfDetachedSignatureWriter.FindAvailableSignatureFieldName(document, preferNewField: true);
            string fieldName = info.VisibleAppearance is null
                ? PdfDetachedSignatureWriter.FindAvailableSignatureFieldName(document) : newField;
            var options = new PdfSignatureOptions
            {
                FieldName = fieldName,
                SignerName = cmsSigner.CertificateName,
                Reason = NullIfWhiteSpace(info.Reason),
                Location = NullIfWhiteSpace(info.Location),
                ContactInformation = NullIfWhiteSpace(info.Contact),
                // A trusted RFC 3161 timestamp proves when the document was signed, which keeps the
                // signature checkable after the certificate itself expires.
                TimestampServerUrl = NullIfWhiteSpace(info.TimestampServer),
                RequestTimestamp = !string.IsNullOrWhiteSpace(info.TimestampServer),
                SignerCertificate = cert.RawDataMemory,
                PageIndex = info.VisibleAppearance?.PageIndex ?? 0,
                VisibleAppearance = info.VisibleAppearance is { } appearance
                    ? new PdfSignatureAppearance
                    {
                        Left = appearance.Left,
                        Bottom = appearance.Bottom,
                        Width = appearance.Width,
                        Height = appearance.Height,
                        FontSize = appearance.FontSize,
                        Text = appearance.Text
                    }
                    : null,
            };

            AtomicFileOutput.Write(outputPath, SignWithFallback(document, cmsSigner.CreateDetachedCms, options, newField));
        }

        // An uncertified document's prepared field can demand seed values or carry locks this
        // signature cannot meet; it is then signed in a new field, as before prepared fields were
        // chosen. A certified document has no such fallback, so its error stands. A failure after
        // the signature was made is not retried, so the signer is asked to sign once.
        internal static byte[] SignWithFallback(PdfDocument document, Func<ReadOnlyMemory<byte>, byte[]> createDetachedCms,
            PdfSignatureOptions options, string newField)
        {
            bool signed = false;
            byte[] SignOnce(ReadOnlyMemory<byte> data)
            {
                signed = true;
                return createDetachedCms(data);
            }
            try
            {
                return PdfDetachedSignatureWriter.Sign(document, SignOnce, options);
            }
            catch (InvalidOperationException) when (options.FieldName != newField && !signed)
            {
                return PdfDetachedSignatureWriter.Sign(document, createDetachedCms, options with { FieldName = newField });
            }
        }

        private static string? NullIfWhiteSpace(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
