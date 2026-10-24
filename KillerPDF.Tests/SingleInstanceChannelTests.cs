using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    // BF-A008: one connected client must not be able to monopolize forwarding.
    public class SingleInstanceChannelTests
    {
        [Fact]
        public void ReadsOneLine()
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes("C:\\Docs\\a.pdf\r\nignored"));
            Assert.Equal("C:\\Docs\\a.pdf", SingleInstanceChannel.ReadMessage(stream, 1024, TimeSpan.FromSeconds(2)));
        }

        [Fact]
        public void RejectsOversizedAndUnterminatedMessages()
        {
            using var big = new MemoryStream(new byte[5000]);
            Assert.Null(SingleInstanceChannel.ReadMessage(big, 4096, TimeSpan.FromSeconds(2)));
            using var partial = new MemoryStream(Encoding.UTF8.GetBytes("no newline"));
            Assert.Null(SingleInstanceChannel.ReadMessage(partial, 4096, TimeSpan.FromSeconds(2)));
        }

        [Fact]
        public void SilentClientIsDroppedAndTheNextOneIsServed()
        {
            var channel = new SingleInstanceChannel("KillerPDF.Test." + Guid.NewGuid().ToString("N"),
                WindowsIdentity.GetCurrent().User!.Value, 0);
            string? second = null;
            // The first pipe exists before the client connects, so a slow start of the server
            // thread on a busy machine cannot time the client out.
            var first = channel.CreateServer();
            var server = Task.Run(() =>
            {
                for (int connection = 0; connection < 2; connection++)
                {
                    using var pipe = connection == 0 ? first : channel.CreateServer();
                    pipe.WaitForConnection();
                    string? message = SingleInstanceChannel.ReadMessage(pipe, 1024, TimeSpan.FromMilliseconds(300));
                    if (connection == 1) second = message;
                }
            });

            using (var silent = new NamedPipeClientStream(".", channel.PipeName, PipeDirection.Out))
            {
                silent.Connect(3000);
                Thread.Sleep(600);   // say nothing past the deadline
            }
            Assert.True(SpinWait.SpinUntil(() => channel.TrySend("C:\\next.pdf", 500), TimeSpan.FromSeconds(5)));
            Assert.True(server.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal("C:\\next.pdf", second);
        }
    }
}
