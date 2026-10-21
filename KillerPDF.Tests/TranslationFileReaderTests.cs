using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests
{
    public class TranslationFileReaderTests
    {
        private const string Head = "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" "
            + "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" xmlns:sys=\"clr-namespace:System;assembly=mscorlib\">";
        private static readonly HashSet<string> Known = ["Str_A", "Str_B", "Num_C"];

        private static Dictionary<string, object> Read(string xml, ICollection<string>? known = null) =>
            TranslationFileReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(xml)), known ?? Known);

        [Fact]
        public void ReadsKeyedStringsAndNumbers()
        {
            var entries = Read(Head + "<!-- note --><sys:String x:Key=\"Str_A\">{0} pages</sys:String>"
                + "<sys:String x:Key=\"Str_B\" xml:space=\"preserve\">a&#10;b</sys:String>"
                + "<sys:Double x:Key=\"Num_C\">12.5</sys:Double></ResourceDictionary>");

            Assert.Equal("{0} pages", entries["Str_A"]);
            Assert.Equal("a\nb", entries["Str_B"]);
            Assert.Equal(12.5, entries["Num_C"]);
        }

        [Theory]
        [InlineData("<Button x:Key=\"Str_A\" />")]                                       // an arbitrary object
        [InlineData("<ObjectDataProvider x:Key=\"Str_A\" MethodName=\"Start\" />")]       // an object factory
        [InlineData("<sys:String x:Key=\"Str_A\" Text=\"x\">y</sys:String>")]             // extra attribute
        [InlineData("<sys:String x:Key=\"Str_A\"><Button /></sys:String>")]               // nested object
        [InlineData("<sys:String x:Key=\"Str_Unknown\">y</sys:String>")]                  // unknown key
        [InlineData("<sys:String>y</sys:String>")]                                        // no key
        [InlineData("<sys:String x:Key=\"Str_A\">a</sys:String><sys:String x:Key=\"Str_A\">b</sys:String>")]
        [InlineData("<sys:Double x:Key=\"Num_C\">wide</sys:Double>")]
        [InlineData("<ResourceDictionary.MergedDictionaries />")]                         // external dictionaries
        public void RejectsAnythingButTheLocaleShape(string body)
        {
            Assert.Throws<FormatException>(() => Read(Head + body + "</ResourceDictionary>"));
        }

        [Fact]
        public void RejectsDtdsAndAnOtherRoot()
        {
            Assert.ThrowsAny<Exception>(() => Read("<!DOCTYPE r [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]>"
                + Head + "<sys:String x:Key=\"Str_A\">&e;</sys:String></ResourceDictionary>"));
            Assert.Throws<FormatException>(() => Read("<Window xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" />"));
        }

        [Fact]
        public void EveryBuiltInLocaleFileReads()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Strings", "en-US.xaml"))) dir = dir.Parent;
            Assert.NotNull(dir);
            string folder = Path.Combine(dir!.FullName, "Strings");
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var known = XDocument.Load(Path.Combine(folder, "en-US.xaml")).Root!.Elements()
                .Select(element => (string)element.Attribute(x + "Key")!).ToHashSet();

            foreach (string file in Directory.GetFiles(folder, "*.xaml"))
            {
                using var stream = File.OpenRead(file);
                Assert.NotEmpty(TranslationFileReader.Read(stream, known));
            }
        }
    }
}
