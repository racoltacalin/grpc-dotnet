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

using System.IO.Pipelines;
using Grpc.AspNetCore.Web.Internal;
using NUnit.Framework;

namespace Grpc.AspNetCore.FunctionalTests.Web.Server
{
    [TestFixture]
    public class GrpcWebUnaryMethodTests : UnaryMethodTestsBase
    {
        protected override string ContentType => GrpcWebProtocolConstants.GrpcWebContentType;

        protected override PipeReader ResolvePipeReader(PipeReader pipeReader)
        {
            return pipeReader;
        }

        protected override PipeWriter ResolvePipeWriter(PipeWriter pipeWriter)
        {
            return pipeWriter;
        }
    }
}
