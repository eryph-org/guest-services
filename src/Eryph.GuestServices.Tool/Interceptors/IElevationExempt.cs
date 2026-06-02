namespace Eryph.GuestServices.Tool.Interceptors;

// Marker for command settings whose command runs on the operator's machine and
// must NOT require host administrator rights. The VM-level commands open a local
// Hyper-V socket / write guest KVP via WMI and need elevation; the eryph command
// group instead talks to the eryph compute API over the network with the
// operator's eryph identity, so requiring admin would defeat the whole point of
// remote access.
public interface IElevationExempt;
