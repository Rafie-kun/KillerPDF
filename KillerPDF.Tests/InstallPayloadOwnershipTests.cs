using System.IO;
using System.Diagnostics;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class InstallPayloadOwnershipTests
{
    [Fact]
    public void Read_ReturnsNullWithoutAManifest()
    {
        string root = NewTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "notes.txt"), "user file");

            Assert.Null(InstallPayloadOwnership.Read(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Read_ListsManifestFilesTheShimAndTheirFolders()
    {
        string root = NewPayload();
        try
        {
            File.WriteAllText(Path.Combine(root, "KillerPDF.cmd"), "shim");

            InstallPayloadOwnership.OwnedPayload? read = InstallPayloadOwnership.Read(root);

            Assert.NotNull(read);
            InstallPayloadOwnership.OwnedPayload owned = read!;
            Assert.Equal(root, owned.Root);
            Assert.Contains(Path.Combine(root, "KillerPDF.exe"), owned.Files);
            Assert.Contains(Path.Combine(root, "runtimes", "win-x64", "native.dll"), owned.Files);
            Assert.Contains(Path.Combine(root, "KillerPDF.cmd"), owned.Files);
            Assert.Contains(Path.Combine(root, "payload.manifest"), owned.Files);
            Assert.DoesNotContain(Path.Combine(root, "notes.txt"), owned.Files);
            Assert.Equal(
                [Path.Combine(root, "runtimes", "win-x64"), Path.Combine(root, "runtimes")],
                owned.Directories);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Read_ReturnsNullWhenTheManifestReachesOutsideTheFolder()
    {
        string root = NewTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "payload.manifest"),
                new string('0', 64) + "\t0\t../outside.dll");

            Assert.Null(InstallPayloadOwnership.Read(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Remove_DeletesOnlyOwnedFilesAndKeepsTheFolderWhenUserFilesRemain()
    {
        string root = NewPayload();
        try
        {
            InstallPayloadOwnership.OwnedPayload owned = InstallPayloadOwnership.Read(root)!;

            bool removedRoot = InstallPayloadOwnership.Remove(owned);

            Assert.False(removedRoot);
            Assert.True(Directory.Exists(root));
            Assert.True(File.Exists(Path.Combine(root, "notes.txt")));
            Assert.False(File.Exists(Path.Combine(root, "KillerPDF.exe")));
            Assert.False(File.Exists(Path.Combine(root, "payload.manifest")));
            Assert.False(Directory.Exists(Path.Combine(root, "runtimes")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Remove_RemovesTheFolderWhenNothingElseIsInIt()
    {
        string root = NewPayload();
        File.Delete(Path.Combine(root, "notes.txt"));
        try
        {
            InstallPayloadOwnership.OwnedPayload owned = InstallPayloadOwnership.Read(root)!;

            Assert.True(InstallPayloadOwnership.Remove(owned));
            Assert.False(Directory.Exists(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void RemovalBatch_NeverRemovesRecursively()
    {
        string root = NewPayload();
        try
        {
            string batch = InstallPayloadOwnership.RemovalBatch(InstallPayloadOwnership.Read(root)!);

            Assert.DoesNotContain("/s", batch, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"del /q \"{Path.Combine(root, "KillerPDF.exe")}\"", batch);
            Assert.Contains($"rmdir \"{Path.Combine(root, "runtimes", "win-x64")}\"", batch);
            Assert.Contains($"rmdir \"{root}\"", batch);
            Assert.True(batch.IndexOf("rmdir \"" + Path.Combine(root, "runtimes", "win-x64"), StringComparison.Ordinal)
                < batch.IndexOf("rmdir \"" + Path.Combine(root, "runtimes") + "\"", StringComparison.Ordinal));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("%KPDF_UNDEFINED_PATH%")]
    [InlineData("%KPDF_DEFINED_PATH%")]
    [InlineData("!KPDF_DEFINED_PATH! & (caret^)")]
    [InlineData("100% complete")]
    public async Task RemovalBatch_PreservesLiteralPathsWhenCmdExpandsCommands(string folderName)
    {
        string temporary = NewTemporaryDirectory();
        try
        {
            string root = Path.Combine(temporary, folderName, "KillerPDF");
            string directory = Path.Combine(root, "native");
            string file = Path.Combine(directory, "app.dll");
            var owned = new InstallPayloadOwnership.OwnedPayload(root, [file], [directory]);
            string batch = InstallPayloadOwnership.RemovalBatch(owned);
            // Exercise cmd's actual expansion rules without executing a deletion command.
            var probe = new System.Text.StringBuilder("@echo off\r\n");
            foreach (string line in batch.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.StartsWith("del /q \"", StringComparison.Ordinal))
                    probe.Append("echo(").AppendLine(line["del /q ".Length..]);
                else if (line.StartsWith("rmdir \"", StringComparison.Ordinal))
                    probe.Append("echo(").AppendLine(line["rmdir ".Length..]);
                else
                {
                    Assert.Equal("setlocal DisableDelayedExpansion", line);
                    probe.AppendLine(line);
                }
            }
            string script = Path.Combine(temporary, "probe.cmd");
            File.WriteAllText(script, probe.ToString(), System.Text.Encoding.ASCII);
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                Arguments = "/d /v:on /c \"\"" + script + "\"\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment.Remove("KPDF_UNDEFINED_PATH");
            start.Environment["KPDF_DEFINED_PATH"] = "different-directory";
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }

            Assert.Equal(0, process.ExitCode);
            Assert.Equal(string.Empty, await error);
            Assert.Equal([$"\"{file}\"", $"\"{directory}\"", $"\"{root}\""],
                (await output).Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries));
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static string NewPayload()
    {
        string root = NewTemporaryDirectory();
        Directory.CreateDirectory(Path.Combine(root, "runtimes", "win-x64"));
        File.WriteAllText(Path.Combine(root, "KillerPDF.exe"), "exe");
        File.WriteAllText(Path.Combine(root, "runtimes", "win-x64", "native.dll"), "native");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "user file");
        File.WriteAllText(Path.Combine(root, "payload.manifest"),
            new string('0', 64) + "\t3\tKillerPDF.exe\n" + new string('0', 64) + "\t6\truntimes/win-x64/native.dll\n");
        return root;
    }

    private static string NewTemporaryDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "kpdf-ownership-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
