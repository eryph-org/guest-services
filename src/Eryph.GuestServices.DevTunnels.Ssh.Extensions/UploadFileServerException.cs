using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Eryph.GuestServices.DevTunnels.Ssh.Extensions;

public class UploadFileServerException : Exception
{
    public UploadFileServerException(string message) : base(message)
    {
    }
}
