using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.Tool.Interceptors;

public class NotElevatedException : Exception
{
    public NotElevatedException() : base("This command requires elevated privileges. Please run the command as an administrator.")
    {
        HResult = unchecked((int)0x80070005);
    }
}
