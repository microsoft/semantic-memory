﻿// Copyright (c) Microsoft. All rights reserved.

#pragma warning disable IDE0130 // reduce number of "using" statements
// ReSharper disable once CheckNamespace - reduce number of "using" statements
using System;
using System.Text;

namespace Microsoft.KernelMemory;

public class RabbitMqConfig
{
    /// <summary>
    /// RabbitMQ hostname, e.g. "127.0.0.1"
    /// </summary>
    public string Host { get; set; } = "";

    /// <summary>
    /// TCP port for the connection, e.g. 5672
    /// </summary>
    public int Port { get; set; } = 0;

    /// <summary>
    /// Authentication username
    /// </summary>
    public string Username { get; set; } = "";

    /// <summary>
    /// Authentication password
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>
    /// RabbitMQ virtual host name, e.g. "/"
    /// See https://www.rabbitmq.com/docs/vhosts
    /// </summary>
    public string VirtualHost { get; set; } = "/";

    /// <summary>
    /// How long to retry messages delivery, ie how long to retry, in seconds.
    /// Default: 3600 second, 1 hour.
    /// </summary>
    public int MessageTTLSecs { get; set; } = 3600;

    /// <summary>
    /// How many times to dequeue a messages and process before moving it to a poison queue.
    /// </summary>
    public int MaxRetriesBeforePoisonQueue { get; set; } = 20;

    /// <summary>
    /// Suffix used for the poison queues.
    /// </summary>
    public string PoisonQueueSuffix { get; set; } = "-poison";

    /// <summary>
    /// Verify that the current state is valid.
    /// </summary>
    public void Validate()
    {
        if (this.MaxRetriesBeforePoisonQueue < 0)
        {
            throw new ConfigurationException($"RabbitMQ: {nameof(this.MaxRetriesBeforePoisonQueue)} cannot be a negative number");
        }

        if (string.IsNullOrWhiteSpace(this.PoisonQueueSuffix))
        {
            throw new ConfigurationException($"RabbitMQ: {nameof(this.PoisonQueueSuffix)} is empty");
        }

        if (!IsValidQueueName(this.PoisonQueueSuffix))
        {
            throw new ConfigurationException($"RabbitMQ: {nameof(this.PoisonQueueSuffix)} is too long or contains invalid chars");
        }
    }

    private static bool IsValidQueueName(string name)
    {
        // Queue names must follow the rules described at
        // https://www.rabbitmq.com/docs/queues#names.
        if (name.StartsWith("amq.", StringComparison.InvariantCulture))
        {
            return false;
        }

        // Queue names can be up to 255 bytes of UTF-8 characters.
        // We define a maximum length of 60 bytes for the suffix,
        // so there is room for the other name part.
        if (Encoding.UTF8.GetByteCount(name) > 60)
        {
            return false;
        }

        return true;
    }
}
