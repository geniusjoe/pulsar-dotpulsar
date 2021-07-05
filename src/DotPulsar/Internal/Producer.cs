﻿/*
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

namespace DotPulsar.Internal
{
    using Abstractions;
    using DotPulsar.Abstractions;
    using DotPulsar.Exceptions;
    using DotPulsar.Extensions;
    using DotPulsar.Internal.Extensions;
    using DotPulsar.Internal.PulsarApi;
    using System;
    using System.Collections.Concurrent;
    using System.Threading;
    using System.Threading.Tasks;

    public sealed class Producer<TMessage> : IProducer<TMessage>, IRegisterEvent
    {
        private readonly StateManager<ProducerState> _state;
        private readonly IConnectionPool _connectionPool;
        private readonly IHandleException _exceptionHandler;
        private readonly ICompressorFactory? _compressorFactory;
        private readonly ProducerOptions<TMessage> _options;
        private readonly ProcessManager _processManager;
        private readonly ConcurrentDictionary<int, IProducer<TMessage>> _producers;
        private readonly IMessageRouter _messageRouter;
        private readonly CancellationTokenSource _cts;
        private readonly IExecute _executor;
        private int _isDisposed;
        private int _producerCount;
        private Exception? _throw;

        public Uri ServiceUrl { get; }
        public string Topic { get; }

        public Producer(
            Uri serviceUrl,
            ProducerOptions<TMessage> options,
            ProcessManager processManager,
            IHandleException exceptionHandler,
            IConnectionPool connectionPool,
            ICompressorFactory? compressorFactory)
        {
            _state = new StateManager<ProducerState>(ProducerState.Disconnected, ProducerState.Closed, ProducerState.Faulted);
            ServiceUrl = serviceUrl;
            Topic = options.Topic;
            _isDisposed = 0;
            _options = options;
            _exceptionHandler = exceptionHandler;
            _connectionPool = connectionPool;
            _compressorFactory = compressorFactory;
            _processManager = processManager;
            _messageRouter = options.MessageRouter;
            _cts = new CancellationTokenSource();
            _executor = new Executor(Guid.Empty, this, _exceptionHandler);
            _producers = new ConcurrentDictionary<int, IProducer<TMessage>>();
            _ = Setup();
        }

        private async Task Setup()
        {
            await Task.Yield();

            try
            {
                await _executor.Execute(Monitor, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (_cts.IsCancellationRequested)
                    return;

                _throw = exception;
                _state.SetState(ProducerState.Faulted);
            }
        }

        private async Task Monitor()
        {
            var numberOfPartitions = await GetNumberOfPartitions(Topic, _cts.Token).ConfigureAwait(false);
            var isPartitionedTopic = numberOfPartitions != 0;
            var monitoringTasks = new Task<ProducerStateChanged>[isPartitionedTopic ? numberOfPartitions : 1];

            var topic = Topic;

            for (var partition = 0; partition < monitoringTasks.Length; ++partition)
            {
                if (isPartitionedTopic)
                    topic = $"{Topic}-partition-{partition}";

                var producer = CreateSubProducer(topic);
                _ = _producers.TryAdd(partition, producer);
                monitoringTasks[partition] = producer.StateChangedFrom(ProducerState.Disconnected, _cts.Token).AsTask();
            }

            Interlocked.Exchange(ref _producerCount, monitoringTasks.Length);

            var connectedProducers = 0;

            while (true)
            {
                await Task.WhenAny(monitoringTasks).ConfigureAwait(false);

                for (var i = 0; i < monitoringTasks.Length; ++i)
                {
                    var task = monitoringTasks[i];
                    if (!task.IsCompleted)
                        continue;

                    var state = task.Result.ProducerState;
                    switch (state)
                    {
                        case ProducerState.Connected:
                            ++connectedProducers;
                            break;
                        case ProducerState.Disconnected:
                            --connectedProducers;
                            break;
                        case ProducerState.Faulted:
                            _state.SetState(ProducerState.Faulted);
                            return;
                    }

                    monitoringTasks[i] = task.Result.Producer.StateChangedFrom(state, _cts.Token).AsTask();
                }

                if (connectedProducers == 0)
                    _state.SetState(ProducerState.Disconnected);
                else if (connectedProducers == monitoringTasks.Length)
                    _state.SetState(ProducerState.Connected);
                else
                    _state.SetState(ProducerState.PartiallyConnected);
            }
        }

        private SubProducer<TMessage> CreateSubProducer(string topic)
        {
            var correlationId = Guid.NewGuid();
            var producerName = _options.ProducerName;
            var schema = _options.Schema;
            var initialSequenceId = _options.InitialSequenceId;
            var factory = new ProducerChannelFactory(correlationId, _processManager, _connectionPool, topic, producerName, schema.SchemaInfo, _compressorFactory);
            var stateManager = new StateManager<ProducerState>(ProducerState.Disconnected, ProducerState.Closed, ProducerState.Faulted);
            var initialChannel = new NotReadyChannel<TMessage>();
            var executor = new Executor(correlationId, _processManager, _exceptionHandler);
            var producer = new SubProducer<TMessage>(correlationId, ServiceUrl, topic, initialSequenceId, _processManager, initialChannel, executor, stateManager, factory, schema);
            var process = new ProducerProcess(correlationId, stateManager, producer);
            _processManager.Add(process);
            process.Start();
            return producer;
        }

        private async Task<uint> GetNumberOfPartitions(string topic, CancellationToken cancellationToken)
        {
            var connection = await _connectionPool.FindConnectionForTopic(topic, cancellationToken).ConfigureAwait(false);
            var commandPartitionedMetadata = new CommandPartitionedTopicMetadata { Topic = topic };
            var response = await connection.Send(commandPartitionedMetadata, cancellationToken).ConfigureAwait(false);

            response.Expect(BaseCommand.Type.PartitionedMetadataResponse);

            if (response.PartitionMetadataResponse.Response == CommandPartitionedTopicMetadataResponse.LookupType.Failed)
                response.PartitionMetadataResponse.Throw();

            return response.PartitionMetadataResponse.Partitions;
        }

        public bool IsFinalState()
            => _state.IsFinalState();

        public bool IsFinalState(ProducerState state)
            => _state.IsFinalState(state);

        public async ValueTask<ProducerState> OnStateChangeTo(ProducerState state, CancellationToken cancellationToken)
            => await _state.StateChangedTo(state, cancellationToken).ConfigureAwait(false);

        public async ValueTask<ProducerState> OnStateChangeFrom(ProducerState state, CancellationToken cancellationToken)
            => await _state.StateChangedFrom(state, cancellationToken).ConfigureAwait(false);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0)
                return;

            _cts.Cancel();
            _cts.Dispose();

            _state.SetState(ProducerState.Closed);

            foreach (var producer in _producers.Values)
            {
                await producer.DisposeAsync().ConfigureAwait(false);
            }
        }

        private async ValueTask<int> ChoosePartitions(DotPulsar.MessageMetadata? metadata, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            if (_producerCount == 0)
            {
                _ = await _state.StateChangedFrom(ProducerState.Disconnected, cancellationToken).ConfigureAwait(false);
                if (_throw is not null)
                    throw _throw;
            }

            if (_producerCount == 1)
                return 0;

            return _messageRouter.ChoosePartition(metadata, _producerCount);
        }

        public async ValueTask<MessageId> Send(TMessage message, CancellationToken cancellationToken)
        {
            var partition = await ChoosePartitions(null, cancellationToken).ConfigureAwait(false);
            return await _producers[partition].Send(message, cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<MessageId> Send(DotPulsar.MessageMetadata metadata, TMessage message, CancellationToken cancellationToken)
        {
            var partition = await ChoosePartitions(null, cancellationToken).ConfigureAwait(false);
            return await _producers[partition].Send(metadata, message, cancellationToken).ConfigureAwait(false);
        }

        private void ThrowIfDisposed()
        {
            if (_isDisposed != 0)
                throw new ProducerDisposedException(GetType().FullName!);
        }

        public void Register(IEvent @event) { }
    }
}
