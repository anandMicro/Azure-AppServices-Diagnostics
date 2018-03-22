﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Text;

namespace Diagnostics.Logger
{
    [EventSource(Name = "Microsoft-Azure-AppService-Diagnostics")]
    public sealed class DiagnosticsETWProvider : DiagnosticsEventSourceBase
    {
        public static DiagnosticsETWProvider Instance = new DiagnosticsETWProvider();

        #region Compile Host Events (ID Range : 1000 - 1999)

        [Event(1000, Level = EventLevel.Informational, Channel = EventChannel.Admin, Message = ETWMessageTemplates.LogCompilerHostMessage)]
        public void LogCompilerHostMessage(string Message)
        {
            WriteDiagnosticsEvent(1000, Message);
        }

        [Event(1001, Level = EventLevel.Error, Channel = EventChannel.Admin, Message = ETWMessageTemplates.LogCompilerHostUnhandledException)]
        public void LogCompilerHostUnhandledException(string RequestId, string Source, string ExceptionType, string ExceptionDetails)
        {
            WriteDiagnosticsEvent(1001,
                RequestId,
                Source,
                ExceptionType,
                ExceptionDetails);
        }

        [Event(1002, Level = EventLevel.Informational, Channel = EventChannel.Admin, Message = ETWMessageTemplates.LogCompilerHostAPISummary)]
        public void LogCompilerHostAPISummary(string RequestId, string Address, string Verb, int StatusCode, long LatencyInMilliseconds, string StartTime, string EndTime)
        {
            WriteDiagnosticsEvent(1002,
                RequestId,
                Address,
                Verb,
                StatusCode,
                LatencyInMilliseconds,
                StartTime,
                EndTime);
        }

        #endregion

        #region Runtime Host Events (ID Range : 2000 - 2499)

        [Event(2000, Level = EventLevel.Informational, Channel = EventChannel.Admin, Message = ETWMessageTemplates.LogRuntimeHostMessage)]
        public void LogRuntimeHostMessage(string Message)
        {
            WriteDiagnosticsEvent(2000, Message);
        }

        [Event(2001, Level = EventLevel.Error, Channel = EventChannel.Admin, Message = ETWMessageTemplates.LogRuntimeHostUnhandledException)]
        public void LogRuntimeHostUnhandledException(string RequestId, string Source, string SubscriptionId, string ResourceGroup, string Resource, string ExceptionType, string ExceptionDetails)
        {
            WriteDiagnosticsEvent(2001,
                RequestId,
                Source,
                SubscriptionId,
                ResourceGroup,
                Resource,
                ExceptionType,
                ExceptionDetails);
        }

        [Event(2002, Level = EventLevel.Informational, Channel = EventChannel.Admin, Message = ETWMessageTemplates.LogRuntimeHostAPISummary)]
        public void LogRuntimeHostAPISummary(string RequestId, string SubscriptionId, string ResourceGroup, string Resource, string Address, string Verb, string OperationName, int StatusCode, long LatencyInMilliseconds, string StartTime, string EndTime)
        {
            WriteDiagnosticsEvent(2002,
                RequestId,
                SubscriptionId,
                ResourceGroup,
                Resource,
                Address,
                Verb,
                OperationName,
                StatusCode,
                LatencyInMilliseconds,
                StartTime,
                EndTime);
        }

        #endregion

        #region SourceWatcher Events (ID Range : 2500 - 2599)

        [Event(2500, Level = EventLevel.Informational, Channel = EventChannel.Admin, Message = ETWMessageTemplates.LogSourceWatcherMessage)]
        public void LogSourceWatcherMessage(string Source, string Message)
        {
            WriteDiagnosticsEvent(2500, Source, Message);
        }

        [Event(2501, Level = EventLevel.Warning, Channel = EventChannel.Admin, Message = ETWMessageTemplates.LogSourceWatcherWarning)]
        public void LogSourceWatcherWarning(string Source, string Message)
        {
            WriteDiagnosticsEvent(2501, Source, Message);
        }

        [Event(2502, Level = EventLevel.Error, Channel = EventChannel.Admin, Message = ETWMessageTemplates.LogSourceWatcherException)]
        public void LogSourceWatcherException(string Source, string Message, string ExceptionType, string ExceptionDetails)
        {
            WriteDiagnosticsEvent(2502, Source, Message, ExceptionType, ExceptionDetails);
        }

        #endregion

        #region Compiler Host Client Events (ID Range: 2600 - 2699)

        [Event(2600, Level = EventLevel.Informational, Channel = EventChannel.Admin, Message = ETWMessageTemplates.LogCompilerHostClientMessage)]
        public void LogCompilerHostClientMessage(string RequestId, string Source, string Message)
        {
            WriteDiagnosticsEvent(2600, RequestId, Source, Message);
        }

        [Event(2601, Level = EventLevel.Error, Channel = EventChannel.Admin, Message = ETWMessageTemplates.LogCompilerHostClientException)]
        public void LogCompilerHostClientException(string RequestId, string Source, string Message, string ExceptionType, string ExceptionDetails)
        {
            WriteDiagnosticsEvent(2601, Source, Message, ExceptionType, ExceptionDetails);
        }

        #endregion

        #region Data Provider Events (ID Range : 3000 - 3999)
        #endregion
    }
}
