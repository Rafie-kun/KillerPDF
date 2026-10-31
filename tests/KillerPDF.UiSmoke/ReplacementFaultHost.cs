using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;

// Forward the real host calls and inject only a candidate-file failure at the transaction boundary.
public class ReplacementFaultHost : DispatchProxy
{
    public object Target = null!;
    public bool Reopen;
    public bool Injected;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        FileStream? locked = null;
        if (method!.Name == "SaveTempAndReload")
        {
            var finalize = (Action<string>)args![2]!;
            args[2] = (Action<string>)(path =>
            {
                if (Reopen)
                {
                    finalize(path);
                    locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                    Injected = true;
                }
                else
                {
                    Injected = true;
                    throw new IOException("Synthetic candidate write failure.");
                }
            });
        }
        try { return method.Invoke(Target, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
        finally { locked?.Dispose(); }
    }
}
