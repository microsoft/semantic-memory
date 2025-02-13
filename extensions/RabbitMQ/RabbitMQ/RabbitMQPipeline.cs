// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.Pipeline;
using Microsoft.KernelMemory.Pipeline.Queue;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Microsoft.KernelMemory.Orchestration.RabbitMQ;

[Experimental("KMEXP04")]
public sealed class RabbitMQPipeline : IQueue, IAsyncDisposable
{
    private readonly ILogger<RabbitMQPipeline> _log;

    private readonly ConnectionFactory _factory;
    private readonly RabbitMQConfig _config;

    private IConnection? _connection;
    private IChannel? _channel;
    private AsyncEventingBasicConsumer? _consumer;

    // The action that will be executed when a new message is received.
    private Func<string, Task<ReturnType>>? _processMessageAction;

    private readonly int _messageTTLMsecs;
    private readonly int _delayBeforeRetryingMsecs;
    private readonly int _maxAttempts;
    private string _queueName = string.Empty;
    private string _poisonQueueName = string.Empty;

    /// <summary>
    /// Create a new RabbitMQ queue instance
    /// </summary>
    public RabbitMQPipeline(RabbitMQConfig config, ILoggerFactory? loggerFactory = null)
    {
        this._log = (loggerFactory ?? DefaultLogger.Factory).CreateLogger<RabbitMQPipeline>();

        this._config = config;
        this._config.Validate(this._log);

        this._factory = new ConnectionFactory
        {
            ClientProvidedName = "KernelMemory",
            HostName = config.Host,
            Port = config.Port,
            UserName = config.Username,
            Password = config.Password,
            VirtualHost = !string.IsNullOrWhiteSpace(config.VirtualHost) ? config.VirtualHost : "/",
            ConsumerDispatchConcurrency = config.ConcurrentThreads,
            Ssl = new SslOption
            {
                Enabled = config.SslEnabled,
                ServerName = config.Host,
            }
        };

        this._messageTTLMsecs = config.MessageTTLSecs * 1000;

        this._delayBeforeRetryingMsecs = Math.Max(0, this._config.DelayBeforeRetryingMsecs);
        this._maxAttempts = Math.Max(0, this._config.MaxRetriesBeforePoisonQueue) + 1;
    }

    /// <inheritdoc />
    /// About poison queue and dead letters, see https://www.rabbitmq.com/docs/dlx
    public async Task<IQueue> ConnectToQueueAsync(string queueName, QueueOptions options = default, CancellationToken cancellationToken = default)
    {
        ArgumentNullExceptionEx.ThrowIfNullOrWhiteSpace(queueName, nameof(queueName), "The queue name is empty");
        ArgumentExceptionEx.ThrowIf(queueName.StartsWith("amq.", StringComparison.OrdinalIgnoreCase), nameof(queueName), "The queue name cannot start with 'amq.'");

        await this.InitializeAsync().ConfigureAwait(false);

        var poisonExchangeName = $"{queueName}.dlx";
        var poisonQueueName = $"{queueName}{this._config.PoisonQueueSuffix}";

        ArgumentExceptionEx.ThrowIf((Encoding.UTF8.GetByteCount(queueName) > 255), nameof(queueName),
            $"The queue name '{queueName}' is too long, max 255 UTF8 bytes allowed");
        ArgumentExceptionEx.ThrowIf((Encoding.UTF8.GetByteCount(poisonExchangeName) > 255), nameof(poisonExchangeName),
            $"The exchange name '{poisonExchangeName}' is too long, max 255 UTF8 bytes allowed, try using a shorter queue name");
        ArgumentExceptionEx.ThrowIf((Encoding.UTF8.GetByteCount(poisonQueueName) > 255), nameof(poisonQueueName),
            $"The poison queue name '{poisonQueueName}' is too long, max 255 UTF8 bytes allowed, try using a shorter queue name");

        if (!string.IsNullOrEmpty(this._queueName))
        {
            throw new InvalidOperationException($"The client is already connected to `{this._queueName}`");
        }

        // Define queue where messages are sent by the orchestrator
        this._queueName = queueName;
        try
        {
            await this._channel!.QueueDeclareAsync(queue: this._queueName, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object?>
            {
                ["x-queue-type"] = "quorum",
                ["x-delivery-limit"] = this._config.MaxRetriesBeforePoisonQueue,
                ["x-dead-letter-exchange"] = poisonExchangeName
            }, cancellationToken: cancellationToken).ConfigureAwait(false);

            this._log.LogTrace("Queue name: {0}", this._queueName);
        }
#pragma warning disable CA2254
        catch (OperationInterruptedException ex)
        {
            var err = ex.Message;
            if (ex.Message.Contains("inequivalent arg 'x-delivery-limit'", StringComparison.OrdinalIgnoreCase))
            {
                err = $"The queue '{this._queueName}' is already configured with a different value for 'x-delivery-limit' " +
                      $"({nameof(this._config.MaxRetriesBeforePoisonQueue)}), the value cannot be changed to {this._config.MaxRetriesBeforePoisonQueue}";
            }
            else if (ex.Message.Contains("inequivalent arg 'x-dead-letter-exchange'", StringComparison.OrdinalIgnoreCase))
            {
                err = $"The queue '{this._queueName}' is already linked to a different dead letter exchange, " +
                      $"it is not possible to change the 'x-dead-letter-exchange' value to {poisonExchangeName}";
            }

            this._log.LogError(ex, err);
            throw new KernelMemoryException(err, ex);
        }
#pragma warning restore CA2254

        // Define poison queue where failed messages are stored
        this._poisonQueueName = poisonQueueName;
        await this._channel.QueueDeclareAsync(
            queue: this._poisonQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Define exchange to route failed messages to poison queue
        await this._channel.ExchangeDeclareAsync(poisonExchangeName, "fanout", durable: true, autoDelete: false, cancellationToken: cancellationToken).ConfigureAwait(false);
        await this._channel.QueueBindAsync(this._poisonQueueName, poisonExchangeName, routingKey: string.Empty, arguments: null, cancellationToken: cancellationToken).ConfigureAwait(false);
        this._log.LogTrace("Poison queue name '{0}' bound to exchange '{1}' for queue '{2}'", this._poisonQueueName, poisonExchangeName, this._queueName);

        // Activate consumer
        if (options.DequeueEnabled)
        {
            await this._channel.BasicConsumeAsync(queue: this._queueName, autoAck: false, consumer: this._consumer!, cancellationToken: cancellationToken).ConfigureAwait(false);
            this._log.LogTrace("Enabling dequeue on queue `{0}`", this._queueName);
        }

        return this;
    }

    /// <inheritdoc />
    public async Task EnqueueAsync(string message, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (string.IsNullOrEmpty(this._queueName))
        {
            throw new InvalidOperationException("The client must be connected to a queue first");
        }

        await this.PublishMessageAsync(
            queueName: this._queueName,
            body: Encoding.UTF8.GetBytes(message),
            messageId: Guid.NewGuid().ToString("N"),
            expirationMsecs: this._messageTTLMsecs).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void OnDequeue(Func<string, Task<ReturnType>> processMessageAction)
    {
        // We just store the action to be executed when a message is received.
        // The actual message processing is registered only when the consumer is created.
        this._processMessageAction = processMessageAction;
    }

    public void Dispose()
    {
        // Note: Start from v7.0, Synchronous Close methods are not available anymore in the library, so we just call Dispose.
        ((IDisposable)this._channel!).Dispose();
        ((IDisposable)this._connection!).Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await this._channel!.CloseAsync().ConfigureAwait(false);
        await this._connection!.CloseAsync().ConfigureAwait(false);

        await this._channel!.DisposeAsync().ConfigureAwait(false);
        await this._connection!.DisposeAsync().ConfigureAwait(false);
    }

    private async Task InitializeAsync()
    {
        if (this._connection is not null)
        {
            // The client is already connected.
            return;
        }

        this._connection = await this._factory.CreateConnectionAsync().ConfigureAwait(false);

        this._channel = await this._connection.CreateChannelAsync().ConfigureAwait(false);
        await this._channel.BasicQosAsync(prefetchSize: 0, prefetchCount: this._config.PrefetchCount, global: false).ConfigureAwait(false);

        this._consumer = new AsyncEventingBasicConsumer(this._channel);
        this._consumer.ReceivedAsync += async (object _, BasicDeliverEventArgs args) =>
        {
            // Just for logging, extract the attempt number from the message headers
            var attemptNumber = 1;
            if (args.BasicProperties?.Headers != null && args.BasicProperties.Headers.TryGetValue("x-delivery-count", out object? value))
            {
                attemptNumber = int.TryParse(value!.ToString(), out var parsedResult) ? ++parsedResult : -1;
            }

            try
            {
                this._log.LogDebug("Message '{0}' received, expires after {1}ms, attempt {2} of {3}",
                    args.BasicProperties?.MessageId, args.BasicProperties?.Expiration, attemptNumber, this._maxAttempts);

                byte[] body = args.Body.ToArray();
                string message = Encoding.UTF8.GetString(body);

                // Invokes the action that has been stored in the OnDequeue method.
                var returnType = await this._processMessageAction!.Invoke(message).ConfigureAwait(false);
                switch (returnType)
                {
                    case ReturnType.Success:
                        this._log.LogTrace("Message '{0}' successfully processed, deleting message", args.BasicProperties?.MessageId);
                        await this._channel.BasicAckAsync(args.DeliveryTag, multiple: false, cancellationToken: args.CancellationToken).ConfigureAwait(false);
                        break;

                    case ReturnType.TransientError:
                        if (attemptNumber < this._maxAttempts)
                        {
                            this._log.LogWarning("Message '{0}' failed to process (attempt {1} of {2}), putting message back in the queue",
                                args.BasicProperties?.MessageId, attemptNumber, this._maxAttempts);
                            if (this._delayBeforeRetryingMsecs > 0)
                            {
                                await Task.Delay(TimeSpan.FromMilliseconds(this._delayBeforeRetryingMsecs)).ConfigureAwait(false);
                            }
                        }
                        else
                        {
                            this._log.LogError("Message '{0}' failed to process (attempt {1} of {2}), moving message to poison queue",
                                args.BasicProperties?.MessageId, attemptNumber, this._maxAttempts);
                        }

                        await this._channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: true, cancellationToken: args.CancellationToken).ConfigureAwait(false);
                        break;

                    case ReturnType.FatalError:
                        this._log.LogError("Message '{0}' failed to process due to a non-recoverable error, moving to poison queue", args.BasicProperties?.MessageId);
                        await this._channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false, cancellationToken: args.CancellationToken).ConfigureAwait(false);
                        break;

                    default:
                        throw new ArgumentOutOfRangeException($"Unknown {returnType:G} result");
                }
            }
            catch (KernelMemoryException e) when (e.IsTransient.HasValue && !e.IsTransient.Value)
            {
                this._log.LogError(e, "Message '{0}' failed to process due to a non-recoverable error, moving to poison queue", args.BasicProperties?.MessageId);
                await this._channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: false, cancellationToken: args.CancellationToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Must catch all to handle queue properly
            catch (Exception e)
            {
                // Exceptions caught by this block:
                // - message processing failed with exception
                // - failed to delete message from queue
                // - failed to unlock message in the queue

                if (attemptNumber < this._maxAttempts)
                {
                    this._log.LogWarning(e, "Message '{0}' processing failed with exception (attempt {1} of {2}), putting message back in the queue",
                        args.BasicProperties?.MessageId, attemptNumber, this._maxAttempts);
                    if (this._delayBeforeRetryingMsecs > 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(this._delayBeforeRetryingMsecs)).ConfigureAwait(false);
                    }
                }
                else
                {
                    this._log.LogError(e, "Message '{0}' processing failed with exception (attempt {1} of {2}), putting message back in the queue",
                        args.BasicProperties?.MessageId, attemptNumber, this._maxAttempts);
                }

                // TODO: verify and document what happens if this fails. RabbitMQ should automatically unlock messages.
                await this._channel.BasicNackAsync(args.DeliveryTag, multiple: false, requeue: true, cancellationToken: args.CancellationToken).ConfigureAwait(false);
            }
#pragma warning restore CA1031
        };
    }

    private async Task PublishMessageAsync(
        string queueName,
        ReadOnlyMemory<byte> body,
        string messageId,
        int? expirationMsecs)
    {
        var properties = new BasicProperties();
        properties.Persistent = true;
        properties.MessageId = messageId;

        if (expirationMsecs.HasValue)
        {
            properties.Expiration = $"{expirationMsecs}";
        }

        this._log.LogDebug("Sending message to {0}: {1} (TTL: {2} secs)...",
            queueName, properties.MessageId, expirationMsecs.HasValue ? expirationMsecs / 1000 : "infinite");

        await this._channel!.BasicPublishAsync(
            routingKey: queueName,
            body: body,
            exchange: string.Empty,
            basicProperties: properties,
            mandatory: true).ConfigureAwait(false);

        this._log.LogDebug("Message sent: {0} (TTL: {1} secs)", properties.MessageId, expirationMsecs.HasValue ? expirationMsecs / 1000 : "infinite");
    }
}
