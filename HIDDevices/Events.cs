﻿// Licensed under the Apache License, Version 2.0 (the "License").
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace HIDDevices
{
    /// <summary>
    ///     Class Event. This class cannot be inherited.
    ///     Each event contains a description of every log event that can be raised by this library.
    /// </summary>
    public sealed class Event
    {
        private static readonly Dictionary<int, Event> s_all = new Dictionary<int, Event>();

        /// <summary>
        ///     The refresh failure event.
        /// </summary>
        public static readonly Event RefreshFailure =
            new Event(nameof(Resources.RefreshFailure), nameof(Resources.RefreshFailureDescription));

        /// <summary>
        ///     The device creation failure event.
        /// </summary>
        public static readonly Event DeviceCreationFailure = new Event(LogLevel.Warning, nameof(Resources
            .DeviceCreationFailure), nameof(Resources.DeviceCreationFailureDescription));

        /// <summary>
        ///     The device add event.
        /// </summary>
        public static readonly Event DeviceAdd = new Event(LogLevel.Information, nameof(Resources.DeviceAdd),
            nameof(Resources.DeviceAddDescription));

        /// <summary>
        ///     The device remove event.
        /// </summary>
        public static readonly Event DeviceRemove = new Event(LogLevel.Information, nameof(Resources
            .DeviceRemove), nameof(Resources.DeviceRemoveDescription));

        /// <summary>
        ///     The device update event.
        /// </summary>
        public static readonly Event DeviceUpdate = new Event(LogLevel.Information, nameof(Resources
            .DeviceUpdate), nameof(Resources.DeviceUpdateDescription));

        /// <summary>
        ///     The device connection failed event.
        /// </summary>
        public static readonly Event DeviceConnectionFailed = new Event(LogLevel.Error, nameof(Resources
            .DeviceConnectionFailed), nameof(Resources.DeviceConnectionFailedDescription));

        /// <summary>
        ///     The device connected event.
        /// </summary>
        public static readonly Event DeviceConnected = new Event(LogLevel.Information,
            nameof(Resources.DeviceConnected),
            nameof(Resources.DeviceConnectedDescription));

        /// <summary>
        ///     The device error event.
        /// </summary>
        public static readonly Event DeviceError = new Event(LogLevel.Error, nameof(Resources.DeviceError),
            nameof(Resources.DeviceErrorDescription));

        /// <summary>
        ///     The device connection closed event.
        /// </summary>
        public static readonly Event DeviceConnectionClosed = new Event(LogLevel.Information,
            nameof(Resources.DeviceConnectionClosed),
            nameof(Resources.DeviceConnectionClosedDescription));

        private static int s_idCounter = 3500;

        private readonly string _descriptionResource;
        private readonly int _id;
        private readonly string _messageResource;

        /// <summary>
        ///     The event's log level.
        /// </summary>
        public readonly LogLevel Level;

        private Event(string messageResource, string? descriptionResource = null)
            : this(LogLevel.Error, messageResource, descriptionResource)
        {
        }

        private Event(LogLevel level, string messageResource, string? descriptionResource = null)
        {
            _id = s_idCounter++;
            Level = level;
            _messageResource = messageResource;
            _descriptionResource = descriptionResource ?? messageResource;
            s_all[_id] = this;
        }

        /// <summary>
        ///     Gets the message format.
        /// </summary>
        /// <value>The message format.</value>
        public string Format => Resources.ResourceManager.GetString(_messageResource);

        /// <summary>
        ///     Gets the event's description.
        /// </summary>
        /// <value>The description.</value>
        public string Description => Resources.ResourceManager.GetString(_descriptionResource);

        /// <summary>
        ///     Gets the unique identifier.
        /// </summary>
        /// <value>The identifier.</value>
        public EventId Id => new EventId(_id, Description);

        /// <summary>
        ///     Gets a collection of all events raised by this library.
        /// </summary>
        /// <value>All events.</value>
        public static IReadOnlyCollection<Event> All => s_all.Values;

        /// <summary>
        ///     Tries to get an event by it's <see cref="id" />.
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <param name="event">The event.</param>
        /// <returns><see langword="true" /> if found, <see langword="false" /> otherwise.</returns>
        public static bool TryGet(int id, [MaybeNullWhen(false)] out Event? @event) =>
            s_all.TryGetValue(id, out @event);

        /// <summary>
        ///     Gets the event with the <see cref="id">specified identifier</see>, if found.
        /// </summary>
        /// <param name="id">The identifier.</param>
        /// <returns>The event with the <see cref="id">specified identifier</see>, if found; otherwise <see langword="null" />.</returns>
        public static Event? Get(int id) => s_all.TryGetValue(id, out var @event) ? @event : null;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Log(ILogger logger, params object[] args) => logger.Log(Level, Id, Format, args);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Log(ILogger logger, Exception? exception, params object[] args) =>
            logger.Log(Level, Id, exception, Format, args);

        /// <inheritdoc />
        public override string ToString() => Description;

        /// <summary>
        ///     Performs an implicit conversion from <see cref="Event" /> to <see cref="EventId" />.
        /// </summary>
        /// <param name="event">The event.</param>
        /// <returns>The result of the conversion.</returns>
        public static implicit operator EventId(Event @event) => @event.Id;
    }
}
