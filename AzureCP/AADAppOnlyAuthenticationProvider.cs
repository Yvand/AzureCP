﻿using Microsoft.Graph;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.SharePoint.Administration;
using Nito.AsyncEx;
using System;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using static azurecp.ClaimsProviderLogging;

namespace azurecp
{
    public class AADAppOnlyAuthenticationProvider : IAuthenticationProvider
    {
        private string Tenant;
        private string ClientId;
        private string ClientSecret;
        private string AuthorityUri;
        private string ClaimsProviderName;
        private int Timeout;

        private AuthenticationContext AuthContext;
        private ClientCredential Creds;
        private AuthenticationResult AuthNResult;
        private AsyncLock GetAccessTokenLock = new AsyncLock();

        public AADAppOnlyAuthenticationProvider(string authorityUriTemplate, string tenant, string clientId, string appKey, string claimsProviderName, int timeout)
        {
            this.Tenant = tenant;
            this.ClientId = clientId;
            this.ClientSecret = appKey;
            this.AuthorityUri = String.Format(CultureInfo.InvariantCulture, authorityUriTemplate, tenant);
            this.ClaimsProviderName = claimsProviderName;
            this.Timeout = timeout;
        }

        public async Task AuthenticateRequestAsync(HttpRequestMessage request)
        {
            using (GetAccessTokenLock.Lock())
            {
                bool getAccessToken = false;
                if (AuthNResult == null)
                {
                    getAccessToken = true;
                }
                else if (DateTime.Now.ToUniversalTime().Ticks > AuthNResult.ExpiresOn.UtcDateTime.Subtract(TimeSpan.FromMinutes(1)).Ticks)
                {
                    // Access token already expired or will expire within 1 min, let's renew it
                    ClaimsProviderLogging.Log($"[{ClaimsProviderName}] Access token for tenant '{Tenant}' expired, renewing it...", TraceSeverity.Verbose, EventSeverity.Information, TraceCategory.Core);
                    getAccessToken = true;
                }

                if (getAccessToken)
                {
                    bool success = await GetAccessToken(false);
                }

                if (AuthNResult != null && !String.IsNullOrEmpty(AuthNResult.AccessToken))
                {
                    request.Headers.Add("Authorization", $"Bearer {AuthNResult.AccessToken}");
                }
            }
        }

        public async Task<bool> GetAccessToken(bool throwExceptionIfFail)
        {
            ClaimsProviderLogging.Log($"[{ClaimsProviderName}] Getting new access token for tenant '{Tenant}'", TraceSeverity.Verbose, EventSeverity.Information, TraceCategory.Core);
            bool success = true;
            Stopwatch timer = new Stopwatch();
            timer.Start();
            int timeout = this.Timeout;

            try
            {
                AuthContext = new AuthenticationContext(AuthorityUri);
                Creds = new ClientCredential(ClientId, ClientSecret);
                Task<AuthenticationResult> acquireTokenTask = AuthContext.AcquireTokenAsync(ClaimsProviderConstants.GraphAPIResource, Creds);
                AuthNResult = await TaskHelper.TimeoutAfter<AuthenticationResult>(acquireTokenTask, new TimeSpan(0, 0, 0, 0, timeout));

                TimeSpan duration = new TimeSpan(AuthNResult.ExpiresOn.UtcTicks - DateTime.Now.ToUniversalTime().Ticks);
                ClaimsProviderLogging.Log($"[{ClaimsProviderName}] Got new access token for tenant '{Tenant}', valid for {Math.Round((duration.TotalHours), 1)} hour(s) and retrieved in {timer.ElapsedMilliseconds.ToString()} ms", TraceSeverity.High, EventSeverity.Information, TraceCategory.Core);
            }
            catch (AdalServiceException ex)
            {
                ClaimsProviderLogging.Log($"[{ClaimsProviderName}] Unable to get access token for tenant '{Tenant}': {ex.Message}", TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Core);
                success = false;
                if (throwExceptionIfFail) throw ex;
            }
            catch (TimeoutException ex)
            {
                ClaimsProviderLogging.Log($"[{ClaimsProviderName}] Could not get access token before timeout of {timeout.ToString()} ms for tenant '{Tenant}'", TraceSeverity.Unexpected, EventSeverity.Error, TraceCategory.Core);
                success = false;
                if (throwExceptionIfFail) throw ex;
            }
            catch (Exception ex)
            {
                ClaimsProviderLogging.LogException(ClaimsProviderName, $"while getting access token for tenant '{Tenant}'", TraceCategory.Lookup, ex);
                success = false;
                if (throwExceptionIfFail) throw ex;
            }
            finally
            {
                timer.Stop();
            }
            return success;
        }
    }

    public static class TaskHelper
    {
        /// <summary>
        /// Use extension method documented in https://stackoverflow.com/questions/4238345/asynchronously-wait-for-taskt-to-complete-with-timeout
        /// </summary>
        /// <typeparam name="TResult"></typeparam>
        /// <param name="task"></param>
        /// <param name="timeout"></param>
        /// <returns></returns>
        public static async Task<TResult> TimeoutAfter<TResult>(this Task<TResult> task, TimeSpan timeout)
        {
            using (var timeoutCancellationTokenSource = new CancellationTokenSource())
            {
                var completedTask = await Task.WhenAny(task, Task.Delay(timeout, timeoutCancellationTokenSource.Token));
                if (completedTask == task)
                {
                    timeoutCancellationTokenSource.Cancel();
                    return await task;  // Very important in order to propagate exceptions
                }
                else
                {
                    throw new TimeoutException("The operation has timed out.");
                }
            }
        }
    }
}
