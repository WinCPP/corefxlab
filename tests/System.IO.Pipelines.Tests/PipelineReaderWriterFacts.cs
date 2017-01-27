﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Pipelines.Text.Primitives;
using Xunit;

namespace System.IO.Pipelines.Tests
{
    public class PipelineReaderWriterFacts : IDisposable
    {
        private Pipe _pipe;
        private PipelineFactory _pipelineFactory;

        public PipelineReaderWriterFacts()
        {
            _pipelineFactory = new PipelineFactory();
            _pipe = _pipelineFactory.Create();
        }

        public void Dispose()
        {
            _pipe.CompleteWriter();
            _pipe.CompleteReader();
            _pipelineFactory?.Dispose();
        }

        [Fact]
        public async Task ReaderShouldNotGetUnflushedBytesWhenOverflowingSegments()
        {
            // Fill the block with stuff leaving 5 bytes at the end
            var buffer = _pipe.Alloc(1);

            var len = buffer.Memory.Length;
            // Fill the buffer with garbage
            //     block 1       ->    block2
            // [padding..hello]  ->  [  world   ]
            var paddingBytes = Enumerable.Repeat((byte)'a', len - 5).ToArray();
            buffer.Write(paddingBytes);
            await buffer.FlushAsync();

            // Write 10 and flush
            buffer = _pipe.Alloc();
            buffer.WriteLittleEndian(10);

            // Write 9
            buffer.WriteLittleEndian(9);

            // Write 8
            buffer.WriteLittleEndian(8);

            // Make sure we don't see it yet
            var result = await _pipe.ReadAsync();
            var reader = result.Buffer;

            Assert.Equal(len - 5, reader.Length);

            // Don't move
            _pipe.Advance(reader.End);

            // Now flush
            await buffer.FlushAsync();

            reader = (await _pipe.ReadAsync()).Buffer;

            Assert.Equal(12, reader.Length);
            Assert.Equal(10, reader.ReadLittleEndian<int>());
            Assert.Equal(9, reader.Slice(4).ReadLittleEndian<int>());
            Assert.Equal(8, reader.Slice(8).ReadLittleEndian<int>());
        }

        [Fact]
        public async Task ReaderShouldNotGetUnflushedBytes()
        {
            // Write 10 and flush
            var buffer = _pipe.Alloc();
            buffer.WriteLittleEndian(10);
            await buffer.FlushAsync();

            // Write 9
            buffer = _pipe.Alloc();
            buffer.WriteLittleEndian(9);

            // Write 8
            buffer.WriteLittleEndian(8);

            // Make sure we don't see it yet
            var result = await _pipe.ReadAsync();
            var reader = result.Buffer;

            Assert.Equal(4, reader.Length);
            Assert.Equal(10, reader.ReadLittleEndian<int>());

            // Don't move
            _pipe.Advance(reader.Start);

            // Now flush
            await buffer.FlushAsync();

            reader = (await _pipe.ReadAsync()).Buffer;

            Assert.Equal(12, reader.Length);
            Assert.Equal(10, reader.ReadLittleEndian<int>());
            Assert.Equal(9, reader.Slice(4).ReadLittleEndian<int>());
            Assert.Equal(8, reader.Slice(8).ReadLittleEndian<int>());
        }

        [Fact]
        public async Task ReaderShouldNotGetUnflushedBytesWithAppend()
        {
            // Write 10 and flush
            var buffer = _pipe.Alloc();
            buffer.WriteLittleEndian(10);
            await buffer.FlushAsync();

            // Write Hello to another pipeline and get the buffer
            var bytes = Encoding.ASCII.GetBytes("Hello");

            var c2 = _pipelineFactory.Create();
            await c2.WriteAsync(bytes);
            var result = await c2.ReadAsync();
            var c2Buffer = result.Buffer;

            Assert.Equal(bytes.Length, c2Buffer.Length);

            // Write 9 to the buffer
            buffer = _pipe.Alloc();
            buffer.WriteLittleEndian(9);

            // Append the data from the other pipeline
            buffer.Append(c2Buffer);

            // Mark it as consumed
            c2.Advance(c2Buffer.End);

            // Now read and make sure we only see the comitted data
            result = await _pipe.ReadAsync();
            var reader = result.Buffer;

            Assert.Equal(4, reader.Length);
            Assert.Equal(10, reader.ReadLittleEndian<int>());

            // Consume nothing
            _pipe.Advance(reader.Start);

            // Flush the second set of writes
            await buffer.FlushAsync();

            reader = (await _pipe.ReadAsync()).Buffer;

            // int, int, "Hello"
            Assert.Equal(13, reader.Length);
            Assert.Equal(10, reader.ReadLittleEndian<int>());
            Assert.Equal(9, reader.Slice(4).ReadLittleEndian<int>());
            Assert.Equal("Hello", reader.Slice(8).GetUtf8String());
        }

        [Fact]
        public async Task WritingDataMakesDataReadableViaPipeline()
        {
            var bytes = Encoding.ASCII.GetBytes("Hello World");

            await _pipe.WriteAsync(bytes);
            var result = await _pipe.ReadAsync();
            var buffer = result.Buffer;

            Assert.Equal(11, buffer.Length);
            Assert.True(buffer.IsSingleSpan);
            var array = new byte[11];
            buffer.First.Span.CopyTo(array);
            Assert.Equal("Hello World", Encoding.ASCII.GetString(array));
        }

        [Fact]
        public async Task AdvanceEmptyBufferAfterWritingResetsAwaitable()
        {
            var bytes = Encoding.ASCII.GetBytes("Hello World");

            await _pipe.WriteAsync(bytes);
            var result = await _pipe.ReadAsync();
            var buffer = result.Buffer;

            Assert.Equal(11, buffer.Length);
            Assert.True(buffer.IsSingleSpan);
            var array = new byte[11];
            buffer.First.Span.CopyTo(array);
            Assert.Equal("Hello World", Encoding.ASCII.GetString(array));

            _pipe.Advance(buffer.End);

            // Now write 0 and advance 0
            await _pipe.WriteAsync(Span<byte>.Empty);
            result = await _pipe.ReadAsync();
            _pipe.Advance(result.Buffer.End);

            var awaitable = _pipe.ReadAsync();
            Assert.False(awaitable.IsCompleted);
        }

        [Fact]
        public async Task AdvanceShouldResetStateIfReadCancelled()
        {
            _pipe.CancelPendingRead();

            var result = await _pipe.ReadAsync();
            var buffer = result.Buffer;
            _pipe.Advance(buffer.End);

            Assert.False(result.IsCompleted);
            Assert.True(result.IsCancelled);
            Assert.True(buffer.IsEmpty);

            var awaitable = _pipe.ReadAsync();
            Assert.False(awaitable.IsCompleted);
        }

        [Fact]
        public async Task CancellingPendingReadBeforeReadAsync()
        {
            _pipe.CancelPendingRead();

            var result = await _pipe.ReadAsync();
            var buffer = result.Buffer;
            _pipe.Advance(buffer.End);

            Assert.False(result.IsCompleted);
            Assert.True(result.IsCancelled);
            Assert.True(buffer.IsEmpty);

            var bytes = Encoding.ASCII.GetBytes("Hello World");
            var output = _pipe.Alloc();
            output.Write(bytes);
            await output.FlushAsync();

            result = await _pipe.ReadAsync();
            buffer = result.Buffer;

            Assert.Equal(11, buffer.Length);
            Assert.False(result.IsCancelled);
            Assert.True(buffer.IsSingleSpan);
            var array = new byte[11];
            buffer.First.Span.CopyTo(array);
            Assert.Equal("Hello World", Encoding.ASCII.GetString(array));
        }

        [Fact]
        public async Task CancellingBeforeAdvance()
        {
            var bytes = Encoding.ASCII.GetBytes("Hello World");
            var output = _pipe.Alloc();
            output.Write(bytes);
            await output.FlushAsync();

            var result = await _pipe.ReadAsync();
            var buffer = result.Buffer;

            Assert.Equal(11, buffer.Length);
            Assert.False(result.IsCancelled);
            Assert.True(buffer.IsSingleSpan);
            var array = new byte[11];
            buffer.First.Span.CopyTo(array);
            Assert.Equal("Hello World", Encoding.ASCII.GetString(array));

            _pipe.CancelPendingRead();

            _pipe.Advance(buffer.End);

            var awaitable = _pipe.ReadAsync();

            Assert.True(awaitable.IsCompleted);

            result = await awaitable;

            Assert.True(result.IsCancelled);
        }

        [Fact]
        public async Task CancellingPendingAfterReadAsync()
        {
            var bytes = Encoding.ASCII.GetBytes("Hello World");
            var output = _pipe.Alloc();
            output.Write(bytes);

            var task = Task.Run(async () =>
            {
                var result = await _pipe.ReadAsync();
                var buffer = result.Buffer;
                _pipe.Advance(buffer.End);

                Assert.False(result.IsCompleted);
                Assert.True(result.IsCancelled);
                Assert.True(buffer.IsEmpty);

                await output.FlushAsync();

                result = await _pipe.ReadAsync();
                buffer = result.Buffer;

                Assert.Equal(11, buffer.Length);
                Assert.True(buffer.IsSingleSpan);
                Assert.False(result.IsCancelled);
                var array = new byte[11];
                buffer.First.Span.CopyTo(array);
                Assert.Equal("Hello World", Encoding.ASCII.GetString(array));
                _pipe.AdvanceReader(result.Buffer.End, result.Buffer.End);

                _pipe.CompleteReader();
            });

            // Wait until reading starts to cancel the pending read
            await _pipe.ReadingStarted;

            _pipe.CancelPendingRead();

            await task;

            _pipe.CompleteWriter();
        }

        [Fact]
        public async Task WriteAndCancellingPendingReadBeforeReadAsync()
        {
            var bytes = Encoding.ASCII.GetBytes("Hello World");
            var output = _pipe.Alloc();
            output.Write(bytes);
            await output.FlushAsync();

            _pipe.CancelPendingRead();

            var result = await _pipe.ReadAsync();
            var buffer = result.Buffer;

            Assert.False(result.IsCompleted);
            Assert.True(result.IsCancelled);
            Assert.False(buffer.IsEmpty);
            Assert.Equal(11, buffer.Length);
            Assert.True(buffer.IsSingleSpan);
            var array = new byte[11];
            buffer.First.Span.CopyTo(array);
            Assert.Equal("Hello World", Encoding.ASCII.GetString(array));
            _pipe.AdvanceReader(buffer.End, buffer.End);
        }

        [Fact]
        public async Task ReadingCanBeCancelled()
        {
            var cts = new CancellationTokenSource();
            cts.Token.Register(() =>
            {
                _pipe.CompleteWriter(new OperationCanceledException(cts.Token));
            });

            var ignore = Task.Run(async () =>
            {
                await Task.Delay(1000);
                cts.Cancel();
            });

            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            {
                var result = await _pipe.ReadAsync();
                var buffer = result.Buffer;
            });
        }

        [Fact]
        public async Task HelloWorldAcrossTwoBlocks()
        {
            const int blockSize = 4032;
            //     block 1       ->    block2
            // [padding..hello]  ->  [  world   ]
            var paddingBytes = Enumerable.Repeat((byte)'a', blockSize - 5).ToArray();
            var bytes = Encoding.ASCII.GetBytes("Hello World");
            var writeBuffer = _pipe.Alloc();
            writeBuffer.Write(paddingBytes);
            writeBuffer.Write(bytes);
            await writeBuffer.FlushAsync();

            var result = await _pipe.ReadAsync();
            var buffer = result.Buffer;
            Assert.False(buffer.IsSingleSpan);
            var helloBuffer = buffer.Slice(blockSize - 5);
            Assert.False(helloBuffer.IsSingleSpan);
            var memory = new List<Memory<byte>>();
            foreach (var m in helloBuffer)
            {
                memory.Add(m);
            }
            var spans = memory;
            Assert.Equal(2, memory.Count);
            var helloBytes = new byte[spans[0].Length];
            spans[0].Span.CopyTo(helloBytes);
            var worldBytes = new byte[spans[1].Length];
            spans[1].Span.CopyTo(worldBytes);
            Assert.Equal("Hello", Encoding.ASCII.GetString(helloBytes));
            Assert.Equal(" World", Encoding.ASCII.GetString(worldBytes));
        }

        [Fact]
        public async Task IndexOfNotFoundReturnsEnd()
        {
            var bytes = Encoding.ASCII.GetBytes("Hello World");

            await _pipe.WriteAsync(bytes);
            var result = await _pipe.ReadAsync();
            var buffer = result.Buffer;
            ReadableBuffer slice;
            ReadCursor cursor;

            Assert.False(buffer.TrySliceTo(10, out slice, out cursor));
        }

        [Fact]
        public async Task FastPathIndexOfAcrossBlocks()
        {
            var vecUpperR = new Vector<byte>((byte)'R');

            const int blockSize = 4032;
            //     block 1       ->    block2
            // [padding..hello]  ->  [  world   ]
            var paddingBytes = Enumerable.Repeat((byte)'a', blockSize - 5).ToArray();
            var bytes = Encoding.ASCII.GetBytes("Hello World");
            var writeBuffer = _pipe.Alloc();
            writeBuffer.Write(paddingBytes);
            writeBuffer.Write(bytes);
            await writeBuffer.FlushAsync();

            var result = await _pipe.ReadAsync();
            var buffer = result.Buffer;
            ReadableBuffer slice;
            ReadCursor cursor;
            Assert.False(buffer.TrySliceTo((byte)'R', out slice, out cursor));
        }

        [Fact]
        public async Task SlowPathIndexOfAcrossBlocks()
        {
            const int blockSize = 4032;
            //     block 1       ->    block2
            // [padding..hello]  ->  [  world   ]
            var paddingBytes = Enumerable.Repeat((byte)'a', blockSize - 5).ToArray();
            var bytes = Encoding.ASCII.GetBytes("Hello World");
            var writeBuffer = _pipe.Alloc();
            writeBuffer.Write(paddingBytes);
            writeBuffer.Write(bytes);
            await writeBuffer.FlushAsync();

            var result = await _pipe.ReadAsync();
            var buffer = result.Buffer;
            ReadableBuffer slice;
            ReadCursor cursor;
            Assert.False(buffer.IsSingleSpan);
            Assert.True(buffer.TrySliceTo((byte)' ', out slice, out cursor));

            slice = buffer.Slice(cursor).Slice(1);
            var array = slice.ToArray();

            Assert.Equal("World", Encoding.ASCII.GetString(array));
        }

        [Fact]
        public void AllocMoreThanPoolBlockSizeThrows()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => _pipe.Alloc(8192));
        }

        [Fact]
        public void ReadingStartedCompletesOnCompleteReader()
        {
            _pipe.CompleteReader();

            Assert.True(_pipe.ReadingStarted.IsCompleted);
        }

        [Fact]
        public void ReadingStartedCompletesOnCallToReadAsync()
        {
            _pipe.ReadAsync();

            Assert.True(_pipe.ReadingStarted.IsCompleted);
        }

        [Fact]
        public void ThrowsOnReadAfterCompleteReader()
        {
            _pipe.CompleteReader();

            Assert.Throws<InvalidOperationException>(() => _pipe.ReadAsync());
        }

        [Fact]
        public void ThrowsOnAllocAfterCompleteWriter()
        {

            _pipe.CompleteWriter();

            Assert.Throws<InvalidOperationException>(() => _pipe.Alloc());
        }

        [Fact]
        public async Task MultipleCompleteReaderWriterCauseDisposeOnlyOnce()
        {
            var pool = new DisposeTrackingOwnedMemory(new byte[1]);

            using (var factory = new PipelineFactory(pool))
            {
                var readerWriter = factory.Create();
                await readerWriter.WriteAsync(new byte[] { 1 });

                readerWriter.CompleteWriter();
                readerWriter.CompleteReader();
                Assert.Equal(1, pool.Disposed);

                readerWriter.CompleteWriter();
                readerWriter.CompleteReader();
                Assert.Equal(1, pool.Disposed);
            }
        }

        [Fact]
        public async Task CompleteReaderThrowsIfReadInProgress()
        {
            await _pipe.WriteAsync(new byte[1]);
            await _pipe.ReadAsync();

            Assert.Throws<InvalidOperationException>(() => _pipe.CompleteReader());
        }

        [Fact]
        public void CompleteWriterThrowsIfWriteInProgress()
        {
            _pipe.Alloc();

            Assert.Throws<InvalidOperationException>(() => _pipe.CompleteWriter());
        }

        [Fact]
        public async Task ReadAsync_ThrowsIfWriterCompletedWithException()
        {
            _pipe.CompleteWriter(new InvalidOperationException("Writer exception"));

            var invalidOperationException = await Assert.ThrowsAsync<InvalidOperationException>(async () => await _pipe.ReadAsync());
            Assert.Equal("Writer exception", invalidOperationException.Message);
            invalidOperationException = await Assert.ThrowsAsync<InvalidOperationException>(async () => await _pipe.ReadAsync());
            Assert.Equal("Writer exception", invalidOperationException.Message);
        }

        [Fact]
        public void FlushAsync_ReturnsCompletedTaskWhenMaxSizeIfZero()
        {
            var writableBuffer = _pipe.Alloc(1);
            writableBuffer.Advance(1);
            var flushTask = writableBuffer.FlushAsync();
            Assert.True(flushTask.IsCompleted);

            writableBuffer = _pipe.Alloc(1);
            writableBuffer.Advance(1);
            flushTask = writableBuffer.FlushAsync();
            Assert.True(flushTask.IsCompleted);
        }

        private class DisposeTrackingOwnedMemory : OwnedMemory<byte>, IBufferPool
        {
            public DisposeTrackingOwnedMemory(byte[] array) : base(array)
            {
            }

            protected override void Dispose(bool disposing)
            {
                Disposed++;
                base.Dispose(disposing);
            }

            public int Disposed { get; set; }

            public OwnedMemory<byte> Lease(int size)
            {
                return this;
            }
        }
    }
}
