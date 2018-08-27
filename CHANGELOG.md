# Change log for AzureCP

## AzureCP v13 enhancements & bug-fixes

* Guest users are now fully supported. AzureCP will use the Mail property to search Guest accounts and create their permissions in SharePoint
* The identity claim type set in the SPTrustedIdentityTokenIssuer is now automatically detected and associated with the property UserPrincipalName
* Fixed no result returned under high load, caused by a thread safety issue where the same filter was used in all threads regardless of the actual input
* Improved validation of changes made to ClaimTypes collection
* Added method ClaimTypeConfigCollection.GetByClaimType()
* Implemented unit tests
* Explicitely encode HTML messages shown in admin pages and renderred from server side code to comply with tools scanning code to detect security vulnerabilities
* Fixed display text of groups that were not using the expected format "(GROUP) groupname"
* Deactivating farm-scoped feature "AzureCP" removes the claims provider from the farm, but it does not delete its configuration anymore. Configuration is now deleted when feature is uninstalled (typically when retracting the solution)
* Added user identifier properties in global configuration page

## AzureCP v12 enhancements & bug-fixes - Published in June 7, 2018

* Improved: AzureCP now uses the unified [Microsoft Graph API](https://developer.microsoft.com/en-us/graph/) instead of the old [Azure AD Graph API](https://docs.microsoft.com/en-us/azure/active-directory/develop/active-directory-graph-api).
* New: AzureCP can be entirely configured with PowerShell, including claim types configuration
* Updated: AzureCP administration pages are now created as User Controls and are reusable by developers.
* Improved: AzureCP claims mapping page is easier to understand
* Updated: Logging is more relevant and generates less messages.
* New: Timeout of connection is 4 secs and can be customized
* Improved: When multiple tenants are set, they are queried in parallel
* Updated: Tenant ID is no longer needed to register a tenant
* Improved: Nested groups are now supported, if group permissions are created using the ID of groups (new default setting).
* **Beaking change**: By default, AzureCP now creates group permissions using the Id of the group instead of its name (group IDs are unique, not names). There are 2 ways to deal with this: Modify group claim type configuration to reuse the name, or migrate existing groups permissions to set their values with their group ID
* **Beaking change**: Due to the amount of changes in this area, the claim types configuration will be reset if you update from an earlier version.
* Many bug fixes and optimizations
