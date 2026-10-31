using System.Buffers.Binary;
using System.IO;
using System.Reflection;

internal static class PhysicalOutputSmoke
{
    internal static int Run(string appPath, string folder)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        Type builderType = engine.GetType("KillerPdf.Engine.Authoring.PdfDocumentBuilder", true)!;
        ConstructorInfo constructor = builderType.GetConstructors().Single();
        dynamic crop = Enum.Parse(engine.GetType("KillerPdf.Engine.Authoring.PdfPageBox", true)!, "Crop");
        foreach (double unit in new[] { 0.5, 1d, 2d })
        foreach (int rotation in new[] { 0, 90 })
        {
            string name = "unit-" + unit.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + rotation;
            string source = Path.Combine(folder, name + ".pdf");
            dynamic builder = constructor.Invoke(constructor.GetParameters().Select(parameter => parameter.DefaultValue).ToArray());
            byte[] original = builder.AddBlankPage(120d, 240d).SetPageUserUnit(0, unit)
                .SetPageBox(0, crop, 10d, 20d, 100d, 200d).SetPageRotation(0, rotation).Build();
            File.WriteAllBytes(source, original);
            string images = Path.Combine(folder, name);
            string exe = Path.ChangeExtension(appPath, ".exe");
            if (CliOutputSmoke.Command(exe, "--to-image", source, images, "--dpi", "72") != 0)
                throw new InvalidOperationException("Physical image export failed.");
            byte[] png = File.ReadAllBytes(Directory.GetFiles(images, "*.png").Single());
            int width = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4));
            int height = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4));
            int expectedWidth = (int)((rotation == 90 ? 200 : 100) * unit);
            int expectedHeight = (int)((rotation == 90 ? 100 : 200) * unit);
            Console.WriteLine($"CONTROL {name}: PNG {width}x{height}, expected {expectedWidth}x{expectedHeight} at 72 DPI.");
            if (width != expectedWidth || height != expectedHeight)
                throw new InvalidOperationException("DPI output ignored UserUnit or scaled its crop/rotation incorrectly.");
            string flattened = Path.Combine(folder, name + "-flattened.pdf");
            if (CliOutputSmoke.Command(exe, "--flatten", source, flattened, "--dpi", "72") != 0)
                throw new InvalidOperationException("Physical flattened output failed.");
            object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
                .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(flattened))])!;
            dynamic pages = engine.GetType("KillerPdf.Engine.Documents.PdfPageInformation", true)!.GetMethod("Read")!.Invoke(null, [document])!;
            if (pages[0].Width != expectedWidth || pages[0].Height != expectedHeight
                || !original.AsSpan().SequenceEqual(File.ReadAllBytes(source)))
                throw new InvalidOperationException("Flatten changed the physical page size or modified its input.");
        }
        Console.WriteLine("PASS physical exports honor UserUnit, crop offsets and rotation without changing raw source bytes.");
        return 0;
    }
}
