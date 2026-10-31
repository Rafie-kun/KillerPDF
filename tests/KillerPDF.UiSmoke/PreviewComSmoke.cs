using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using Microsoft.Win32;
using KillerPDF.PreviewHandler;
using static DocumentStateSmoke;
using Condition = System.Windows.Automation.Condition;

internal static class PreviewComSmoke
{
    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateStreamOnFileEx(string file, uint mode, uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create, IStream? template, out IStream stream);
    [DllImport("shlwapi.dll", EntryPoint = "SHCreateStreamOnFileEx", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int CreateNativeStream(string file, uint mode, uint attributes,
        [MarshalAs(UnmanagedType.Bool)] bool create, IntPtr template, out IntPtr stream);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NativeInitialize(IntPtr instance, IntPtr stream, uint mode);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int NativeStat(IntPtr instance, IntPtr stat, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect bounds);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parent, IntPtr child);

    private static object CreateHandler()
    {
        Guid clsid = new("0EB8F20D-6E18-4B80-9CDF-FF663C52B5BD"), iid = typeof(IPreviewHandler).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 4, ref iid, out IntPtr pointer));
        try { return Marshal.GetObjectForIUnknown(pointer); }
        finally { Marshal.Release(pointer); }
    }

    private static void InitializeHandler(object instance, string file)
    {
        Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(file, 0x20, 0, false, null, out IStream stream));
        try { ((IInitializeWithStream)instance).Initialize(stream, 0); }
        finally { Marshal.FinalReleaseComObject(stream); }
    }

    private static void VerifyLifetimes(object instance, string file, IntPtr owner, string neighborFile)
    {
        object neighbor = CreateHandler();
        var other = (IPreviewHandler)neighbor;
        var handler = (IPreviewHandler)instance;
        try
        {
            InitializeHandler(neighbor, neighborFile);
            var rect = new NativeRect { Right = 640, Bottom = 700 };
            other.SetWindow(owner, ref rect);
            other.DoPreview();
            Marshal.ThrowExceptionForHR(((IOleWindow)neighbor).GetWindow(out IntPtr otherWindow));
            for (int cycle = 0; cycle < 10; cycle++)
            {
                InitializeHandler(instance, file);
                handler.SetWindow(owner, ref rect);
                handler.DoPreview();
                Marshal.ThrowExceptionForHR(((IOleWindow)instance).GetWindow(out IntPtr window));
                if (window == otherWindow) throw new InvalidOperationException("Preview instances share a child window.");
                var resized = new NativeRect { Right = 500, Bottom = 600 };
                handler.SetRect(ref resized);
                if (!GetClientRect(window, out NativeRect actual) || actual.Right != 500 || actual.Bottom != 600)
                    throw new InvalidOperationException("Registered preview did not resize.");
                handler.SetFocus();
                handler.QueryFocus(out IntPtr focus);
                if (focus != window && !IsChild(window, focus)) throw new InvalidOperationException("Preview focus did not enter its own window.");
                handler.Unload();
                handler.Unload();
                if (((IOleWindow)instance).GetWindow(out IntPtr gone) != unchecked((int)0x80004005)
                    || gone != IntPtr.Zero || IsWindow(window)) throw new InvalidOperationException("Immediate unload retained its window.");
                Marshal.ThrowExceptionForHR(((IOleWindow)neighbor).GetWindow(out IntPtr survivor));
                if (survivor != otherWindow || !IsWindow(survivor)) throw new InvalidOperationException("Unloading one preview stopped its neighbor.");
            }
            Console.WriteLine("PASS ten native resize/focus/immediate unload/reinitialize cycles preserve another live preview.");
        }
        finally { other.Unload(); Marshal.FinalReleaseComObject(neighbor); }
    }

    private static void VerifyNativeInitialize(string file)
    {
        Guid clsid = new("0EB8F20D-6E18-4B80-9CDF-FF663C52B5BD"), iid = typeof(IInitializeWithStream).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 4, ref iid, out IntPtr initialize));
        IntPtr stream = IntPtr.Zero, stat = Marshal.AllocCoTaskMem(128);
        try
        {
            Marshal.ThrowExceptionForHR(CreateNativeStream(file, 0x20, 0, false, IntPtr.Zero, out stream));
            IntPtr streamTable = Marshal.ReadIntPtr(stream);
            var nativeStat = Marshal.GetDelegateForFunctionPointer<NativeStat>(Marshal.ReadIntPtr(streamTable, 12 * IntPtr.Size));
            Marshal.ThrowExceptionForHR(nativeStat(stream, stat, 1));
            Console.WriteLine("CONTROL direct native IStream Stat succeeded without a managed stream wrapper.");
            IntPtr table = Marshal.ReadIntPtr(initialize);
            var nativeInitialize = Marshal.GetDelegateForFunctionPointer<NativeInitialize>(Marshal.ReadIntPtr(table, 3 * IntPtr.Size));
            int result = nativeInitialize(initialize, stream, 0);
            Console.WriteLine($"CONTROL direct native Initialize returned 0x{result:X8}.");
            Marshal.ThrowExceptionForHR(result);
        }
        finally
        {
            if (stream != IntPtr.Zero) Marshal.Release(stream);
            Marshal.FreeCoTaskMem(stat);
            Marshal.Release(initialize);
        }
    }

    internal static int Run(Window owner, string appPath, string folder) => FormWorkflowFailureSmoke.WithCleanup(owner, () =>
    {
        const string classId = "{0EB8F20D-6E18-4B80-9CDF-FF663C52B5BD}";
        const string appId = "{CC8F3FF2-266A-4827-9CFA-964310EB6676}";
        const string extension = @"Software\Classes\.pdf\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}";
        string installed = Path.GetFullPath(Environment.GetEnvironmentVariable("KILLERPDF_TEST_PREVIEW_ROOT")
            ?? Path.Combine(Path.GetDirectoryName(appPath)!, "..", "..", "package-routes-test", "installed"));
        if (!File.Exists(Path.Combine(installed, "KillerPDF.PreviewHandler.comhost.dll")))
            throw new FileNotFoundException("Install the test-owned ordinary package first.");
        string[] ownedKeys = [@"Software\Classes\CLSID\" + classId, @"Software\Classes\AppID\" + appId,
            @"Software\Classes\KillerPDF.pdf\shellex\{8895b1c6-b41f-4c1c-a562-0d564250836f}", @"Software\KillerPDF\PreviewHandler"];
        foreach (string key in ownedKeys)
        {
            using RegistryKey? existing = Registry.CurrentUser.OpenSubKey(key);
            if (existing is not null) throw new InvalidOperationException("Existing preview registration must be preserved: " + key);
        }
        using (RegistryKey? handlers = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\PreviewHandlers"))
            if (handlers?.GetValue(classId) is not null) throw new InvalidOperationException("An existing handler listing must be preserved.");
        string? previous;
        using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(extension))
        {
            previous = key?.GetValue("") as string;
            if (key is not null && string.IsNullOrEmpty(previous)) throw new InvalidOperationException("An existing unusual preview association must be preserved.");
        }
        File.WriteAllText(Path.Combine(folder, "previous-preview.txt"), previous ?? "<absent>");
        Type registration = owner.GetType().Assembly.GetType("KillerPDF.Services.PreviewHandlerRegistration", true)!;
        const BindingFlags methods = BindingFlags.Static | BindingFlags.NonPublic;
        try
        {
            registration.GetMethod("Register", methods)!.Invoke(null, [Registry.CurrentUser, installed]);
            owner.Opacity = 1; owner.Activate();
            IntPtr ownerHandle = new WindowInteropHelper(owner).Handle;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new Thread(() =>
            {
                try
                {
                    foreach (string state in new[] { "incomplete", "failed" })
                    {
                        string file = Path.Combine(folder, state + ".pdf");
                        File.WriteAllBytes(file, PreviewFailureSmoke.Fixture(state));
                        string nativeControl = Path.Combine(AppContext.BaseDirectory, "preview-native-stream.exe");
                        if (File.Exists(nativeControl) && CliOutputSmoke.Command(nativeControl, file, ownerHandle.ToInt64().ToString()) != 0)
                            throw new InvalidOperationException("The separate C++ caller could not initialize the registered handler.");
                        VerifyNativeInitialize(file);
                        Guid clsid = new(classId), iid = typeof(IPreviewHandler).GUID;
                        Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 4, ref iid, out IntPtr pointer));
                        object instance;
                        try { instance = Marshal.GetObjectForIUnknown(pointer); }
                        finally { Marshal.Release(pointer); }
                        var handler = (IPreviewHandler)instance;
                        var windowContract = (IOleWindow)instance;
                        int initialWindowResult = windowContract.GetWindow(out IntPtr unpublished);
                        Console.WriteLine($"CONTROL unpublished GetWindow=0x{initialWindowResult:X8}, HWND={unpublished}.");
                        if (initialWindowResult != unchecked((int)0x80004005) || unpublished != IntPtr.Zero)
                            throw new InvalidOperationException("A preview without a window returned the host window.");
                        bool verified = false;
                        try
                        {
                            Marshal.ThrowExceptionForHR(SHCreateStreamOnFileEx(file, 0x20, 0, false, null, out IStream stream));
                            try { ((IInitializeWithStream)instance).Initialize(stream, 0); }
                            finally { Marshal.FinalReleaseComObject(stream); }
                            var rect = new NativeRect { Right = 640, Bottom = 700 };
                            handler.SetWindow(ownerHandle, ref rect);
                            Console.WriteLine("CONTROL native preview parent set.");
                            handler.DoPreview();
                            Console.WriteLine("CONTROL native preview created.");
                            Marshal.ThrowExceptionForHR(((IOleWindow)instance).GetWindow(out IntPtr handle));
                            AutomationElement root = AutomationElement.FromHandle(handle);
                            AutomationElement? retry = null;
                            Wait(() =>
                            {
                                var descendants = root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().ToArray();
                                retry = descendants.FirstOrDefault(element => element.Current.Name == "Retry" && !element.Current.IsOffscreen);
                                return retry is not null && descendants.Any(element => !element.Current.IsOffscreen
                                    && element.Current.Name == "This page could not be displayed completely.");
                            }, "registered surrogate preview warning and accessible retry");
                            ((InvokePattern)retry!.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
                            Wait(() => retry.Current.IsEnabled, "surrogate retry finished");
                            File.WriteAllLines(Path.Combine(folder, state + "-automation.txt"),
                                root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Select(element => element.Current.Name));
                            Console.WriteLine("PASS actual registered COM surrogate " + state + " notice and accessible Retry.");
                            handler.Unload();
                            if (windowContract.GetWindow(out IntPtr unloaded) != unchecked((int)0x80004005)
                                || unloaded != IntPtr.Zero || IsWindow(handle))
                                throw new InvalidOperationException("Unload retained a preview HWND or reported the host HWND.");
                            string neighborFile = Path.Combine(folder, state + "-neighbor.pdf");
                            File.WriteAllBytes(neighborFile, PreviewFailureSmoke.Fixture("complete"));
                            VerifyLifetimes(instance, file, ownerHandle, neighborFile);
                            verified = true;
                        }
                        finally
                        {
                            try { handler.Unload(); }
                            catch (COMException error) when (!verified) { Console.WriteLine("CONTROL cleanup after the earlier COM failure: " + error.Message); }
                            finally { Marshal.FinalReleaseComObject(instance); }
                        }
                    }
                    completion.TrySetResult(true);
                }
                catch (Exception error) { completion.TrySetException(error); }
            });
            client.SetApartmentState(ApartmentState.STA);
            client.IsBackground = true;
            client.Start();
            Wait(() => completion.Task.IsCompleted, "native preview checks with a responsive host window");
            completion.Task.GetAwaiter().GetResult();
        }
        finally
        {
            registration.GetMethod("Unregister", methods)!.Invoke(null, [Registry.CurrentUser]);
            registration.GetMethod("StopHosts", methods)!.Invoke(null, [installed]);
        }
        using (RegistryKey? restored = Registry.CurrentUser.OpenSubKey(extension))
            if (!Equals(previous, restored?.GetValue(""))) throw new InvalidOperationException("Preview association was not restored.");
        Console.WriteLine("PASS previous preview association restored.");
        return 0;
    });
}
