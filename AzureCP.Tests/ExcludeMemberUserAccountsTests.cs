﻿using NUnit.Framework;

namespace AzureCP.Tests
{
    [TestFixture]
    public class ExcludeMemberUserAccountsTests : UserAccountsTestsBase
    {
        public override bool ExcludeGuestUsers => false;
        public override bool ExcludeMemberUsers => true;
    }
}
