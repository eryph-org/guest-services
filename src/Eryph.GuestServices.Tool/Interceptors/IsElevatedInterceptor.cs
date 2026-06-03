using System.Security.Principal;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Interceptors;

public class IsElevatedInterceptor : ICommandInterceptor
{
    public void Intercept(CommandContext context, CommandSettings settings)
    {
        // Commands that talk to the eryph fabric run on the operator's machine
        // and authenticate with the operator's eryph identity; they must work
        // without host admin rights.
        if (settings is IElevationExempt)
            return;

        if (!IsElevated())
            throw new NotElevatedException();
    }

    public static bool IsElevated()
    {
        var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
