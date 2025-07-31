using System.Security.Principal;
using Spectre.Console.Cli;

namespace Eryph.GuestServices.Tool.Interceptors;

public class IsElevatedInterceptor : ICommandInterceptor
{
    public void Intercept(CommandContext context, CommandSettings settings)
    {
        if (!IsElevated())
            throw new NotElevatedException();
    }

    public static bool IsElevated()
    {
        var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}
