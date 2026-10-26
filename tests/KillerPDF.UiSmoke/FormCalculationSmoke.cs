using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using static DocumentStateSmoke;

internal static class FormCalculationSmoke
{
    private const string Raw = "-1234.5";
    private const string Display = "($1,234.50)";

    internal static int RunTotal(Window owner, string folder)
    {
        byte[] bytes = FormFieldEditingSmoke.Fixture("0", siblingValue: "3",
            calculationScript: "event.value = this.getField(\"untouched.sibling\").value * 2;");
        string source = Path.Combine(folder, "total.pdf");
        File.WriteAllBytes(source, bytes);
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
        owner.Left = 32; owner.Top = 32; owner.Width = 950; owner.Height = 700; owner.Opacity = 1; owner.Activate();
        TextBox? Field(string name) => Descendants<TextBox>(owner).SingleOrDefault(box => Equals(box.ToolTip, name));
        Wait(() => owner.IsActive && Field("untouched.sibling") is { IsLoaded: true }, "total input editor");
        TextBox input = Field("untouched.sibling")!;
        if (!input.Focus()) throw new InvalidOperationException("Cannot focus total input.");
        input.Text = "4";
        if (!((Button)owner.FindName("ToolSelectBtn")).Focus()) throw new InvalidOperationException("Cannot commit total input.");
        Wait(() => !input.IsKeyboardFocusWithin, "total calculation blur");
        if (Field("existing.text")?.Text != "8") throw new InvalidOperationException("Supported total did not update from the edited raw input.");
        if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes)) throw new InvalidOperationException("Live calculation changed source bytes before Save.");
        Save(owner);
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object saved = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
            .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))])!;
        var values = (IReadOnlyDictionary<string, string>)engine.GetType("KillerPdf.Engine.Documents.PdfFormCalculation", true)!
            .GetMethod("ReadValues")!.Invoke(null, [saved])!;
        if (values["existing.text"] != "8" || values["untouched.sibling"] != "4")
            throw new InvalidOperationException("Strict saved reopen lost calculated raw total or its input.");
        const string result = "PASS verify-form-total: edited raw input recalculates the total on blur; both values survive strict saved reopen without modifying source before Save.";
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }

    internal static int RunStatus(Window owner, string folder)
    {
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        List<string> failures = [];
        foreach (string kind in new[] { "invalid", "unsupported", "failed" })
        {
            byte[] bytes = FormFieldEditingSmoke.Fixture("15",
                validationScript: kind == "invalid" ? "AFRange_Validate(true, 0, true, 10);" : null,
                calculationScript: kind == "unsupported" ? "app.alert(\"never execute\");"
                    : kind == "failed" ? "event.value = 1 / 0;" : null);
            string source = Path.Combine(folder, kind + ".pdf");
            File.WriteAllBytes(source, bytes);
            object document = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
                .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(bytes)])!;
            object report = engine.GetType("KillerPdf.Engine.Documents.PdfFormCalculation", true)!
                .GetMethod("Evaluate")!.Invoke(null, [document, null])!;
            string count = kind == "invalid" ? "InvalidCount" : kind == "unsupported" ? "UnsupportedCount" : "FailedCount";
            if ((int)report.GetType().GetProperty(count)!.GetValue(report)! != 1)
                throw new InvalidOperationException("Public engine status fixture control failed: " + kind);
            Console.WriteLine("CONTROL public engine reports " + kind + " without executing arbitrary script.");
            owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
            owner.Left = 32; owner.Top = 32; owner.Width = 950; owner.Height = 700; owner.Opacity = 1; owner.Activate();
            TextBox? Field() => Descendants<TextBox>(owner).SingleOrDefault(box => Equals(box.ToolTip, "existing.text"));
            Wait(() => owner.IsActive && Field() is { IsLoaded: true }, kind + " form editor");
            TextBox field = Field()!;
            if (!field.Focus()) throw new InvalidOperationException("Cannot focus status fixture editor.");
            if (!((Button)owner.FindName("ToolSelectBtn")).Focus()) throw new InvalidOperationException("Cannot blur status fixture editor.");
            Wait(() => !field.IsKeyboardFocusWithin, kind + " form blur");
            string expected = kind == "invalid" ? "The value does not meet this field's validation rules."
                : kind == "unsupported" ? "This field uses an unsupported script. It was not run."
                : "This field's calculation could not be completed.";
            string actual = AutomationProperties.GetItemStatus(field);
            Console.WriteLine("CONTROL " + kind + " accessible status: " + actual);
            if (actual != expected || AutomationProperties.GetHelpText(field) != expected || field.Text != "15")
                failures.Add(kind);
            if (!File.ReadAllBytes(source).AsSpan().SequenceEqual(bytes))
                throw new InvalidOperationException("Form evaluation changed the source: " + kind);
            if (kind == "invalid" && actual == expected)
            {
                if (!field.Focus()) throw new InvalidOperationException("Cannot focus invalid value for correction.");
                field.Text = "5";
                ((Button)owner.FindName("ToolSelectBtn")).Focus();
                Wait(() => !field.IsKeyboardFocusWithin, "corrected form blur");
                if (AutomationProperties.GetItemStatus(field).Length != 0 || AutomationProperties.GetHelpText(field).Length != 0
                    || field.Text != "5") throw new InvalidOperationException("Corrected form value kept stale validation feedback.");
                Save(owner);
                object saved = engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!
                    .GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!.Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))])!;
                object values = engine.GetType("KillerPdf.Engine.Documents.PdfFormCalculation", true)!
                    .GetMethod("ReadValues")!.Invoke(null, [saved])!;
                if (((IReadOnlyDictionary<string, string>)values)["existing.text"] != "5")
                    throw new InvalidOperationException("Corrected raw form value did not survive Save and strict reopen.");
                Console.WriteLine("CONTROL correction clears feedback and strictly reopened raw value is5.");
            }
        }
        if (failures.Count > 0) throw new InvalidOperationException("UI-002: missing accessible form evaluation feedback: " + string.Join(", ", failures));
        const string result = "PASS verify-form-status: invalid, unsupported and failed feedback is accessible; correction clears feedback and survives strict saved reopen; untouched sources stay intact.";
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }

    internal static int Run(Window owner, string folder)
    {
        string source = Path.Combine(folder, "format-only.pdf");
        byte[] bytes = FormFieldEditingSmoke.Fixture(Raw, formatScript: "AFNumber_Format(2, 0, 3, 0, \"$\", true);");
        File.WriteAllBytes(source, bytes);
        Assembly engine = Assembly.Load("KillerPdf.Engine");
        object Open() => engine.GetType("KillerPdf.Engine.Documents.PdfDocument", true)!.GetMethod("Open", [typeof(ReadOnlyMemory<byte>)])!
            .Invoke(null, [new ReadOnlyMemory<byte>(File.ReadAllBytes(source))])!;
        object document = Open();
        object report = engine.GetType("KillerPdf.Engine.Documents.PdfFormCalculation", true)!.GetMethod("Evaluate")!.Invoke(null, [document, null])!;
        object entry = ((System.Collections.IEnumerable)report.GetType().GetProperty("Fields")!.GetValue(report)!).Cast<object>()
            .Single(item => Equals(item.GetType().GetProperty("FieldName")!.GetValue(item), "existing.text"));
        if (!Equals(entry.GetType().GetProperty("Value")!.GetValue(entry), Raw)
            || !Equals(entry.GetType().GetProperty("DisplayValue")!.GetValue(entry), Display)
            || !(bool)entry.GetType().GetProperty("IsRed")!.GetValue(entry)!)
            throw new InvalidOperationException("Public engine format-only fixture control failed.");
        Console.WriteLine("CONTROL public engine preserves raw value and formats negative currency in red.");
        owner.GetType().GetMethod("OpenFromExternal", BindingFlags.Public | BindingFlags.Instance)!.Invoke(owner, [source]);
        owner.Left = 32; owner.Top = 32; owner.Width = 950; owner.Height = 700; owner.Opacity = 1; owner.Activate();
        TextBox? Field() => Descendants<TextBox>(owner).SingleOrDefault(box => Equals(box.ToolTip, "existing.text"));
        Wait(() => owner.IsActive && Field() is not null, "scripted form field");
        TextBox field = Field()!;
        if (!field.Focus() || !field.IsKeyboardFocusWithin) throw new InvalidOperationException("Cannot focus the real form editor.");
        if (field.Text != Raw) throw new InvalidOperationException("The focused form editor did not expose the raw value.");
        if (!((Button)owner.FindName("ToolSelectBtn")).Focus()) throw new InvalidOperationException("Cannot blur the real form editor.");
        Wait(() => !field.IsKeyboardFocusWithin, "form formatting blur");
        Console.WriteLine("CONTROL form display after blur: " + field.Text);
        if (field.Text != Display || field.Foreground is not SolidColorBrush brush || brush.Color != Colors.Red)
            throw new InvalidOperationException("UI-002: format-only display or negative color was discarded on blur.");
        Save(owner);
        document = Open();
        object widget = ((System.Collections.IEnumerable)engine.GetType("KillerPdf.Engine.Documents.PdfFormWidgetReader", true)!
            .GetMethod("ReadPage")!.Invoke(null, [document, 0])!).Cast<object>()
            .Single(item => Equals(item.GetType().GetProperty("FieldName")!.GetValue(item), "existing.text"));
        if (!Equals(widget.GetType().GetProperty("Value")!.GetValue(widget), Raw))
            throw new InvalidOperationException("Formatted display replaced the saved raw form value.");
        Wait(() => Field() is { IsLoaded: true, IsVisible: true }, "saved form editor readiness");
        field = Field()!;
        if (!field.Focus()) throw new InvalidOperationException("Cannot refocus the saved form editor.");
        if (field.Text != Raw) throw new InvalidOperationException("Refocusing the form did not restore raw editing text.");
        const string result = "PASS verify-form-format: format-only display and color apply on blur while raw editing and strictly reopened saved values remain intact.";
        File.WriteAllText(Path.Combine(folder, "result.txt"), result);
        Console.WriteLine(result);
        return 0;
    }
}
