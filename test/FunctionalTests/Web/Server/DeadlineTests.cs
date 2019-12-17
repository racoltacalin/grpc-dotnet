#region Copyright notice and license

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
using System.IO.Pipelines;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Greet;
using Grpc.AspNetCore.FunctionalTests.Infrastructure;
using Grpc.AspNetCore.Server.Internal;
using Grpc.Core;
using Grpc.Net.Client.Web;
using Grpc.Tests.Shared;
using NUnit.Framework;

namespace Grpc.AspNetCore.FunctionalTests.Web.Server
{
    [TestFixture(GrpcWebMode.GrpcWeb, TestServerEndpointName.Http1)]
    [TestFixture(GrpcWebMode.GrpcWeb, TestServerEndpointName.Http2)]
    [TestFixture(GrpcWebMode.GrpcWebText, TestServerEndpointName.Http1)]
    [TestFixture(GrpcWebMode.GrpcWebText, TestServerEndpointName.Http2)]
    public class DeadlineTests : GrpcWebFunctionalTestBase
    {
        public DeadlineTests(GrpcWebMode grpcWebMode, TestServerEndpointName endpointName) : base(grpcWebMode, endpointName)
        {
        }

        [Test]
        public async Task UnaryMethodDeadlineExceeded()
        {
            static async Task<HelloReply> WaitUntilDeadline(HelloRequest request, ServerCallContext context)
            {
                while (!context.CancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(10);
                }

                await Task.Delay(50);

                return new HelloReply();
            }

            SetExpectedErrorsFilter(writeContext =>
            {
                if (writeContext.LoggerName == TestConstants.ServerCallHandlerTestName)
                {
                    // Deadline happened before write
                    if (writeContext.EventId.Name == "ErrorExecutingServiceMethod" &&
                        writeContext.State.ToString() == "Error when executing service method 'WriteUntilError'." &&
                        writeContext.Exception!.Message == "Cannot write message after request is complete.")
                    {
                        return true;
                    }

                    // Deadline happened during write (error raised from pipeline writer)
                    if (writeContext.Exception is InvalidOperationException &&
                        writeContext.Exception.Message == "Writing is not allowed after writer was completed.")
                    {
                        return true;
                    }
                }

                return false;
            });

            var method = Fixture.DynamicGrpc.AddUnaryMethod<HelloRequest, HelloReply>(WaitUntilDeadline, nameof(WaitUntilDeadline));

            var grpcWebClient = CreateGrpcWebClient();

            for (int i = 0; i < 50; i++)
            {
                var requestMessage = new HelloRequest
                {
                    Name = "World"
                };

                var requestStream = new MemoryStream();
                MessageHelpers.WriteMessage(requestStream, requestMessage);

                var httpRequest = GrpcHttpHelper.Create(method.FullName);
                httpRequest.Headers.Add(GrpcProtocolConstants.TimeoutHeader, "50m");
                httpRequest.Content = new GrpcStreamContent(requestStream);

                try
                {
                    // Act
                    var response = await grpcWebClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead).DefaultTimeout();

                    // Assert
                    response.AssertIsSuccessfulGrpcRequest();
                    response.AssertTrailerStatus(StatusCode.DeadlineExceeded, "Deadline Exceeded");
                }
                catch (HttpRequestException ex) when (ex.InnerException?.Message == "The response ended prematurely.")
                {
                    // There seems to be some flakyness in HTTP/1 and CompleteAsync. Will sometimes never return data.
                }
            }
        }

        [Test]
        public async Task WriteMessageAfterDeadline()
        {
            static async Task WriteUntilError(HelloRequest request, IServerStreamWriter<HelloReply> responseStream, ServerCallContext context)
            {
                var i = 0;
                var message = $"How are you {request.Name}? {i}";
                await responseStream.WriteAsync(new HelloReply { Message = message }).DefaultTimeout();

                i++;

                while (!context.CancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(50);
                }

                message = $"How are you {request.Name}? {i}";
                await responseStream.WriteAsync(new HelloReply { Message = message }).DefaultTimeout();
            }

            // Arrange
            SetExpectedErrorsFilter(writeContext =>
            {
                if (writeContext.LoggerName == TestConstants.ServerCallHandlerTestName)
                {
                    // Deadline happened before write
                    if (writeContext.EventId.Name == "ErrorExecutingServiceMethod" &&
                        writeContext.State.ToString() == "Error when executing service method 'WriteUntilError'." &&
                        writeContext.Exception!.Message == "Cannot write message after request is complete.")
                    {
                        return true;
                    }

                    // Deadline happened during write (error raised from pipeline writer)
                    if (writeContext.Exception is InvalidOperationException &&
                        writeContext.Exception.Message == "Writing is not allowed after writer was completed.")
                    {
                        return true;
                    }
                }

                return false;
            });

            var method = Fixture.DynamicGrpc.AddServerStreamingMethod<HelloRequest, HelloReply>(WriteUntilError, nameof(WriteUntilError));

            var requestMessage = new HelloRequest
            {
                Name = "World"
            };

            var requestStream = new MemoryStream();
            MessageHelpers.WriteMessage(requestStream, requestMessage);

            var httpRequest = GrpcHttpHelper.Create(method.FullName);
            httpRequest.Headers.Add(GrpcProtocolConstants.TimeoutHeader, "200m");
            httpRequest.Content = new GrpcStreamContent(requestStream);

            // Act
            var grpcWebClient = CreateGrpcWebClient();
            var response = await grpcWebClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead).DefaultTimeout();

            // Assert
            response.AssertIsSuccessfulGrpcRequest();

            var responseStream = await response.Content.ReadAsStreamAsync().DefaultTimeout();
            var pipeReader = PipeReader.Create(responseStream);

            var messageCount = 0;

            var readTask = Task.Run(async () =>
            {
                while (true)
                {
                    var greeting = await MessageHelpers.AssertReadStreamMessageAsync<HelloReply>(pipeReader).DefaultTimeout();

                    if (greeting != null)
                    {
                        Assert.AreEqual($"How are you World? {messageCount}", greeting.Message);
                        messageCount++;
                    }
                    else
                    {
                        break;
                    }
                }

            });

            await readTask.DefaultTimeout();

            Assert.AreNotEqual(0, messageCount);
            response.AssertTrailerStatus(StatusCode.DeadlineExceeded, "Deadline Exceeded");

            // The server has completed the response but is still running
            // Allow time for the server to complete
            await TestHelpers.AssertIsTrueRetryAsync(() =>
            {
                var errorLogged = Logs.Any(r =>
                    r.EventId.Name == "ErrorExecutingServiceMethod" &&
                    r.State.ToString() == "Error when executing service method 'WriteUntilError'." &&
                    (r.Exception!.Message == "Cannot write message after request is complete." || r.Exception!.Message == "Writing is not allowed after writer was completed."));

                return errorLogged;
            }, "Expected error not thrown.");
        }
    }
}