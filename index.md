This claims provider for SharePoint 2013 and 2016 leverages [Azure AD Graph Client Library](http://www.nuget.org/packages/Microsoft.Azure.ActiveDirectory.GraphClient/) to query Azure Active Directory from the people picker. It also gets the group membership of Azure users, so that permissions can be granted on Azure groups.

![People picker with AzureCP](https://github.com/Yvand/AzureCP/raw/gh-pages/assets/people%20picker%20AzureCP_2.png)

[Check this article](https://docs.microsoft.com/en-us/office365/enterprise/using-azure-ad-for-sharepoint-server-authentication) to find out how to configure SharePoint 2013 / 2016 to trust Azure AD.

## Features

- Easy to configure with administration pages added in Central administration > Security.
- Connect to multiple Azure AD tenants in parallel (multi-threaded queries).
- Populate properties upon permission creation, e.g. email to allow email invitations to be sent.
- Supports rehydration for provider hosted apps. 
- Implements SharePoint logging infrastructure and logs messages in Area/Product "AzureCP". 
- Augment Azure AD users with their group membership.

## Customization capabilities

- Customize list of claim types, and their mapping with Azure AD users or groups. 
- Enable/disable augmentation.
- Enable/disable Azure AD lookup (to keep people picker returning results even if connectivity to Azure tenant is lost).
- Customize display of permissions. 
- Set a keyword to bypass Azure AD lookup. E.g. input "extuser:partner@contoso.com" directly creates permission "partner@contoso.com" on claim type set for this.
- Developers can easily customize it by inheriting AzureCP class and override many methods.
