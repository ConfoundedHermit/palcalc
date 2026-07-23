param(
    [Parameter(Mandatory = $true)]
    [string]$RepositoryRoot
)

$resourceFiles = Get-ChildItem -LiteralPath $RepositoryRoot -Recurse -Filter *.resx -File
$duplicates = foreach ($resourceFile in $resourceFiles) {
    [xml]$resourceDocument = Get-Content -LiteralPath $resourceFile.FullName -Raw
    $resourceDocument.root.data |
        Group-Object -Property name |
        Where-Object Count -gt 1 |
        ForEach-Object {
            "$($resourceFile.FullName): duplicate resource name '$($_.Name)'"
        }
}

if ($duplicates) {
    $duplicates | ForEach-Object { Write-Error $_ }
    exit 1
}
