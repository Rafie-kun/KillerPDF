using System.IO;
using System.Net.Http;

namespace KillerPDF.Services;

/// <summary>Streams an HTTP response with a size limit and a whole-transfer deadline.</summary>
public static class HttpDownload
{
    public static async Task CopyToAsync(HttpClient http, Uri target, Stream output,
        TimeSpan deadline, long maximumBytes = long.MaxValue, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        if (deadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(deadline));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        var token = timeout.Token;
        using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length > maximumBytes)
            throw new HttpDownloadSizeException();
        using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (read > maximumBytes - total) throw new HttpDownloadSizeException();
            total += read;
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
    }
}

/// <summary>The response exceeds the caller's permitted download size.</summary>
public sealed class HttpDownloadSizeException : IOException
{
    public HttpDownloadSizeException() : base("The download exceeds the permitted size.") { }
}
