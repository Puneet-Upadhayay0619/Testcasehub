# Rotates the TestCaseHub CI API key: revokes every currently-active key, creates a fresh one,
# and prints ONLY the new key at the end. Passwords are entered via a masked prompt (dots, not
# plain text) so they never appear as visible text in your terminal or in a screenshot.
#
# Run it from anywhere:  .\rotate-api-key.ps1

$base = "https://testcasehub.onrender.com"

$adminEmail = Read-Host "Admin email"
$securePassword = Read-Host "Admin password" -AsSecureString
$adminPassword = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
    [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
)

$loginBody = @{ email = $adminEmail; password = $adminPassword } | ConvertTo-Json
$login = Invoke-RestMethod -Uri "$base/api/auth/login" -Method Post -ContentType "application/json" -Body $loginBody
$headers = @{ Authorization = "Bearer $($login.token)" }

Write-Host "Logged in OK. Looking up existing API keys..."
$keys = Invoke-RestMethod -Uri "$base/api/apikeys" -Headers $headers
$active = $keys | Where-Object { -not $_.revoked }

foreach ($k in $active) {
    Invoke-RestMethod -Uri "$base/api/apikeys/$($k.id)/revoke" -Method Post -Headers $headers | Out-Null
    Write-Host "Revoked old key: $($k.name) (id $($k.id))"
}

$createBody = @{ name = "CI Pipeline"; scope = "ReportResults" } | ConvertTo-Json
$new = Invoke-RestMethod -Uri "$base/api/apikeys" -Method Post -Headers $headers -ContentType "application/json" -Body $createBody

Write-Host ""
Write-Host "====================================================================="
Write-Host "NEW TCH_API_KEY = $($new.rawKey)"
Write-Host "Save this now -- it will not be shown again."
Write-Host "====================================================================="
