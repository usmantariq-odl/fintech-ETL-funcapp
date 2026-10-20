param(
    [Parameter(Mandatory = $true)][string]$FunctionAppName,
    [string]$FunctionResourceGroup = 'RG-fintech-prod-sea',
    [string]$SubscriptionId = '43e8904e-4836-4504-8c18-3e88d5fa6b78',
    [string]$FunctionSubscriptionId = $SubscriptionId,
    [string]$StorageResourceGroup = 'RG-fintech-prod-sea',
    [string]$StorageAccount = 'fintechsftpdata',
    [switch]$RoleSetupOnly,
    [string]$ExistingPrincipalId
)
$ErrorActionPreference = 'Stop'
function Assert-AzSuccess { if ($LASTEXITCODE -ne 0) { throw 'Azure CLI command failed. See its error above.' } }

# Opt-in deployment preparation: no code deployment, no ETL run and no local-user creation.
# Creates a narrowly scoped role and assigns it to this Function App's system identity.
$storageScope = "/subscriptions/$SubscriptionId/resourceGroups/$StorageResourceGroup/providers/Microsoft.Storage/storageAccounts/$StorageAccount"
$roleName = "Fintech SFTP User Provisioner $StorageAccount"
if ($RoleSetupOnly) {
    $parsedPrincipalId = [guid]::Empty
    if (!$ExistingPrincipalId -or ![guid]::TryParse($ExistingPrincipalId, [ref]$parsedPrincipalId)) {
        throw '-RoleSetupOnly requires a valid -ExistingPrincipalId for the already configured Function App identity.'
    }
    $principalId = $ExistingPrincipalId
}
else {
    $identityJson = az functionapp identity assign --name $FunctionAppName --resource-group $FunctionResourceGroup --subscription $FunctionSubscriptionId --output json --only-show-errors
    Assert-AzSuccess
    $principalId = ($identityJson | ConvertFrom-Json).principalId
}
if (!$principalId) { throw 'Azure did not return the Function App identity.' }
$roleJson = az role definition list --name $roleName --scope $storageScope --subscription $SubscriptionId --output json --only-show-errors
Assert-AzSuccess
$roles = @($roleJson | ConvertFrom-Json)
if ($roles.Count -eq 0) {
    $definition = @{
        Name = $roleName; IsCustom = $true
        Description = 'Manage SFTP local users for site onboarding; no storage keys or blob data access.'
        Actions = @(
            'Microsoft.Storage/storageAccounts/localusers/read',
            'Microsoft.Storage/storageAccounts/localusers/write',
            'Microsoft.Storage/storageAccounts/localUsers/delete',
            'Microsoft.Storage/storageAccounts/localusers/regeneratePassword/action'
        )
        NotActions = @(); DataActions = @(); NotDataActions = @(); AssignableScopes = @("/subscriptions/$SubscriptionId/resourceGroups/$StorageResourceGroup")
    }
    $temporaryRole = Join-Path ([System.IO.Path]::GetTempPath()) ("fintech-sftp-role-" + [guid]::NewGuid().ToString('N') + '.json')
    try {
        $definition | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporaryRole -Encoding utf8
        $createdRole = az role definition create --role-definition $temporaryRole --subscription $SubscriptionId --output json --only-show-errors
        Assert-AzSuccess
        $roleId = ($createdRole | ConvertFrom-Json).id
    }
    finally { Remove-Item -LiteralPath $temporaryRole -ErrorAction SilentlyContinue }
}
else { $roleId = $roles[0].id }

az role assignment create --assignee-object-id $principalId --assignee-principal-type ServicePrincipal --role $roleId --scope $storageScope --subscription $SubscriptionId --output none --only-show-errors
Assert-AzSuccess
if (!$RoleSetupOnly) {
    az functionapp config appsettings set --name $FunctionAppName --resource-group $FunctionResourceGroup --subscription $FunctionSubscriptionId --settings "SftpProvisioningSubscriptionId=$SubscriptionId" "SftpProvisioningResourceGroup=$StorageResourceGroup" "SftpProvisioningStorageAccount=$StorageAccount" --output none --only-show-errors
    Assert-AzSuccess
    Write-Output 'Managed identity, scoped provisioning role and three nonsecret application settings configured. Allow time for RBAC propagation before testing.'
}
else {
    Write-Output 'Scoped storage local-user role assigned to the supplied Function App identity. No Function App settings were changed. Allow time for RBAC propagation.'
}
