## How to remove AzureCP

### Step 1: Reset property ClaimProviderName in the SPTrustedIdentityTokenIssuer

Unfortunately, the only supported way to reset property ClaimProviderName is to remove and recreate the SPTrustedIdentityTokenIssuer object, which requires to remove the trust from all the web apps where it is used.

Alternatively, this property can be reset using .NET reflection, but this is not supported and you do this at your own risks:

```powershell
# Set private member m_ClaimProviderName to null. Note that using .NET reflection on SharePoint objects is not supported and you do this at your own risks
$trust = Get-SPTrustedIdentityTokenIssuer "SPTRUST NAME"
$trust.GetType().GetField("m_ClaimProviderName", "NonPublic, Instance").SetValue($trust, $null)
$trust.Update()
```

### Step 2: Uninstall AzureCP

Randomly, SharePoint doesn’t uninstall the solution correctly: it removes the assembly too early and fails to call the feature receiver... When this happens, the claims provider is not removed and that causes issues when you re-install it.

> **Important**: Start a **new PowerShell console** to ensure the use of up to date persisted objects, which avoids concurrency update errors.  

```powershell
Disable-SPFeature -identity "AzureCP"
Uninstall-SPSolution -Identity "AzureCP.wsp"
# Wait for the timer job to complete before running Remove-SPSolution
Remove-SPSolution -Identity "AzureCP.wsp"
```
