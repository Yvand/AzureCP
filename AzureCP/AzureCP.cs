﻿using Microsoft.Graph;
using Microsoft.SharePoint.Administration;
using Microsoft.SharePoint.Administration.Claims;
using Microsoft.SharePoint.Utilities;
using Microsoft.SharePoint.WebControls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using static azurecp.ClaimsProviderLogging;
using WIF4_5 = System.Security.Claims;

/*
 * DO NOT directly edit AzureCP class. It is designed to be inherited to customize it as desired.
 * Please download "AzureCP for Developers.zip" on https://github.com/Yvand/AzureCP to find examples and guidance.
 * */

namespace azurecp
{
    /// <summary>
    /// Provides search and resolution against Azure Active Directory
    /// Visit https://github.com/Yvand/AzureCP for documentation and updates.
    /// Please report any bug to https://github.com/Yvand/AzureCP.
    /// Author: Yvan Duhamel
    /// </summary>
    public class AzureCP : SPClaimProvider
    {
        public const string _ProviderInternalName = "AzureCP";
        public virtual string ProviderInternalName => "AzureCP";
        public virtual string PersistedObjectName => ClaimsProviderConstants.AZURECPCONFIG_NAME;

        private object Lock_Init = new object();
        private ReaderWriterLockSlim Lock_Config = new ReaderWriterLockSlim();
        private long CurrentConfigurationVersion = 0;

        /// <summary>
        /// Contains configuration currently used by claims provider
        /// </summary>
        public IAzureCPConfiguration CurrentConfiguration;

        /// <summary>
        /// SPTrust associated with the claims provider
        /// </summary>
        protected SPTrustedLoginProvider SPTrust;

        /// <summary>
        /// ClaimTypeConfig mapped to the identity claim in the SPTrustedIdentityTokenIssuer
        /// </summary>
        IdentityClaimTypeConfig IdentityClaimTypeConfig;

        /// <summary>
        /// Group ClaimTypeConfig used to set the claim type for other group ClaimTypeConfig that have UseMainClaimTypeOfDirectoryObject set to true
        /// </summary>
        ClaimTypeConfig MainGroupClaimTypeConfig;

        /// <summary>
        /// Processed list to use. It is guarranted to never contain an empty ClaimType
        /// </summary>
        public List<ClaimTypeConfig> ProcessedClaimTypesList;
        protected IEnumerable<ClaimTypeConfig> MetadataConfig;
        protected virtual string PickerEntityDisplayText { get { return "({0}) {1}"; } }
        protected virtual string PickerEntityOnMouseOver { get { return "{0}={1}"; } }
        protected string IssuerName => SPOriginalIssuers.Format(SPOriginalIssuerType.TrustedProvider, SPTrust.Name);

        public AzureCP(string displayName) : base(displayName) { }

        /// <summary>
        /// Initializes claim provider. This method is reserved for internal use and is not intended to be called from external code or changed
        /// </summary>
        public bool Initialize(Uri context, string[] entityTypes)
        {
            // Ensures thread safety to initialize class variables
            lock (Lock_Init)
            {
                // 1ST PART: GET CONFIGURATION OBJECT
                IAzureCPConfiguration globalConfiguration = null;
                bool refreshConfig = false;
                bool success = true;
                try
                {
                    if (SPTrust == null)
                    {
                        SPTrust = GetSPTrustAssociatedWithCP(ProviderInternalName);
                        if (SPTrust == null) return false;
                    }
                    if (!CheckIfShouldProcessInput(context)) return false;

                    globalConfiguration = GetConfiguration(context, entityTypes, PersistedObjectName);
                    if (globalConfiguration == null)
                    {
                        ClaimsProviderLogging.Log($"[{ProviderInternalName}] Configuration '{PersistedObjectName}' was not found in configuration database, use default configuration instead. Visit AzureCP admin pages in central administration to create it.",
                            TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Core);
                        // Return default configuration and set refreshConfig to true to give a chance to deprecated method SetCustomConfiguration() to set AzureTenants list
                        globalConfiguration = AzureCPConfig.ReturnDefaultConfiguration(SPTrust.Name);
                        refreshConfig = true;
                    }
                    else
                    {
                        ((AzureCPConfig)globalConfiguration).CheckAndCleanConfiguration(SPTrust.Name);
                    }

                    if (globalConfiguration.ClaimTypes == null || globalConfiguration.ClaimTypes.Count == 0)
                    {
                        ClaimsProviderLogging.Log($"[{ProviderInternalName}] Configuration '{PersistedObjectName}' was found but collection ClaimTypes is null or empty. Visit AzureCP admin pages in central administration to create it.",
                            TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Core);
                        // Cannot continue 
                        success = false;
                    }

                    if (success)
                    {
                        if (this.CurrentConfigurationVersion == ((SPPersistedObject)globalConfiguration).Version)
                        {
                            ClaimsProviderLogging.Log($"[{ProviderInternalName}] Configuration '{PersistedObjectName}' was found, version {((SPPersistedObject)globalConfiguration).Version.ToString()}",
                                TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Core);
                        }
                        else
                        {
                            refreshConfig = true;
                            this.CurrentConfigurationVersion = ((SPPersistedObject)globalConfiguration).Version;
                            ClaimsProviderLogging.Log($"[{ProviderInternalName}] Configuration '{PersistedObjectName}' changed to version {((SPPersistedObject)globalConfiguration).Version.ToString()}, refreshing local copy",
                                TraceSeverity.Medium, EventSeverity.Information, TraceCategory.Core);
                        }
                    }

                    // ProcessedClaimTypesList can be null if:
                    // - 1st initialization
                    // - Initialized before but it failed. If so, try again to refresh config
                    if (this.ProcessedClaimTypesList == null) refreshConfig = true;
                }
                catch (Exception ex)
                {
                    success = false;
                    ClaimsProviderLogging.LogException(ProviderInternalName, "in Initialize", TraceCategory.Core, ex);
                }

                if (!success) return success;
                if (!refreshConfig) return success;

                // 2ND PART: APPLY CONFIGURATION
                // Configuration needs to be refreshed, lock current thread in write mode
                Lock_Config.EnterWriteLock();
                try
                {
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Refreshing local copy of configuration '{PersistedObjectName}'",
                        TraceSeverity.Verbose, EventSeverity.Information, TraceCategory.Core);

                    // Create local persisted object that will never be saved in config DB, it's just a local copy
                    // This copy is unique to current object instance to avoid thread safety issues
                    this.CurrentConfiguration = ((AzureCPConfig)globalConfiguration).CopyPersistedProperties();

                    SetCustomConfiguration(context, entityTypes);
                    if (this.CurrentConfiguration.ClaimTypes == null)
                    {
                        ClaimsProviderLogging.Log($"[{ProviderInternalName}] List if claim types was set to null in method SetCustomConfiguration for configuration '{PersistedObjectName}'.", TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Core);
                        return false;
                    }

                    if (this.CurrentConfiguration.AzureTenants == null || this.CurrentConfiguration.AzureTenants.Count == 0)
                    {
                        ClaimsProviderLogging.Log($"[{ProviderInternalName}] There is no Azure tenant registered in the configuration '{PersistedObjectName}'. Visit AzureCP in central administration to add it, or override method GetConfiguration.", TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Core);
                        return false;
                    }

                    // Set properties AuthenticationProvider and GraphService
                    foreach (var tenant in this.CurrentConfiguration.AzureTenants)
                    {
                        tenant.SetAzureADContext(ProviderInternalName, this.CurrentConfiguration.Timeout);
                    }
                    success = this.InitializeClaimTypeConfigList(this.CurrentConfiguration.ClaimTypes);
                }
                catch (Exception ex)
                {
                    success = false;
                    ClaimsProviderLogging.LogException(ProviderInternalName, "in Initialize, while refreshing configuration", TraceCategory.Core, ex);
                }
                finally
                {
                    Lock_Config.ExitWriteLock();
                }
                return success;
            }
        }

        /// <summary>
        /// Initializes claim provider. This method is reserved for internal use and is not intended to be called from external code or changed
        /// </summary>
        /// <param name="nonProcessedClaimTypes"></param>
        /// <returns></returns>
        private bool InitializeClaimTypeConfigList(ClaimTypeConfigCollection nonProcessedClaimTypes)
        {
            bool success = true;
            try
            {
                bool identityClaimTypeFound = false;
                bool groupClaimTypeFound = false;
                List<ClaimTypeConfig> claimTypesSetInTrust = new List<ClaimTypeConfig>();
                // Foreach MappedClaimType in the SPTrustedLoginProvider
                foreach (SPTrustedClaimTypeInformation claimTypeInformation in SPTrust.ClaimTypeInformation)
                {
                    // Search if current claim type in trust exists in ClaimTypeConfigCollection
                    ClaimTypeConfig claimTypeConfig = nonProcessedClaimTypes.FirstOrDefault(x =>
                        String.Equals(x.ClaimType, claimTypeInformation.MappedClaimType, StringComparison.InvariantCultureIgnoreCase) &&
                        !x.UseMainClaimTypeOfDirectoryObject &&
                        x.DirectoryObjectProperty != AzureADObjectProperty.NotSet);

                    if (claimTypeConfig == null) continue;
                    claimTypeConfig.ClaimTypeDisplayName = claimTypeInformation.DisplayName;
                    claimTypesSetInTrust.Add(claimTypeConfig);
                    if (String.Equals(SPTrust.IdentityClaimTypeInformation.MappedClaimType, claimTypeConfig.ClaimType, StringComparison.InvariantCultureIgnoreCase))
                    {
                        // Identity claim type found, set IdentityClaimTypeConfig property
                        identityClaimTypeFound = true;
                        //IdentityClaimTypeConfig = claimTypeConfig as IdentityClaimTypeConfig;
                        IdentityClaimTypeConfig = IdentityClaimTypeConfig.ConvertClaimTypeConfig(claimTypeConfig);
                    }
                    else if (!groupClaimTypeFound && claimTypeConfig.EntityType == DirectoryObjectType.Group)
                    {
                        groupClaimTypeFound = true;
                        MainGroupClaimTypeConfig = claimTypeConfig;
                    }
                }

                if (!identityClaimTypeFound)
                {
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Cannot continue because identity claim type '{SPTrust.IdentityClaimTypeInformation.MappedClaimType}' set in the SPTrustedIdentityTokenIssuer '{SPTrust.Name}' is missing in the ClaimTypeConfig list.", TraceSeverity.Unexpected, EventSeverity.ErrorCritical, TraceCategory.Core);
                    return false;
                }

                // Check if there are additional properties to use in queries (UseMainClaimTypeOfDirectoryObject set to true)
                List<ClaimTypeConfig> additionalClaimTypeConfigList = new List<ClaimTypeConfig>();
                foreach (ClaimTypeConfig claimTypeConfig in nonProcessedClaimTypes.Where(x => x.UseMainClaimTypeOfDirectoryObject))
                {
                    if (claimTypeConfig.EntityType == DirectoryObjectType.User)
                    {
                        claimTypeConfig.ClaimType = IdentityClaimTypeConfig.ClaimType;
                        claimTypeConfig.DirectoryObjectPropertyToShowAsDisplayText = IdentityClaimTypeConfig.DirectoryObjectPropertyToShowAsDisplayText;
                    }
                    else
                    {
                        // If not a user, it must be a group
                        if (MainGroupClaimTypeConfig == null) continue;
                        claimTypeConfig.ClaimType = MainGroupClaimTypeConfig.ClaimType;
                        claimTypeConfig.DirectoryObjectPropertyToShowAsDisplayText = MainGroupClaimTypeConfig.DirectoryObjectPropertyToShowAsDisplayText;
                    }
                    additionalClaimTypeConfigList.Add(claimTypeConfig);
                }

                this.ProcessedClaimTypesList = new List<ClaimTypeConfig>(claimTypesSetInTrust.Count + additionalClaimTypeConfigList.Count);
                this.ProcessedClaimTypesList.AddRange(claimTypesSetInTrust);
                this.ProcessedClaimTypesList.AddRange(additionalClaimTypeConfigList);

                // Get all PickerEntity metadata with a DirectoryObjectProperty set
                this.MetadataConfig = nonProcessedClaimTypes.Where(x =>
                    !String.IsNullOrEmpty(x.EntityDataKey) &&
                    x.DirectoryObjectProperty != AzureADObjectProperty.NotSet);
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in InitializeClaimTypeConfigList", TraceCategory.Core, ex);
                success = false;
            }
            return success;
        }

        /// <summary>
        /// Override this method to return a custom configuration of AzureCP.
        /// DO NOT Override this method if you use a custom persisted object to store configuration in config DB.
        /// To use a custom persisted object, override property PersistedObjectName and set its name
        /// </summary>
        /// <returns></returns>
        protected virtual IAzureCPConfiguration GetConfiguration(Uri context, string[] entityTypes, string persistedObjectName)
        {
            return AzureCPConfig.GetConfiguration(persistedObjectName);
        }

        /// <summary>
        /// [Deprecated] Override this method to customize the configuration of AzureCP. Please override GetConfiguration instead.
        /// </summary> 
        /// <param name="context">The context, as a URI</param>
        /// <param name="entityTypes">The EntityType entity types set to scope the search to</param>
        [Obsolete("SetCustomConfiguration is deprecated, please override GetConfiguration instead.")]
        protected virtual void SetCustomConfiguration(Uri context, string[] entityTypes)
        {
        }

        /// <summary>
        /// Check if AzureCP should process input (and show results) based on current URL (context)
        /// </summary>
        /// <param name="context">The context, as a URI</param>
        /// <returns></returns>
        protected virtual bool CheckIfShouldProcessInput(Uri context)
        {
            if (context == null) return true;
            var webApp = SPWebApplication.Lookup(context);
            if (webApp == null) return false;
            if (webApp.IsAdministrationWebApplication) return true;

            // Not central admin web app, enable AzureCP only if current web app uses it
            // It is not possible to exclude zones where AzureCP is not used because:
            // Consider following scenario: default zone is WinClaims, intranet zone is Federated:
            // In intranet zone, when creating permission, AzureCP will be called 2 times. The 2nd time (in FillResolve (SPClaim)), the context will always be the URL of the default zone
            foreach (var zone in Enum.GetValues(typeof(SPUrlZone)))
            {
                SPIisSettings iisSettings = webApp.GetIisSettingsWithFallback((SPUrlZone)zone);
                if (!iisSettings.UseTrustedClaimsAuthenticationProvider)
                    continue;

                // Get the list of authentication providers associated with the zone
                foreach (SPAuthenticationProvider prov in iisSettings.ClaimsAuthenticationProviders)
                {
                    if (prov.GetType() == typeof(Microsoft.SharePoint.Administration.SPTrustedAuthenticationProvider))
                    {
                        // Check if the current SPTrustedAuthenticationProvider is associated with the claim provider
                        if (String.Equals(prov.ClaimProviderName, ProviderInternalName, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Get the first TrustedLoginProvider associated with current claim provider
        /// LIMITATION: The same claims provider (uniquely identified by its name) cannot be associated to multiple TrustedLoginProvider because at runtime there is no way to determine what TrustedLoginProvider is currently calling
        /// </summary>
        /// <param name="providerInternalName"></param>
        /// <returns></returns>
        public static SPTrustedLoginProvider GetSPTrustAssociatedWithCP(string providerInternalName)
        {
            var lp = SPSecurityTokenServiceManager.Local.TrustedLoginProviders.Where(x => String.Equals(x.ClaimProviderName, providerInternalName, StringComparison.OrdinalIgnoreCase));

            if (lp != null && lp.Count() == 1)
                return lp.First();

            if (lp != null && lp.Count() > 1)
                ClaimsProviderLogging.Log($"[{providerInternalName}] Cannot continue because '{providerInternalName}' is set with multiple SPTrustedIdentityTokenIssuer", TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Core);

            ClaimsProviderLogging.Log($"[{providerInternalName}] Cannot continue because '{providerInternalName}' is not set with any SPTrustedIdentityTokenIssuer.\r\nVisit {ClaimsProviderConstants.PUBLICSITEURL} for more information.", TraceSeverity.High, EventSeverity.Warning, TraceCategory.Core);
            return null;
        }

        /// <summary>
        /// Uses reflection to return the value of a public property for the given object
        /// </summary>
        /// <param name="directoryObject"></param>
        /// <param name="propertyName"></param>
        /// <returns>Null if property doesn't exist, String.Empty if property exists but has no value, actual value otherwise</returns>
        public static string GetPropertyValue(object directoryObject, string propertyName)
        {
            if (directoryObject == null) return null;
            PropertyInfo pi = directoryObject.GetType().GetProperty(propertyName);
            if (pi == null) return null;    // Property doesn't exist
            object propertyValue = pi.GetValue(directoryObject, null);
            return propertyValue == null ? String.Empty : propertyValue.ToString();
        }

        /// <summary>
        /// Create a SPClaim with property OriginalIssuer correctly set
        /// </summary>
        /// <param name="type">Claim type</param>
        /// <param name="value">Claim value</param>
        /// <param name="valueType">Claim value type</param>
        /// <returns>SPClaim object</returns>
        protected virtual new SPClaim CreateClaim(string type, string value, string valueType)
        {
            // SPClaimProvider.CreateClaim sets property OriginalIssuer to SPOriginalIssuerType.ClaimProvider, which is not correct
            //return CreateClaim(type, value, valueType);
            return new SPClaim(type, value, valueType, IssuerName);
        }

        protected virtual PickerEntity CreatePickerEntityHelper(AzureCPResult result)
        {
            PickerEntity entity = CreatePickerEntity();
            SPClaim claim;
            string permissionValue = result.PermissionValue;
            string permissionClaimType = result.ClaimTypeConfig.ClaimType;
            bool isMappedClaimTypeConfig = false;

            if (String.Equals(result.ClaimTypeConfig.ClaimType, IdentityClaimTypeConfig.ClaimType, StringComparison.InvariantCultureIgnoreCase)
                || result.ClaimTypeConfig.UseMainClaimTypeOfDirectoryObject)
            {
                isMappedClaimTypeConfig = true;
            }

            if (result.ClaimTypeConfig.UseMainClaimTypeOfDirectoryObject)
            {
                string claimValueType;
                if (result.ClaimTypeConfig.EntityType == DirectoryObjectType.User)
                {
                    permissionClaimType = IdentityClaimTypeConfig.ClaimType;
                    entity.EntityType = SPClaimEntityTypes.User;
                    claimValueType = IdentityClaimTypeConfig.ClaimValueType;
                }
                else
                {
                    permissionClaimType = MainGroupClaimTypeConfig.ClaimType;
                    entity.EntityType = ClaimsProviderConstants.GroupClaimEntityType;
                    claimValueType = MainGroupClaimTypeConfig.ClaimValueType;
                }
                permissionValue = FormatPermissionValue(permissionClaimType, permissionValue, isMappedClaimTypeConfig, result);
                claim = CreateClaim(
                    permissionClaimType,
                    permissionValue,
                    claimValueType);
            }
            else
            {
                permissionValue = FormatPermissionValue(permissionClaimType, permissionValue, isMappedClaimTypeConfig, result);
                claim = CreateClaim(
                    permissionClaimType,
                    permissionValue,
                    result.ClaimTypeConfig.ClaimValueType);
                entity.EntityType = result.ClaimTypeConfig.EntityType == DirectoryObjectType.User ? SPClaimEntityTypes.User : ClaimsProviderConstants.GroupClaimEntityType;
            }

            entity.Claim = claim;
            entity.IsResolved = true;
            //entity.EntityGroupName = "";
            entity.Description = String.Format(
                PickerEntityOnMouseOver,
                result.ClaimTypeConfig.DirectoryObjectProperty.ToString(),
                result.QueryMatchValue);

            int nbMetadata = 0;
            // Populate metadata of new PickerEntity
            foreach (ClaimTypeConfig ctConfig in MetadataConfig.Where(x => x.EntityType == result.ClaimTypeConfig.EntityType))
            {
                // if there is actally a value in the GraphObject, then it can be set
                string entityAttribValue = GetPropertyValue(result.UserOrGroupResult, ctConfig.DirectoryObjectProperty.ToString());
                if (!String.IsNullOrEmpty(entityAttribValue))
                {
                    entity.EntityData[ctConfig.EntityDataKey] = entityAttribValue;
                    nbMetadata++;
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Set metadata '{ctConfig.EntityDataKey}' of new entity to '{entityAttribValue}'", TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Claims_Picking);
                }
            }
            entity.DisplayText = FormatPermissionDisplayText(entity, isMappedClaimTypeConfig, result);
            ClaimsProviderLogging.Log($"[{ProviderInternalName}] Created entity: display text: '{entity.DisplayText}', value: '{entity.Claim.Value}', claim type: '{entity.Claim.ClaimType}', and filled with {nbMetadata.ToString()} metadata.", TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Claims_Picking);
            return entity;
        }

        /// <summary>
        /// Override this method to customize value of permission created
        /// </summary>
        /// <param name="claimType"></param>
        /// <param name="claimValue"></param>
        /// <param name="isIdentityClaimType"></param>
        /// <param name="result"></param>
        /// <returns></returns>
        protected virtual string FormatPermissionValue(string claimType, string claimValue, bool isIdentityClaimType, AzureCPResult result)
        {
            return claimValue;
        }

        /// <summary>
        /// Override this method to customize display text of permission created
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="isIdentityClaimType"></param>
        /// <param name="result"></param>
        /// <returns></returns>
        protected virtual string FormatPermissionDisplayText(PickerEntity entity, bool isIdentityClaimType, AzureCPResult result)
        {
            string entityDisplayText = this.CurrentConfiguration.EntityDisplayTextPrefix;
            if (result.ClaimTypeConfig.DirectoryObjectPropertyToShowAsDisplayText != AzureADObjectProperty.NotSet)
            {
                if (!isIdentityClaimType) entityDisplayText += "(" + result.ClaimTypeConfig.ClaimTypeDisplayName + ") ";

                string graphPropertyToDisplayValue = GetPropertyValue(result.UserOrGroupResult, result.ClaimTypeConfig.DirectoryObjectPropertyToShowAsDisplayText.ToString());
                if (!String.IsNullOrEmpty(graphPropertyToDisplayValue)) entityDisplayText += graphPropertyToDisplayValue;
                else entityDisplayText += result.PermissionValue;
            }
            else
            {
                if (isIdentityClaimType)
                {
                    entityDisplayText += result.QueryMatchValue;
                }
                else
                {
                    entityDisplayText += String.Format(
                        PickerEntityDisplayText,
                        result.ClaimTypeConfig.ClaimTypeDisplayName,
                        result.PermissionValue);
                }
            }
            return entityDisplayText;
        }

        protected virtual PickerEntity CreatePickerEntityForSpecificClaimType(string input, ClaimTypeConfig ctConfig, bool inputHasKeyword)
        {
            List<PickerEntity> entities = CreatePickerEntityForSpecificClaimTypes(
                input,
                new List<ClaimTypeConfig>()
                    {
                        ctConfig,
                    },
                inputHasKeyword);
            return entities == null ? null : entities.First();
        }

        protected virtual List<PickerEntity> CreatePickerEntityForSpecificClaimTypes(string input, List<ClaimTypeConfig> ctConfigs, bool inputHasKeyword)
        {
            List<PickerEntity> entities = new List<PickerEntity>();
            foreach (var ctConfig in ctConfigs)
            {
                SPClaim claim = CreateClaim(ctConfig.ClaimType, input, ctConfig.ClaimValueType);
                PickerEntity entity = CreatePickerEntity();
                entity.Claim = claim;
                entity.IsResolved = true;
                entity.EntityType = ctConfig.EntityType == DirectoryObjectType.User ? SPClaimEntityTypes.User : ClaimsProviderConstants.GroupClaimEntityType;
                //entity.EntityGroupName = "";
                entity.Description = String.Format(PickerEntityOnMouseOver, ctConfig.DirectoryObjectProperty.ToString(), input);

                if (!String.IsNullOrEmpty(ctConfig.EntityDataKey))
                {
                    entity.EntityData[ctConfig.EntityDataKey] = entity.Claim.Value;
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Added metadata '{ctConfig.EntityDataKey}' with value '{entity.EntityData[ctConfig.EntityDataKey]}' to new entity", TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Claims_Picking);
                }

                AzureCPResult result = new AzureCPResult(null);
                result.ClaimTypeConfig = ctConfig;
                result.PermissionValue = input;
                result.QueryMatchValue = input;
                bool isIdentityClaimType = String.Equals(claim.ClaimType, IdentityClaimTypeConfig.ClaimType, StringComparison.InvariantCultureIgnoreCase);
                entity.DisplayText = FormatPermissionDisplayText(entity, isIdentityClaimType, result);

                entities.Add(entity);
                ClaimsProviderLogging.Log($"[{ProviderInternalName}] Created entity: display text: '{entity.DisplayText}', value: '{entity.Claim.Value}', claim type: '{entity.Claim.ClaimType}'.", TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Claims_Picking);
            }
            return entities.Count > 0 ? entities : null;
        }

        /// <summary>
        /// Called when claims provider is added to the farm. At this point the persisted object is not created yet so we can't pass actual claim type list
        /// If assemblyBinding for Newtonsoft.Json was not correctly added on the server, this method will generate an assembly load exception during feature activation
        /// Also called every 1st query in people picker
        /// </summary>
        /// <param name="claimTypes"></param>
        protected override void FillClaimTypes(List<string> claimTypes)
        {
            if (claimTypes == null) return;
            try
            {
                this.Lock_Config.EnterReadLock();
                if (ProcessedClaimTypesList == null) return;
                foreach (var claimTypeSettings in ProcessedClaimTypesList)
                {
                    claimTypes.Add(claimTypeSettings.ClaimType);
                }
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in FillClaimTypes", TraceCategory.Core, ex);
            }
            finally
            {
                this.Lock_Config.ExitReadLock();
            }
        }

        protected override void FillClaimValueTypes(List<string> claimValueTypes)
        {
            claimValueTypes.Add(WIF4_5.ClaimValueTypes.String);
        }

        protected override void FillClaimsForEntity(Uri context, SPClaim entity, SPClaimProviderContext claimProviderContext, List<SPClaim> claims)
        {
            AugmentEntity(context, entity, claimProviderContext, claims);
        }

        protected override void FillClaimsForEntity(Uri context, SPClaim entity, List<SPClaim> claims)
        {
            AugmentEntity(context, entity, null, claims);
        }

        /// <summary>
        /// Perform augmentation of entity supplied
        /// </summary>
        /// <param name="context"></param>
        /// <param name="entity">entity to augment</param>
        /// <param name="claimProviderContext">Can be null</param>
        /// <param name="claims"></param>
        protected void AugmentEntity(Uri context, SPClaim entity, SPClaimProviderContext claimProviderContext, List<SPClaim> claims)
        {
            Stopwatch timer = new Stopwatch();
            timer.Start();
            SPClaim decodedEntity;
            if (SPClaimProviderManager.IsUserIdentifierClaim(entity))
                decodedEntity = SPClaimProviderManager.DecodeUserIdentifierClaim(entity);
            else
            {
                if (SPClaimProviderManager.IsEncodedClaim(entity.Value))
                    decodedEntity = SPClaimProviderManager.Local.DecodeClaim(entity.Value);
                else
                    decodedEntity = entity;
            }

            SPOriginalIssuerType loginType = SPOriginalIssuers.GetIssuerType(decodedEntity.OriginalIssuer);
            if (loginType != SPOriginalIssuerType.TrustedProvider && loginType != SPOriginalIssuerType.ClaimProvider)
            {
                ClaimsProviderLogging.Log($"[{ProviderInternalName}] Not trying to augment '{decodedEntity.Value}' because his OriginalIssuer is '{decodedEntity.OriginalIssuer}'.",
                    TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Augmentation);
                return;
            }

            if (!Initialize(context, null))
                return;

            this.Lock_Config.EnterReadLock();
            try
            {
                if (!this.CurrentConfiguration.EnableAugmentation) return;

                ClaimsProviderLogging.Log($"[{ProviderInternalName}] Starting augmentation for user '{decodedEntity.Value}'.", TraceSeverity.Verbose, EventSeverity.Information, TraceCategory.Augmentation);
                ClaimTypeConfig groupClaimTypeSettings = this.ProcessedClaimTypesList.FirstOrDefault(x => x.EntityType == DirectoryObjectType.Group);
                if (groupClaimTypeSettings == null)
                {
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] No claim type with EntityType 'Group' was found, please check claims mapping table.",
                        TraceSeverity.High, EventSeverity.Error, TraceCategory.Augmentation);
                    return;
                }

                OperationContext currentContext = new OperationContext(CurrentConfiguration, OperationType.Augmentation, ProcessedClaimTypesList, null, decodedEntity, context, null, null, Int32.MaxValue);
                Task<List<SPClaim>> resultsTask = GetGroupMembershipAsync(currentContext, groupClaimTypeSettings);
                resultsTask.Wait();
                List<SPClaim> groups = resultsTask.Result;
                timer.Stop();
                if (groups?.Count > 0)
                {
                    foreach (SPClaim group in groups)
                    {
                        claims.Add(group);
                        ClaimsProviderLogging.Log($"[{ProviderInternalName}] Added group '{group.Value}' to user '{currentContext.IncomingEntity.Value}'",
                            TraceSeverity.Verbose, EventSeverity.Information, TraceCategory.Augmentation);
                    }
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] User '{currentContext.IncomingEntity.Value}' was augmented with {groups.Count.ToString()} groups in {timer.ElapsedMilliseconds.ToString()} ms",
                        TraceSeverity.Medium, EventSeverity.Information, TraceCategory.Augmentation);
                }
                else
                {
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] No group found for user '{currentContext.IncomingEntity.Value}', search took {timer.ElapsedMilliseconds.ToString()} ms",
                        TraceSeverity.Medium, EventSeverity.Information, TraceCategory.Augmentation);
                }
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in AugmentEntity", TraceCategory.Augmentation, ex);
            }
            finally
            {
                this.Lock_Config.ExitReadLock();
            }
        }

        protected async Task<List<SPClaim>> GetGroupMembershipAsync(OperationContext currentContext, ClaimTypeConfig groupClaimTypeSettings)
        {
            List<SPClaim> groups = new List<SPClaim>();

            // Create a task for each tenant to query
            var tenantQueryTasks = this.CurrentConfiguration.AzureTenants.Select(async tenant =>
            {
                return await GetGroupMembershipFromAzureADAsync(currentContext, groupClaimTypeSettings, tenant).ConfigureAwait(false);
            });

            // Wait for all tasks to complete
            List<SPClaim>[] tenantResults = await Task.WhenAll(tenantQueryTasks).ConfigureAwait(false);

            // Process result returned by each tenant
            foreach (List<SPClaim> tenantResult in tenantResults)
            {
                if (tenantResult?.Count > 0)
                {
                    // The logic is that there will always be only 1 tenant returning groups, so as soon as 1 returned groups, foreach can stop
                    groups = tenantResult;
                    break;
                }
            }
            return groups;
        }

        protected async Task<List<SPClaim>> GetGroupMembershipFromAzureADAsync(OperationContext currentContext, ClaimTypeConfig groupClaimTypeConfig, AzureTenant tenant)
        {
            List<SPClaim> claims = new List<SPClaim>();
            IGraphServiceUsersCollectionPage userResult = await tenant.GraphService.Users.Request().Filter($"{currentContext.IncomingEntityClaimTypeConfig.DirectoryObjectProperty} eq '{currentContext.IncomingEntity.Value}'").GetAsync().ConfigureAwait(false);
            User user = userResult.FirstOrDefault();

            if (user == null)
            {
                // If user was not found, he might be a Guest user. Query to check this: /users?$filter=userType eq 'Guest' and mail eq 'guest@live.com'&$select=userPrincipalName, Id
                string guestFilter = HttpUtility.UrlEncode($"userType eq 'Guest' and mail eq '{currentContext.IncomingEntity.Value}'");
                userResult = await tenant.GraphService.Users.Request().Filter(guestFilter).Select(HttpUtility.UrlEncode("userPrincipalName, Id")).GetAsync().ConfigureAwait(false);
                user = userResult.FirstOrDefault();
                if (user == null) return claims;
            }

            if (groupClaimTypeConfig.DirectoryObjectProperty == AzureADObjectProperty.Id)
            {
                // POST to /v1.0/users/user@TENANT.onmicrosoft.com/microsoft.graph.getMemberGroups is the preferred way to return security groups as it includes nested groups
                // But it returns only the group IDs so it can be used only if groupClaimTypeConfig.DirectoryObjectProperty == AzureADObjectProperty.Id
                IDirectoryObjectGetMemberGroupsCollectionPage groupIDs = await tenant.GraphService.Users[user.Id].GetMemberGroups(true).Request().PostAsync().ConfigureAwait(false);
                bool morePages = groupIDs?.Count > 0;
                while (morePages)
                {
                    foreach (string groupID in groupIDs)
                    {
                        claims.Add(CreateClaim(groupClaimTypeConfig.ClaimType, groupID, groupClaimTypeConfig.ClaimValueType));
                    }
                    if (groupIDs.NextPageRequest != null) groupIDs = await groupIDs.NextPageRequest.PostAsync().ConfigureAwait(false);
                    else morePages = false;
                }

            }
            else
            {
                // Fallback to GET to /v1.0/users/user@TENANT.onmicrosoft.com/memberOf, which returns all group properties but does not return nested groups
                IUserMemberOfCollectionWithReferencesPage groups = await tenant.GraphService.Users[user.Id].MemberOf.Request().GetAsync().ConfigureAwait(false);
                bool morePages = groups?.Count > 0;
                while (morePages)
                {
                    foreach (Group group in groups.OfType<Group>())
                    {
                        string groupClaimValue = GetPropertyValue(group, groupClaimTypeConfig.DirectoryObjectProperty.ToString());
                        claims.Add(CreateClaim(groupClaimTypeConfig.ClaimType, groupClaimValue, groupClaimTypeConfig.ClaimValueType));
                    }
                    if (groups.NextPageRequest != null) groups = await groups.NextPageRequest.GetAsync().ConfigureAwait(false);
                    else morePages = false;
                }
            }
            return claims;
        }

        protected override void FillEntityTypes(List<string> entityTypes)
        {
            entityTypes.Add(SPClaimEntityTypes.User);
            entityTypes.Add(ClaimsProviderConstants.GroupClaimEntityType);
        }

        protected override void FillHierarchy(Uri context, string[] entityTypes, string hierarchyNodeID, int numberOfLevels, Microsoft.SharePoint.WebControls.SPProviderHierarchyTree hierarchy)
        {
            List<DirectoryObjectType> aadEntityTypes = new List<DirectoryObjectType>();
            if (entityTypes.Contains(SPClaimEntityTypes.User))
                aadEntityTypes.Add(DirectoryObjectType.User);
            if (entityTypes.Contains(ClaimsProviderConstants.GroupClaimEntityType))
                aadEntityTypes.Add(DirectoryObjectType.Group);

            if (!Initialize(context, entityTypes))
                return;

            this.Lock_Config.EnterReadLock();
            try
            {
                if (hierarchyNodeID == null)
                {
                    // Root level
                    foreach (var azureObject in this.ProcessedClaimTypesList.FindAll(x => !x.UseMainClaimTypeOfDirectoryObject && aadEntityTypes.Contains(x.EntityType)))
                    {
                        hierarchy.AddChild(
                            new Microsoft.SharePoint.WebControls.SPProviderHierarchyNode(
                                _ProviderInternalName,
                                azureObject.ClaimTypeDisplayName,
                                azureObject.ClaimType,
                                true));
                    }
                }
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in FillHierarchy", TraceCategory.Claims_Picking, ex);
            }
            finally
            {
                this.Lock_Config.ExitReadLock();
            }
        }

        /// <summary>
        /// Override this method to change / remove entities created by AzureCP, or add new ones
        /// </summary>
        /// <param name="currentContext"></param>
        /// <param name="entityTypes"></param>
        /// <param name="input"></param>
        /// <param name="resolved">List of entities created by LDAPCP</param>
        protected virtual void FillEntities(OperationContext currentContext, ref List<PickerEntity> resolved)
        {
        }

        protected override void FillResolve(Uri context, string[] entityTypes, SPClaim resolveInput, List<Microsoft.SharePoint.WebControls.PickerEntity> resolved)
        {
            //ClaimsProviderLogging.LogDebug($"context passed to FillResolve (SPClaim): {context.ToString()}");
            if (!Initialize(context, entityTypes))
                return;

            // Ensure incoming claim should be validated by AzureCP
            // Must be made after call to Initialize because SPTrustedLoginProvider name must be known
            if (!String.Equals(resolveInput.OriginalIssuer, IssuerName, StringComparison.InvariantCultureIgnoreCase))
                return;

            this.Lock_Config.EnterReadLock();
            try
            {
                OperationContext currentContext = new OperationContext(CurrentConfiguration, OperationType.Validation, ProcessedClaimTypesList, resolveInput.Value, resolveInput, context, entityTypes, null, 1);
                List<PickerEntity> entities = SearchOrValidate(currentContext);
                if (entities?.Count == 1)
                {
                    resolved.Add(entities[0]);
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Validated entity: display text: '{entities[0].DisplayText}', claim value: '{entities[0].Claim.Value}', claim type: '{entities[0].Claim.ClaimType}'",
                        TraceSeverity.High, EventSeverity.Information, TraceCategory.Claims_Picking);
                }
                else
                {
                    int entityCount = entities == null ? 0 : entities.Count;
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Validation failed: found {entityCount.ToString()} entities instead of 1 for incoming claim with value '{currentContext.IncomingEntity.Value}' and type '{currentContext.IncomingEntity.ClaimType}'", TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Claims_Picking);
                }
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in FillResolve(SPClaim)", TraceCategory.Claims_Picking, ex);
            }
            finally
            {
                this.Lock_Config.ExitReadLock();
            }
        }

        protected override void FillResolve(Uri context, string[] entityTypes, string resolveInput, List<Microsoft.SharePoint.WebControls.PickerEntity> resolved)
        {
            if (!Initialize(context, entityTypes))
                return;

            this.Lock_Config.EnterReadLock();
            try
            {
                int maxCount = 30;  // SharePoint sets maxCount to 30 in method FillSearch
                OperationContext currentContext = new OperationContext(CurrentConfiguration, OperationType.Search, ProcessedClaimTypesList, resolveInput, null, context, entityTypes, null, maxCount);
                List<PickerEntity> entities = SearchOrValidate(currentContext);
                FillEntities(currentContext, ref entities);
                if (entities == null || entities.Count == 0) return;
                foreach (PickerEntity entity in entities)
                {
                    resolved.Add(entity);
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Added entity: display text: '{entity.DisplayText}', claim value: '{entity.Claim.Value}', claim type: '{entity.Claim.ClaimType}'",
                        TraceSeverity.Verbose, EventSeverity.Information, TraceCategory.Claims_Picking);
                }
                ClaimsProviderLogging.Log($"[{ProviderInternalName}] Returned {entities.Count} entities with input '{currentContext.Input}'",
                    TraceSeverity.Medium, EventSeverity.Information, TraceCategory.Claims_Picking);
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in FillResolve(string)", TraceCategory.Claims_Picking, ex);
            }
            finally
            {
                this.Lock_Config.ExitReadLock();
            }
        }

        protected override void FillSchema(Microsoft.SharePoint.WebControls.SPProviderSchema schema)
        {
            schema.AddSchemaElement(new SPSchemaElement(PeopleEditorEntityDataKeys.DisplayName, "Display Name", SPSchemaElementType.Both));
        }

        protected override void FillSearch(Uri context, string[] entityTypes, string searchPattern, string hierarchyNodeID, int maxCount, Microsoft.SharePoint.WebControls.SPProviderHierarchyTree searchTree)
        {
            if (!Initialize(context, entityTypes))
                return;

            this.Lock_Config.EnterReadLock();
            try
            {
                OperationContext currentContext = new OperationContext(CurrentConfiguration, OperationType.Search, ProcessedClaimTypesList, searchPattern, null, context, entityTypes, hierarchyNodeID, maxCount);
                List<PickerEntity> entities = SearchOrValidate(currentContext);
                FillEntities(currentContext, ref entities);
                if (entities == null || entities.Count == 0) return;
                SPProviderHierarchyNode matchNode = null;
                foreach (PickerEntity entity in entities)
                {
                    // Add current PickerEntity to the corresponding ClaimType in the hierarchy
                    if (searchTree.HasChild(entity.Claim.ClaimType))
                    {
                        matchNode = searchTree.Children.First(x => x.HierarchyNodeID == entity.Claim.ClaimType);
                    }
                    else
                    {
                        ClaimTypeConfig ctConfig = ProcessedClaimTypesList.FirstOrDefault(x =>
                            !x.UseMainClaimTypeOfDirectoryObject &&
                            String.Equals(x.ClaimType, entity.Claim.ClaimType, StringComparison.InvariantCultureIgnoreCase));

                        string nodeName = ctConfig != null ? ctConfig.ClaimTypeDisplayName : entity.Claim.ClaimType;
                        matchNode = new SPProviderHierarchyNode(_ProviderInternalName, nodeName, entity.Claim.ClaimType, true);
                        searchTree.AddChild(matchNode);
                    }
                    matchNode.AddEntity(entity);
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Added entity: display text: '{entity.DisplayText}', claim value: '{entity.Claim.Value}', claim type: '{entity.Claim.ClaimType}'",
                        TraceSeverity.Verbose, EventSeverity.Information, TraceCategory.Claims_Picking);
                }
                ClaimsProviderLogging.Log($"[{ProviderInternalName}] Returned {entities.Count} entities from input '{currentContext.Input}'",
                    TraceSeverity.Medium, EventSeverity.Information, TraceCategory.Claims_Picking);
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in FillSearch", TraceCategory.Claims_Picking, ex);
            }
            finally
            {
                this.Lock_Config.ExitReadLock();
            }
        }

        /// <summary>
        /// Search or validate incoming input or entity
        /// </summary>
        /// <param name="currentContext">Information about current context and operation</param>
        /// <returns>Entities generated by AzureCP</returns>
        protected List<PickerEntity> SearchOrValidate(OperationContext currentContext)
        {
            List<PickerEntity> entities = new List<PickerEntity>();
            try
            {
                if (this.CurrentConfiguration.AlwaysResolveUserInput)
                {
                    // Completely bypass query to Azure AD
                    entities = CreatePickerEntityForSpecificClaimTypes(
                        currentContext.Input,
                        currentContext.CurrentClaimTypeConfigList.FindAll(x => !x.UseMainClaimTypeOfDirectoryObject),
                        false);
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Created {entities.Count} entity(ies) without contacting Azure AD tenant(s) because AzureCP property AlwaysResolveUserInput is set to true.",
                        TraceSeverity.Medium, EventSeverity.Information, TraceCategory.Claims_Picking);
                    return entities;
                }

                if (currentContext.OperationType == OperationType.Search)
                {
                    entities = SearchOrValidateInAzureAD(currentContext);

                    // Check if input starts with a prefix configured on a ClaimTypeConfig. If so an entity should be returned using ClaimTypeConfig found
                    // ClaimTypeConfigEnsureUniquePrefixToBypassLookup ensures that collection cannot contain duplicates
                    ClaimTypeConfig ctConfigWithInputPrefixMatch = currentContext.CurrentClaimTypeConfigList.FirstOrDefault(x =>
                        !String.IsNullOrEmpty(x.PrefixToBypassLookup) &&
                        currentContext.Input.StartsWith(x.PrefixToBypassLookup, StringComparison.InvariantCultureIgnoreCase));
                    if (ctConfigWithInputPrefixMatch != null)
                    {
                        currentContext.Input = currentContext.Input.Substring(ctConfigWithInputPrefixMatch.PrefixToBypassLookup.Length);
                        if (String.IsNullOrEmpty(currentContext.Input))
                        {
                            // No value in the input after the prefix, return
                            return entities;
                        }
                        PickerEntity entity = CreatePickerEntityForSpecificClaimType(
                            currentContext.Input,
                            ctConfigWithInputPrefixMatch,
                            true);
                        if (entity != null)
                        {
                            if (entities == null) entities = new List<PickerEntity>();
                            entities.Add(entity);
                            ClaimsProviderLogging.Log($"[{ProviderInternalName}] Created entity without contacting Azure AD tenant(s) because input started with prefix '{ctConfigWithInputPrefixMatch.PrefixToBypassLookup}', which is configured for claim type '{ctConfigWithInputPrefixMatch.ClaimType}'. Claim value: '{entity.Claim.Value}', claim type: '{entity.Claim.ClaimType}'",
                                TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Claims_Picking);
                            //return entities;
                        }
                    }
                }
                else if (currentContext.OperationType == OperationType.Validation)
                {
                    entities = SearchOrValidateInAzureAD(currentContext);
                    if (entities?.Count == 1) return entities;

                    if (!String.IsNullOrEmpty(currentContext.IncomingEntityClaimTypeConfig.PrefixToBypassLookup))
                    {
                        // At this stage, it is impossible to know if entity was originally created with the keyword that bypass query to Azure AD
                        // But it should be always validated since property PrefixToBypassLookup is set for current ClaimTypeConfig, so create entity manually
                        PickerEntity entity = CreatePickerEntityForSpecificClaimType(
                            currentContext.Input,
                            currentContext.IncomingEntityClaimTypeConfig,
                            currentContext.InputHasKeyword);
                        if (entity != null)
                        {
                            entities = new List<PickerEntity>(1) { entity };
                            ClaimsProviderLogging.Log($"[{ProviderInternalName}] Validated entity without contacting Azure AD tenant(s) because its claim type ('{currentContext.IncomingEntityClaimTypeConfig.ClaimType}') has property 'PrefixToBypassLookup' set in AzureCPConfig.ClaimTypes. Claim value: '{entity.Claim.Value}', claim type: '{entity.Claim.ClaimType}'",
                                TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Claims_Picking);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in SearchOrValidate", TraceCategory.Claims_Picking, ex);
            }
            return entities;
        }

        protected List<PickerEntity> SearchOrValidateInAzureAD(OperationContext currentContext)
        {
            string userFilter = String.Empty;
            string groupFilter = String.Empty;
            string userSelect = String.Empty;
            string groupSelect = String.Empty;

            // BUG: FILTERS MUST BE SET IN AN OBJECT CREATED IN THIS METHOD (TO BE BOUND TO CURRENT THREAD), OTHERWISE FILTER MAY BE UPDATED BY MULTIPLE THREADS
            // Somehow, this constructor is not working, AzureTenant must be explicitely copied into new list
            //List<AzureTenant> azureTenants = new List<AzureTenant>(this.CurrentConfiguration.AzureTenants);
            List<AzureTenant> azureTenants = new List<AzureTenant>(this.CurrentConfiguration.AzureTenants.Count);
            foreach (AzureTenant tenant in this.CurrentConfiguration.AzureTenants)
            {
                azureTenants.Add(tenant.CopyPersistedProperties());
            }

            BuildFilter(currentContext, azureTenants);

            List<AzureADResult> aadResults = null;
            using (new SPMonitoredScope($"[{ProviderInternalName}] Total time spent to query Azure AD tenant(s)", 1000))
            {
                // Call async method in a task to avoid error "Asynchronous operations are not allowed in this context" error when permission is validated (POST from people picker)
                // More info on the error: https://stackoverflow.com/questions/672237/running-an-asynchronous-operation-triggered-by-an-asp-net-web-page-request
                Task azureADQueryTask = Task.Run(async () =>
                {
                    aadResults = await QueryAzureADTenantsAsync(currentContext, azureTenants).ConfigureAwait(false);
                });
                azureADQueryTask.Wait();
            }

            if (aadResults == null || aadResults.Count <= 0) return null;
            List<AzureCPResult> results = ProcessAzureADResults(currentContext, aadResults);
            if (results == null || results.Count <= 0) return null;
            List<PickerEntity> entities = new List<PickerEntity>();
            foreach (var result in results)
            {
                entities.Add(result.PickerEntity);
                //ClaimsProviderLogging.Log($"[{ProviderInternalName}] Added entity returned by Azure AD: claim value: '{result.PickerEntity.Claim.Value}', claim type: '{result.PickerEntity.Claim.ClaimType}'",
                //    TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Claims_Picking);
            }
            return entities;
        }

        /// <summary>
        /// Build filter and select statements used in queries sent to Azure AD
        /// $filter and $select must be URL encoded as documented in https://developer.microsoft.com/en-us/graph/docs/concepts/query_parameters#encoding-query-parameters
        /// </summary>
        /// <param name="currentContext"></param>
        protected virtual void BuildFilter(OperationContext currentContext, List<AzureTenant> azureTenants)
        {
            //StringBuilder userFilterBuilder = new StringBuilder("( ");
            StringBuilder userFilterBuilder = new StringBuilder("");
            StringBuilder groupFilterBuilder = new StringBuilder();
            StringBuilder userSelectBuilder = new StringBuilder("UserType, Mail, ");    // UserType and Mail are always needed to deal with Guest users
            StringBuilder groupSelectBuilder = new StringBuilder("Id, ");               // Id is always required for groups
            string memberOnlyUserTypeFilter = " and UserType eq 'Member'";

            string preferredFilterPattern;
            string input = currentContext.Input;
            if (currentContext.ExactSearch) preferredFilterPattern = String.Format(ClaimsProviderConstants.SearchPatternEquals, "{0}", input);
            else preferredFilterPattern = String.Format(ClaimsProviderConstants.SearchPatternStartsWith, "{0}", input);

            bool firstUserObjectProcessed = false;
            bool firstGroupObjectProcessed = false;
            foreach (ClaimTypeConfig ctConfig in currentContext.CurrentClaimTypeConfigList)
            {
                string currentPropertyString = ctConfig.DirectoryObjectProperty.ToString();
                string currentFilter;
                if (!ctConfig.SupportsWildcard)
                    currentFilter = String.Format(ClaimsProviderConstants.SearchPatternEquals, currentPropertyString, input);
                else
                    currentFilter = String.Format(preferredFilterPattern, currentPropertyString);

                // Id needs a specific check: input must be a valid GUID AND equals filter must be used, otherwise Azure AD will throw an error
                if (ctConfig.DirectoryObjectProperty == AzureADObjectProperty.Id)
                {
                    Guid idGuid = new Guid();
                    if (!Guid.TryParse(input, out idGuid)) continue;
                    else currentFilter = String.Format(ClaimsProviderConstants.SearchPatternEquals, currentPropertyString, idGuid.ToString());
                }

                if (ctConfig.EntityType == DirectoryObjectType.User)
                {
                    if (ctConfig is IdentityClaimTypeConfig)
                    {
                        IdentityClaimTypeConfig identityClaimTypeConfig = ctConfig as IdentityClaimTypeConfig;
                        if (!ctConfig.SupportsWildcard)
                            currentFilter = "( " + String.Format(ClaimsProviderConstants.IdentityConfigSearchPatternEquals, currentPropertyString, input, AzureADUserTypeHelper.MemberUserType) + " or " + String.Format(ClaimsProviderConstants.IdentityConfigSearchPatternEquals, identityClaimTypeConfig.DirectoryObjectPropertyForGuestUsers, input, AzureADUserTypeHelper.GuestUserType) + " )";
                        else
                        {
                            if (currentContext.ExactSearch) currentFilter = "( " + String.Format(ClaimsProviderConstants.IdentityConfigSearchPatternEquals, currentPropertyString, input, AzureADUserTypeHelper.MemberUserType) + " or " + String.Format(ClaimsProviderConstants.IdentityConfigSearchPatternEquals, identityClaimTypeConfig.DirectoryObjectPropertyForGuestUsers, input, AzureADUserTypeHelper.GuestUserType) + " )";
                            else currentFilter = "( " + String.Format(ClaimsProviderConstants.IdentityConfigSearchPatternStartsWith, currentPropertyString, input, AzureADUserTypeHelper.MemberUserType) + " or " + String.Format(ClaimsProviderConstants.IdentityConfigSearchPatternStartsWith, identityClaimTypeConfig.DirectoryObjectPropertyForGuestUsers, input, AzureADUserTypeHelper.GuestUserType) + " )";
                        }
                    }

                    if (!firstUserObjectProcessed) firstUserObjectProcessed = true;
                    else
                    {
                        currentFilter = " or " + currentFilter;
                        currentPropertyString = ", " + currentPropertyString;
                    }
                    userFilterBuilder.Append(currentFilter);
                    userSelectBuilder.Append(currentPropertyString);
                }
                else
                {
                    // else assume it's a Group
                    if (!firstGroupObjectProcessed) firstGroupObjectProcessed = true;
                    else
                    {
                        currentFilter = " or " + currentFilter;
                        currentPropertyString = ", " + currentPropertyString;
                    }
                    groupFilterBuilder.Append(currentFilter);
                    groupSelectBuilder.Append(currentPropertyString);
                }
            }

            // Also add metadata properties to $select of corresponding object type
            if (firstUserObjectProcessed)
            {
                foreach (ClaimTypeConfig ctConfig in MetadataConfig.Where(x => x.EntityType == DirectoryObjectType.User))
                {
                    userSelectBuilder.Append($", {ctConfig.DirectoryObjectProperty.ToString()}");
                }
            }
            if (firstGroupObjectProcessed)
            {
                foreach (ClaimTypeConfig ctConfig in MetadataConfig.Where(x => x.EntityType == DirectoryObjectType.Group))
                {
                    groupSelectBuilder.Append($", {ctConfig.DirectoryObjectProperty.ToString()}");
                }
            }

            //userFilterBuilder.Append(" ) and accountEnabled eq true");  // Graph throws this error if used: "Search filter expression has excessive height: 4. Max allowed: 3."
            string encodedUserFilter = HttpUtility.UrlEncode(userFilterBuilder.ToString());
            string encodedGroupFilter = HttpUtility.UrlEncode(groupFilterBuilder.ToString());
            string encodedUserSelect = HttpUtility.UrlEncode(userSelectBuilder.ToString());
            string encodedgroupSelect = HttpUtility.UrlEncode(groupSelectBuilder.ToString());
            string encodedMemberOnlyUserTypeFilter = HttpUtility.UrlEncode(memberOnlyUserTypeFilter);

            foreach (AzureTenant tenant in azureTenants)
            {
                // Reset filters if no corresponding object was found in requestInfo.ClaimTypeConfigList, to detect that tenant should not be actually queried
                if (firstUserObjectProcessed)
                    tenant.UserFilter = tenant.MemberUserTypeOnly ? encodedUserFilter + encodedMemberOnlyUserTypeFilter : encodedUserFilter;
                else
                    tenant.UserFilter = String.Empty;

                if (firstGroupObjectProcessed)
                    tenant.GroupFilter = encodedGroupFilter;
                else
                    tenant.GroupFilter = String.Empty;

                tenant.UserSelect = encodedUserSelect;
                tenant.GroupSelect = encodedgroupSelect;
            }
        }

        protected async Task<List<AzureADResult>> QueryAzureADTenantsAsync(OperationContext currentContext, List<AzureTenant> azureTenants)
        {
            // Create a task for each tenant to query
            var tenantQueryTasks = azureTenants.Select(async tenant =>
            {
                Stopwatch timer = new Stopwatch();
                AzureADResult tenantResult = null;
                try
                {
                    timer.Start();
                    tenantResult = await QueryAzureADTenantAsync(currentContext, tenant, true).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ClaimsProviderLogging.LogException(ProviderInternalName, $"in QueryAzureADTenantsAsync while querying tenant '{tenant.TenantName}'", TraceCategory.Lookup, ex);
                }
                finally
                {
                    timer.Stop();
                }
                if (tenantResult != null)
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Got {tenantResult.UsersAndGroups.Count().ToString()} users/groups in {timer.ElapsedMilliseconds.ToString()} ms from '{tenant.TenantName}' with input '{currentContext.Input}'", TraceSeverity.Medium, EventSeverity.Information, TraceCategory.Lookup);
                else
                    ClaimsProviderLogging.Log($"[{ProviderInternalName}] Got no result from '{tenant.TenantName}' with input '{currentContext.Input}', search took {timer.ElapsedMilliseconds.ToString()} ms", TraceSeverity.Medium, EventSeverity.Information, TraceCategory.Lookup);
                return tenantResult;
            });

            // Wait for all tasks to complete and return result as a List<AzureADResult>
            AzureADResult[] tenantResults = await Task.WhenAll(tenantQueryTasks).ConfigureAwait(false);
            return tenantResults.ToList();
        }

        protected virtual async Task<AzureADResult> QueryAzureADTenantAsync(OperationContext currentContext, AzureTenant tenant, bool firstAttempt)
        {
            if (tenant.UserFilter == null && tenant.GroupFilter == null) return null;

            ClaimsProviderLogging.Log($"[{ProviderInternalName}] Querying Azure AD tenant '{tenant.TenantName}' for users/groups/domains, with input '{currentContext.Input}'", TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Lookup);
            AzureADResult tenantResults = new AzureADResult();
            bool tryAgain = false;
            object lockAddResultToCollection = new object();
            CancellationTokenSource cts = new CancellationTokenSource(this.CurrentConfiguration.Timeout);
            try
            {
                using (new SPMonitoredScope($"[{ProviderInternalName}] Querying Azure AD tenant '{tenant.TenantName}' for users/groups/domains, with input '{currentContext.Input}'", 1000))
                {
                    // No need to lock here: as per https://stackoverflow.com/questions/49108179/need-advice-on-getting-access-token-with-multiple-task-in-microsoft-graph:
                    // The Graph client object is thread-safe and re-entrant
                    Task userQueryTask = Task.Run(async () =>
                    {
                        if (String.IsNullOrEmpty(tenant.UserFilter))
                        {
                            return;
                        }
                        IGraphServiceUsersCollectionPage users = await tenant.GraphService.Users.Request().Select(tenant.UserSelect).Filter(tenant.UserFilter).Top(currentContext.MaxCount).GetAsync().ConfigureAwait(false);
                        ClaimsProviderLogging.Log($"[{ProviderInternalName}] Query to tenant '{tenant.TenantName}' returned {users.Count} user(s) with filter \"{HttpUtility.UrlDecode(tenant.UserFilter)}\"", TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Lookup);
                        if (users?.Count > 0)
                        {
                            do
                            {
                                lock (lockAddResultToCollection)
                                {
                                    tenantResults.UsersAndGroups.AddRange(users.CurrentPage);
                                }
                                if (users.NextPageRequest != null) users = await users.NextPageRequest.GetAsync().ConfigureAwait(false);
                            }
                            while (users?.Count > 0 && users.NextPageRequest != null);
                        }
                    }, cts.Token);
                    Task groupQueryTask = Task.Run(async () =>
                    {
                        if (String.IsNullOrEmpty(tenant.GroupFilter)) return;
                        IGraphServiceGroupsCollectionPage groups = await tenant.GraphService.Groups.Request().Select(tenant.GroupSelect).Filter(tenant.GroupFilter).Top(currentContext.MaxCount).GetAsync().ConfigureAwait(false);
                        ClaimsProviderLogging.Log($"[{ProviderInternalName}] Query to tenant '{tenant.TenantName}' returned {groups.Count} group(s) with filter \"{HttpUtility.UrlDecode(tenant.GroupFilter)}\"", TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Lookup);
                        if (groups?.Count > 0)
                        {
                            do
                            {
                                lock (lockAddResultToCollection)
                                {
                                    tenantResults.UsersAndGroups.AddRange(groups.CurrentPage);
                                }
                                if (groups.NextPageRequest != null) groups = await groups.NextPageRequest.GetAsync().ConfigureAwait(false);
                            }
                            while (groups?.Count > 0 && groups.NextPageRequest != null);
                        }
                    }, cts.Token);
                    //Task domainQueryTask = Task.Run(async () =>
                    //{
                    //    IGraphServiceDomainsCollectionPage domains = await tenant.GraphService.Domains.Request().GetAsync().ConfigureAwait(false);
                    //    lock (lockAddResultToCollection)
                    //    {
                    //        tenantResults.DomainsRegisteredInAzureADTenant.AddRange(domains.Where(x => x.IsVerified == true).Select(x => x.Id));
                    //    }
                    //}, cts.Token);

                    // Waits for all tasks to complete execution within a specified number of milliseconds
                    // Use specifically WaitAll(Task[], Int32, CancellationToken) as it will thwrow an OperationCanceledException if cancellationToken is canceled
                    //bool tasksCompletedInTime = Task.WaitAll(new Task[3] { userQueryTask, groupQueryTask, domainQueryTask }, this.CurrentConfiguration.Timeout, cts.Token);
                    bool tasksCompletedInTime = Task.WaitAll(new Task[2] { userQueryTask, groupQueryTask }, this.CurrentConfiguration.Timeout, cts.Token);
                    if (!tasksCompletedInTime)
                    {
                        // Some or all tasks didn't complete on time, cancel them
                        //ClaimsProviderLogging.Log($"[{ProviderInternalName}] DEBUG: Exceeded Timeout on Azure AD tenant '{tenant.TenantName}', cancelling token.", TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Lookup);
                        cts.Cancel();
                        // For some reason, Cancel() doesn't make Task.WaitAll to throw an OperationCanceledException
                        ClaimsProviderLogging.Log($"[{ProviderInternalName}] Queries on Azure AD tenant '{tenant.TenantName}' exceeded Timeout of {this.CurrentConfiguration.Timeout} ms and were cancelled.", TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Lookup);
                        tryAgain = true;
                    }
                    //await Task.WhenAll(userQueryTask, groupQueryTask).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                ClaimsProviderLogging.Log($"[{ProviderInternalName}] Queries on Azure AD tenant '{tenant.TenantName}' exceeded timeout of {this.CurrentConfiguration.Timeout} ms and were cancelled.", TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Lookup);
                tryAgain = true;
            }
            catch (AggregateException ex)
            {
                // Task.WaitAll throws an AggregateException, which contains all exceptions thrown by tasks it waited on
                ClaimsProviderLogging.LogException(ProviderInternalName, $"while querying Azure AD tenant '{tenant.TenantName}'", TraceCategory.Lookup, ex);
                tryAgain = true;
            }
            finally
            {
                ClaimsProviderLogging.LogDebug($"[{ProviderInternalName}] Finished queries on Azure AD tenant '{tenant.TenantName}'");
                cts.Dispose();
            }

            if (tryAgain && !CurrentConfiguration.EnableRetry) tryAgain = false;

            if (firstAttempt && tryAgain)
            {
                ClaimsProviderLogging.Log($"[{ProviderInternalName}] Doing new attempt to query Azure AD tenant '{tenant.TenantName}'...",
                    TraceSeverity.Medium, EventSeverity.Information, TraceCategory.Lookup);
                tenantResults = await QueryAzureADTenantAsync(currentContext, tenant, false).ConfigureAwait(false);
            }
            return tenantResults;
        }

        protected virtual List<AzureCPResult> ProcessAzureADResults(OperationContext currentContext, List<AzureADResult> azureADResults)
        {
            // Split results between users/groups and list of registered domains in the tenant
            List<DirectoryObject> usersAndGroups = new List<DirectoryObject>();
            //List<string> domains = new List<string>();
            // For each Azure AD tenant
            foreach (AzureADResult tenantResults in azureADResults)
            {
                usersAndGroups.AddRange(tenantResults.UsersAndGroups);
                //domains.AddRange(tenantResults.DomainsRegisteredInAzureADTenant);
            }

            // Return if no user / groups is found, or if no registered domain is found
            if (usersAndGroups == null || !usersAndGroups.Any() /*|| domains == null || !domains.Any()*/)
            {
                return null;
            };

            List<ClaimTypeConfig> ctConfigs = currentContext.CurrentClaimTypeConfigList;
            if (currentContext.ExactSearch) ctConfigs = currentContext.CurrentClaimTypeConfigList.FindAll(x => !x.UseMainClaimTypeOfDirectoryObject);

            List<AzureCPResult> processedResults = new List<AzureCPResult>();
            foreach (DirectoryObject userOrGroup in usersAndGroups)
            {
                DirectoryObject currentObject = null;
                DirectoryObjectType objectType;
                if (userOrGroup is User)
                {
                    // This section has become irrelevant since the specific handling of guest users done lower in the filtering, introduced in v13
                    //// Always exclude shadow users: UserType is Guest and his mail matches a verified domain in any Azure AD tenant
                    //string userType = ((User)userOrGroup).UserType;
                    //if (String.Equals(userType, AzureADUserTypeHelper.GuestUserType, StringComparison.InvariantCultureIgnoreCase))
                    //{
                    //    string userMail = ((User)userOrGroup).Mail;
                    //    if (String.IsNullOrEmpty(userMail))
                    //    {
                    //        ClaimsProviderLogging.Log($"[{ProviderInternalName}] Guest user '{((User)userOrGroup).UserPrincipalName}' filtered out because his mail is empty.",
                    //            TraceSeverity.Unexpected, EventSeverity.Warning, TraceCategory.Lookup);
                    //        continue;
                    //    }
                    //    if (!userMail.Contains('@')) continue;
                    //    string maildomain = userMail.Split('@')[1];
                    //    if (domains.Any(x => String.Equals(x, maildomain, StringComparison.InvariantCultureIgnoreCase)))
                    //    {
                    //        ClaimsProviderLogging.Log($"[{ProviderInternalName}] Guest user '{((User)userOrGroup).UserPrincipalName}' filtered out because his email '{userMail}' matches a domain registered in a Azure AD tenant.",
                    //            TraceSeverity.Verbose, EventSeverity.Verbose, TraceCategory.Lookup);
                    //        continue;
                    //    }
                    //}
                    currentObject = userOrGroup;
                    objectType = DirectoryObjectType.User;
                }
                else
                {
                    currentObject = userOrGroup;
                    objectType = DirectoryObjectType.Group;
                }

                foreach (ClaimTypeConfig ctConfig in ctConfigs.Where(x => x.EntityType == objectType))
                {
                    // Get value with of current GraphProperty
                    string directoryObjectPropertyValue = GetPropertyValue(currentObject, ctConfig.DirectoryObjectProperty.ToString());

                    if (ctConfig is IdentityClaimTypeConfig)
                    {
                        if (String.Equals(((User)currentObject).UserType, AzureADUserTypeHelper.GuestUserType, StringComparison.InvariantCultureIgnoreCase))
                        {
                            // For Guest users, use the value set in property DirectoryObjectPropertyForGuestUsers
                            directoryObjectPropertyValue = GetPropertyValue(currentObject, ((IdentityClaimTypeConfig)ctConfig).DirectoryObjectPropertyForGuestUsers.ToString());
                        }
                    }

                    // Check if property exists (not null) and has a value (not String.Empty)
                    if (String.IsNullOrEmpty(directoryObjectPropertyValue)) continue;

                    // Check if current value mathes input, otherwise go to next GraphProperty to check
                    if (currentContext.ExactSearch)
                    {
                        if (!String.Equals(directoryObjectPropertyValue, currentContext.Input, StringComparison.InvariantCultureIgnoreCase)) continue;
                    }
                    else
                    {
                        if (!directoryObjectPropertyValue.StartsWith(currentContext.Input, StringComparison.InvariantCultureIgnoreCase)) continue;
                    }

                    // Current DirectoryObjectProperty value matches user input. Add current result to search results if it is not already present
                    string entityClaimValue = directoryObjectPropertyValue;
                    ClaimTypeConfig claimTypeConfigToCompare;
                    if (ctConfig.UseMainClaimTypeOfDirectoryObject)
                    {
                        if (objectType == DirectoryObjectType.User)
                        {
                            claimTypeConfigToCompare = IdentityClaimTypeConfig;
                            if (String.Equals(((User)currentObject).UserType, AzureADUserTypeHelper.GuestUserType, StringComparison.InvariantCultureIgnoreCase))
                            {
                                // For Guest users, use the value set in property DirectoryObjectPropertyForGuestUsers
                                entityClaimValue = GetPropertyValue(currentObject, IdentityClaimTypeConfig.DirectoryObjectPropertyForGuestUsers.ToString());
                            }
                            else
                            {
                                // Get the value of the DirectoryObjectProperty linked to current directory object
                                entityClaimValue = GetPropertyValue(currentObject, IdentityClaimTypeConfig.DirectoryObjectProperty.ToString());
                            }
                        }
                        else
                        {
                            claimTypeConfigToCompare = MainGroupClaimTypeConfig;
                            // Get the value of the DirectoryObjectProperty linked to current directory object
                            entityClaimValue = GetPropertyValue(currentObject, claimTypeConfigToCompare.DirectoryObjectProperty.ToString());
                        }

                        if (String.IsNullOrEmpty(entityClaimValue)) continue;
                    }
                    else
                    {
                        claimTypeConfigToCompare = ctConfig;
                    }

                    // if claim type and claim value already exists, skip
                    bool resultAlreadyExists = processedResults.Exists(x =>
                        String.Equals(x.ClaimTypeConfig.ClaimType, claimTypeConfigToCompare.ClaimType, StringComparison.InvariantCultureIgnoreCase) &&
                        String.Equals(x.PermissionValue, entityClaimValue, StringComparison.InvariantCultureIgnoreCase));
                    if (resultAlreadyExists) continue;

                    // Passed the checks, add it to the processedResults list
                    processedResults.Add(
                        new AzureCPResult(currentObject)
                        {
                            ClaimTypeConfig = ctConfig,
                            PermissionValue = entityClaimValue,
                            QueryMatchValue = directoryObjectPropertyValue,
                        });
                }
            }

            ClaimsProviderLogging.Log($"[{ProviderInternalName}] {processedResults.Count} entity(ies) to create after filtering", TraceSeverity.Verbose, EventSeverity.Information, TraceCategory.Lookup);
            foreach (AzureCPResult result in processedResults)
            {
                PickerEntity pe = CreatePickerEntityHelper(result);
                result.PickerEntity = pe;
            }
            return processedResults;
        }

        public override string Name { get { return ProviderInternalName; } }
        public override bool SupportsEntityInformation { get { return true; } }
        public override bool SupportsHierarchy { get { return true; } }
        public override bool SupportsResolve { get { return true; } }
        public override bool SupportsSearch { get { return true; } }
        public override bool SupportsUserKey { get { return true; } }

        /// <summary>
        /// Return the identity claim type
        /// </summary>
        /// <returns></returns>
        public override string GetClaimTypeForUserKey()
        {
            if (!Initialize(null, null))
                return null;

            this.Lock_Config.EnterReadLock();
            try
            {
                return IdentityClaimTypeConfig.ClaimType;
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in GetClaimTypeForUserKey", TraceCategory.Rehydration, ex);
            }
            finally
            {
                this.Lock_Config.ExitReadLock();
            }
            return null;
        }

        /// <summary>
        /// Return the user key (SPClaim with identity claim type) from the incoming entity
        /// </summary>
        /// <param name="entity"></param>
        /// <returns></returns>
        protected override SPClaim GetUserKeyForEntity(SPClaim entity)
        {
            if (!Initialize(null, null))
                return null;

            // There are 2 scenarios:
            // 1: OriginalIssuer is "SecurityTokenService": Value looks like "05.t|yvanhost|yvand@yvanhost.local", claim type is "http://schemas.microsoft.com/sharepoint/2009/08/claims/userid" and it must be decoded properly
            // 2: OriginalIssuer is AzureCP: in this case incoming entity is valid and returned as is
            if (String.Equals(entity.OriginalIssuer, IssuerName, StringComparison.InvariantCultureIgnoreCase))
                return entity;

            SPClaimProviderManager cpm = SPClaimProviderManager.Local;
            SPClaim curUser = SPClaimProviderManager.DecodeUserIdentifierClaim(entity);

            this.Lock_Config.EnterReadLock();
            try
            {
                ClaimsProviderLogging.Log($"[{ProviderInternalName}] Returning user key for '{entity.Value}'",
                    TraceSeverity.VerboseEx, EventSeverity.Information, TraceCategory.Rehydration);
                return CreateClaim(IdentityClaimTypeConfig.ClaimType, curUser.Value, curUser.ValueType);
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ProviderInternalName, "in GetUserKeyForEntity", TraceCategory.Rehydration, ex);
            }
            finally
            {
                this.Lock_Config.ExitReadLock();
            }
            return null;
        }
    }

    public class AzureADResult
    {
        public List<DirectoryObject> UsersAndGroups;
        public List<string> DomainsRegisteredInAzureADTenant;
        //public string TenantName;

        public AzureADResult()
        {
            UsersAndGroups = new List<DirectoryObject>();
            DomainsRegisteredInAzureADTenant = new List<string>();
            //this.TenantName = tenantName;
        }
    }

    /// <summary>
    /// User / group found in Azure AD, with additional information
    /// </summary>
    public class AzureCPResult
    {
        public DirectoryObject UserOrGroupResult;
        public ClaimTypeConfig ClaimTypeConfig;
        public PickerEntity PickerEntity;
        public string PermissionValue;
        public string QueryMatchValue;
        //public string TenantName;

        public AzureCPResult(DirectoryObject directoryObject)
        {
            UserOrGroupResult = directoryObject;
            //TenantName = tenantName;
        }
    }
}
