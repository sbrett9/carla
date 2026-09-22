// Ported from Eclipse SUMO's reference TraCI client, tools/traci/domain.py -- the SubscriptionResults
// store at domain.py:69-105.
//
//   Upstream:      https://github.com/eclipse-sumo/sumo
//   Pinned commit: e238ea04b7150ba23a348a285d3048919fa4830b (Eclipse SUMO 1.27.0)
//   Copyright (C) 2008-2026 German Aerospace Center (DLR) and others.
//   SPDX-License-Identifier: EPL-2.0 OR GPL-2.0-or-later

namespace CarlaNet.Sumo;

/// <summary>
/// What one domain's subscriptions delivered with the last step, by object and by variable.
/// </summary>
/// <remarks>
/// <para>Refilled every step and read every step, for as many objects as are subscribed, so it is
/// written to hold its storage rather than rebuild it: a step clears the per-object maps in place
/// and leaves them attached to their objects. A vehicle that stays subscribed for a thousand steps
/// therefore costs one dictionary, not a thousand. <see cref="Forget"/> is what actually releases
/// one, and the subscription calls it when a vehicle leaves.</para>
///
/// <para>A subscribed object that is simply absent from a step's results has left the simulation.
/// That absence is the notification -- there is no separate message for it -- which is why
/// <see cref="ObjectIds"/> lists what arrived this step rather than what is subscribed.</para>
/// </remarks>
public sealed class TraCISubscriptionResults
{
    private readonly Dictionary<string, Dictionary<int, TraCIValue>> _byObject = [];
    private readonly List<string> _present = [];

    internal TraCISubscriptionResults()
    {
    }

    /// <summary>Every object that delivered results in the last step, in the order they arrived.</summary>
    public IReadOnlyList<string> ObjectIds => _present;

    /// <summary>One variable of one object from the last step.</summary>
    public bool TryGetValue(string objectId, int variableId, out TraCIValue value)
    {
        if (_byObject.TryGetValue(objectId, out Dictionary<int, TraCIValue>? values))
        {
            return values.TryGetValue(variableId, out value);
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Every variable one object delivered in the last step, or an empty map where it delivered
    /// none.
    /// </summary>
    public IReadOnlyDictionary<int, TraCIValue> ValuesFor(string objectId) =>
        _byObject.TryGetValue(objectId, out Dictionary<int, TraCIValue>? values)
            ? values
            : ReadOnlyEmpty;

    private static readonly Dictionary<int, TraCIValue> ReadOnlyEmpty = [];

    /// <summary>Drop everything the previous step delivered, keeping the storage.</summary>
    internal void Reset()
    {
        foreach (Dictionary<int, TraCIValue> values in _byObject.Values)
        {
            values.Clear();
        }

        _present.Clear();
    }

    /// <summary>Record one decoded variable for one object.</summary>
    internal void Add(string objectId, int variableId, TraCIValue value)
    {
        if (!_byObject.TryGetValue(objectId, out Dictionary<int, TraCIValue>? values))
        {
            values = [];
            _byObject[objectId] = values;
        }

        if (values.Count == 0)
        {
            _present.Add(objectId);
        }

        values[variableId] = value;
    }

    /// <summary>Release the storage held for an object that will not be seen again.</summary>
    internal void Forget(string objectId) => _byObject.Remove(objectId);
}
