﻿// Licensed under the Apache License, Version 2.0 (the "License").
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using DynamicData;
using HIDControllers.Controls;
using HidSharp;
using HidSharp.Reports;
using HidSharp.Utility;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.Threading;
using IAsyncDisposable = System.IAsyncDisposable;

namespace HIDControllers
{
    public sealed class Controllers : IReadOnlyCollection<Controller>, IAsyncDisposable
    {
        private readonly TaskCompletionSource<bool>? _loadedCompletionSource = new TaskCompletionSource<bool>();

        private readonly AsyncAutoResetEvent _triggerRefresh = new AsyncAutoResetEvent(true);

        internal readonly ILogger<Controllers>? Logger;

        private SourceCache<Controller, string>? _controllers;
        private CancellationTokenSource? _refreshCancellationTokenSource;

        /// <summary>
        /// Initializes a new instance of the <see cref="Controllers"/> class.
        /// </summary>
        /// <param name="logger">The logger (optional).</param>
        public Controllers(ILogger<Controllers>? logger = null)
        {
            Logger = logger;
            _controllers = new SourceCache<Controller, string>(c => c.DevicePath);

            DeviceList.Local.Changed += (sender, args) => Refresh();
            _refreshCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _refreshCancellationTokenSource.Token;
            Task.Run(() => RefreshAsync(cancellationToken), cancellationToken)
                .ConfigureAwait(false); // Launch in background thread
            Refresh();
        }

        /// <summary>
        /// Gets an observable to monitor changes to controls across all controllers.
        /// </summary>
        /// <value>The changes.</value>
        /// <exception cref="ObjectDisposedException">Controllers</exception>
        public IObservable<IList<ControlChange>> Changes
            => _controllers?
                   .Connect()
                   .SelectMany(cs => cs)
                   .Where(c => c.Reason != ChangeReason.Remove)
                   .Select(c => c.Current)
                   .SelectMany(c => c.Changes)
               ?? throw new ObjectDisposedException(nameof(Controllers));

        /// <summary>
        /// Gets an observable to monitor changes to the collection of controllers.
        /// </summary>
        /// <value>The updates.</value>
        /// <exception cref="ObjectDisposedException">Controllers</exception>
        public IObservable<IChangeSet<Controller, string>> Updates =>
            _controllers?.Connect() ?? throw new ObjectDisposedException(nameof(Controllers));

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            // Cancel any refresh.
            var cts = Interlocked.Exchange(ref _refreshCancellationTokenSource, null);
            if (cts != null)
            {
                cts.Cancel();
                cts.Dispose();
            }

            var controllers = Interlocked.Exchange(ref _controllers, null);
            if (controllers is null)
            {
                return default;
            }

            // Remove all controllers, disposing them
            if (controllers.Count < 1)
            {
                return new ValueTask();
            }

            var toDispose = controllers.Items.ToArray();
            controllers.Clear();

            // Dispose all controllers
            return toDispose.Length > 1
                ? new ValueTask(Task.WhenAll(toDispose.Select(c => c.DisposeAsync().AsTask())))
                : toDispose[0].DisposeAsync();
        }

        /// <inheritdoc />
        public IEnumerator<Controller> GetEnumerator() =>
            _controllers?.Items.GetEnumerator() ?? throw new ObjectDisposedException(nameof(Controllers));

        /// <inheritdoc />
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <inheritdoc />
        public int Count => _controllers?.Count ?? throw new ObjectDisposedException(nameof(Controllers));

        /// <summary>
        /// Force a refresh of controllers.  The refresh will occur asynchronously.
        /// </summary>
        /// TODO Consider making this async and awaiting next refresh completion.
        public void Refresh() => _triggerRefresh.Set();

        /// <summary>
        /// Wait for the initial load of controllers.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <returns>An awaitable task that completes on initial load of controllers.</returns>
        public Task LoadAsync(CancellationToken cancellationToken = default) =>
            _loadedCompletionSource?.Task.WithCancellation(cancellationToken) ??
            Task.FromException(new ObjectDisposedException(nameof(Controllers)));

        private async Task RefreshAsync(CancellationToken cancellationToken)
        {
#if DEBUG
            HidSharpDiagnostics.EnableTracing = true;
#else
            HidSharpDiagnostics.EnableTracing = false;
#endif
            do
            {
                try
                {
                    await _triggerRefresh.WaitAsync(cancellationToken).ConfigureAwait(false);
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    // Get all existing values
                    var controllers = _controllers ?? throw new ObjectDisposedException(nameof(Controllers));
                    var existing = controllers.KeyValues.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                    var added = new List<Controller>();
                    var updated = new List<(Controller existing, Controller updated)>();
                    var toDispose = new List<Controller>();

                    var list = DeviceList.Local;
                    foreach (var device in list.GetHidDevices())
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        try
                        {
                            var rawReportDescriptor = device.GetRawReportDescriptor();
                            // Check to see if controller already exists and is unchanged.
#pragma warning disable IDE0007 // Use implicit type
                            if (existing.TryGetValue(device.DevicePath, out Controller? existingController))
#pragma warning restore IDE0007 // Use implicit type
                            {
                                if (rawReportDescriptor.SequenceEqual(existingController.RawReportDescriptor))
                                {
                                    // We found this controller, so remove from the existing list.
                                    existing.Remove(existingController.DevicePath);
                                    continue;
                                }
                            }
                            else
                            {
                                existingController = null;
                            }

                            var reportDescriptor = new ReportDescriptor(rawReportDescriptor);
                            var deviceItems = reportDescriptor.DeviceItems
                                .Select(i =>
                                (
                                    item: i,
                                    type: i.Usages.GetAllValues().Cast<Usage>()
                                        .Select(DeviceType.Get)
                                        .FirstOrDefault(n => n != null),
                                    usages: (IReadOnlyList<(Usage, DataItem)>)i.InputReports
                                        .SelectMany(r => r.DataItems
                                            .SelectMany(dataItem =>
                                                dataItem.Usages.GetAllValues().Cast<Usage>()
                                                    .Where(AxisType.SupportedUsage)
                                                    .Select(u => (u, dataItem))))
                                        .ToArray()))
                                .Where(t => t.type != null && t.usages.Count > 0)
                                .ToArray();

                            if (deviceItems.Length < 1)
                            {
                                continue;
                            }

#pragma warning disable CS8620 // Argument cannot be used for parameter due to differences in the nullability of reference types.
                            var controller = new Controller(this, device, rawReportDescriptor, reportDescriptor,
                                deviceItems);
#pragma warning restore CS8620 // Argument cannot be used for parameter due to differences in the nullability of reference types.

                            // Update collection with new controller info
                            if (existingController is null)
                            {
                                added.Add(controller);
                            }
                            else
                            {
                                existing.Remove(controller.DevicePath);
                                updated.Add((existingController, controller));
                            }
                        }
#pragma warning disable CA1031 // Do not catch general exception types
                        catch (Exception exception)
                        {
                            Logger?.Log(Event.ControllerCreationFailure, exception, device);
                        }
#pragma warning restore CA1031 // Do not catch general exception types
                    }

                    // Remove existing controllers that weren't found or updated
                    if (existing.Count > 0 || added.Count > 0 || updated.Count > 0)
                    {
                        // Batch changes
                        controllers.Edit(cache =>
                        {
                            foreach (var kvp in existing)
                            {
                                cache.RemoveKey(kvp.Key);
                                toDispose.Add(kvp.Value);
                                Logger?.Log(Event.ControllerRemove, kvp.Value);
                            }

                            foreach (var c in added)
                            {
                                cache.AddOrUpdate(c);
                                Logger?.Log(Event.ControllerAdd, c);
                            }

                            foreach (var t in updated)
                            {
                                cache.AddOrUpdate(t.updated);
                                toDispose.Add(t.existing);
                                Logger?.Log(Event.ControllerUpdate, t.updated);
                            }
                        });
                    }

                    // We can now safely dispose all the old controllers
                    if (toDispose.Count > 0)
                    {
                        // Group disposals if > 1
                        if (toDispose.Count > 1)
                        {
                            await Task.WhenAll(toDispose.Select(c => c.DisposeAsync().AsTask())).ConfigureAwait(false);
                        }
                        else
                        {
                            await toDispose[0].DisposeAsync();
                        }
                    }
                }
#pragma warning disable CA1031 // Do not catch general exception types
                catch (OperationCanceledException)
                {
                    // If we get a cancellation exception we must be disposing, so abort.
                    return;
                }
                catch (Exception exception)
                {
                    Logger?.Log(Event.RefreshFailure, exception);
                }
#pragma warning restore CA1031 // Do not catch general exception types
                _loadedCompletionSource?.TrySetResult(true);
            } while (!cancellationToken.IsCancellationRequested);
        }
    }
}
