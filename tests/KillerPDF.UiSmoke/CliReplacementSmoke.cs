using System.IO;
using System.Text;

internal static class CliReplacementSmoke
{
    internal static int Run(string appPath, string folder)
    {
        const string normal = "BT /F1 14 Tf 20 250 Td (Name Name) Tj ET";
        const string refused = "BT /F1 14 Tf 3 Tr 20 150 Td (Name) Tj ET";
        const string turned = "BT /F1 14 Tf 0 1 -1 0 150 20 Tm (Name) Tj ET";
        foreach (var item in new[]
        {
            (Name: "mixed", Content: normal + "\n" + refused, Exit: 3, Count: 2),
            (Name: "turned", Content: turned, Exit: 3, Count: 0),
            (Name: "refused", Content: refused, Exit: 1, Count: 0),
            (Name: "normal", Content: normal, Exit: 0, Count: 2),
            (Name: "absent", Content: normal.Replace("Name", "Other"), Exit: 0, Count: 0)
        })
        {
            string source = Path.Combine(folder, item.Name + ".pdf");
            byte[] original = Fixture(item.Content);
            File.WriteAllBytes(source, original);
            var result = CliOutputSmoke.CommandCapture(Path.ChangeExtension(appPath, ".exe"), "--replace-text",
                source, Path.Combine(folder, item.Name + "-output.pdf"), "Name", "Done");
            if (result.ExitCode != item.Exit || (item.Name != "absent" && item.Name != "turned"
                && !result.Output.Contains($"Replaced {item.Count} matches", StringComparison.Ordinal))
                || !original.AsSpan().SequenceEqual(File.ReadAllBytes(source)))
                throw new InvalidOperationException($"Incorrect {item.Name} replacement outcome: expected exit {item.Exit} and {item.Count} published matches.");
        }
        Console.WriteLine("PASS CLI replacement distinguishes partial, refused, complete and no-match outcomes and counts only published matches.");
        return 0;
    }

    internal static byte[] Fixture(string content)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 400] /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> /Contents 4 0 R >>",
            $"<< /Length {content.Length} >>\nstream\n{content}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
