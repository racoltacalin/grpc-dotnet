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

using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Grpc.Net.Client.Web;
using Grpc.Net.Client.Web.Internal;
using NUnit.Framework;

namespace Grpc.AspNetCore.FunctionalTests.Web.Client
{
    [TestFixture]
    public class GrpcWebResponseContentTests
    {
        [Test]
        public void ReadAsync_EmptyMessage_ParseMessageAndTrailers()
        {
            // Arrange
            var testHttpContext = new TestHttpContext();
            var content = new GrpcWebResponseContent(testHttpContext, GrpcWebMode.GrpcWeb, new HttpResponseMessage());

            // Act
            content.Dispose();

            // Assert
            Assert.IsTrue(testHttpContext.Disposed);
        }

        private class TestHttpContext : HttpContent
        {
            public bool Disposed { get; private set; }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                throw new System.NotImplementedException();
            }

            protected override bool TryComputeLength(out long length)
            {
                throw new System.NotImplementedException();
            }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }
    }
}
