﻿using Microsoft.SharePoint.Administration.Claims;
using Microsoft.SharePoint.WebControls;
using NUnit.Framework;
using System;
using System.Security.Claims;

namespace AzureCP.Tests
{
    [TestFixture]
    [Parallelizable(ParallelScope.Children)]
    public class PeoplePickerTests
    {
        [Test, TestCaseSource(typeof(SearchEntityDataSource), "GetTestData")]
        public void SearchEntities(SearchEntityData registrationData)
        {
            SPProviderHierarchyTree[] providerResults = UnitTestsHelper.DoSearchOperation(registrationData.Input);
            UnitTestsHelper.VerifySearchResult(providerResults, registrationData.ExpectedResultCount, registrationData.ExpectedEntityClaimValue);
        }

        [Test, TestCaseSource(typeof(ValidateEntityDataSource), "GetTestData")]
        [MaxTime(UnitTestsHelper.MaxTime)]
        [Repeat(UnitTestsHelper.TestRepeatCount)]
        public void ValidateClaim(ValidateEntityData registrationData)
        {
            SPClaim inputClaim = new SPClaim(UnitTestsHelper.SPTrust.IdentityClaimTypeInformation.MappedClaimType, registrationData.ClaimValue, ClaimValueTypes.String, SPOriginalIssuers.Format(SPOriginalIssuerType.TrustedProvider, UnitTestsHelper.SPTrust.Name));
            PickerEntity[] entities = UnitTestsHelper.DoValidationOperation(inputClaim);
            UnitTestsHelper.VerifyValidationResult(entities, registrationData.ShouldValidate, registrationData.ClaimValue);
        }

        //[TestCaseSource(typeof(SearchEntityDataSourceCollection))]
        public void DEBUG_SearchEntitiesFromCollection(string inputValue, string expectedCount, string expectedClaimValue)
        {
            SPProviderHierarchyTree[] providerResults = UnitTestsHelper.DoSearchOperation(inputValue);
            UnitTestsHelper.VerifySearchResult(providerResults, Convert.ToInt32(expectedCount), expectedClaimValue);
        }

        //[TestCase(@"AADGroup1", 1, "5b0f6c56-c87f-44c3-9354-56cba03da433")]
        public void DEBUG_SearchEntities(string inputValue, int expectedResultCount, string expectedEntityClaimValue)
        {
            SPProviderHierarchyTree[] providerResults = UnitTestsHelper.DoSearchOperation(inputValue);
            UnitTestsHelper.VerifySearchResult(providerResults, expectedResultCount, expectedEntityClaimValue);
        }

        //[TestCase("http://schemas.microsoft.com/ws/2008/06/identity/claims/role", "5b0f6c56-c87f-44c3-9354-56cba03da433", true)]
        public void DEBUG_ValidateClaim(string claimType, string claimValue, bool shouldValidate)
        {
            SPClaim inputClaim = new SPClaim(claimType, claimValue, ClaimValueTypes.String, SPOriginalIssuers.Format(SPOriginalIssuerType.TrustedProvider, UnitTestsHelper.SPTrust.Name));
            PickerEntity[] entities = UnitTestsHelper.DoValidationOperation(inputClaim);
            UnitTestsHelper.VerifyValidationResult(entities, shouldValidate, claimValue);
        }
    }    
}
