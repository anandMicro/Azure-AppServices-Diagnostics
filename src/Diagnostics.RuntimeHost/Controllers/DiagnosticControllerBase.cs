﻿using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web;
using Diagnostics.DataProviders;
using Diagnostics.Logger;
using Diagnostics.ModelsAndUtils.Attributes;
using Diagnostics.ModelsAndUtils.Models;
using Diagnostics.ModelsAndUtils.Models.ResponseExtensions;
using Diagnostics.ModelsAndUtils.ScriptUtilities;
using Diagnostics.ModelsAndUtils.Utilities;
using Diagnostics.RuntimeHost.Models;
using Diagnostics.RuntimeHost.Services;
using Diagnostics.RuntimeHost.Services.CacheService;
using Diagnostics.RuntimeHost.Services.SourceWatcher;
using Diagnostics.RuntimeHost.Utilities;
using Diagnostics.Scripts;
using Diagnostics.Scripts.Models;
using Diagnostics.Scripts.Utilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using Diagnostics.RuntimeHost.Services.CacheService.Interfaces;

namespace Diagnostics.RuntimeHost.Controllers
{
    public abstract class DiagnosticControllerBase<TResource> : Controller where TResource : IResource
    {
        protected ICompilerHostClient _compilerHostClient;
        protected ISourceWatcherService _sourceWatcherService;
        protected IInvokerCacheService _invokerCache;
        protected IGistCacheService _gistCache;
        protected IDataSourcesConfigurationService _dataSourcesConfigService;
        protected IStampService _stampService;
        protected IAssemblyCacheService _assemblyCacheService;

        public DiagnosticControllerBase(IStampService stampService, ICompilerHostClient compilerHostClient, ISourceWatcherService sourceWatcherService, IInvokerCacheService invokerCache, IGistCacheService gistCache, IDataSourcesConfigurationService dataSourcesConfigService, IAssemblyCacheService assemblyCacheService)
        {
            this._compilerHostClient = compilerHostClient;
            this._sourceWatcherService = sourceWatcherService;
            this._invokerCache = invokerCache;
            this._gistCache = gistCache;
            this._dataSourcesConfigService = dataSourcesConfigService;
            this._stampService = stampService;
            this._assemblyCacheService = assemblyCacheService;
        }

        #region API Response Methods

        protected async Task<IActionResult> ListDetectors(TResource resource)
        {
            DateTimeHelper.PrepareStartEndTimeWithTimeGrain(string.Empty, string.Empty, string.Empty, out DateTime startTimeUtc, out DateTime endTimeUtc, out TimeSpan timeGrainTimeSpan, out string errorMessage);
            RuntimeContext<TResource> cxt = PrepareContext(resource, startTimeUtc, endTimeUtc);
            return Ok(await this.ListDetectorsInternal(cxt));
        }

        protected async Task<IActionResult> GetDetector(TResource resource, string detectorId, string startTime, string endTime, string timeGrain, Form form = null)
        {
            if (!DateTimeHelper.PrepareStartEndTimeWithTimeGrain(startTime, endTime, timeGrain, out DateTime startTimeUtc, out DateTime endTimeUtc, out TimeSpan timeGrainTimeSpan, out string errorMessage))
            {
                return BadRequest(errorMessage);
            }

            var cxt = PrepareContext(resource, startTimeUtc, endTimeUtc, Form: form);
            var detectorResponse = await GetDetectorInternal(detectorId, cxt);
            return detectorResponse == null ? (IActionResult)NotFound() : Ok(DiagnosticApiResponse.FromCsxResponse(detectorResponse.Item1, detectorResponse.Item2));
        }

        protected async Task<IActionResult> ListGists(TResource resource)
        {
            DateTimeHelper.PrepareStartEndTimeWithTimeGrain(string.Empty, string.Empty, string.Empty, out DateTime startTimeUtc, out DateTime endTimeUtc, out TimeSpan timeGrainTimeSpan, out string errorMessage);
            RuntimeContext<TResource> cxt = PrepareContext(resource, startTimeUtc, endTimeUtc);
            return Ok(await ListGistsInternal(cxt));
        }

        protected async Task<IActionResult> GetGist(TResource resource, string id, string startTime, string endTime, string timeGrain)
        {
            if (!DateTimeHelper.PrepareStartEndTimeWithTimeGrain(startTime, endTime, timeGrain, out DateTime startTimeUtc, out DateTime endTimeUtc, out TimeSpan timeGrainTimeSpan, out string errorMessage))
            {
                return BadRequest(errorMessage);
            }

            RuntimeContext<TResource> cxt = PrepareContext(resource, startTimeUtc, endTimeUtc);
            return Ok(await GetGistInternal(id, cxt));
        }

        protected async Task<IActionResult> ListSystemInvokers(TResource resource)
        {
            DateTimeHelper.PrepareStartEndTimeWithTimeGrain(string.Empty, string.Empty, string.Empty, out DateTime startTimeUtc, out DateTime endTimeUtc, out TimeSpan timeGrainTimeSpan, out string errorMessage);
            RuntimeContext<TResource> context = PrepareContext(resource, startTimeUtc, endTimeUtc);

            await this._sourceWatcherService.Watcher.WaitForFirstCompletion();

            var systemInvokers = _invokerCache.GetSystemInvokerList<TResource>(context)
               .Select(p => new DiagnosticApiResponse { Metadata = p.EntryPointDefinitionAttribute });

            return Ok(systemInvokers);
        }

        protected async Task<IActionResult> GetSystemInvoker(TResource resource, string detectorId, string invokerId, string dataSource, string timeRange)
        {
            Dictionary<string, dynamic> systemContext = PrepareSystemContext(resource, detectorId, dataSource, timeRange);

            await this._sourceWatcherService.Watcher.WaitForFirstCompletion();
            var dataProviders = new DataProviders.DataProviders((DataProviderContext)this.HttpContext.Items[HostConstants.DataProviderContextKey]);
            var invoker = this._invokerCache.GetSystemInvoker(invokerId);

            if (invoker == null)
            {
                return null;
            }

            Response res = new Response
            {
                Metadata = invoker.EntryPointDefinitionAttribute,
                IsInternalCall = systemContext["isInternal"]
            };

            var response = (Response)await invoker.Invoke(new object[] { dataProviders, systemContext, res });

            List<DataProviderMetadata> dataProvidersMetadata = GetDataProvidersMetadata(dataProviders);

            return response == null ? (IActionResult)NotFound() : Ok(DiagnosticApiResponse.FromCsxResponse(response, dataProvidersMetadata));
        }

        protected async Task<IActionResult> ExecuteQuery<TPostBodyResource>(TResource resource, CompilationPostBody<TPostBodyResource> jsonBody, string startTime, string endTime, string timeGrain, string detectorId = null, string dataSource = null, string timeRange = null, Form Form = null)
        {
            if (jsonBody == null)
            {
                return BadRequest("Missing body");
            }

            if (string.IsNullOrWhiteSpace(jsonBody.Script))
            {
                return BadRequest("Missing script in body");
            }

            if (jsonBody.References == null)
            {
                jsonBody.References = new Dictionary<string, string>();
            }

            if (!DateTimeHelper.PrepareStartEndTimeWithTimeGrain(startTime, endTime, timeGrain, out DateTime startTimeUtc, out DateTime endTimeUtc, out TimeSpan timeGrainTimeSpan, out string errorMessage))
            {
                return BadRequest(errorMessage);
            }

            await _sourceWatcherService.Watcher.WaitForFirstCompletion();

            var runtimeContext = PrepareContext(resource, startTimeUtc, endTimeUtc, Form: Form);
            var dataProviders = new DataProviders.DataProviders((DataProviderContext)HttpContext.Items[HostConstants.DataProviderContextKey]);

            foreach (var gistRef in _gistCache.GetAllReferences(runtimeContext))
            {
                if (!jsonBody.References.ContainsKey(gistRef.Key))
                {
                    // Add latest version to references
                    jsonBody.References.Add(gistRef);
                }
            }

            if (!Enum.TryParse(jsonBody.EntityType, true, out EntityType entityType))
            {
                entityType = EntityType.Signal;
            }

            var queryRes = new QueryResponse<DiagnosticApiResponse>
            {
                InvocationOutput = new DiagnosticApiResponse()
            };

            var scriptETag = Request.Headers.ContainsKey("diag-script-etag") ? (string)Request.Headers["diag-script-etag"] : string.Empty;
            var assemblyFullName = Request.Headers.ContainsKey("diag-assembly-name") ? HttpUtility.UrlDecode(Request.Headers["diag-assembly-name"]) : string.Empty;

            Assembly tempAsm = null;

            var isCompilationNeeded = !ScriptCompilation.IsSameScript(jsonBody.Script, scriptETag) || !_assemblyCacheService.IsAssemblyLoaded(assemblyFullName, out tempAsm);
            if (isCompilationNeeded)
            {
                queryRes.CompilationOutput = await _compilerHostClient.GetCompilationResponse(jsonBody.Script, jsonBody.EntityType, jsonBody.References, runtimeContext.OperationContext.RequestId);
            }
            else
            {
                // Setting compilation succeeded to be true as it has been successfully compiled before
                queryRes.CompilationOutput = new CompilerResponse
                {
                    CompilationSucceeded = true,
                    CompilationTraces = new string[] { "No code changes were detected. Detector code was executed using previous compilation." }
                };
            }

            if (queryRes.CompilationOutput.CompilationSucceeded)
            {
                try
                {
                    if (isCompilationNeeded)
                    {
                        var asmData = Convert.FromBase64String(queryRes.CompilationOutput.AssemblyBytes);
                        var pdbData = Convert.FromBase64String(queryRes.CompilationOutput.PdbBytes);
                        tempAsm = Assembly.Load(asmData, pdbData);
                        queryRes.CompilationOutput.AssemblyName = tempAsm.FullName;
                        _assemblyCacheService.AddAssemblyToCache(tempAsm.FullName, tempAsm);
                    }

                    Request.HttpContext.Response.Headers.Add("diag-script-etag", Convert.ToBase64String(ScriptCompilation.GetHashFromScript(jsonBody.Script)));
                }
                catch (Exception e)
                {
                    throw new Exception($"Problem while loading Assembly: {e.Message}");
                }

                var metaData = new EntityMetadata(jsonBody.Script, entityType);
                using (var invoker = new EntityInvoker(metaData, ScriptHelper.GetFrameworkReferences(), ScriptHelper.GetFrameworkImports(), jsonBody.References.ToImmutableDictionary()))
                {
                    invoker.InitializeEntryPoint(tempAsm);

                    // Verify Detector with other detectors in the system in case of conflicts
                    List<DataProviderMetadata> dataProvidersMetadata = null;
                    Response invocationResponse = null;
                    bool isInternalCall = true;
                    try
                    {
                        if (detectorId == null)
                        {
                            if (!VerifyEntity(invoker, ref queryRes))
                            {
                                return Ok(queryRes);
                            }

                            var cxt = PrepareContext(resource, startTimeUtc, endTimeUtc, Form: Form);
                            var responseInput = new Response()
                            {
                                Metadata = RemovePIIFromDefinition(invoker.EntryPointDefinitionAttribute, cxt.ClientIsInternal),
                                IsInternalCall = cxt.OperationContext.IsInternalCall
                            };
                            invocationResponse = (Response)await invoker.Invoke(new object[] { dataProviders, cxt.OperationContext, responseInput });
                            invocationResponse.UpdateDetectorStatusFromInsights();
                            isInternalCall = cxt.ClientIsInternal;
                        }
                        else
                        {
                            var systemContext = PrepareSystemContext(resource, detectorId, dataSource, timeRange);
                            var responseInput = new Response()
                            {
                                Metadata = invoker.EntryPointDefinitionAttribute,
                                IsInternalCall = systemContext["isInternal"]
                            };
                            invocationResponse = (Response)await invoker.Invoke(new object[] { dataProviders, systemContext, responseInput });
                        }

                        ValidateForms(invocationResponse.Dataset);
                        if (isInternalCall)
                        {
                            dataProvidersMetadata = GetDataProvidersMetadata(dataProviders);
                        }

                        queryRes.RuntimeSucceeded = true;
                        queryRes.InvocationOutput = DiagnosticApiResponse.FromCsxResponse(invocationResponse, dataProvidersMetadata);
                    }
                    catch (Exception ex)
                    {
                        if (isInternalCall)
                        {
                            queryRes.RuntimeSucceeded = false;
                            queryRes.InvocationOutput = CreateQueryExceptionResponse(ex, invoker.EntryPointDefinitionAttribute, isInternalCall, GetDataProvidersMetadata(dataProviders));
                        }
                        else
                        {
                            throw;
                        }
                    }
                }
            }

            return Ok(queryRes);
        }

        protected async Task<IActionResult> PublishPackage(Package pkg)
        {
            if (pkg == null || string.IsNullOrWhiteSpace(pkg.Id))
            {
                return BadRequest();
            }

            await _sourceWatcherService.Watcher.CreateOrUpdatePackage(pkg);
            return Ok();
        }

        private DiagnosticApiResponse CreateQueryExceptionResponse(Exception ex, Definition detectorDefinition, bool isInternal, List<DataProviderMetadata> dataProvidersMetadata)
        {
            Response response = new Response()
            {
                Metadata = RemovePIIFromDefinition(detectorDefinition, isInternal),
                IsInternalCall = isInternal
            };
            response.AddMarkdownView($"<pre><code>Exception message:<strong> {ex.Message}</strong><br>Stack trace: {ex.StackTrace}</code></pre>", "Detector Runtime Exception");
            return DiagnosticApiResponse.FromCsxResponse(response, dataProvidersMetadata);
        }

        protected async Task<IActionResult> GetInsights(TResource resource, string pesId, string supportTopicId, string startTime, string endTime, string timeGrain)
        {
            if (!DateTimeHelper.PrepareStartEndTimeWithTimeGrain(startTime, endTime, timeGrain, out DateTime startTimeUtc, out DateTime endTimeUtc, out TimeSpan timeGrainTimeSpan, out string errorMessage))
            {
                return BadRequest(errorMessage);
            }

            supportTopicId = ParseCorrectSupportTopicId(supportTopicId);
            var supportTopic = new SupportTopic() { Id = supportTopicId, PesId = pesId };
            RuntimeContext<TResource> cxt = PrepareContext(resource, startTimeUtc, endTimeUtc, true, supportTopic);

            List<AzureSupportCenterInsight> insights = null;
            string error = null;
            var detectorsRun = new List<Definition>();
            IEnumerable<Definition> allDetectors = null;
            try
            {
                allDetectors = (await ListDetectorsInternal(cxt)).Select(detectorResponse => detectorResponse.Metadata);

                var applicableDetectors = allDetectors
                    .Where(detector => string.IsNullOrWhiteSpace(supportTopicId) || detector.SupportTopicList.FirstOrDefault(st => st.Id == supportTopicId) != null);

                var insightGroups = await Task.WhenAll(applicableDetectors.Select(detector => GetInsightsFromDetector(cxt, detector, detectorsRun)));

                insights = insightGroups.Where(group => group != null).SelectMany(group => group).ToList();
            }
            catch (Exception ex)
            {
                error = ex.GetType().ToString();
                DiagnosticsETWProvider.Instance.LogRuntimeHostHandledException(cxt.OperationContext.RequestId, "GetInsights", cxt.OperationContext.Resource.SubscriptionId,
                    cxt.OperationContext.Resource.ResourceGroup, cxt.OperationContext.Resource.Name, ex.GetType().ToString(), ex.ToString());
            }

            var correlationId = Guid.NewGuid();
            var defaultInsightReturned = false;

            // Detectors Ran but no insights
            if (!insights.Any() && detectorsRun.Any())
            {
                defaultInsightReturned = true;
                insights.Add(AzureSupportCenterInsightUtilites.CreateDefaultInsight(cxt.OperationContext, detectorsRun));
            }
            else if (!detectorsRun.Any())
            {
                var defaultDetector = allDetectors.FirstOrDefault(detector => detector.Id.StartsWith("default_insights"));
                if (defaultDetector != null)
                {
                    var defaultDetectorInsights = await GetInsightsFromDetector(cxt, defaultDetector, new List<Definition>());
                    defaultInsightReturned = defaultDetectorInsights.Any();
                    insights.AddRange(defaultDetectorInsights);
                }
            }

            var insightInfo = new
            {
                Total = insights.Count,
                Critical = insights.Count(insight => insight.ImportanceLevel == ImportanceLevel.Critical),
                Warning = insights.Count(insight => insight.ImportanceLevel == ImportanceLevel.Warning),
                Info = insights.Count(insight => insight.ImportanceLevel == ImportanceLevel.Info),
                Default = defaultInsightReturned ? 1 : 0
            };

            DiagnosticsETWProvider.Instance.LogRuntimeHostInsightCorrelation(cxt.OperationContext.RequestId, "ControllerBase.GetInsights", cxt.OperationContext.Resource.SubscriptionId,
                cxt.OperationContext.Resource.ResourceGroup, cxt.OperationContext.Resource.Name, correlationId.ToString(), JsonConvert.SerializeObject(insightInfo));

            var response = new AzureSupportCenterInsightEnvelope()
            {
                CorrelationId = correlationId,
                ErrorMessage = error,
                TotalInsightsFound = insights != null ? insights.Count() : 0,
                Insights = insights
            };

            return Ok(response);
        }

        #endregion API Response Methods

        protected TResource GetResource(string subscriptionId, string resourceGroup, string name)
        {
            return (TResource)Activator.CreateInstance(typeof(TResource), subscriptionId, resourceGroup, name);
        }

        // Purposefully leaving this method in Base class. This method is shared between two resources right now - HostingEnvironment and WebApp
        protected async Task<HostingEnvironment> GetHostingEnvironment(string subscriptionId, string resourceGroup, string name, DiagnosticStampData stampPostBody, DateTime startTime, DateTime endTime)
        {
            if (stampPostBody == null)
            {
                return new HostingEnvironment(subscriptionId, resourceGroup, name);
            }

            var hostingEnv = new HostingEnvironment(subscriptionId, resourceGroup, name)
            {
                Name = stampPostBody.InternalName,
                InternalName = stampPostBody.InternalName,
                ServiceAddress = stampPostBody.ServiceAddress,
                State = stampPostBody.State,
                DnsSuffix = stampPostBody.DnsSuffix,
                UnhealthySince = stampPostBody.UnhealthySince,
                SuspendedOn = stampPostBody.SuspendedOn,
                Location = stampPostBody.Location
            };

            switch (stampPostBody.Kind)
            {
                case DiagnosticStampType.ASEV1:
                    hostingEnv.HostingEnvironmentType = HostingEnvironmentType.V1;
                    break;
                case DiagnosticStampType.ASEV2:
                    hostingEnv.HostingEnvironmentType = HostingEnvironmentType.V2;
                    break;
                default:
                    hostingEnv.HostingEnvironmentType = HostingEnvironmentType.None;
                    break;
            }

            var stampName = !string.IsNullOrWhiteSpace(hostingEnv.InternalName) ? hostingEnv.InternalName : hostingEnv.Name;

            var result = await this._stampService.GetTenantIdForStamp(stampName, hostingEnv.HostingEnvironmentType == HostingEnvironmentType.None, startTime, endTime, (DataProviderContext)HttpContext.Items[HostConstants.DataProviderContextKey]);
            hostingEnv.TenantIdList = result.Item1;
            hostingEnv.PlatformType = result.Item2;

            return hostingEnv;
        }

        private Dictionary<string, dynamic> PrepareSystemContext(TResource resource, string detectorId, string dataSource, string timeRange)
        {
            dataSource = string.IsNullOrWhiteSpace(dataSource) ? "0" : dataSource;
            timeRange = string.IsNullOrWhiteSpace(timeRange) ? "168" : timeRange;

            this.Request.Headers.TryGetValue(HeaderConstants.RequestIdHeaderName, out StringValues requestIds);

            OperationContext<TResource> cxt = new OperationContext<TResource>(
                resource,
                "",
                "",
                true,
                requestIds.FirstOrDefault()
            );

            RuntimeContext<TResource> runtimeContext = new RuntimeContext<TResource>()
            {
                ClientIsInternal = true,
                OperationContext = cxt
            };

            var invoker = this._invokerCache.GetEntityInvoker<TResource>(detectorId, runtimeContext);
            IEnumerable<SupportTopic> supportTopicList = null;
            Definition definition = null;
            if (invoker != null && invoker.EntryPointDefinitionAttribute != null)
            {
                if (invoker.EntryPointDefinitionAttribute.SupportTopicList != null && invoker.EntryPointDefinitionAttribute.SupportTopicList.Any())
                {
                    supportTopicList = invoker.EntryPointDefinitionAttribute.SupportTopicList;
                }

                definition = invoker.EntryPointDefinitionAttribute;
            }

            var systemContext = new Dictionary<string, dynamic>
            {
                { "detectorId", detectorId },
                { "requestIds", requestIds },
                { "isInternal", true },
                { "dataSource", dataSource },
                { "timeRange", timeRange },
                { "supportTopicList", supportTopicList },
                { "definition", definition }
            };
            return systemContext;
        }

        private RuntimeContext<TResource> PrepareContext(TResource resource, DateTime startTime, DateTime endTime, bool forceInternal = false, SupportTopic supportTopic = null, Form Form = null)
        {
            Request.Headers.TryGetValue(HeaderConstants.RequestIdHeaderName, out StringValues requestIds);
            Request.Headers.TryGetValue(HeaderConstants.InternalClientHeader, out StringValues internalCallHeader);
            var isInternalClient = false;
            var internalViewRequested = false;
            if (internalCallHeader.Any())
            {
                bool.TryParse(internalCallHeader.First(), out isInternalClient);
            }

            if (isInternalClient)
            {
                this.Request.Headers.TryGetValue(HeaderConstants.InternalViewHeader, out StringValues internalViewHeader);
                if (internalViewHeader.Any())
                {
                    bool.TryParse(internalViewHeader.First(), out internalViewRequested);
                }
            }

            var operationContext = new OperationContext<TResource>(
                resource,
                DateTimeHelper.GetDateTimeInUtcFormat(startTime).ToString(DataProviderConstants.KustoTimeFormat),
                DateTimeHelper.GetDateTimeInUtcFormat(endTime).ToString(DataProviderConstants.KustoTimeFormat),
                internalViewRequested || forceInternal,
                requestIds.FirstOrDefault(),
                supportTopic: supportTopic,
                form: Form
            );

            return new RuntimeContext<TResource>()
            {
                ClientIsInternal = isInternalClient || forceInternal,
                OperationContext = operationContext
            };
        }

        private async Task<IEnumerable<DiagnosticApiResponse>> ListGistsInternal(RuntimeContext<TResource> context)
        {
            await _sourceWatcherService.Watcher.WaitForFirstCompletion();
            return _gistCache.GetEntityInvokerList(context).Select(p => new DiagnosticApiResponse { Metadata = RemovePIIFromDefinition(p.EntryPointDefinitionAttribute, context.ClientIsInternal) });
        }

        private async Task<string> GetGistInternal(string gistId, RuntimeContext<TResource> context)
        {
            await _sourceWatcherService.Watcher.WaitForFirstCompletion();
            var invoker = this._invokerCache.GetEntityInvoker<TResource>(gistId, context);

            if (invoker == null)
            {
                return null;
            }

            return invoker.EntityMetadata.ScriptText;
        }

        private async Task<IEnumerable<DiagnosticApiResponse>> ListDetectorsInternal(RuntimeContext<TResource> context)
        {
            await this._sourceWatcherService.Watcher.WaitForFirstCompletion();

            return _invokerCache.GetEntityInvokerList<TResource>(context)
                .Select(p => new DiagnosticApiResponse { Metadata = RemovePIIFromDefinition(p.EntryPointDefinitionAttribute, context.ClientIsInternal) });
        }

        private async Task<Tuple<Response, List<DataProviderMetadata>>> GetDetectorInternal(string detectorId, RuntimeContext<TResource> context)
        {
            await this._sourceWatcherService.Watcher.WaitForFirstCompletion();
            var dataProviders = new DataProviders.DataProviders((DataProviderContext)HttpContext.Items[HostConstants.DataProviderContextKey]);
            var invoker = this._invokerCache.GetEntityInvoker<TResource>(detectorId, context);

            if (invoker == null)
            {
                return null;
            }

            var res = new Response
            {
                Metadata = RemovePIIFromDefinition(invoker.EntryPointDefinitionAttribute, context.ClientIsInternal),
                IsInternalCall = context.OperationContext.IsInternalCall
            };

            var response = (Response)await invoker.Invoke(new object[] { dataProviders, context.OperationContext, res });

            List<DataProviderMetadata> dataProvidersMetadata = null;
            response.UpdateDetectorStatusFromInsights();

            if (context.ClientIsInternal)
            {
                dataProvidersMetadata = GetDataProvidersMetadata(dataProviders);
            }

            return new Tuple<Response, List<DataProviderMetadata>>(response, dataProvidersMetadata);
        }

        private async Task<IEnumerable<AzureSupportCenterInsight>> GetInsightsFromDetector(RuntimeContext<TResource> context, Definition detector, List<Definition> detectorsRun)
        {
            Response response = null;

            detectorsRun.Add(detector);

            try
            {
                var fullResponse = await GetDetectorInternal(detector.Id, context);
                if (fullResponse != null)
                {
                    response = fullResponse.Item1;
                }
            }
            catch (Exception ex)
            {
                DiagnosticsETWProvider.Instance.LogRuntimeHostHandledException(context.OperationContext.RequestId, "GetInsightsFromDetector", context.OperationContext.Resource.SubscriptionId,
                    context.OperationContext.Resource.ResourceGroup, context.OperationContext.Resource.Name, ex.GetType().ToString(), ex.ToString());
            }

            // Handle Exception or Not Found
            // Not found can occur if invalid detector is put in detector list
            if (response == null)
            {
                return null;
            }

            var supportCenterInsights = new List<AzureSupportCenterInsight>();

            if (response.AscInsights.Any())
            {
                foreach (var ascInsight in response.AscInsights)
                {
                    LogAscInsight(context, detector, ascInsight);
                    supportCenterInsights.Add(ascInsight);
                }
            }
            else
            {
                var regularToAscInsights = response.Insights.Select(insight =>
                {
                    var ascInsight = AzureSupportCenterInsightUtilites.CreateInsight(insight, context.OperationContext, detector);
                    LogAscInsight(context, detector, ascInsight);
                    return ascInsight;
                });
                supportCenterInsights.AddRange(regularToAscInsights);
            }

            var detectorLists = response.Dataset
                .Where(diagnosicData => diagnosicData.RenderingProperties.Type == RenderingType.Detector)
                .SelectMany(diagnosticData => ((DetectorCollectionRendering)diagnosticData.RenderingProperties).DetectorIds)
                .Distinct();

            if (detectorLists.Any())
            {
                var applicableDetectorMetaData = (await this.ListDetectorsInternal(context)).Where(detectorResponse => detectorLists.Contains(detectorResponse.Metadata.Id));
                var detectorListResponses = await Task.WhenAll(applicableDetectorMetaData.Select(detectorResponse => GetInsightsFromDetector(context, detectorResponse.Metadata, detectorsRun)));

                supportCenterInsights.AddRange(detectorListResponses.Where(detectorInsights => detectorInsights != null).SelectMany(detectorInsights => detectorInsights));
            }

            return supportCenterInsights;
        }

        private void LogAscInsight(RuntimeContext<TResource> context, Definition detector, AzureSupportCenterInsight ascInsight)
        {
            var loggingContent = new
            {
                supportTopicId = context.OperationContext.SupportTopic.Id,
                pesId = context.OperationContext.SupportTopic.PesId,
                insight = ascInsight
            };

            DiagnosticsETWProvider.Instance.LogFullAscInsight(context.OperationContext.RequestId, detector.Id, ascInsight?.ImportanceLevel.ToString(), JsonConvert.SerializeObject(loggingContent));
        }

        // The reason we have this method is that Azure Support Center will pass support topic id in the format below:
        // 1003023/32440119/32457411
        // But the support topic we are using is only the last one
        private string ParseCorrectSupportTopicId(string supportTopicId)
        {
            if (supportTopicId == null)
            {
                return null;
            }

            string[] subIds = supportTopicId.Split("\\");
            return subIds[subIds.Length - 1];
        }

        private List<DataProviderMetadata> GetDataProvidersMetadata(DataProviders.DataProviders dataProviders)
        {
            var dataprovidersMetadata = new List<DataProviderMetadata>();
            foreach (var dataProvider in dataProviders.GetType().GetFields())
            {
                if (dataProvider.FieldType.IsInterface)
                {
                    var metadataProvider = dataProvider.GetValue(dataProviders) as IMetadataProvider;
                    if (metadataProvider != null)
                    {
                        var metadata = metadataProvider.GetMetadata();
                        if (metadata != null)
                        {
                            dataprovidersMetadata.Add(metadata);
                        }
                    }
                }
            }

            return dataprovidersMetadata;
        }

        private bool VerifyEntity(EntityInvoker invoker, ref QueryResponse<DiagnosticApiResponse> queryRes)
        {
            List<EntityInvoker> allDetectors = this._invokerCache.GetAll().ToList();

            foreach (var topicId in invoker.EntryPointDefinitionAttribute.SupportTopicList)
            {
                var existingDetector = allDetectors.FirstOrDefault(p =>
                (!p.EntryPointDefinitionAttribute.Id.Equals(invoker.EntryPointDefinitionAttribute.Id, StringComparison.OrdinalIgnoreCase) && p.EntryPointDefinitionAttribute.SupportTopicList.Contains(topicId)));
                if (existingDetector != default(EntityInvoker))
                {
                    // There exists a detector which has same support topic id.
                    queryRes.CompilationOutput.CompilationSucceeded = false;
                    queryRes.CompilationOutput.AssemblyBytes = string.Empty;
                    queryRes.CompilationOutput.PdbBytes = string.Empty;
                    queryRes.CompilationOutput.CompilationTraces = queryRes.CompilationOutput.CompilationTraces.Concat(new List<string>()
                    {
                        $"Error : There is already a detector(id : {existingDetector.EntryPointDefinitionAttribute.Id}, name : {existingDetector.EntryPointDefinitionAttribute.Name})" +
                        $" that uses the SupportTopic (id : {topicId.Id}, pesId : {topicId.PesId}). System can't have two detectors for same support topic id. Consider merging these two detectors."
                    });

                    return false;
                }
            }

            return true;
        }

        private Definition RemovePIIFromDefinition(Definition definition, bool isInternal)
        {
            if (!isInternal) definition.Author = string.Empty;
            return definition;
        }

        /// <summary>
        /// Validation to check if Form ID is unique and if a form contains a button
        /// </summary>
        private void ValidateForms(List<DiagnosticData> diagnosticDataSet)
        {
            HashSet<int> formIds = new HashSet<int>();
            var detectorForms = diagnosticDataSet.Where(dataset => dataset.RenderingProperties.Type == RenderingType.Form).Select(d => d.Table);
            foreach (DataTable table in detectorForms)
            {
                // Each row has FormID and FormInputs
                foreach (DataRow row in table.Rows)
                {
                    var formId = (int)row[0];
                    if (!formIds.Add(formId))
                    {
                        throw new Exception($"Form ID {formId} already exists. Please give a unique Form ID.");
                    }

                    var formInputs = row[2].CastTo<List<FormInputBase>>();
                    if (!formInputs.Any(input => input.InputType == FormInputTypes.Button))
                    {
                        throw new Exception($"There must at least one button for form id {formId}.");
                    }

                    if (formInputs.Where(input => input.InputType != FormInputTypes.Button).Count() > 5)
                    {
                        throw new Exception("Total number of inputs for a form cannot exceed 5.");
                    }
                }
            }
        }
    }
}
