﻿using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.ServiceFabric.Data;
using Microsoft.ServiceFabric.Data.Collections;
using Microsoft.ServiceFabric.Data.Notifications;
using Microsoft.ServiceFabric.Services.Communication.AspNetCore;
using Microsoft.ServiceFabric.Services.Communication.Runtime;
using Microsoft.ServiceFabric.Services.Runtime;
using ReportProcessingService.Models;
using System;
using System.Collections.Generic;
using System.Fabric;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReportProcessingService
{
    /// <summary>
    /// The FabricRuntime creates an instance of this class for each service type instance. 
    /// </summary>
    internal sealed class ReportProcessingService : StatefulService
    {
        internal static readonly Uri ProcessingQueueName = new Uri("store://processingQueue");
        internal static readonly Uri StatusDictionaryName = new Uri("store://statusDictionary");

        private const string cpuMetricName = "CPU";
        private const string memoryMetricName = "MemoryMB";
        private const string processingCapacityMetricName = "ProcessingCapacity";
        private const int queueLengthMultiplier = 10;

        private readonly ProcessPerformanceCounter cpuCounter;
        private readonly ProcessPerformanceCounter memoryCounter;
        private readonly int processingMultiplier;
        private readonly Random random = new Random();
        private readonly ReportContext reportContext;

        private CancellationTokenSource metricReportCancellationSource;
        private Task metricReportTask;

        public ReportProcessingService(StatefulServiceContext context)
            : base(context)
        {
            this.reportContext = new ReportContext(this.Context.ServiceName.Segments.Last());
            this.processingMultiplier = BitConverter.ToInt32(context.InitializationData, 0);
            this.cpuCounter = new ProcessPerformanceCounter("% Processor Time");
            this.memoryCounter = new ProcessPerformanceCounter("Working Set - Private");
        }

        protected override Task OnOpenAsync(ReplicaOpenMode openMode, CancellationToken cancellationToken)
        {
            this.metricReportCancellationSource = new CancellationTokenSource();
            CancellationToken token = this.metricReportCancellationSource.Token;

            this.metricReportTask = Task.Run(async () =>
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();

                    await ReportLoadMetricsAsync(token);
                    await Task.Delay(TimeSpan.FromSeconds(20), token);
                }
            }
            , token)
            .ContinueWith(t => t.Exception.Handle(e => true));

            return base.OnOpenAsync(openMode, cancellationToken);
        }

        protected override Task OnCloseAsync(CancellationToken cancellationToken)
        {
            if (this.metricReportCancellationSource != null)
            {
                this.metricReportCancellationSource.Cancel();
                this.metricReportCancellationSource.Dispose();
            }

            return base.OnCloseAsync(cancellationToken);
        }

        /// <summary>
        /// Starts up ASP.NET Core IWebHost to handle requests to the service.
        /// </summary>
        /// <returns>The collection of listeners.</returns>
        protected override IEnumerable<ServiceReplicaListener> CreateServiceReplicaListeners()
        {
            return new ServiceReplicaListener[]
            {
                new ServiceReplicaListener(serviceContext =>
                    new KestrelCommunicationListener(serviceContext, (url, listener) =>
                    {
                        ServiceEventSource.Current.ServiceMessage(serviceContext, $"Starting Kestrel on {url}");

                        return new WebHostBuilder()
                            .UseKestrel()
                            .ConfigureServices(
                                services => services
                                    .AddSingleton<ReportContext>(this.reportContext)
                                    .AddSingleton<IReliableStateManager>(this.StateManager)
                                    .AddSingleton<StatefulServiceContext>(serviceContext))
                            .UseContentRoot(Directory.GetCurrentDirectory())
                            .UseStartup<Startup>()
                            .UseApplicationInsights()
                            .UseServiceFabricIntegration(listener, ServiceFabricIntegrationOptions.UseUniqueServiceUrl)
                            .UseUrls(url)
                            .Build();
                    }))
            };
        }

        /// <summary>
        /// Performs the main processing work.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        protected override async Task RunAsync(CancellationToken cancellationToken)
        {
            try
            {
                IReliableQueue<ReportProcessingStep> processQueue
                    = await this.StateManager.GetOrAddAsync<IReliableQueue<ReportProcessingStep>>(ProcessingQueueName);

                IReliableDictionary<string, ReportStatus> statusDictionary
                    = await this.StateManager.GetOrAddAsync<IReliableDictionary<string, ReportStatus>>(StatusDictionaryName);

                // First time setup: queue up all the processing steps and create an initial processing status if one doesn't exist already
                // Note that this will execute every time a replica is promoted to primary,
                // so we need to check to see if it's already been done because we only want to initialize once.
                using (ITransaction tx = this.StateManager.CreateTransaction())
                {
                    ConditionalValue<ReportStatus> tryGetResult
                        = await statusDictionary.TryGetValueAsync(tx, this.reportContext.Name, LockMode.Update);

                    if (!tryGetResult.HasValue)
                    {
                        await processQueue.EnqueueAsync(tx, new ReportProcessingStep("Creating"));
                        await processQueue.EnqueueAsync(tx, new ReportProcessingStep("Evaluating"));
                        await processQueue.EnqueueAsync(tx, new ReportProcessingStep("Reticulating"));

                        for (int i = 0; i < processingMultiplier * queueLengthMultiplier; ++i)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await processQueue.EnqueueAsync(tx, new ReportProcessingStep($"Processing {i}"));
                        }

                        await processQueue.EnqueueAsync(tx, new ReportProcessingStep("Sanitizing"));
                        await processQueue.EnqueueAsync(tx, new ReportProcessingStep("Mystery Step"));
                        await processQueue.EnqueueAsync(tx, new ReportProcessingStep("Finalizing"));
                        await processQueue.EnqueueAsync(tx, new ReportProcessingStep("Complete"));

                        await statusDictionary.AddAsync(tx, this.reportContext.Name, new ReportStatus(0, processingMultiplier * queueLengthMultiplier + 7, "Not started.", String.Empty));
                    }

                    await tx.CommitAsync();
                }

                // start processing and checkpoint between each step so we don't lose any progress in the event of a fail-over
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        // Get the next step from the queue with a peek.
                        // This keeps the item on the queue in case processing fails.
                        ConditionalValue<ReportProcessingStep> dequeueResult;
                        using (ITransaction tx = this.StateManager.CreateTransaction())
                        {
                            dequeueResult = await processQueue.TryPeekAsync(tx, LockMode.Default);
                        }

                        if (!dequeueResult.HasValue)
                        {
                            // all done!
                            break;
                        }

                        ReportProcessingStep currentProcessingStep = dequeueResult.Value;

                        ServiceEventSource.Current.ServiceMessage(
                            this.Context,
                            $"Processing step: {currentProcessingStep.Name}");

                        // Get the current processing step
                        ConditionalValue<ReportStatus> dictionaryGetResult;
                        using (ITransaction tx = this.StateManager.CreateTransaction())
                        {
                            dictionaryGetResult = await statusDictionary.TryGetValueAsync(tx, this.reportContext.Name, LockMode.Default);
                        }

                        // Perform the next processing step. 
                        // This is potentially a long-running operation, therefore it is not executed within a transaction.
                        ReportStatus currentStatus = dictionaryGetResult.Value;
                        ReportStatus newStatus = await this.ProcessReport(currentStatus, currentProcessingStep, cancellationToken);

                        // Once processing is done, save the results and dequeue the processing step.
                        // If an exception or other failure occurs at this point, the processing step will run again.
                        using (ITransaction tx = this.StateManager.CreateTransaction())
                        {
                            dequeueResult = await processQueue.TryDequeueAsync(tx);

                            await statusDictionary.SetAsync(tx, this.reportContext.Name, newStatus);

                            await tx.CommitAsync();
                        }

                        ServiceEventSource.Current.ServiceMessage(
                           this.Context,
                           $"Processing step: {currentProcessingStep.Name} complete.");

                    }
                    catch (TimeoutException)
                    {
                        // transient error. Retry.
                        ServiceEventSource.Current.ServiceMessage(this.Context, "TimeoutException in RunAsync.");
                    }
                    catch (FabricTransientException fte)
                    {
                        // transient error. Retry.
                        ServiceEventSource.Current.ServiceMessage(this.Context, "FabricTransientException in RunAsync: {0}", fte.Message);
                    }
                    catch (FabricNotReadableException)
                    {
                        // retry or wait until not primary
                        ServiceEventSource.Current.ServiceMessage(this.Context, "FabricNotReadableException in RunAsync.");
                    }

                    // delay between each to step to prevent starving other processing service instances.
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }

                ServiceEventSource.Current.ServiceMessage(
                    this.Context,
                        $"Processing complete!");
            }
            catch (OperationCanceledException)
            {
                // time to quit
                throw;
            }
            catch (FabricNotPrimaryException)
            {
                // time to quit
                return;
            }
            catch (Exception ex)
            {
                // all other exceptions: log and re-throw.
                ServiceEventSource.Current.ServiceMessage(this.Context, "Exception in RunAsync: {0}", ex.Message);

                throw;
            }
        }


        private async Task ReportLoadMetricsAsync(CancellationToken token)
        {
            List<LoadMetric> metrics = new List<LoadMetric>(3);

            try
            {
                ConditionalValue<IReliableDictionary<string, ReportStatus>> tryGetDictionaryResult =
                    await this.StateManager.TryGetAsync<IReliableDictionary<string, ReportStatus>>(ReportProcessingService.StatusDictionaryName);

                if (tryGetDictionaryResult.HasValue)
                {
                    IReliableDictionary<string, ReportStatus> dictionary = tryGetDictionaryResult.Value;

                    using (ITransaction tx = this.StateManager.CreateTransaction())
                    {
                        ConditionalValue<ReportStatus> getResult = await dictionary.TryGetValueAsync(tx, this.reportContext.Name);

                        if (getResult.HasValue)
                        {
                            metrics.Add(new LoadMetric(processingCapacityMetricName, getResult.Value.Remaining / queueLengthMultiplier));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ServiceEventSource.Current.ServiceMessage(this.Context, $"Failed to get processing capacity metric. {ex.Message}");
            }

            try
            {
                int cpuMetricValue = this.GetCpuCounterValue();
                int memoryMetricValue = this.GetMemoryMbCounterValue();

                metrics.Add(new LoadMetric(cpuMetricName, cpuMetricValue));
                metrics.Add(new LoadMetric(memoryMetricName, memoryMetricValue));

                this.Partition.ReportLoad(metrics);

                ServiceEventSource.Current.ServiceMessage(
                    this.Context,
                    $"Metrics reported: {String.Join(", ", metrics.Select(x => x.Name + ": " + x.Value))}");

            }
            catch (ProcessPerformanceCounterException ppce)
            {
                // transient error.
                ServiceEventSource.Current.ServiceMessage(this.Context, ppce.Message);
            }
        }


        /// <summary>
        /// Gets the next value from the process CPU performance counter.
        /// </summary>
        /// <remarks>
        /// The process CPU performance counter returns the average CPU usage over the time period since it was last called.
        /// The value is divided by the number of processors on the machine to provide a percentage of overall CPU usage.
        /// </remarks>
        /// <returns></returns>
        private int GetCpuCounterValue()
        {
            return (int)Math.Round(this.cpuCounter.NextValue() / Environment.ProcessorCount);
        }

        /// <summary>
        /// Gets the next value from the process memory performance counter in MB.
        /// </summary>
        /// <returns></returns>
        private int GetMemoryMbCounterValue()
        {
            return (int)Math.Round(this.memoryCounter.NextValue() / 1048576);
        }

        /// <summary>
        /// "Processes" a report
        /// </summary>
        /// <param name="currentStatus"></param>
        /// <param name="currentProcessingStep"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        internal async Task<ReportStatus> ProcessReport(ReportStatus currentStatus, ReportProcessingStep currentProcessingStep, CancellationToken cancellationToken)
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

            TimeSpan ioDelay = TimeSpan.FromMilliseconds(this.random.Next(100, 200) / this.processingMultiplier);
            int randomLength = this.random.Next(1000, 2000) * this.processingMultiplier;
            string randomJunk = "";

            for (int i = 0; i < (10 * this.processingMultiplier); ++i)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // pointless inefficient string processing to drive up CPU and memory use
                randomJunk += new string(Enumerable.Repeat(chars, randomLength).Select(s => s[this.random.Next(s.Length)]).ToArray());

                int numberOfAs = 0;
                for (int j = 0; j < randomJunk.Length; ++j)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (randomJunk[j] == 'A')
                    {
                        ++numberOfAs;
                    }
                }

                await Task.Delay(ioDelay, cancellationToken);
            }

            return new ReportStatus(currentStatus.Step + 1, currentStatus.Remaining - 1, currentProcessingStep.Name, randomJunk);
        }
    }
}