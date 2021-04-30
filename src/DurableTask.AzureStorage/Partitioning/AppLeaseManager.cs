﻿//  ----------------------------------------------------------------------------------
//  Copyright Microsoft Corporation
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//  http://www.apache.org/licenses/LICENSE-2.0
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//  ----------------------------------------------------------------------------------

namespace DurableTask.AzureStorage.Partitioning
{
    using DurableTask.AzureStorage.Monitoring;
    using Microsoft.WindowsAzure.Storage;
    using Microsoft.WindowsAzure.Storage.Blob;
    using Newtonsoft.Json;
    using System;
    using System.Net;
    using System.Security.Cryptography;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;

    /// <summary>
    /// Class responsible for starting and stopping the partition manager. Also implements the app lease feature to ensure a single app's partition manager is started at a time.
    /// </summary>
    sealed class AppLeaseManager
    {
        readonly IPartitionManager partitionManager;
        readonly AzureStorageOrchestrationServiceSettings settings;
        readonly string accountName;
        readonly CloudBlobClient storageClient;
        readonly string appLeaseContainerName;
        readonly string appLeaseInfoBlobName;
        readonly AppLeaseOptions options;
        readonly AzureStorageOrchestrationServiceStats stats;
        readonly string taskHub;
        readonly string workerName;
        readonly string appName;
        readonly bool appLeaseIsEnabled;
        readonly CloudBlobContainer appLeaseContainer;
        readonly CloudBlockBlob appLeaseInfoBlob;
        readonly string appLeaseId;

        bool isLeaseOwner;
        int appLeaseIsStarted;
        bool appLeaseShutdownComplete;
        Task renewTask;
        CancellationTokenSource starterTokenSource;
        CancellationTokenSource leaseRenewerCancellationTokenSource;
        TaskCompletionSource<bool> appLeaseTCS;

        public AppLeaseManager(
            IPartitionManager partitionManager,
            AzureStorageOrchestrationServiceSettings settings,
            string accountName, 
            CloudBlobClient storageClient, 
            string appLeaseContainerName,
            string appLeaseInfoBlobName,
            AppLeaseOptions options, 
            AzureStorageOrchestrationServiceStats stats)
        {
            this.partitionManager = partitionManager;
            this.settings = settings;
            this.accountName = accountName;
            this.storageClient = storageClient;
            this.appLeaseContainerName = appLeaseContainerName;
            this.appLeaseInfoBlobName = appLeaseInfoBlobName;
            this.options = options;
            this.stats = stats ?? new AzureStorageOrchestrationServiceStats();

            this.taskHub = settings.TaskHubName;
            this.workerName = settings.WorkerId;
            this.appName = settings.AppName;
            this.appLeaseIsEnabled = this.settings.UseAppLease;
            this.appLeaseContainer = this.storageClient.GetContainerReference(this.appLeaseContainerName);
            this.appLeaseInfoBlob = this.appLeaseContainer.GetBlockBlobReference(this.appLeaseInfoBlobName);

            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.Default.GetBytes(this.appName));
               this.appLeaseId = new Guid(hash).ToString();
            }

            this.isLeaseOwner = false;
        }

        public async Task StartAsync()
        {
            if (!this.appLeaseIsEnabled)
            {
                this.starterTokenSource = new CancellationTokenSource();

                await Task.Run(() => this.PartitionManagerStarter(this.starterTokenSource.Token), this.starterTokenSource.Token);
            }
            else
            {
                await RestartAppLeaseStarterTask();
            }
        }

        async Task PartitionManagerStarter(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await this.partitionManager.StartAsync();
                }
                catch (Exception e)
                {
                    this.settings.Logger.PartitionManagerError(
                        this.accountName,
                        this.settings.TaskHubName,
                        this.workerName,
                        null,
                        $"Error in PartitionManagerStarter task. Exception: {e}");

                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                    continue;
                }

                break;
            }
        }

        async Task RestartAppLeaseStarterTask()
        {
            if (this.starterTokenSource != null)
            {
                this.starterTokenSource.Cancel();
                this.starterTokenSource.Dispose();
            }
            this.starterTokenSource = new CancellationTokenSource();

            await Task.Run(() => this.AppLeaseManagerStarter(this.starterTokenSource.Token), this.starterTokenSource.Token);
        }

        async Task AppLeaseManagerStarter(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    while (!token.IsCancellationRequested && !await this.TryAquireAppLeaseAsync())
                    {
                        await Task.Delay(this.settings.AppLeaseOptions.AcquireInterval);
                    }

                    await this.StartAppLeaseAsync();

                    await this.AwaitUntilAppLeaseStopped();
                }
                catch (Exception e)
                {
                    this.settings.Logger.PartitionManagerError(
                        this.accountName,
                        this.settings.TaskHubName,
                        this.workerName,
                        null,
                        $"Error in AppLeaseStarter task. Exception: {e}");

                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                    continue;
                }
            }
        }

        public async Task StopAsync()
        {
            if (this.appLeaseIsEnabled)
            {
                await this.StopAppLeaseAsync();
            }
            else
            {
                await this.partitionManager.StopAsync();
            }

            this.starterTokenSource.Cancel();
        }

        public async Task ForceChangeAppLeaseAsync()
        {
            if (!this.appLeaseIsEnabled)
            {
                throw new InvalidOperationException("Cannot force change app lease. UseAppLease is not enabled.");
            }
            if (this.isLeaseOwner)
            {
                throw new InvalidOperationException("Cannot force change app lease. App is already the current lease owner.");
            }

            await this.UpdateDesiredSwapAppIdToCurrentApp();

            await this.RestartAppLeaseStarterTask();
        }

        public async Task<bool> CreateContainerIfNotExistsAsync()
        {
            bool result = await appLeaseContainer.CreateIfNotExistsAsync();
            this.stats.StorageRequests.Increment();

            await this.CreateAppLeaseInfoIfNotExistsAsync();

            return result;
        }

        public async Task DeleteContainerAsync()
        {
            try
            {
                if (this.isLeaseOwner)
                {
                    AccessCondition accessCondition = new AccessCondition() { LeaseId = appLeaseId };
                    await this.appLeaseContainer.DeleteIfExistsAsync(accessCondition, null, null);
                }
                else
                {
                    await this.appLeaseContainer.DeleteIfExistsAsync();
                }
            }
            catch (StorageException)
            {
                // If we cannot delete the existing app lease due to another app having a lease, just ignore it.
            }
            finally
            {
                this.stats.StorageRequests.Increment();
            }
        }

        async Task CreateAppLeaseInfoIfNotExistsAsync()
        {
            try
            {
                await this.appLeaseInfoBlob.UploadTextAsync("{}", null, AccessCondition.GenerateIfNoneMatchCondition("*"), null, null);
            }
            catch (StorageException)
            {
                // eat any storage exception related to conflict
                // this means the blob already exist
            }
            finally
            {
                    this.stats.StorageRequests.Increment();
            }
        }

        async Task StartAppLeaseAsync()
        {
            if (Interlocked.CompareExchange(ref this.appLeaseIsStarted, 1, 0) != 0)
            {
                throw new InvalidOperationException("AppLeaseManager has already started");
            }

            this.appLeaseShutdownComplete = false;
            this.leaseRenewerCancellationTokenSource = new CancellationTokenSource();

            await this.partitionManager.StartAsync();

            this.renewTask = await Task.Factory.StartNew(() => this.LeaseRenewer(), this.leaseRenewerCancellationTokenSource.Token);
        }

        async Task StopAppLeaseAsync()
        {
            if (Interlocked.CompareExchange(ref this.appLeaseIsStarted, 0, 1) != 1)
            {
                //idempotent
                return;
            }

            await this.ReleaseLeaseAsync();
            await this.partitionManager.StopAsync();

            if (this.renewTask != null)
            {
                this.leaseRenewerCancellationTokenSource.Cancel();
                await this.renewTask;
            }

            if (appLeaseTCS != null && !appLeaseTCS.Task.IsCompleted)
            {
                appLeaseTCS.SetResult(true);
            }

            this.leaseRenewerCancellationTokenSource?.Dispose();
            this.leaseRenewerCancellationTokenSource = null;

            this.appLeaseShutdownComplete = true;
        }

        async Task<bool> TryAquireAppLeaseAsync()
        {
            AppLeaseInfo appLeaseInfo = await this.GetAppLeaseInfoAsync();

            bool leaseAcquired;
            if (appLeaseInfo.DesiredSwapId == this.appLeaseId)
            {
                leaseAcquired = await this.ChangeLeaseAsync(appLeaseInfo.OwnerId);
            }
            else
            {
                leaseAcquired = await this.TryAquireLeaseAsync();
            }

            this.isLeaseOwner = leaseAcquired;

            return leaseAcquired;
        }

        async Task<bool> ChangeLeaseAsync(string currentLeaseId)
        {
            this.settings.Logger.PartitionManagerInfo(
                this.accountName,
                this.taskHub,
                this.workerName,
                this.appLeaseContainerName,
                $"Attempting to change lease from current owner {currentLeaseId} to {this.appLeaseId}.");

            bool leaseAcquired;

            try
            {
                this.settings.Logger.LeaseAcquisitionStarted(
                    this.accountName,
                    this.taskHub,
                    this.workerName,
                    this.appLeaseContainerName);

                AccessCondition accessCondition = new AccessCondition() { LeaseId = currentLeaseId };
                await appLeaseContainer.ChangeLeaseAsync(this.appLeaseId, accessCondition);

                var appLeaseInfo = new AppLeaseInfo()
                {
                    OwnerId = this.appLeaseId,
                };

                await this.UpdateAppLeaseInfoBlob(appLeaseInfo);
                leaseAcquired = true;

                this.settings.Logger.LeaseAcquisitionSucceeded(
                    this.accountName,
                    this.taskHub,
                    this.workerName,
                    this.appLeaseContainerName);

                // When changing the lease over to another app, the paritions will still be listened to on the first app until the AppLeaseManager
                // renew task fails to renew the lease. To avoid potential split brain we must delay before the new lease holder can start
                // listening to the partitions.
                if (this.settings.UseLegacyPartitionManagement == true)
                {
                    await Task.Delay(this.settings.AppLeaseOptions.RenewInterval);
                }
            }
            catch (StorageException e)
            {
                leaseAcquired = false;

                this.settings.Logger.PartitionManagerWarning(
                    this.accountName,
                    this.taskHub,
                    this.workerName,
                    this.appLeaseContainerName,
                    $"Failed to change app lease from currentLeaseId {currentLeaseId} to {this.appLeaseId}. Exception: {e.Message}");
            }
            finally
            {
                this.stats.StorageRequests.Increment();
            }

            return leaseAcquired;
        }

        async Task<bool> TryAquireLeaseAsync()
        {
            bool leaseAcquired;

            try
            {
                this.settings.Logger.LeaseAcquisitionStarted(
                    this.accountName,
                    this.taskHub,
                    this.workerName,
                    this.appLeaseContainerName);

                await appLeaseContainer.AcquireLeaseAsync(this.options.LeaseInterval, this.appLeaseId);

                await this.UpdateOwnerAppIdToCurrentApp();
                leaseAcquired = true;

                this.settings.Logger.LeaseAcquisitionSucceeded(
                    this.accountName,
                    this.taskHub,
                    this.workerName,
                    this.appLeaseContainerName);
            }
            catch (StorageException e)
            {
                leaseAcquired = false;

                this.settings.Logger.LeaseAcquisitionFailed(
                    this.accountName,
                    this.taskHub,
                    this.workerName,
                    this.appLeaseContainerName);

                this.settings.Logger.PartitionManagerWarning(
                    this.accountName,
                    this.taskHub,
                    this.workerName,
                    this.appLeaseContainerName,
                    $"Failed to acquire app lease with appLeaseId {this.appLeaseId}. Another app likely has the lease on this container. Exception: {e.Message}");
            }
            finally
            {
                this.stats.StorageRequests.Increment();
            }

            return leaseAcquired;
        }

        async Task LeaseRenewer()
        {
            this.settings.Logger.PartitionManagerInfo(
                this.accountName,
                this.taskHub,
                this.workerName,
                this.appLeaseContainerName,
                $"Starting background renewal of app lease with interval: {this.options.RenewInterval}.");

            while (this.appLeaseIsStarted == 1 || !appLeaseShutdownComplete)
            {
                try
                {
                    bool renewSucceeded = await RenewLeaseAsync();

                    if (!renewSucceeded)
                    {
                        break;
                    }

                    await Task.Delay(this.options.RenewInterval, this.leaseRenewerCancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    this.settings.Logger.PartitionManagerInfo(
                        this.accountName,
                        this.taskHub,
                        this.workerName,
                        this.appLeaseContainerName,
                        "Background renewal task was canceled.");
                }
                catch (Exception ex)
                {
                    this.settings.Logger.PartitionManagerError(
                        this.accountName, 
                        this.taskHub, 
                        this.workerName,
                        this.appLeaseContainerName, 
                        $"App lease renewer task failed. AppLeaseId: {this.appLeaseId} Exception: {ex}");
                }
            }

            this.settings.Logger.PartitionManagerInfo(
                this.accountName,
                this.taskHub,
                this.workerName,
                this.appLeaseContainerName,
                "Background app lease renewer task completed.");

            this.settings.Logger.PartitionManagerInfo(
                this.accountName,
                this.taskHub,
                this.workerName,
                this.appLeaseContainerName,
                "Lease renewer task completing. Stopping AppLeaseManager.");

            _ = Task.Run(() => this.StopAppLeaseAsync());
        }

        async Task<bool> RenewLeaseAsync()
        {
            bool renewed;
            string errorMessage = string.Empty;

            try
            {
                this.settings.Logger.StartingLeaseRenewal(
                    this.accountName,
                    this.taskHub,
                    this.workerName,
                    this.appLeaseContainerName,
                    this.appLeaseId);

                AccessCondition accessCondition = new AccessCondition() { LeaseId = appLeaseId };
                await appLeaseContainer.RenewLeaseAsync(accessCondition);

                renewed = true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;

                if (ex is StorageException storageException
                    && (storageException.RequestInformation.HttpStatusCode == (int)HttpStatusCode.Conflict
                        || storageException.RequestInformation.HttpStatusCode == (int)HttpStatusCode.PreconditionFailed))
                {
                    renewed = false;
                    this.isLeaseOwner = false;

                    this.settings.Logger.LeaseRenewalFailed(
                        this.accountName,
                        this.taskHub,
                        this.workerName,
                        this.appLeaseContainerName,
                        this.appLeaseId,
                        ex.Message);

                    this.settings.Logger.PartitionManagerWarning(
                        this.accountName,
                        this.taskHub,
                        this.workerName,
                        this.appLeaseContainerName,
                        $"AppLeaseManager failed to renew lease. AppLeaseId: {this.appLeaseId} Exception: {ex}");
                }
                else
                {
                    // Eat any exceptions during renew and keep going.
                    // Consider the lease as renewed.  Maybe lease store outage is causing the lease to not get renewed.
                    renewed = true;
                }
            }
            finally
            {
                this.stats.StorageRequests.Increment();
            }

            this.settings.Logger.LeaseRenewalResult(
                this.accountName,
                this.taskHub,
                this.workerName,
                this.appLeaseContainerName,
                renewed,
                this.appLeaseId,
                errorMessage);

            return renewed;
        }

        async Task ReleaseLeaseAsync()
        {
            try
            {
                AccessCondition accessCondition = new AccessCondition() { LeaseId = this.appLeaseId };
                await this.appLeaseContainer.ReleaseLeaseAsync(accessCondition);

                this.isLeaseOwner = false;

                this.settings.Logger.LeaseRemoved(
                    this.accountName,
                    this.taskHub,
                    this.workerName,
                    this.appLeaseContainerName,
                    this.appLeaseId);
            }
            catch (Exception)
            {
                this.settings.Logger.LeaseRemovalFailed(
                    this.accountName, 
                    this.taskHub, 
                    this.workerName,
                    this.appLeaseContainerName, 
                    this.appLeaseId);
            }
        }

        async Task UpdateOwnerAppIdToCurrentApp()
        {
            var appLeaseInfo = await GetAppLeaseInfoAsync();
            if (appLeaseInfo.OwnerId != this.appLeaseId)
            {
                appLeaseInfo.OwnerId = this.appLeaseId;
                await UpdateAppLeaseInfoBlob(appLeaseInfo);
            }
        }

        async Task UpdateDesiredSwapAppIdToCurrentApp()
        {
            var appLeaseInfo = await GetAppLeaseInfoAsync();
            if (appLeaseInfo.DesiredSwapId != this.appLeaseId)
            {
                appLeaseInfo.DesiredSwapId = this.appLeaseId;
                await UpdateAppLeaseInfoBlob(appLeaseInfo);
            }
        }

        async Task UpdateAppLeaseInfoBlob(AppLeaseInfo appLeaseInfo)
        {
            string serializedInfo = JsonConvert.SerializeObject(appLeaseInfo);
            try
            {
                await this.appLeaseInfoBlob.UploadTextAsync(serializedInfo);
            }
            catch (StorageException)
            {
                // eat any storage exception related to conflict
            }
            finally
            {
                this.stats.StorageRequests.Increment();
            }
        }

        async Task<AppLeaseInfo> GetAppLeaseInfoAsync()
        {
            if (await this.appLeaseInfoBlob.ExistsAsync())
            {
                await appLeaseInfoBlob.FetchAttributesAsync();
                this.stats.StorageRequests.Increment();
                string serializedEventHubInfo = await this.appLeaseInfoBlob.DownloadTextAsync();
                this.stats.StorageRequests.Increment();
                return JsonConvert.DeserializeObject<AppLeaseInfo>(serializedEventHubInfo);
            }

            this.stats.StorageRequests.Increment();
            return null;
        }

        Task AwaitUntilAppLeaseStopped()
        {
            this.appLeaseTCS = new TaskCompletionSource<bool>();
            return this.appLeaseTCS.Task;
        }

        private class AppLeaseInfo
        {
            public string OwnerId { get; set; }
            public string DesiredSwapId { get; set; }
        }
    }
}
