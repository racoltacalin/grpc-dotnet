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
using System.Buffers;
using System.Buffers.Text;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Grpc.Net.Client.Web.Internal
{
    internal class Base64RequestStream : Stream
    {
        private readonly Stream _inner;
        private byte[]? _buffer;
        private int _remainder;

        public Base64RequestStream(Stream inner)
        {
            _inner = inner;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (_buffer == null)
            {
                _buffer = ArrayPool<byte>.Shared.Rent(minimumLength: 4096);
            }

            Memory<byte> localBuffer;
            if (_remainder > 0)
            {
                var required = 3 - _remainder;
                if (data.Length < required)
                {
                    // There is remainder and the new buffer doesn't have enough content for the
                    // remainder to be written as base64
                    data.CopyTo(_buffer.AsMemory(_remainder));
                    _remainder += data.Length;
                    return;
                }

                // Use data to complete remainder and write to buffer
                data.Slice(0, required).CopyTo(_buffer.AsMemory(_remainder));
                Base64.EncodeToUtf8InPlace(_buffer, 3, out var bytesWritten);

                // Trim used data
                data = data.Slice(required);
                localBuffer = _buffer.AsMemory(bytesWritten);
            }
            else
            {
                localBuffer = _buffer;
            }

            while (CanWriteData(data))
            {
                Base64.EncodeToUtf8(data.Span, localBuffer.Span, out var bytesConsumed, out var bytesWritten, isFinalBlock: false);

                var base64Remainder = _buffer.Length - localBuffer.Length;
                await _inner.WriteAsync(_buffer.AsMemory(0, bytesWritten + base64Remainder), cancellationToken);

                data = data.Slice(bytesConsumed);
                localBuffer = _buffer;
            }

            // Remainder content will usually be written with other data
            // If there was not enough data to write along with remainder then write it here
            if (localBuffer.Length < _buffer.Length)
            {
                await _inner.WriteAsync(_buffer.AsMemory(0, 4), cancellationToken);
            }

            if (data.Length > 0)
            {
                data.CopyTo(_buffer);
            }

            _remainder = data.Length;
        }

        private static bool CanWriteData(ReadOnlyMemory<byte> data)
        {
            return data.Length >= 3;
        }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            if (_remainder > 0)
            {
                Base64.EncodeToUtf8InPlace(_buffer, _remainder, out var bytesWritten);

                await _inner.WriteAsync(_buffer.AsMemory(0, bytesWritten), cancellationToken);
                _remainder = 0;
            }

            await _inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_buffer != null)
                {
                    ArrayPool<byte>.Shared.Return(_buffer);
                }
            }
            base.Dispose(disposing);
        }

        #region Stream implementation
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set { _inner.Position = value; }
        }

        public override void Flush()
        {
            throw new NotImplementedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            // Used by unit tests
            WriteAsync(buffer.AsMemory(0, count)).GetAwaiter().GetResult();
            FlushAsync().GetAwaiter().GetResult();
        }
        #endregion
    }
}