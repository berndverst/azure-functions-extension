﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// ------------------------------------------------------------

namespace Dapr.AzureFunctions.Extension
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.WebJobs;
    using Newtonsoft.Json;
    using Newtonsoft.Json.Linq;

    class DaprServiceClient
    {
        readonly HttpClient httpClient;
        readonly string defaultDaprAddress;

        public DaprServiceClient(
            IHttpClientFactory clientFactory,
            INameResolver nameResolver)
        {
            this.httpClient = clientFactory.CreateClient("DaprServiceClient");

            // "daprAddress" is an environment variable created by the Dapr process
            this.defaultDaprAddress = GetDefaultDaprAddress(nameResolver);
        }

        static string GetDefaultDaprAddress(INameResolver resolver)
        {
            if (!int.TryParse(resolver.Resolve("DAPR_HTTP_PORT"), out int daprPort))
            {
                daprPort = 3500;
            }

            return $"http://localhost:{daprPort}";
        }

        static async Task ThrowIfDaprFailure(HttpResponseMessage response)
        {
            if (!response.IsSuccessStatusCode)
            {
                string errorCode = string.Empty;
                string errorMessage = string.Empty;

                if (response.Content != null && response.Content.Headers.ContentLength != 0)
                {
                    JObject daprError;

                    try
                    {
                        string content = await response.Content.ReadAsStringAsync();
                        daprError = JObject.Parse(content);
                    }
                    catch (Exception e)
                    {
                        throw new DaprException(
                            response.StatusCode,
                            "ERR_UNKNOWN",
                            "The returned error message from Dapr Service is not a valid JSON Object.",
                            e);
                    }

                    if (daprError.TryGetValue("message", out JToken? errorMessageToken))
                    {
                        errorMessage = errorMessageToken.ToString();
                    }

                    if (daprError.TryGetValue("errorCode", out JToken? errorCodeToken))
                    {
                        errorCode = errorCodeToken.ToString();
                    }
                }

                // avoid potential overrides: specific 404 error messages can be returned from Dapr
                // ex: https://docs.dapr.io/reference/api/actors_api/#get-actor-state
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    throw new DaprException(
                        response.StatusCode,
                        string.IsNullOrEmpty(errorCode) ? "ERR_DOES_NOT_EXIST" : errorCode,
                        string.IsNullOrEmpty(errorMessage) ? "The requested Dapr resource is not properly configured." : errorMessage);
                }

                throw new DaprException(
                    response.StatusCode,
                    string.IsNullOrEmpty(errorCode) ? "ERR_UNKNOWN" : errorCode,
                    string.IsNullOrEmpty(errorMessage) ? "No meaningful error message is returned." : errorMessage);
            }

            return;
        }

        internal async Task SaveStateAsync(
            string? daprAddress,
            string? stateStore,
            IEnumerable<DaprStateRecord> values,
            CancellationToken cancellationToken)
        {
            if (stateStore == null)
            {
                throw new ArgumentNullException(nameof(stateStore));
            }

            this.EnsureDaprAddress(ref daprAddress);

            HttpResponseMessage response = await this.httpClient.PostAsJsonAsync(
                $"{daprAddress}/v1.0/state/{Uri.EscapeDataString(stateStore)}",
                values,
                cancellationToken);

            await ThrowIfDaprFailure(response);
        }

        internal async Task<DaprStateRecord> GetStateAsync(
            string? daprAddress,
            string stateStore,
            string key,
            CancellationToken cancellationToken)
        {
            this.EnsureDaprAddress(ref daprAddress);

            HttpResponseMessage response = await this.httpClient.GetAsync(
                $"{daprAddress}/v1.0/state/{stateStore}/{key}",
                cancellationToken);

            await ThrowIfDaprFailure(response);

            Stream contentStream = await response.Content.ReadAsStreamAsync();
            string? eTag = response.Headers.ETag?.Tag;
            return new DaprStateRecord(key, contentStream, eTag);
        }

        internal async Task InvokeMethodAsync(
            string? daprAddress,
            string appId,
            string methodName,
            string httpVerb,
            JToken? body,
            CancellationToken cancellationToken)
        {
            this.EnsureDaprAddress(ref daprAddress);

            var req = new HttpRequestMessage(new HttpMethod(httpVerb), $"{daprAddress}/v1.0/invoke/{appId}/method/{methodName}");
            if (body != null)
            {
                req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response = await this.httpClient.SendAsync(req, cancellationToken);
            await ThrowIfDaprFailure(response);
        }

        internal async Task SendToDaprBindingAsync(
            string? daprAddress,
            DaprBindingMessage message,
            CancellationToken cancellationToken)
        {
            this.EnsureDaprAddress(ref daprAddress);

            HttpResponseMessage response = await this.httpClient.PostAsJsonAsync(
                $"{daprAddress}/v1.0/bindings/{message.BindingName}",
                message,
                cancellationToken);

            await ThrowIfDaprFailure(response);
        }

        internal async Task PublishEventAsync(
            string? daprAddress,
            string name,
            string topicName,
            JToken? payload,
            CancellationToken cancellationToken)
        {
            this.EnsureDaprAddress(ref daprAddress);

            var req = new HttpRequestMessage(HttpMethod.Post, $"{daprAddress}/v1.0/publish/{name}/{topicName}");
            if (payload != null)
            {
                req.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");
            }

            HttpResponseMessage response = await this.httpClient.SendAsync(req, cancellationToken);

            await ThrowIfDaprFailure(response);
        }

        internal async Task<JObject> GetSecretAsync(
            string? daprAddress,
            string secretStoreName,
            string? key,
            string? metadata,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(secretStoreName))
            {
                throw new ArgumentNullException(nameof(secretStoreName));
            }

            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentNullException(nameof(key));
            }

            this.EnsureDaprAddress(ref daprAddress);

            string metadataQuery = string.Empty;
            if (!string.IsNullOrEmpty(metadata))
            {
                metadataQuery = "?" + metadata;
            }

            HttpResponseMessage response = await this.httpClient.GetAsync(
                $"{daprAddress}/v1.0/secrets/{secretStoreName}/{key}{metadataQuery}",
                cancellationToken);

            await ThrowIfDaprFailure(response);

            string secretPayload = await response.Content.ReadAsStringAsync();

            // The response is always expected to be a JSON object
            return JObject.Parse(secretPayload);
        }

        void EnsureDaprAddress(ref string? daprAddress)
        {
            (daprAddress ??= this.defaultDaprAddress).TrimEnd('/');
        }

        class DaprException : Exception
        {
            public DaprException(HttpStatusCode statusCode, string errorCode, string message)
                : base(message)
            {
                this.StatusCode = statusCode;
                this.ErrorCode = errorCode;
            }

            public DaprException(HttpStatusCode statusCode, string errorCode, string message, Exception innerException)
                : base(message, innerException)
            {
                this.StatusCode = statusCode;
                this.ErrorCode = errorCode;
            }

            HttpStatusCode StatusCode { get; set; }

            string ErrorCode { get; set; }

            public override string ToString()
            {
                if (this.InnerException != null)
                {
                    return string.Format(
                        "Status Code: {0}; Error Code: {1} ; Message: {2}; Inner Exception: {3}",
                        this.StatusCode,
                        this.ErrorCode,
                        this.Message,
                        this.InnerException);
                }

                return string.Format(
                    "Status Code: {0}; Error Code: {1} ; Message: {2}",
                    this.StatusCode,
                    this.ErrorCode,
                    this.Message);
            }
        }
    }
}