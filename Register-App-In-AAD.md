## Register the application for AzureCP in your Azure Active Directory tenant

AzureCP needs its own application in your Azure AD tenant, with permissions "Group.Read.All" and "User.Read.All".  
This page shows you how to create it, using either the Azure portal or az cli.

### Create the app registration using the Azure portal

- Sign-in to your [Azure Active Directory tenant](https://aad.portal.azure.com/).
- Go to "App Registrations" > "New registration" > Type the following information:
    > Name: AzureCP  
    > Supported account types: "Accounts in this organizational directory only (Single tenant)"
- Click on "Register"
    > **Note:** Copy the "Application (client) ID": it is required by AzureCP to add a tenant.
- Click on "API permissions"
    - Remove the default permission.
    - Add a permission > Select "Microsoft Graph" > "Application permissions". Here you can add "Group.Read.All" and "User.Read.All"
    - Click on "Grant admin consent for TenantName" > Yes
    > **Note:** "After this operation, you should have only permissions "Group.Read.All" and "User.Read.All", of type "Application", with status "Granted".
- Click on "Certificates & secrets": AzureCP supports both a certificate or a client secret, choose either option depending on your needs.

### Create the app registration using az cli

Run this script to create the application, set the permissions, and return its client ID and client secret:

```shell
# Sign-in to Azure AD tenant. Use --allow-no-subscriptions only if it doesn't have a subscription
az login --allow-no-subscriptions

# Create the app for AzureCP
appName="AzureCP"
az ad app create --display-name "$appName" --key-type Password --credential-description 'client secret'
appId=$(az ad app list --display-name "$appName" --query [].appId -o tsv)
# Add API permission Group.Read.All to application (not delegated)
az ad app permission add --id $appId --api 00000003-0000-0000-c000-000000000000 --api-permissions 5b567255-7703-4780-807c-7be8301ae99b=Role
# Add API permission User.Read.All to application (not delegated)
az ad app permission add --id $appId --api 00000003-0000-0000-c000-000000000000 --api-permissions df021288-bdef-4463-88db-98f22de89214=Role
# Set a client secret generated by Azure and retrieve its value
appSecret=$(az ad app credential reset --id $appId -o tsv | cut -f3)
# Wait for 5 seconds before granting admin consent to avoid BadRequest error
sleep 5
az ad app permission admin-consent --id $appId
echo "Application $appName was created successfully with client id $appId and client secret $appSecret"
```