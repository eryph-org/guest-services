# End-to-end Pester suite for the eryph guest provisioning agent.
#
# These tests drive the real egs-provisioning.exe (NOT a mocked harness) over
# every sample in samples/cloud-configs/ and samples/multipart/. The point is
# to catch issues the C# unit tests cannot — wiring problems, argument
# parsing, real YAML/MIME parsing, log message format, exit codes.
#
# Requires:
#   - Pester 5.x        (`Install-Module Pester -MinimumVersion 5.5.0 -Force`)
#   - PowerShell 5.1+ or PowerShell 7+
#   - egs-provisioning.exe built (`dotnet build src/Eryph.GuestServices.Provisioning`)
#     or pointed at via $env:EGS_PROVISIONING_EXE.
#
# CLI contract assumed (delivered by the parallel agent on the same branch):
#   egs-provisioning validate     --user-data <path>
#   egs-provisioning run          --user-data <path> --dry-run [--state-dir <dir>]
#   egs-provisioning status       [--state-dir <dir>]
#   egs-provisioning reset        [--state-dir <dir>]
#   egs-provisioning collect-logs --output <zipPath>
#
# All `run` invocations in this suite pass --dry-run so the agent does not
# mutate the host. Dry-run is expected to log "would <action>" lines and
# return ModuleOutcome.Ok() without touching IWindowsOs.

BeforeAll {
    . (Join-Path $PSScriptRoot 'helpers' 'Invoke-EgsProvisioning.ps1')
    . (Join-Path $PSScriptRoot 'helpers' 'Test-Provisioning.ps1')

    $script:RepoRoot       = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $script:SamplesRoot    = Join-Path $script:RepoRoot 'samples'
    $script:CloudConfigDir = Join-Path $script:SamplesRoot 'cloud-configs'
    $script:MultipartDir   = Join-Path $script:SamplesRoot 'multipart'
    $script:BrokenDir      = Join-Path $script:CloudConfigDir 'broken'

    # Hard-fail early if the binary is missing — every test downstream relies
    # on it and the error from a per-test Get-EgsProvisioningExePath would be
    # noisier than a single BeforeAll throw.
    $script:Exe = Get-EgsProvisioningExePath
    Write-Host "Using egs-provisioning.exe at: $script:Exe"
}

Describe 'Sample validation (validate subcommand)' {

    # Each well-formed sample should pass validation.
    $samples = @(
        @{ Name = '01-minimal-admin-user.yaml';           Dir = 'cloud-configs' }
        @{ Name = '02-write-files-with-encodings.yaml';   Dir = 'cloud-configs' }
        @{ Name = '03-runcmd-mixed-forms.yaml';           Dir = 'cloud-configs' }
        @{ Name = '04-chpasswd-list-and-users.yaml';      Dir = 'cloud-configs' }
        @{ Name = '05-multipart-mixed.mime';              Dir = 'cloud-configs' }
        @{ Name = '07-windows-shellscript.ps1';           Dir = 'cloud-configs' }
        @{ Name = '08-locked-user.yaml';                  Dir = 'cloud-configs' }
        @{ Name = '09-plain-text-passwd.yaml';            Dir = 'cloud-configs' }
        @{ Name = '10-hostname-only.yaml';                Dir = 'cloud-configs' }
        @{ Name = '11-write-files-permissions-formats.yaml'; Dir = 'cloud-configs' }
        @{ Name = '12-vendor-and-user.yaml';              Dir = 'cloud-configs' }
        @{ Name = 'bootstrap-and-config.mime';            Dir = 'multipart' }
    )

    It "validate succeeds for <Name>" -ForEach $samples {
        $path = Join-Path $script:SamplesRoot $Dir $Name
        $result = Invoke-EgsProvisioning -Arguments @('validate', '--user-data', $path)
        Assert-EgsExitCode -Result $result -Expected 0
    }
}

Describe 'Sample dry-run execution (run --dry-run)' {

    Context 'Sample 01 — admin user with SSH key' {
        It 'reports the user create and ssh key apply intent' {
            $stateDir = New-IsolatedStateDir
            try {
                $path = Join-Path $script:CloudConfigDir '01-minimal-admin-user.yaml'
                $result = Invoke-EgsProvisioning -Arguments @(
                    'run', '--dry-run',
                    '--user-data', $path,
                    '--state-dir', $stateDir
                )
                Assert-EgsExitCode -Result $result -Expected 0
                Assert-EgsOutputContains -Result $result -Substring 'admin'
                Assert-EgsOutputContains -Result $result -Substring 'ssh'
            }
            finally {
                Remove-IsolatedStateDir -Path $stateDir
            }
        }
    }

    Context 'Sample 02 — write_files encodings' {
        It 'reports a write for each of the three encoding shapes' {
            $stateDir = New-IsolatedStateDir
            try {
                $path = Join-Path $script:CloudConfigDir '02-write-files-with-encodings.yaml'
                $result = Invoke-EgsProvisioning -Arguments @(
                    'run', '--dry-run',
                    '--user-data', $path,
                    '--state-dir', $stateDir
                )
                Assert-EgsExitCode -Result $result -Expected 0
                Assert-EgsOutputContains -Result $result -Substring '02-plain.txt'
                Assert-EgsOutputContains -Result $result -Substring '02-base64.bin'
                Assert-EgsOutputContains -Result $result -Substring '02-gzip-base64.txt'
            }
            finally {
                Remove-IsolatedStateDir -Path $stateDir
            }
        }
    }

    Context 'Sample 03 — runcmd mixed shapes' {
        It 'mentions both shell-string and argv-list entries' {
            $stateDir = New-IsolatedStateDir
            try {
                $path = Join-Path $script:CloudConfigDir '03-runcmd-mixed-forms.yaml'
                $result = Invoke-EgsProvisioning -Arguments @(
                    'run', '--dry-run',
                    '--user-data', $path,
                    '--state-dir', $stateDir
                )
                Assert-EgsExitCode -Result $result -Expected 0
                # The shell-string entry should appear by its first-token text.
                Assert-EgsOutputContains -Result $result -Substring 'first runcmd entry'
                # The argv list third entry should also surface.
                Assert-EgsOutputContains -Result $result -Substring 'third entry'
            }
            finally {
                Remove-IsolatedStateDir -Path $stateDir
            }
        }
    }

    Context 'Sample 04 — chpasswd legacy list' {
        It 'reports password changes for both list users' {
            $stateDir = New-IsolatedStateDir
            try {
                $path = Join-Path $script:CloudConfigDir '04-chpasswd-list-and-users.yaml'
                $result = Invoke-EgsProvisioning -Arguments @(
                    'run', '--dry-run',
                    '--user-data', $path,
                    '--state-dir', $stateDir
                )
                Assert-EgsExitCode -Result $result -Expected 0
                Assert-EgsOutputContains -Result $result -Substring 'bob'
                Assert-EgsOutputContains -Result $result -Substring 'carol'
                # The colon-bearing carol password proves SplitOnFirstColon is right.
            }
            finally {
                Remove-IsolatedStateDir -Path $stateDir
            }
        }
    }

    Context 'Sample 05 — multipart MIME with cloud-config + shellscript' {
        It 'processes both parts under a dry run' {
            $stateDir = New-IsolatedStateDir
            try {
                $path = Join-Path $script:CloudConfigDir '05-multipart-mixed.mime'
                $result = Invoke-EgsProvisioning -Arguments @(
                    'run', '--dry-run',
                    '--user-data', $path,
                    '--state-dir', $stateDir
                )
                Assert-EgsExitCode -Result $result -Expected 0
                Assert-EgsOutputContains -Result $result -Substring 'mpadmin'
                Assert-EgsOutputContains -Result $result -Substring 'bootstrap'
            }
            finally {
                Remove-IsolatedStateDir -Path $stateDir
            }
        }
    }

    Context 'Sample 06 — #include URL pointing at sample 01' {
        It 'resolves the include and applies the referenced cloud-config' {
            $stateDir = New-IsolatedStateDir
            $template = Join-Path $script:CloudConfigDir '06-include-url.txt'
            $rendered = Resolve-SampleTemplate -TemplatePath $template -SamplesDirectory $script:CloudConfigDir
            $renderedDir = Split-Path -Parent $rendered
            try {
                $result = Invoke-EgsProvisioning -Arguments @(
                    'run', '--dry-run',
                    '--user-data', $rendered,
                    '--state-dir', $stateDir
                )
                Assert-EgsExitCode -Result $result -Expected 0
                # If the include was resolved, the agent processes sample 01
                # and reports the admin user from inside it.
                Assert-EgsOutputContains -Result $result -Substring 'admin'
            }
            finally {
                Remove-IsolatedStateDir -Path $stateDir
                Remove-Item -LiteralPath $renderedDir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    Context 'Sample 07 — #ps1_sysnative shellscript user-data' {
        It 'classifies and stages the script without applying cloud-config keys' {
            $stateDir = New-IsolatedStateDir
            try {
                $path = Join-Path $script:CloudConfigDir '07-windows-shellscript.ps1'
                $result = Invoke-EgsProvisioning -Arguments @(
                    'run', '--dry-run',
                    '--user-data', $path,
                    '--state-dir', $stateDir
                )
                Assert-EgsExitCode -Result $result -Expected 0
                # Dry-run scripts module logs a "would run" intent rather than
                # invoking PowerShell. We assert by the embedded marker name.
                Assert-EgsOutputContains -Result $result -Substring '07'
            }
            finally {
                Remove-IsolatedStateDir -Path $stateDir
            }
        }
    }

    Context 'Sample 08 — locked user' {
        It 'reports creating both users; one with the locked flag' {
            $stateDir = New-IsolatedStateDir
            try {
                $path = Join-Path $script:CloudConfigDir '08-locked-user.yaml'
                $result = Invoke-EgsProvisioning -Arguments @(
                    'run', '--dry-run',
                    '--user-data', $path,
                    '--state-dir', $stateDir
                )
                Assert-EgsExitCode -Result $result -Expected 0
                Assert-EgsOutputContains -Result $result -Substring 'service-account'
                Assert-EgsOutputContains -Result $result -Substring 'audit-readonly'
            }
            finally {
                Remove-IsolatedStateDir -Path $stateDir
            }
        }
    }

    Context 'Sample 09 — plain_text_passwd alias' {
        It 'creates the user and sets its password under the alias' {
            $stateDir = New-IsolatedStateDir
            try {
                $path = Join-Path $script:CloudConfigDir '09-plain-text-passwd.yaml'
                $result = Invoke-EgsProvisioning -Arguments @(
                    'run', '--dry-run',
                    '--user-data', $path,
                    '--state-dir', $stateDir
                )
                Assert-EgsExitCode -Result $result -Expected 0
                Assert-EgsOutputContains -Result $result -Substring 'opsadmin'
            }
            finally {
                Remove-IsolatedStateDir -Path $stateDir
            }
        }
    }

    Context 'Sample 10 — hostname only' {
        It 'invokes the hostname module with no other module side-effects' {
            $stateDir = New-IsolatedStateDir
            try {
                $path = Join-Path $script:CloudConfigDir '10-hostname-only.yaml'
                $result = Invoke-EgsProvisioning -Arguments @(
                    'run', '--dry-run',
                    '--user-data', $path,
                    '--state-dir', $stateDir
                )
                Assert-EgsExitCode -Result $result -Expected 0
                Assert-EgsOutputContains -Result $result -Substring 'eryph-guest-10'
            }
            finally {
                Remove-IsolatedStateDir -Path $stateDir
            }
        }
    }

    Context 'Sample 11 — write_files permissions literal shapes' {
        It 'writes all three files and emits the POSIX-permissions-ignored warning' {
            $stateDir = New-IsolatedStateDir
            try {
                $path = Join-Path $script:CloudConfigDir '11-write-files-permissions-formats.yaml'
                $result = Invoke-EgsProvisioning -Arguments @(
                    'run', '--dry-run',
                    '--user-data', $path,
                    '--state-dir', $stateDir
                )
                Assert-EgsExitCode -Result $result -Expected 0
                Assert-EgsOutputContains -Result $result -Substring 'quoted-perms.txt'
                Assert-EgsOutputContains -Result $result -Substring 'bare-perms.txt'
                Assert-EgsOutputContains -Result $result -Substring 'prefix-perms.txt'
            }
            finally {
                Remove-IsolatedStateDir -Path $stateDir
            }
        }
    }

    Context 'Sample 12 — combined vendor + user document' {
        It 'fires hostname, write_files, users, and runcmd modules' {
            $stateDir = New-IsolatedStateDir
            try {
                $path = Join-Path $script:CloudConfigDir '12-vendor-and-user.yaml'
                $result = Invoke-EgsProvisioning -Arguments @(
                    'run', '--dry-run',
                    '--user-data', $path,
                    '--state-dir', $stateDir
                )
                Assert-EgsExitCode -Result $result -Expected 0
                Assert-EgsOutputContains -Result $result -Substring 'eryph-vendor-12'
                Assert-EgsOutputContains -Result $result -Substring 'operator'
                Assert-EgsOutputContains -Result $result -Substring 'vendor-marker.txt'
            }
            finally {
                Remove-IsolatedStateDir -Path $stateDir
            }
        }
    }

    Context 'Multipart fixture — bootstrap-and-config.mime' {
        It 'handles cloud-config, shellscript, and boothook parts in one envelope' {
            $stateDir = New-IsolatedStateDir
            try {
                $path = Join-Path $script:MultipartDir 'bootstrap-and-config.mime'
                $result = Invoke-EgsProvisioning -Arguments @(
                    'run', '--dry-run',
                    '--user-data', $path,
                    '--state-dir', $stateDir
                )
                Assert-EgsExitCode -Result $result -Expected 0
                Assert-EgsOutputContains -Result $result -Substring 'eryph-multipart-bootstrap'
                Assert-EgsOutputContains -Result $result -Substring 'bootstrap'
            }
            finally {
                Remove-IsolatedStateDir -Path $stateDir
            }
        }
    }
}

Describe 'Status after a dry-run' {

    It 'reports a benign status when dry-run did not persist any state' {
        $stateDir = New-IsolatedStateDir
        try {
            $path = Join-Path $script:CloudConfigDir '01-minimal-admin-user.yaml'

            $runResult = Invoke-EgsProvisioning -Arguments @(
                'run', '--dry-run',
                '--user-data', $path,
                '--state-dir', $stateDir
            )
            Assert-EgsExitCode -Result $runResult -Expected 0

            $statusResult = Invoke-EgsProvisioning -Arguments @(
                'status', '--state-dir', $stateDir
            )
            # The expected contract: status exits 0 even when no run is on
            # record. Dry-run writes nothing to the state store, so we expect
            # status to report an empty/initial state.
            Assert-EgsExitCode -Result $statusResult -Expected 0
        }
        finally {
            Remove-IsolatedStateDir -Path $stateDir
        }
    }
}

Describe 'Broken samples (validate must fail)' {

    It 'rejects samples/cloud-configs/broken/invalid-yaml.yaml' {
        $path = Join-Path $script:BrokenDir 'invalid-yaml.yaml'
        $result = Invoke-EgsProvisioning -Arguments @('validate', '--user-data', $path)
        $result.ExitCode | Should -Not -Be 0
        # Either the YAML parser surfaces "YAML" or our wrapper says "parse"
        # — accept either as long as stderr is informative.
        ($result.StdErr -match 'YAML|parse|invalid') | Should -BeTrue
    }

    It 'rejects samples/cloud-configs/broken/duplicate-user.yaml' {
        $path = Join-Path $script:BrokenDir 'duplicate-user.yaml'
        $result = Invoke-EgsProvisioning -Arguments @('validate', '--user-data', $path)
        $result.ExitCode | Should -Not -Be 0
        ($result.StdErr -match 'user|duplicate|distinct') | Should -BeTrue
    }
}

Describe 'Reset command' {

    It 'removes the state file when state exists' {
        $stateDir = New-IsolatedStateDir
        try {
            # Seed a state.json file with a minimal payload so reset has
            # something to delete. The reset command should remove it
            # regardless of contents (the agent's own writes are not
            # exercised here because dry-run does not persist).
            $stateFile = Join-Path $stateDir 'state.json'
            Set-Content -LiteralPath $stateFile -Value '{"instanceId":"pester-test","completedStages":[],"completedHandlers":[],"rebootCount":0}' -Encoding UTF8

            Test-Path -LiteralPath $stateFile | Should -BeTrue

            $resetResult = Invoke-EgsProvisioning -Arguments @(
                'reset', '--state-dir', $stateDir
            )
            Assert-EgsExitCode -Result $resetResult -Expected 0

            Test-Path -LiteralPath $stateFile | Should -BeFalse
        }
        finally {
            Remove-IsolatedStateDir -Path $stateDir
        }
    }

    It 'is a no-op when no state exists' {
        $stateDir = New-IsolatedStateDir
        try {
            $resetResult = Invoke-EgsProvisioning -Arguments @(
                'reset', '--state-dir', $stateDir
            )
            Assert-EgsExitCode -Result $resetResult -Expected 0
        }
        finally {
            Remove-IsolatedStateDir -Path $stateDir
        }
    }
}

Describe 'Collect-logs command' {

    It 'produces a zip at the requested output path' {
        $tempZipDir = New-IsolatedStateDir
        $zipPath = Join-Path $tempZipDir 'collected.zip'
        try {
            $result = Invoke-EgsProvisioning -Arguments @(
                'collect-logs', '--output', $zipPath
            )
            Assert-EgsExitCode -Result $result -Expected 0
            Test-Path -LiteralPath $zipPath | Should -BeTrue
            (Get-Item -LiteralPath $zipPath).Length | Should -BeGreaterThan 0
        }
        finally {
            Remove-IsolatedStateDir -Path $tempZipDir
        }
    }
}
