@{
    # PowerShell module manifest for NVMeDriverPatcher wrapper.
    # Publishes thin PS cmdlets around the CLI exe so sysadmins can pipeline patch ops
    # (Get-NvmePatchStatus | Where Applied | ...) without manually parsing CLI output.
    RootModule        = 'NVMeDriverPatcher.psm1'
    ModuleVersion     = '4.6.0'
    GUID              = 'A8C5D9F1-7B24-4E83-9D2A-1C6E5F9A2B38'
    Author            = 'SysAdminDoc'
    CompanyName       = 'SysAdminDoc'
    Copyright         = '(c) SysAdminDoc. MIT licensed.'
    Description       = 'PowerShell wrapper around the NVMe Driver Patcher CLI. Exposes typed cmdlets over the native CLI subcommands for fleet automation.'
    PowerShellVersion = '5.1'

    FunctionsToExport = @(
        'Get-NvmePatchStatus',
        'Invoke-NvmePatchApply',
        'Invoke-NvmePatchRemove',
        'Get-NvmeWatchdogReport',
        'Get-NvmeControllerAudit',
        'Invoke-NvmeDryRun',
        'Export-NvmeDiagnostics',
        'Export-NvmeDashboard'
    )
    CmdletsToExport   = @()
    AliasesToExport   = @()

    PrivateData = @{
        PSData = @{
            Tags        = @('NVMe','Storage','Windows11','Performance','SysAdmin')
            LicenseUri  = 'https://github.com/SysAdminDoc/win11-nvme-driver-patcher/blob/main/LICENSE'
            ProjectUri  = 'https://github.com/SysAdminDoc/win11-nvme-driver-patcher'
            ReleaseNotes = 'See CHANGELOG.md in the project repo.'
        }
    }
}
