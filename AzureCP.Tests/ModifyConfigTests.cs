﻿using azurecp;
using NUnit.Framework;
using System;
using System.Linq;

namespace AzureCP.Tests
{
    [TestFixture]
    public class ModifyConfigTests : BackupCurrentConfig
    {
        const string ConfigUpdateErrorMessage = "Some changes made to list ClaimTypes are invalid and cannot be committed to configuration database. Inspect inner exception for more details about the error.";

        [Test]
        public void AddClaimTypeConfig()
        {
            ClaimTypeConfig ctConfig = new ClaimTypeConfig();

            // Add a ClaimTypeConfig with a claim type already set should throw exception InvalidOperationException
            ctConfig.ClaimType = UnitTestsHelper.SPTrust.IdentityClaimTypeInformation.MappedClaimType;
            ctConfig.DirectoryObjectProperty = UnitTestsHelper.RandomObjectProperty;
            Assert.Throws<InvalidOperationException>(() => Config.ClaimTypes.Add(ctConfig), $"Add a ClaimTypeConfig with a claim type already set should throw exception InvalidOperationException with this message: \"Claim type '{UnitTestsHelper.SPTrust.IdentityClaimTypeInformation.MappedClaimType}' already exists in the collection\"");

            // Add a ClaimTypeConfig with UseMainClaimTypeOfDirectoryObject = false (default value) and DirectoryObjectProperty not set should throw exception InvalidOperationException
            ctConfig.ClaimType = UnitTestsHelper.RandomClaimType;
            ctConfig.DirectoryObjectProperty = AzureADObjectProperty.NotSet;
            Assert.Throws<InvalidOperationException>(() => Config.ClaimTypes.Add(ctConfig), $"Add a ClaimTypeConfig with UseMainClaimTypeOfDirectoryObject = false (default value) and DirectoryObjectProperty not set should throw exception InvalidOperationException with this message: \"Property DirectoryObjectProperty is required\"");

            // Add a ClaimTypeConfig with UseMainClaimTypeOfDirectoryObject = true and ClaimType set should throw exception InvalidOperationException
            ctConfig.ClaimType = UnitTestsHelper.RandomClaimType;
            ctConfig.DirectoryObjectProperty = UnitTestsHelper.RandomObjectProperty;
            ctConfig.UseMainClaimTypeOfDirectoryObject = true;
            Assert.Throws<InvalidOperationException>(() => Config.ClaimTypes.Add(ctConfig), $"Add a ClaimTypeConfig with UseMainClaimTypeOfDirectoryObject = true and ClaimType set should throw exception InvalidOperationException with this message: \"No claim type should be set if UseMainClaimTypeOfDirectoryObject is set to true\"");
            
            // Add a ClaimTypeConfig with EntityType 'Group' should throw exception InvalidOperationException since 1 already exists by default and AzureCP allows only 1 claim type for EntityType 'Group'
            ctConfig.ClaimType = UnitTestsHelper.RandomClaimType;
            ctConfig.DirectoryObjectProperty = UnitTestsHelper.RandomObjectProperty;
            ctConfig.EntityType = DirectoryObjectType.Group;
            ctConfig.UseMainClaimTypeOfDirectoryObject = false;
            Assert.Throws<InvalidOperationException>(() => Config.ClaimTypes.Add(ctConfig), $"Add a ClaimTypeConfig with EntityType 'Group' should throw exception InvalidOperationException with this message: \"A claim type for EntityType 'Group' already exists in the collection\"");

            // Add a valid ClaimTypeConfig should succeed
            ctConfig.ClaimType = UnitTestsHelper.RandomClaimType;
            ctConfig.DirectoryObjectProperty = UnitTestsHelper.RandomObjectProperty;
            ctConfig.EntityType = DirectoryObjectType.User;
            ctConfig.UseMainClaimTypeOfDirectoryObject = false;
            Assert.DoesNotThrow(() => Config.ClaimTypes.Add(ctConfig), $"Add a valid ClaimTypeConfig should succeed");

            // Add a ClaimTypeConfig twice should throw exception InvalidOperationException
            Assert.Throws<InvalidOperationException>(() => Config.ClaimTypes.Add(ctConfig), $"Add a ClaimTypeConfig with a claim type already set should throw exception InvalidOperationException with this message: \"Claim type '{UnitTestsHelper.RandomClaimType}' already exists in the collection\"");

            // Delete the ClaimTypeConfig by calling method ClaimTypeConfigCollection.Remove(ClaimTypeConfig) should succeed
            Assert.IsTrue(Config.ClaimTypes.Remove(ctConfig), $"Delete the ClaimTypeConfig by calling method ClaimTypeConfigCollection.Remove(ClaimTypeConfig) should succeed");
        }

        [Test]
        public void ModifyOrDeleteIdentityClaimTypeConfig()
        {
            // Delete identity claim type from ClaimTypes list based on its claim type should throw exception InvalidOperationException
            string identityClaimType = UnitTestsHelper.SPTrust.IdentityClaimTypeInformation.MappedClaimType;
            Assert.Throws<InvalidOperationException>(() => Config.ClaimTypes.Remove(identityClaimType), $"Delete identity claim type from ClaimTypes list should throw exception InvalidOperationException with this message: \"Cannot delete claim type \"{UnitTestsHelper.SPTrust.IdentityClaimTypeInformation.MappedClaimType}\" because it is the identity claim type of \"{UnitTestsHelper.SPTrust.Name}\"\"");

            // Delete identity claim type from ClaimTypes list based on its ClaimTypeConfig should throw exception InvalidOperationException
            ClaimTypeConfig identityCTConfig = Config.ClaimTypes.FirstOrDefault(x => String.Equals(UnitTestsHelper.SPTrust.IdentityClaimTypeInformation.MappedClaimType, x.ClaimType, StringComparison.InvariantCultureIgnoreCase));
            Assert.Throws<InvalidOperationException>(() => Config.ClaimTypes.Remove(identityClaimType), $"Delete identity claim type from ClaimTypes list should throw exception InvalidOperationException with this message: \"Cannot delete claim type \"{UnitTestsHelper.SPTrust.IdentityClaimTypeInformation.MappedClaimType}\" because it is the identity claim type of \"{UnitTestsHelper.SPTrust.Name}\"\"");

            // Modify identity ClaimTypeConfig to set its EntityType to Group should throw exception InvalidOperationException
            identityCTConfig.EntityType = DirectoryObjectType.Group;
            Assert.Throws<InvalidOperationException>(() => Config.Update(), $"Modify identity claim type to set its EntityType to Group should throw exception InvalidOperationException with this message: \"{ConfigUpdateErrorMessage}\"");
        }

        [Test]
        public void DuplicateClaimType()
        {
            var firstCTConfig = Config.ClaimTypes.FirstOrDefault(x => !String.IsNullOrEmpty(x.ClaimType));

            // Add a ClaimTypeConfig with property ClaimType already defined in another ClaimTypeConfig should throw exception InvalidOperationException
            ClaimTypeConfig ctConfig = new ClaimTypeConfig() { ClaimType = firstCTConfig.ClaimType, DirectoryObjectProperty = UnitTestsHelper.RandomObjectProperty };
            Assert.Throws<InvalidOperationException>(() => Config.ClaimTypes.Add(ctConfig), $"Add a ClaimTypeConfig with property ClaimType already defined in another ClaimTypeConfig should throw exception InvalidOperationException with this message: \"Claim type '{firstCTConfig.ClaimType}' already exists in the collection\"");

            // Modify an existing claim type to set a claim type already defined should throw exception InvalidOperationException
            ClaimTypeConfig anotherCTConfig = Config.ClaimTypes.FirstOrDefault(x => !String.IsNullOrEmpty(x.ClaimType) && !String.Equals(firstCTConfig.ClaimType, x.ClaimType, StringComparison.InvariantCultureIgnoreCase));
            anotherCTConfig.ClaimType = firstCTConfig.ClaimType;
            Assert.Throws<InvalidOperationException>(() => Config.Update(), $"Modify an existing claim type to set a claim type already defined should throw exception InvalidOperationException with this message: \"{ConfigUpdateErrorMessage}\"");
        }

        [Test]
        public void DuplicatePrefixToBypassLookup()
        {
            string prefixToBypassLookup = "test:";

            // Set a duplicate PrefixToBypassLookup on 2 items already existing in the list should throw exception InvalidOperationException
            Config.ClaimTypes.Where(x => !String.IsNullOrEmpty(x.ClaimType)).Take(2).Select(x => x.PrefixToBypassLookup = prefixToBypassLookup).ToList();
            Assert.Throws<InvalidOperationException>(() => Config.Update(), $"Set a duplicate PrefixToBypassLookup on 2 items already existing in the list should throw exception InvalidOperationException with this message: \"{ConfigUpdateErrorMessage}\"");

            // Set a PrefixToBypassLookup on an existing item and add a new item with the same PrefixToBypassLookup should throw exception InvalidOperationException
            var firstCTConfig = Config.ClaimTypes.FirstOrDefault(x => !String.IsNullOrEmpty(x.ClaimType));
            firstCTConfig.PrefixToBypassLookup = prefixToBypassLookup;
            ClaimTypeConfig ctConfig = new ClaimTypeConfig() { ClaimType = UnitTestsHelper.RandomClaimType, PrefixToBypassLookup = prefixToBypassLookup, DirectoryObjectProperty = UnitTestsHelper.RandomObjectProperty };
            Assert.Throws<InvalidOperationException>(() => Config.Update(), $"Set a duplicate PrefixToBypassLookup on an existing item and add a new item with the same PrefixToBypassLookup should throw exception InvalidOperationException with this message: \"{ConfigUpdateErrorMessage}\"");
        }

        [Test]
        public void DuplicateEntityDataKey()
        {
            string entityDataKey = "test";

            // Duplicate EntityDataKey on 2 items already existing in the list should throw exception InvalidOperationException
            Config.ClaimTypes.Where(x => x.EntityType == DirectoryObjectType.User).Take(2).Select(x => x.EntityDataKey = entityDataKey).ToList();
            Assert.Throws<InvalidOperationException>(() => Config.Update(), $"Duplicate EntityDataKey on 2 items already existing in the list should throw exception InvalidOperationException with this message: \"{ConfigUpdateErrorMessage}\"");

            // Remove one of the duplicated EntityDataKey
            Config.ClaimTypes.FirstOrDefault(x => x.EntityDataKey == entityDataKey).EntityDataKey = String.Empty;
            // Set an EntityDataKey on an existing item and add a new item with the same EntityDataKey should throw exception InvalidOperationException
            ClaimTypeConfig ctConfig = new ClaimTypeConfig() { ClaimType = UnitTestsHelper.RandomClaimType, EntityDataKey = entityDataKey, DirectoryObjectProperty = UnitTestsHelper.RandomObjectProperty };
            Assert.Throws<InvalidOperationException>(() => Config.ClaimTypes.Add(ctConfig), $"Set an EntityDataKey on an existing item and add a new item with the same EntityDataKey should throw exception InvalidOperationException with this message: \"Entity metadata '{entityDataKey}' already exists in the collection for the directory object User\"");
        }

        [Test]
        public void DuplicateDirectoryObjectProperty()
        {
            ClaimTypeConfig existingCTConfig = Config.ClaimTypes.FirstOrDefault(x => !String.IsNullOrEmpty(x.ClaimType) && x.EntityType == DirectoryObjectType.User);

            // Create a new ClaimTypeConfig with a DirectoryObjectProperty already set should throw exception InvalidOperationException
            ClaimTypeConfig ctConfig = new ClaimTypeConfig() { ClaimType = UnitTestsHelper.RandomClaimType, EntityType = DirectoryObjectType.User, DirectoryObjectProperty = existingCTConfig.DirectoryObjectProperty };
            Assert.Throws<InvalidOperationException>(() => Config.ClaimTypes.Add(ctConfig), $"Create a new ClaimTypeConfig with a DirectoryObjectProperty already set should throw exception InvalidOperationException with this message: \"An item with property '{existingCTConfig.DirectoryObjectProperty.ToString()}' already exists for the object type 'User'\"");

            // Add a valid ClaimTypeConfig should succeed (done for next test)
            ctConfig.DirectoryObjectProperty = UnitTestsHelper.RandomObjectProperty;
            Assert.DoesNotThrow(() => Config.ClaimTypes.Add(ctConfig), $"Add a valid ClaimTypeConfig should succeed");

            // Update an existing ClaimTypeConfig with a DirectoryObjectProperty already set should throw exception InvalidOperationException
            ctConfig.DirectoryObjectProperty = existingCTConfig.DirectoryObjectProperty;
            Assert.Throws<InvalidOperationException>(() => Config.Update(), $"Update an existing ClaimTypeConfig with a DirectoryObjectProperty already set should throw exception InvalidOperationException with this message: \"{ConfigUpdateErrorMessage}\"");

            // Delete the ClaimTypeConfig should succeed
            Assert.IsTrue(Config.ClaimTypes.Remove(ctConfig), "Delete the ClaimTypeConfig should succeed");
        }

        [Test]
        public void ModifyUserIdentifier()
        {
            IdentityClaimTypeConfig backupIdentityCTConfig = Config.ClaimTypes.FirstOrDefault(x => x is IdentityClaimTypeConfig) as IdentityClaimTypeConfig;
            backupIdentityCTConfig = backupIdentityCTConfig.CopyConfiguration() as IdentityClaimTypeConfig;

            // Member UserType
            Assert.Throws<ArgumentNullException>(() => Config.ClaimTypes.UpdateUserIdentifier(AzureADObjectProperty.NotSet), $"Update user identifier with value NotSet should throw exception ArgumentNullException");

            bool configUpdated = Config.ClaimTypes.UpdateUserIdentifier(UnitTestsHelper.RandomObjectProperty);
            Assert.IsTrue(configUpdated, $"Update user identifier with any AzureADObjectProperty should succeed and return true");

            configUpdated = Config.ClaimTypes.UpdateUserIdentifier(backupIdentityCTConfig.DirectoryObjectProperty);
            Assert.IsTrue(configUpdated, $"Update user identifier with any AzureADObjectProperty should succeed and return true");

            configUpdated = Config.ClaimTypes.UpdateUserIdentifier(backupIdentityCTConfig.DirectoryObjectProperty);
            Assert.IsFalse(configUpdated, $"Update user identifier with the same AzureADObjectProperty should not change anything and return false");

            // Guest UserType
            Assert.Throws<ArgumentNullException>(() => Config.ClaimTypes.UpdateIdentifierForGuestUsers(AzureADObjectProperty.NotSet), $"Update user identifier of Guest UserType with value NotSet should throw exception ArgumentNullException");

            configUpdated = Config.ClaimTypes.UpdateIdentifierForGuestUsers(UnitTestsHelper.RandomObjectProperty);
            Assert.IsTrue(configUpdated, $"Update user identifier of Guest UserType with any AzureADObjectProperty should succeed and return true");

            configUpdated = Config.ClaimTypes.UpdateIdentifierForGuestUsers(backupIdentityCTConfig.DirectoryObjectPropertyForGuestUsers);
            Assert.IsTrue(configUpdated, $"Update user identifier of Guest UserType with any AzureADObjectProperty should succeed and return true");

            configUpdated = Config.ClaimTypes.UpdateIdentifierForGuestUsers(backupIdentityCTConfig.DirectoryObjectPropertyForGuestUsers);
            Assert.IsFalse(configUpdated, $"Update user identifier of Guest UserType with the same AzureADObjectProperty should not change anything and return false");
        }
    }
}
