using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace Pulse.Agent.Ipc;

/// <summary>
/// Creates the server ends of the agent pipe for <see cref="Pulse.Engine.Ipc.IpcServer"/> (one per client): byte mode,
/// asynchronous, up to 8 instances, DACL = the current user only (an elevated and an unelevated process of the same user
/// share the user SID, so no other account on the machine can connect), plus a best-effort medium-integrity mandatory
/// label (<c>S:(ML;;NW;;;ME)</c>) so a medium-integrity popover can write to the pipe of an elevated agent (objects
/// created by an elevated process get its High label and refuse writes from below).
/// <para>
/// The label cannot go through <see cref="PipeSecurity"/>: .NET drops the ML ACE when parsing the SDDL, and the empty SACL
/// it leaves behind makes CreateNamedPipe fail with ERROR_PRIVILEGE_NOT_HELD (measured). It is therefore set on the handle
/// with SetSecurityInfo(LABEL_SECURITY_INFORMATION), which needs WRITE_OWNER on it. Only the instance that creates the pipe
/// may ask for WRITE_OWNER (later instances get ERROR_ACCESS_DENIED); they share the pipe's security descriptor, label
/// included, so they are created without it.
/// </para>
/// </summary>
internal static class AgentPipeFactory
{
    public const int MaxInstances = 8;
    private const string MediumLabelSddl = "S:(ML;;NW;;;ME)";

    public static Func<NamedPipeServerStream> Create(string pipeName, ILogger log)
    {
        var security = CreateSecurity();
        var labelFailureLogged = 0;

        return () =>
        {
            NamedPipeServerStream pipe;
            bool createdPipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, security, HandleInheritability.None, PipeAccessRights.TakeOwnership);
                createdPipe = true;
            }
            catch (UnauthorizedAccessException)
            {
                // Another instance of our pipe exists (WRITE_OWNER is only granted to the first one): it already carries the label.
                pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, MaxInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous, 0, 0, security);
                createdPipe = false;
            }

            if (createdPipe && !TrySetMediumLabel(pipe, out var error) && Interlocked.Exchange(ref labelFailureLogged, 1) == 0)
            {
                log.LogDebug("Medium-integrity label not applied to the agent pipe ({Error}); an unelevated popover may not reach an elevated agent", error);
            }
            return pipe;
        };
    }

    internal static PipeSecurity CreateSecurity()
    {
        var security = new PipeSecurity();
        using (var identity = WindowsIdentity.GetCurrent())
        {
            security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        }
        return security;
    }

    private static bool TrySetMediumLabel(NamedPipeServerStream pipe, out string error)
    {
        error = string.Empty;
        var descriptor = IntPtr.Zero;
        var handle = pipe.SafePipeHandle;
        var addedRef = false;
        try
        {
            if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptorW(MediumLabelSddl, Native.SDDL_REVISION_1, out descriptor, IntPtr.Zero))
            {
                error = $"ConvertStringSecurityDescriptor {Marshal.GetLastPInvokeError()}";
                return false;
            }
            if (!Native.GetSecurityDescriptorSacl(descriptor, out var present, out var sacl, out _) || present == 0)
            {
                error = $"GetSecurityDescriptorSacl {Marshal.GetLastPInvokeError()}";
                return false;
            }

            handle.DangerousAddRef(ref addedRef);
            var rc = Native.SetSecurityInfo(handle.DangerousGetHandle(), Native.SE_KERNEL_OBJECT, Native.LABEL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl);
            if (rc != 0)
            {
                error = $"SetSecurityInfo {rc}";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (addedRef) handle.DangerousRelease();
            if (descriptor != IntPtr.Zero) Native.LocalFree(descriptor);
        }
    }

    /// <summary>The pipe's DACL + label as SDDL (diagnostics / self-test).</summary>
    internal static string DescribeSecurity(NamedPipeServerStream pipe)
    {
        var handle = pipe.SafePipeHandle;
        var addedRef = false;
        var descriptor = IntPtr.Zero;
        try
        {
            handle.DangerousAddRef(ref addedRef);
            const uint what = Native.DACL_SECURITY_INFORMATION | Native.LABEL_SECURITY_INFORMATION;
            var rc = Native.GetSecurityInfo(handle.DangerousGetHandle(), Native.SE_KERNEL_OBJECT, what,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out descriptor);
            if (rc != 0) return $"GetSecurityInfo failed ({rc})";
            if (!Native.ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, Native.SDDL_REVISION_1, what, out var text, IntPtr.Zero))
            {
                return $"ConvertSecurityDescriptorToStringSecurityDescriptor failed ({Marshal.GetLastPInvokeError()})";
            }
            try
            {
                return Marshal.PtrToStringUni(text) ?? string.Empty;
            }
            finally
            {
                Native.LocalFree(text);
            }
        }
        finally
        {
            if (addedRef) handle.DangerousRelease();
            if (descriptor != IntPtr.Zero) Native.LocalFree(descriptor);
        }
    }
}
