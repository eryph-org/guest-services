# Eryph Guest Services


[![License](https://img.shields.io/github/license/eryph-org/guest-services.svg)](LICENSE)


Eryph Guest Services (EGS) provides secure SSH connectivity to Hyper-V virtual machines through Hyper-V sockets, eliminating the need for network configuration. The service can be used both as part of the [eryph platform](https://www.eryph.io) and standalone with plain Hyper-V.

## Why EGS?

EGS provides a unified, cross-platform solution that works consistently on both Windows and Linux VMs without network dependencies.  
While Windows Server 2025 ships with OpenSSH by default, native Hyper-V socket support remains missing ([Win32-OpenSSH issue #2200](https://github.com/PowerShell/Win32-OpenSSH/issues/2200)). [PowerShell Direct](https://learn.microsoft.com/en-us/powershell/scripting/learn/remoting/ps-direct) is Windows-only with poor file transfer performance, and Linux vsock SSH support varies by distribution.  


## Features

- **Network-free connectivity**: Access VMs via Hyper-V sockets without network setup
- **Cross-platform support**: Works on Windows and Linux VMs
- **SSH-based access**: Standard SSH protocol over Hyper-V transport
- **File transfer capabilities**: Upload/download files and directories to/from VMs
- **Pseudo-terminal support**: Interactive shell sessions
- **Public key authentication**: Secure key-based authentication
- **Easy installation**: Simple installer scripts for both platforms

## Limitations
- **No FTP subsystem** - use `unison` for file synchronization instead of `scp`

## How It Works

EGS doesn't reinvent the wheel. Like the `hvc ssh` command, EGS builds a Hyper-V socket connection using SSH ProxyCommand:

```
ProxyCommand hvc nc -t vsock <vmid> 5002
```

The `egs-tool` writes SSH configuration to `%LOCALAPPDATA%\.eryph\ssh\config`, mapping hostnames to VM IDs and proxy commands. During configuration, SSH keys are exchanged between host and guest, enabling passwordless authentication to the EGS service account (Windows) or root (Linux).

The guest implements a custom SSH server independent of any existing OpenSSH installation, avoiding configuration conflicts.

**Components:**
1. **Guest Service** (`egs-service`): Runs inside VMs as a system service, providing SSH server functionality over Hyper-V sockets
2. **Host Tool** (`egs-tool`): Command-line tool running on the Hyper-V host for managing connections and file uploads

## System Requirements

### Host Requirements
- Windows 10/11 Pro, Enterprise, or Education
- Windows Server 2016 or newer
- Hyper-V enabled
- Administrator privileges

### Guest Requirements
- Windows VMs: Windows Server 2016+ or Windows 10+
- Linux VMs: Modern Linux distribution with systemd
- Hyper-V integration services enabled

## Installation

There are two ways to use eryph guest services:

### Option 1: With eryph Platform

If you're using eryph, guest services are installed automatically via genes:

#### Guest Installation (via eryph genes)
```yaml
# For Linux VMs
fodder:
  - source: gene:dbosoft/guest-services:linux-install

# For Windows VMs  
fodder:
  - source: gene:dbosoft/guest-services:win-install
```

More details: [genepool.eryph.io/b/dbosoft/guest-services](https://genepool.eryph.io/b/dbosoft/guest-services)

#### egs-tool
Same installations as for standalone Hyper-V - see below.

### Option 2: Standalone with Plain Hyper-V

For standalone Hyper-V environments without eryph:

#### Guest Installation (manual ISO)
1. Download the installation ISO from [releases.dbosoft.eu/eryph/guest-services/](https://releases.dbosoft.eu/eryph/guest-services/)
2. Mount the ISO in your VM
3. Run the installation script from the mounted ISO:

**Windows VMs:**
```powershell
# Run as Administrator from the mounted ISO
D:\install.ps1
```

**Linux VMs:**
```bash
# Run as root from the mounted ISO
sudo /media/cdrom/install.sh
```

#### Host Tool Installation
1. Install `egs-tool` using the PowerShell installer:
```powershell
# Run as Administrator
iex ((New-Object System.Net.WebClient).DownloadString('https://raw.githubusercontent.com/eryph-org/guest-services/main/src/Eryph.GuestServices.Tool/install.ps1'))
```

2. Initialize the host-side configuration:

```powershell
# Initialize the host-side configuration
egs-tool initialize
```

This registers the Hyper-V integration service and generates SSH keys.

## Usage

### Standalone Hyper-V Usage

For plain Hyper-V environments without eryph:

#### 1. Check VM Status
```powershell
egs-tool get-status <VM-ID>
```

#### 2. Configure SSH Access
```powershell
# Add VM to SSH configuration
egs-tool add-ssh-config <VM-ID> [optional-alias]

# Update SSH config (creates ~/.ssh/config entries)
egs-tool update-ssh-config
```

#### 3. Connect via SSH
```powershell
# Connect using the generated alias
ssh <VM-ID>.hyper-v.alt
# or if you provided a custom alias:
ssh <alias>
```

#### 4. File Transfer
```powershell
# Upload single file to VM
egs-tool upload-file <VM-ID> <local-file> <remote-file>

# Upload directory to VM (non-recursive - root files only)
egs-tool upload-directory <VM-ID> <local-directory> <remote-directory>

# Upload directory recursively (including subdirectories)
egs-tool upload-directory <VM-ID> <local-directory> <remote-directory> --recursive

# Download single file from VM
egs-tool download-file <VM-ID> <remote-file> <local-file>

# Download directory from VM (non-recursive - root files only)
egs-tool download-directory <VM-ID> <remote-directory> <local-directory>

# Download directory recursively (including subdirectories)
egs-tool download-directory <VM-ID> <remote-directory> <local-directory> --recursive

# All commands support --overwrite flag
egs-tool upload-file <VM-ID> <local-file> <remote-file> --overwrite
```

### Eryph Platform Usage

When used with eryph, the guest services are typically installed via genes:

```yaml
# For Linux VMs
fodder:
  - source: gene:dbosoft/guest-services:linux-install

# For Windows VMs  
fodder:
  - source: gene:dbosoft/guest-services:win-install
```

#### SSH Configuration with eryph
If you don't provide an SSH key in the gene configuration, set up access after VM creation:

```powershell
# Update SSH config for all eryph catlets
egs-tool update-ssh-config
```

Then connect using the generated aliases:
```powershell
# Connect using catlet name and project
ssh <catlet-name>.<project-name>.eryph.alt

# For default project, you can also use:
ssh <catlet-name>.eryph.alt
```

## Configuration

### SSH Key Management

The guest services use dedicated SSH keys separate from your regular SSH keys:

```powershell
# View the public key
egs-tool get-ssh-key

# Reinitialize keys if needed
egs-tool initialize
```

### Gene Configuration Variables

When installing via eryph genes, these variables are supported:

- **version**: Version to install (`latest`, `prerelease`, or specific version like `0.1.0`)
- **downloadUrl**: Custom download URL if not using GitHub releases
- **sshPublicKey**: SSH public key for authentication (optional - if not provided, use `egs-tool add-ssh-config` after VM creation)

Example:
```yaml
fodder:
  - source: gene:dbosoft/guest-services:linux-install
    variables:
      - name: version
        value: "0.3"
      - name: sshPublicKey
        value: "ssh-rsa AAAAB3NzaC1yc2E..."
```

## Authentication

The guest services use SSH public key authentication:

1. **Username**: Always `egs` 
2. **Authentication**: SSH public key only (password authentication disabled)
3. **Key Exchange**: Keys are exchanged via Hyper-V data exchange service
4. **Host Trust**: Host keys are automatically trusted (secure due to Hyper-V socket isolation)

## Command Reference

### egs-tool Commands

| Command | Description | Arguments |
|---------|-------------|-----------|
| `initialize` | Register Hyper-V service and generate SSH keys | None |
| `unregister` | Unregister Hyper-V integration service | None |
| `get-status <VM-ID>` | Check if guest services are available | VM GUID |
| `get-ssh-key` | Display the SSH public key | None |
| `add-ssh-config <VM-ID> [alias]` | Configure SSH access for a VM | VM GUID, optional alias |
| `update-ssh-config` | Update SSH config for all VMs/catlets | None |
| `upload-file <VM-ID> <local> <remote>` | Upload single file to VM | VM GUID, local file, remote file |
| `upload-directory <VM-ID> <local> <remote>` | Upload directory to VM | VM GUID, local dir, remote dir |
| `download-file <VM-ID> <remote> <local>` | Download single file from VM | VM GUID, remote file, local file |
| `download-directory <VM-ID> <remote> <local>` | Download directory from VM | VM GUID, remote dir, local dir |

**Flags for file/directory commands:**
- `--overwrite`: Overwrite existing files/directories
- `--recursive`: Include subdirectories (directory commands only)

### Finding VM IDs

For standalone Hyper-V, get VM IDs using:

```powershell
# PowerShell
Get-VM | Select-Object Name, Id

# Or using Hyper-V Manager
# VM Settings → Hardware → Details shows the VM ID
```

For eryph catlets:
```powershell
Get-Catlet | Select-Object Name, VmId
```

## Troubleshooting

### Common Issues

1. **Status shows "unknown"**
   - Guest services not installed or not running in VM
   - Check VM has Hyper-V integration services enabled

2. **Authentication failed**
   - Run `egs-tool add-ssh-config <VM-ID>` to set up authentication
   - Ensure you ran `egs-tool initialize` on the host

3. **Connection refused**
   - Guest service may not be running: check service status in VM
   - Hyper-V integration services may be disabled

4. **File transfer failed**
   - Check paths exist and are writable/readable
   - Use `--overwrite` flag if file/directory already exists
   - Use `--recursive` flag for directory operations that need subdirectories

### Service Management

#### Windows VMs
```powershell
# Check service status
Get-Service eryph-guest-services

# Restart service
Restart-Service eryph-guest-services
```

#### Linux VMs
```bash
# Check service status
sudo systemctl status eryph-guest-services

# Restart service
sudo systemctl restart eryph-guest-services
```

## Architecture

### Components

- **egs-service**: Guest service (SSH server over Hyper-V sockets)
- **egs-tool**: Host tool (SSH client and management)
- **Hyper-V Integration Service**: Transport layer for communication
- **Hyper-V Data Exchange**: Key exchange and status communication

### Network Stack

```
Host (egs-tool) ←→ Hyper-V Socket ←→ Guest (egs-service)
                        ↑
               Service ID: 0000138a-facb-11e6-bd58-64006a7986d3
               Linux VSock Port: 5002
```

### Security

- All communication encrypted via SSH
- Public key authentication only
- Host keys automatically trusted (isolated transport)
- Separate key management from system SSH

## Development

### Building

```bash
# Build solution
dotnet build

# Run tests
dotnet test

# Create packages
dotnet pack
```

### Project Structure

- `src/Eryph.GuestServices.Service/` - Guest service implementation
- `src/Eryph.GuestServices.Tool/` - Host tool implementation  
- `src/Eryph.GuestServices.Sockets/` - Hyper-V socket abstraction
- `src/Eryph.GuestServices.Pty/` - Pseudo-terminal support
- `src/Eryph.GuestServices.DevTunnels.Ssh.Extensions/` - SSH server extensions
- `packaging/iso/` - Installation scripts
- `tests/` - Test projects

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Contributing

1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests for new functionality
5. Ensure all tests pass
6. Submit a pull request

## Support

- **Issues**: [GitHub Issues](https://github.com/eryph-org/guest-services/issues)
- **Documentation**: [eryph Documentation](https://eryph.io/docs)
- **Community**: [eryph Discussions](https://github.com/eryph-org/eryph/discussions)
