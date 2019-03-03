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
using System.Net.Http;
using System.Threading;
using Grpc.Core;
using Grpc.NetCore.HttpClient;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Grpc.AspNetCore.Server.GrpcClient
{
    internal class GrpcHttpClientFactory<TClient> : ITypedHttpClientFactory<TClient> where TClient : ClientBase<TClient>
    {
        private readonly Cache _cache;
        private readonly IServiceProvider _services;
        private readonly IHttpContextAccessor _httpContextAccessor;
        private readonly GrpcClientOptions<TClient> _clientOptions;

        public GrpcHttpClientFactory(Cache cache, IServiceProvider services, IOptions<GrpcClientOptions<TClient>> clientOptions)
        {
            if (cache == null)
            {
                throw new ArgumentNullException(nameof(cache));
            }

            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (clientOptions == null)
            {
                throw new ArgumentNullException(nameof(clientOptions));
            }

            _cache = cache;
            _services = services;
            _httpContextAccessor = services.GetService<IHttpContextAccessor>();
            _clientOptions = clientOptions.Value;
        }

        public TClient CreateClient(HttpClient httpClient)
        {
            if (httpClient == null)
            {
                throw new ArgumentNullException(nameof(httpClient));
            }

            var callInvoker = new HttpClientCallInvoker(httpClient);
            if (_clientOptions.UseRequestCancellationToken)
            {
                callInvoker.CancellationToken = _httpContextAccessor.HttpContext.RequestAborted;
            }

            // TODO(JamesNK): Need to set deadline and context propagation token
            // Either add HttpContextServerCallContext to HttpContext.Items or provide equivilent of IHttpContextAccessor

            return (TClient)_cache.Activator(_services, new object[] { callInvoker });
        }

        // The Cache should be registered as a singleton, so it that it can
        // act as a cache for the Activator. This allows the outer class to be registered
        // as a transient, so that it doesn't close over the application root service provider.
        public class Cache
        {
            private readonly static Func<ObjectFactory> _createActivator = () => ActivatorUtilities.CreateFactory(typeof(TClient), new Type[] { typeof(CallInvoker), });

            private ObjectFactory _activator;
            private bool _initialized;
            private object _lock;

            public ObjectFactory Activator => LazyInitializer.EnsureInitialized(
                ref _activator,
                ref _initialized,
                ref _lock,
                _createActivator);
        }
    }
}