﻿// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Threading;
using EnsureThat;
using MediatR;
using Microsoft.Azure.Documents.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Health.CosmosDb.Features.Storage;
using Microsoft.Health.Fhir.Core.Features.Context;

namespace Microsoft.Health.Fhir.CosmosDb.Features.Metrics
{
    public class MetricProcessor : IMetricProcessor
    {
        private readonly IFhirRequestContextAccessor _fhirRequestContextAccessor;
        private readonly IMediator _mediator;
        private readonly ILogger<MetricProcessor> _logger;

        public MetricProcessor(IFhirRequestContextAccessor fhirRequestContextAccessor, IMediator mediator, ILogger<MetricProcessor> logger)
        {
            EnsureArg.IsNotNull(fhirRequestContextAccessor, nameof(fhirRequestContextAccessor));
            EnsureArg.IsNotNull(mediator, nameof(mediator));
            EnsureArg.IsNotNull(logger, nameof(logger));

            _fhirRequestContextAccessor = fhirRequestContextAccessor;
            _mediator = mediator;
            _logger = logger;
        }

        public void UpdateFhirRequestContext<T>(T resourceResponseBase)
            where T : ResourceResponseBase
        {
            UpdateFhirRequestContext(resourceResponseBase.SessionToken, resourceResponseBase.RequestCharge, resourceResponseBase.CollectionSizeUsage, resourceResponseBase.StatusCode);
        }

        public void UpdateFhirRequestContext<T>(FeedResponse<T> feedResponse)
        {
            UpdateFhirRequestContext(feedResponse.SessionToken, feedResponse.RequestCharge, feedResponse.CollectionSizeUsage, statusCode: null);
        }

        public void UpdateFhirRequestContext<T>(StoredProcedureResponse<T> storedProcedureResponse)
        {
            UpdateFhirRequestContext(storedProcedureResponse.SessionToken, storedProcedureResponse.RequestCharge, collectionSizeUsageKilobytes: null, statusCode: storedProcedureResponse.StatusCode);
        }

        /// <summary>
        /// Updates the request context with Cosmos DB info and updates response headers with the session token and request change values.
        /// </summary>
        /// <param name="sessionToken">THe session token</param>
        /// <param name="responseRequestCharge">The request charge.</param>
        /// <param name="collectionSizeUsageKilobytes">The size usage of the Cosmos collection in kilobytes.</param>
        /// <param name="statusCode">The HTTP status code.</param>
        public void UpdateFhirRequestContext(string sessionToken, double responseRequestCharge, long? collectionSizeUsageKilobytes, HttpStatusCode? statusCode)
        {
            if (_fhirRequestContextAccessor.FhirRequestContext == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(sessionToken))
            {
                _fhirRequestContextAccessor.FhirRequestContext.ResponseHeaders[CosmosDbHeaders.SessionToken] = sessionToken;
            }

            AddRequestChargeToFhirRequestContext(responseRequestCharge, collectionSizeUsageKilobytes, statusCode);
        }

        private void AddRequestChargeToFhirRequestContext(double responseRequestCharge, long? collectionSizeUsage, HttpStatusCode? statusCode)
        {
            // If there has already been a request to the database for this request, then there will already by a request charge.
            // We want to update it to the new total.
            // Instead of parsing the header value, we could store the double value on the IFhirRequestContext in addition to storing the header value.
            // The problem with that approach is that the request charge is a Cosmos DB-specific concept and the IFhirRequestContext is independent of data store.
            // Also, at the time of writing, we do not typically issue more than one request to the database per request anyway, so the performance impact should
            // not be felt.

            IFhirRequestContext requestContext = _fhirRequestContextAccessor.FhirRequestContext;

            if (requestContext.ResponseHeaders.ContainsKey(CosmosDbHeaders.RequestCharge))
            {
                requestContext.ResponseHeaders[CosmosDbHeaders.RequestCharge].Append(responseRequestCharge.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                requestContext.ResponseHeaders[CosmosDbHeaders.RequestCharge] = responseRequestCharge.ToString(CultureInfo.InvariantCulture);
            }

            var cosmosMetrics = new CosmosStorageRequestMetricsNotification(requestContext.AuditEventType, requestContext.ResourceType)
            {
                TotalRequestCharge = responseRequestCharge,
            };

            if (collectionSizeUsage.HasValue)
            {
                cosmosMetrics.CollectionSizeUsageKilobytes = collectionSizeUsage;
            }

            if (statusCode.HasValue && statusCode == HttpStatusCode.TooManyRequests)
            {
                cosmosMetrics.ThrottledCount = 1;
            }

            cosmosMetrics.RequestCount = 1;

            try
            {
                _mediator.Publish(cosmosMetrics, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex, "Unable to publish CosmosDB metric.");
            }
        }
    }
}
