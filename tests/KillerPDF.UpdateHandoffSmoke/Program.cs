using System.Diagnostics;
using System.Text;
using System.Text.Json;
using KillerPDF.Features;

if (Environment.GetEnvironmentVariable("KP_UPDATE_CAPTURE") is string capture)
{
    File.WriteAllText(capture, JsonSerializer.Serialize(args));
    return 0;
}

string root = Path.GetFullPath(args.Single());
if (Directory.Exists(root)) throw new IOException("Use a fresh output directory.");
Directory.CreateDirectory(root);
string probe = Path.Combine(AppContext.BaseDirectory, "UpdateHandoffSmoke.exe");
int failures = 0;
foreach (bool machine in new[] { false, true })
foreach (string folder in new[] { "Custom & More", "Custom é 日本" })
{
    string destination = Path.Combine(root, folder, "PDF App");
    string executable = Path.Combine(destination, "KillerPDF.App.exe");
    string key = (machine ? "machine" : "user") + (folder.Contains('&') ? "-ascii" : "-unicode");
    string result = Path.Combine(root, key + ".json");
    string command = InstalledUpdateCommand.Create(probe, executable, machine);
    string script = Path.Combine(root, key + ".cmd");
    File.WriteAllText(script, "@echo off\r\n" + command + "\r\n", Encoding.ASCII);
    var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
    start.ArgumentList.Add("/d");
    start.ArgumentList.Add("/c");
    start.ArgumentList.Add(script);
    start.Environment["KP_UPDATE_CAPTURE"] = result;
    using var process = Process.Start(start)!;
    if (!process.WaitForExit(10000)) { process.Kill(); throw new TimeoutException("Update probe did not exit."); }
    string[] received = JsonSerializer.Deserialize<string[]>(File.ReadAllText(result))!;
    string? encoded = received.SingleOrDefault(value => value.StartsWith("/install-dir64:", StringComparison.Ordinal));
    string? actual = encoded is null ? null : Encoding.UTF8.GetString(Convert.FromBase64String(encoded[15..]));
    bool passed = process.ExitCode == 0 && received[0] == (machine ? "/silent" : "/install-user")
        && actual == destination;
    Console.WriteLine($"{(machine ? "Machine" : "User")}: destination forwarded={passed}; arguments={JsonSerializer.Serialize(received)}");
    if (!passed) failures++;
}
return failures == 0 ? 0 : 1;
