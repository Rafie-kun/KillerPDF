using System.Text;
using KillerLauncher;
using KillerPDF.Features;
using Xunit;

namespace KillerPDF.Tests;

public sealed class UpdateRelaunchArgumentsTests
{
    [Fact]
    public void RelaunchWithoutAFileRoundTrips()
    {
        string[] args = UpdateRelaunchArguments.Encode(null).Split(' ');

        Assert.True(UpdateRelaunchArguments.Requested(args));
        Assert.Null(UpdateRelaunchArguments.ReopenFile(args));
    }

    [Fact]
    public void RelaunchWithAUnicodeFileRoundTrips()
    {
        string file = @"C:\Users\steve\Documents\Custom & More\report é 日本.pdf";
        string[] args = UpdateRelaunchArguments.Encode(file).Split(' ');

        Assert.True(UpdateRelaunchArguments.Requested(args));
        Assert.Equal(file, UpdateRelaunchArguments.ReopenFile(args));
    }

    [Fact]
    public void MalformedReopenValuesAreIgnored()
    {
        Assert.Null(UpdateRelaunchArguments.ReopenFile(["/relaunch", "/reopen:not base64!"]));
        Assert.False(UpdateRelaunchArguments.Requested(["/silent"]));
    }

    [Fact]
    public void InstalledUpdateArgumentsNameTheScopeTheFolderAndTheRelaunch()
    {
        string executable = @"C:\Program Files\PDF App\KillerPDF.App.exe";

        string[] user = InstalledUpdateCommand.Arguments(executable, machine: false).Split(' ');
        string[] machine = InstalledUpdateCommand.Arguments(executable, machine: true, reopenFile: @"C:\doc.pdf").Split(' ');

        Assert.Equal("/install-user", user[0]);
        Assert.Equal("/silent", machine[0]);
        Assert.Equal(@"C:\Program Files\PDF App", Encoding.UTF8.GetString(Convert.FromBase64String(user[1]["/install-dir64:".Length..])));
        Assert.Equal("/relaunch", user[2]);
        Assert.Equal(3, user.Length);
        Assert.Equal(@"C:\doc.pdf", UpdateRelaunchArguments.ReopenFile(machine));
    }
}
