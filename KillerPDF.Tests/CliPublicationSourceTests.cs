using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests
{
    // A direct File.WriteAllBytes truncates an existing destination before the new bytes are
    // complete, so every CLI PDF output goes through AtomicFileOutput (BF-E004).
    public class CliPublicationSourceTests
    {
        [Fact]
        public void CliPdfOutputsAreNeverWrittenDirectly()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Strings", "en-US.xaml")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from the test output folder");

            var direct = new Regex(@"File\.WriteAllBytes\(\s*(output|outPath|outputPaths)\b");
            string[] offenders = [.. Directory.GetFiles(Path.Combine(dir!.FullName, "Features", "Cli"), "*.cs")
                .SelectMany(file => File.ReadAllLines(file)
                    .Select((line, index) => (file, line, index))
                    .Where(item => direct.IsMatch(item.line))
                    .Select(item => $"{Path.GetFileName(item.file)}:{item.index + 1}"))];

            Assert.Empty(offenders);
        }

        // The batch resave writes thousands of new files into an output tree, so it takes the bulk
        // write that skips the disk flush for files that did not exist before.
        [Fact]
        public void BatchResaveWritesAsBulkOutput()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Strings", "en-US.xaml")))
                dir = dir.Parent;
            Assert.True(dir != null, "could not locate the repo root from the test output folder");

            string source = File.ReadAllText(Path.Combine(dir!.FullName, "Features", "Cli", "BatchRunner.cs"));
            Assert.Matches(new Regex(@"ResaveDocument\(\s*document,\s*dst,\s*allowSignatureInvalidation:\s*true,\s*bulkOutput:\s*true\)"), source);
        }
    }
}
