using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using KillerPDF.Services;

// A real socket boundary: headers finish immediately, but the announced body never finishes.
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
using var release = new CancellationTokenSource();
var server = Task.Run(async () =>
{
    using var peer = await listener.AcceptTcpClientAsync();
    using var stream = peer.GetStream();
    using var request = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
    while (!string.IsNullOrEmpty(await request.ReadLineAsync())) { }
    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\nConnection: close\r\n\r\nx"));
    try { await Task.Delay(Timeout.Infinite, release.Token); } catch (OperationCanceledException) { }
});
using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(100) };
using var output = new MemoryStream();
var watch = Stopwatch.StartNew();
var transfer = HttpDownload.CopyToAsync(client, new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/stalled"), output, TimeSpan.FromMilliseconds(250));
try
{
    if (await Task.WhenAny(transfer, Task.Delay(2000)) != transfer)
        throw new Exception("FAIL: body remains pending after the 250ms transfer deadline, although headers completed.");
    try { await transfer; throw new Exception("FAIL: incomplete response was accepted."); }
    catch (OperationCanceledException) { }
    if (watch.ElapsedMilliseconds < 150 || watch.ElapsedMilliseconds > 1800)
        throw new Exception("FAIL: deadline timing outside expected bounds.");
    Console.WriteLine($"PASS: stalled body canceled after {watch.ElapsedMilliseconds}ms; received {output.Length} byte.");
}
finally { release.Cancel(); await server; }

// Other response contracts use the real HttpClient boundary with deterministic external responses.
using var successful = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new ByteArrayContent([1, 2, 3]) }));
using var copied = new MemoryStream();
await HttpDownload.CopyToAsync(successful, new Uri("https://test.invalid/success"), copied, TimeSpan.FromSeconds(1), 3);
if (!copied.ToArray().SequenceEqual(new byte[] { 1, 2, 3 })) throw new Exception("Response body changed.");
Console.WriteLine("PASS: successful response preserves exact bytes at the size boundary.");
using var tooLarge = new MemoryStream();
try
{
    await HttpDownload.CopyToAsync(successful, new Uri("https://test.invalid/limit"), tooLarge, TimeSpan.FromSeconds(1), 2);
    throw new Exception("Oversized response was accepted.");
}
catch (HttpDownloadSizeException) { }
if (tooLarge.Length != 0) throw new Exception("Oversized declared body was written.");
Console.WriteLine("PASS: oversized declared body is rejected before writing.");
using var streaming = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new UnknownLengthContent() }));
using var streamingOutput = new MemoryStream();
try
{
    await HttpDownload.CopyToAsync(streaming, new Uri("https://test.invalid/streaming-limit"), streamingOutput, TimeSpan.FromSeconds(1), 2);
    throw new Exception("Oversized unknown-length response was accepted.");
}
catch (HttpDownloadSizeException) { }
if (streamingOutput.Length > 2) throw new Exception("Unknown-length response exceeded the output limit.");
Console.WriteLine("PASS: body reads enforce the limit when Content-Length is absent.");
using var canceled = new CancellationTokenSource();
canceled.Cancel();
try
{
    await HttpDownload.CopyToAsync(successful, new Uri("https://test.invalid/cancel"), copied, TimeSpan.FromSeconds(1), cancellationToken: canceled.Token);
    throw new Exception("Caller cancellation was ignored.");
}
catch (OperationCanceledException) { }
Console.WriteLine("PASS: caller cancellation remains observable.");
using var failed = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound)));
try
{
    await HttpDownload.CopyToAsync(failed, new Uri("https://test.invalid/missing"), copied, TimeSpan.FromSeconds(1));
    throw new Exception("Failed HTTP status was accepted.");
}
catch (HttpRequestException) { }
Console.WriteLine("PASS: failed HTTP status remains a request error.");

foreach (var invalid in new[] { (Deadline: TimeSpan.FromSeconds(1), Limit: -1L),
    (Deadline: TimeSpan.Zero, Limit: 1L), (Deadline: TimeSpan.FromMilliseconds(-1), Limit: 1L) })
{
    try
    {
        await HttpDownload.CopyToAsync(successful, new Uri("https://test.invalid/invalid"), copied, invalid.Deadline, invalid.Limit);
        throw new Exception("Invalid download policy was accepted.");
    }
    catch (ArgumentOutOfRangeException) { }
}
Console.WriteLine("PASS: negative byte limits and nonpositive deadlines are rejected.");
using var empty = new HttpClient(new ResponseHandler(() => new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new ByteArrayContent([]) }));
using var emptyOutput = new MemoryStream();
await HttpDownload.CopyToAsync(empty, new Uri("https://test.invalid/empty"), emptyOutput, TimeSpan.FromSeconds(1), 0);
if (emptyOutput.Length != 0) throw new Exception("Empty response changed.");
Console.WriteLine("PASS: zero byte limit accepts an empty response.");

sealed class ResponseHandler(Func<HttpResponseMessage> response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(response());
}

sealed class UnknownLengthContent : HttpContent
{
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        stream.WriteAsync(new byte[] { 1, 2, 3 }).AsTask();
}
