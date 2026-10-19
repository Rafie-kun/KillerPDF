using System.IO;
using System.IO.Pipes;
using System.Globalization;
using System.Text;

namespace KillerPDF.Services;

internal sealed class SingleInstanceChannel(string applicationName, string accountId, int sessionId)
{
    private readonly string scope = accountId + "." + sessionId.ToString(CultureInfo.InvariantCulture);
    public string MutexName => @"Local\" + applicationName + ".SingleInstance." + scope;
    public string PipeName => applicationName + ".OpenPipe." + scope;

    public NamedPipeServerStream CreateServer() => new(
        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);

    public int ForwardLaunch(string? path, int timeoutMilliseconds = 3000)
    {
        return TrySend(path, timeoutMilliseconds) ? 0 : 1;
    }

    public bool TrySend(string? path, int timeoutMilliseconds = 3000)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(timeoutMilliseconds);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(path ?? "");
            return true;
        }
        catch { return false; }
    }
}
