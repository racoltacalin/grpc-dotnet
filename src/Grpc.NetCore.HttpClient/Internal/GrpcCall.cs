﻿#region Copyright notice and license

// Copyright 2019 The gRPC Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#endregion

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;

namespace Grpc.NetCore.HttpClient.Internal
{
    internal class GrpcCall<TRequest, TResponse> : IDisposable
    {
        private readonly CancellationTokenSource _callCts;
        private readonly CancellationTokenRegistration? _ctsRegistration;
        private readonly ISystemClock _clock;
        private readonly TimeSpan? _timeout;
        private Timer _deadlineTimer;
        private Metadata _trailers;
        private string _headerValidationError;
        private TaskCompletionSource<Stream> _writeStreamTcs;
        private TaskCompletionSource<bool> _completeTcs;

        public bool DeadlineReached { get; private set; }
        public bool Disposed { get; private set; }
        public bool ResponseFinished { get; private set; }
        public HttpResponseMessage HttpResponse { get; private set; }
        public CallOptions Options { get; }
        public Method<TRequest, TResponse> Method { get; }
        public Task SendTask { get; private set; }
        public HttpContentClientStreamWriter<TRequest, TResponse> ClientStreamWriter { get; private set; }
        public HttpContentClientStreamReader<TRequest, TResponse> ClientStreamReader { get; private set; }

        public GrpcCall(Method<TRequest, TResponse> method, CallOptions options, ISystemClock clock)
        {
            // Validate deadline before creating any objects that require cleanup
            ValidateDeadline(options.Deadline);

            _callCts = new CancellationTokenSource();
            Method = method;
            Options = options;
            _clock = clock;

            if (options.CancellationToken.CanBeCanceled)
            {
                // The cancellation token will cancel the call CTS
                _ctsRegistration = options.CancellationToken.Register(CancelCall);
            }

            if (options.Deadline != null && options.Deadline != DateTime.MaxValue)
            {
                var timeout = options.Deadline.Value - _clock.UtcNow;
                _timeout = (timeout > TimeSpan.Zero) ? timeout : TimeSpan.Zero;
            }
        }

        private void ValidateDeadline(DateTime? deadline)
        {
            if (deadline != null && deadline != DateTime.MaxValue && deadline != DateTime.MinValue && deadline.Value.Kind != DateTimeKind.Utc)
            {
                throw new InvalidOperationException("Deadline must have a kind DateTimeKind.Utc or be equal to DateTime.MaxValue or DateTime.MinValue.");
            }
        }

        public CancellationToken CancellationToken
        {
            get { return _callCts.Token; }
        }

        public bool IsCancellationRequested
        {
            get { return _callCts.IsCancellationRequested; }
        }

        public void StartUnary(System.Net.Http.HttpClient client, TRequest request)
        {
            var message = CreateHttpRequestMessage();
            SetMessageContent(request, message);
            StartSend(client, message);
        }

        public void StartClientStreaming(System.Net.Http.HttpClient client)
        {
            var message = CreateHttpRequestMessage();
            ClientStreamWriter = CreateWriter(message);
            StartSend(client, message);
        }

        public void StartServerStreaming(System.Net.Http.HttpClient client, TRequest request)
        {
            var message = CreateHttpRequestMessage();
            SetMessageContent(request, message);
            StartSend(client, message);
            ClientStreamReader = new HttpContentClientStreamReader<TRequest, TResponse>(this);
        }

        public void StartDuplexStreaming(System.Net.Http.HttpClient client)
        {
            var message = CreateHttpRequestMessage();
            ClientStreamWriter = CreateWriter(message);
            StartSend(client, message);
            ClientStreamReader = new HttpContentClientStreamReader<TRequest, TResponse>(this);
        }

        /// <summary>
        /// Dispose can be called by:
        /// 1. The user. AsyncUnaryCall.Dispose et al will call this Dispose
        /// 2. <see cref="ValidateHeaders"/> will call dispose if errors fail validation
        /// 3. <see cref="FinishResponse"/> will call dispose
        /// </summary>
        public void Dispose()
        {
            if (!Disposed)
            {
                Disposed = true;

                if (!ResponseFinished)
                {
                    // If the response is not finished then cancel any pending actions:
                    // 1. Call HttpClient.SendAsync
                    // 2. Response Stream.ReadAsync
                    // 3. Client stream
                    //    - Getting the Stream from the Request.HttpContent
                    //    - Holding the Request.HttpContent.SerializeToStream open
                    //    - Writing to the client stream
                    CancelCall();
                }
                else
                {
                    _writeStreamTcs?.TrySetCanceled();
                    _completeTcs?.TrySetCanceled();
                }

                _ctsRegistration?.Dispose();
                _deadlineTimer?.Dispose();
                HttpResponse?.Dispose();
                ClientStreamReader?.Dispose();
                ClientStreamWriter?.Dispose();

                // To avoid racing with Dispose, skip disposing the call CTS
                // This avoid Dispose potentially calling cancel on a disposed CTS
                // The call CTS is not exposed externally and all dependent registrations
                // are cleaned up
            }
        }

        public void EnsureNotDisposed()
        {
            if (Disposed)
            {
                throw new ObjectDisposedException(nameof(GrpcCall<TRequest, TResponse>));
            }
        }

        public void EnsureHeadersValid()
        {
            if (_headerValidationError != null)
            {
                throw new InvalidOperationException(_headerValidationError);
            }
        }

        public Exception CreateCanceledStatusException()
        {
            if (_headerValidationError != null)
            {
                return new InvalidOperationException(_headerValidationError);
            }

            var statusCode = DeadlineReached ? StatusCode.DeadlineExceeded : StatusCode.Cancelled;
            return new RpcException(new Status(statusCode, string.Empty));
        }

        /// <summary>
        /// Marks the response as finished, i.e. all response content has been read and trailers are available.
        /// Can be called by <see cref="GetResponseAsync"/> for unary and client streaming calls, or
        /// <see cref="HttpContentClientStreamReader{TRequest,TResponse}.MoveNextCore(CancellationToken)"/>
        /// for server streaming and duplex streaming calls.
        /// </summary>
        public void FinishResponse()
        {
            ResponseFinished = true;

            try
            {
                // Get status from response before dispose
                // This may throw an error if the grpc-status is missing or malformed
                var status = GetStatusCore(HttpResponse);

                if (status.StatusCode != StatusCode.OK)
                {
                    throw new RpcException(status);
                }
            }
            finally
            {
                // Clean up call resources once this call is finished
                // Call may not be explicitly disposed when used with unary methods
                // e.g. var reply = await client.SayHelloAsync(new HelloRequest());
                Dispose();
            }
        }

        public async Task<Metadata> GetResponseHeadersAsync()
        {
            try
            {
                await SendTask.ConfigureAwait(false);

                // The task of this method is cached so there is no need to cache the headers here
                return GrpcProtocolHelpers.BuildMetadata(HttpResponse.Headers);
            }
            catch (OperationCanceledException)
            {
                EnsureNotDisposed();
                throw CreateCanceledStatusException();
            }
        }

        public Status GetStatus()
        {
            ValidateTrailersAvailable();

            return GetStatusCore(HttpResponse);
        }

        public async Task<TResponse> GetResponseAsync()
        {
            try
            {
                await SendTask.ConfigureAwait(false);

                // Trailers are only available once the response body had been read
                var responseStream = await HttpResponse.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var message = await responseStream.ReadSingleMessageAsync(Method.ResponseMarshaller.Deserializer, _callCts.Token).ConfigureAwait(false);
                FinishResponse();

                if (message == null)
                {
                    throw new InvalidOperationException("Call did not return a response message");
                }

                // The task of this method is cached so there is no need to cache the message here
                return message;
            }
            catch (OperationCanceledException)
            {
                EnsureNotDisposed();
                throw CreateCanceledStatusException();
            }
        }

        private void ValidateHeaders()
        {
            if (HttpResponse.StatusCode != HttpStatusCode.OK)
            {
                _headerValidationError = "Bad gRPC response. Expected HTTP status code 200. Got status code: " + (int)HttpResponse.StatusCode;
            }
            else if (HttpResponse.Content.Headers.ContentType == null)
            {
                _headerValidationError = "Bad gRPC response. Response did not have a content-type header.";
            }
            else
            {
                var grpcEncoding = HttpResponse.Content.Headers.ContentType.ToString();
                if (!GrpcProtocolHelpers.IsGrpcContentType(grpcEncoding))
                {
                    _headerValidationError = "Bad gRPC response. Invalid content-type value: " + grpcEncoding;
                }
            }

            if (_headerValidationError != null)
            {
                // Response is not valid gRPC
                // Clean up/cancel any pending operations
                Dispose();

                throw new InvalidOperationException(_headerValidationError);
            }

            // Success!
        }

        public Metadata GetTrailers()
        {
            if (_trailers == null)
            {
                ValidateTrailersAvailable();

                _trailers = GrpcProtocolHelpers.BuildMetadata(HttpResponse.TrailingHeaders);
            }

            return _trailers;
        }

        private void SetMessageContent(TRequest request, HttpRequestMessage message)
        {
            message.Content = new PushStreamContent(
                (stream) =>
                {
                    return SerializationHelpers.WriteMessage<TRequest>(stream, request, Method.RequestMarshaller.Serializer, Options.CancellationToken);
                },
                GrpcProtocolConstants.GrpcContentTypeHeaderValue);
        }

        private void CancelCall()
        {
            _callCts.Cancel();

            // Canceling call will cancel pending writes to the stream
            _completeTcs?.TrySetCanceled();
            _writeStreamTcs?.TrySetCanceled();
        }

        private void StartSend(System.Net.Http.HttpClient client, HttpRequestMessage message)
        {
            if (_timeout != null)
            {
                // Deadline timer will cancel the call CTS
                // Start timer after reader/writer have been created, otherwise a zero length deadline could cancel
                // the call CTS before they are created and leave them in a non-canceled state
                _deadlineTimer = new Timer(DeadlineExceeded, null, _timeout.Value, Timeout.InfiniteTimeSpan);
            }

            SendTask = SendAsync(client, message);
        }

        private async Task SendAsync(System.Net.Http.HttpClient client, HttpRequestMessage message)
        {
            HttpResponse = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, _callCts.Token).ConfigureAwait(false);
            ValidateHeaders();
        }

        private HttpContentClientStreamWriter<TRequest, TResponse> CreateWriter(HttpRequestMessage message)
        {
            _writeStreamTcs = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
            _completeTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            message.Content = new PushStreamContent(
                (stream) =>
                {
                    _writeStreamTcs.TrySetResult(stream);
                    return _completeTcs.Task;
                },
                GrpcProtocolConstants.GrpcContentTypeHeaderValue);

            var writer = new HttpContentClientStreamWriter<TRequest, TResponse>(this, _writeStreamTcs.Task, _completeTcs);
            return writer;
        }

        private HttpRequestMessage CreateHttpRequestMessage()
        {
            var message = new HttpRequestMessage(HttpMethod.Post, Method.FullName);
            message.Version = new Version(2, 0);
            // User agent is optional but recommended
            message.Headers.UserAgent.Add(GrpcProtocolConstants.UserAgentHeader);
            // TE is required by some servers, e.g. C Core
            // A missing TE header results in servers aborting the gRPC call
            message.Headers.TE.Add(GrpcProtocolConstants.TEHeader);

            if (Options.Headers != null && Options.Headers.Count > 0)
            {
                foreach (var entry in Options.Headers)
                {
                    // Deadline is set via CallOptions.Deadline
                    if (entry.Key == GrpcProtocolConstants.TimeoutHeader)
                    {
                        continue;
                    }

                    var value = entry.IsBinary ? Convert.ToBase64String(entry.ValueBytes) : entry.Value;
                    message.Headers.Add(entry.Key, value);
                }
            }

            if (_timeout != null)
            {
                message.Headers.Add(GrpcProtocolConstants.TimeoutHeader, GrpcProtocolHelpers.EncodeTimeout(Convert.ToInt64(_timeout.Value.TotalMilliseconds)));
            }

            return message;
        }

        private void DeadlineExceeded(object state)
        {
            // Deadline is only exceeded if the timeout has passed and
            // the response has not been finished or canceled
            if (!_callCts.IsCancellationRequested && !ResponseFinished)
            {
                // Flag is used to determine status code when generating exceptions
                DeadlineReached = true;

                CancelCall();
            }
        }

        private static Status GetStatusCore(HttpResponseMessage httpResponseMessage)
        {
            string grpcStatus = GetHeaderValue(httpResponseMessage.TrailingHeaders, GrpcProtocolConstants.StatusTrailer);
            // grpc-status is a required trailer
            if (grpcStatus == null)
            {
                throw new InvalidOperationException("Response did not have a grpc-status trailer.");
            }

            int statusValue;
            if (!int.TryParse(grpcStatus, out statusValue))
            {
                throw new InvalidOperationException("Unexpected grpc-status value: " + grpcStatus);
            }

            // grpc-message is optional
            string grpcMessage = GetHeaderValue(httpResponseMessage.TrailingHeaders, GrpcProtocolConstants.MessageTrailer);
            if (!string.IsNullOrEmpty(grpcMessage))
            {
                // https://github.com/grpc/grpc/blob/master/doc/PROTOCOL-HTTP2.md#responses
                // The value portion of Status-Message is conceptually a Unicode string description of the error,
                // physically encoded as UTF-8 followed by percent-encoding.
                grpcMessage = Uri.UnescapeDataString(grpcMessage);
            }

            return new Status((StatusCode)statusValue, grpcMessage);
        }

        private static string GetHeaderValue(HttpHeaders headers, string name)
        {
            if (!headers.TryGetValues(name, out var values))
            {
                return null;
            }

            // HttpHeaders appears to always return an array, but fallback to converting values to one just in case
            var valuesArray = values as string[] ?? values.ToArray();

            switch (valuesArray.Length)
            {
                case 0:
                    return null;
                case 1:
                    return valuesArray[0];
                default:
                    throw new InvalidOperationException($"Multiple {name} headers.");
            }
        }

        private void ValidateTrailersAvailable()
        {
            // Response headers have been returned and are not a valid grpc response
            EnsureHeadersValid();

            // Response is finished
            if (ResponseFinished)
            {
                return;
            }

            // Async call could have been disposed
            EnsureNotDisposed();

            // Call could have been canceled or deadline exceeded
            if (_callCts.IsCancellationRequested)
            {
                throw CreateCanceledStatusException();
            }

            // HttpClient.SendAsync could have failed
            if (SendTask.IsFaulted)
            {
                throw new InvalidOperationException("Can't get the call trailers because an error occured when making the request.", SendTask.Exception);
            }

            throw new InvalidOperationException("Can't get the call trailers because the call is not complete.");
        }
    }
}
