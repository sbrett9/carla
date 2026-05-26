// Source: carla/nav/WalkerEvent.{h,cpp}
//
// In the C++ code, walker events are a `boost::variant<WalkerEventIgnore,
// WalkerEventWait, WalkerEventStopAndCheck>` dispatched by a visitor object
// (`WalkerEventVisitor` in WalkerEvent.cpp). The visitor mutates the event's
// countdown timer in place and returns an `EventResult` telling the state
// machine in WalkerManager::Update whether to:
//
//   * Continue  — stay in WALKER_IN_EVENT, re-tick next frame
//   * End       — advance to the next route point (SetWalkerNextPoint)
//   * TimeOut   — abandon the route and re-plan (SetWalkerRoute)
//
// In C# we replace the variant + visitor with a sealed record hierarchy and
// pattern-match in WalkerManager.ExecuteEvent. The countdown mutation is
// modelled by replacing the event with a `with`-expression copy on the
// owning `WalkerRoutePoint` slot.
#nullable enable

namespace CarlaNet.Nav;

/// <summary>Result of dispatching a walker event (mirrors C++ <c>nav::EventResult</c>).</summary>
internal enum EventResult : byte
{
    Continue,
    End,
    TimeOut,
}

/// <summary>
/// Discriminated union of walker events. Closed hierarchy — only the three
/// concrete types defined below are valid. Pattern-match with a switch
/// expression for dispatch; do NOT add a default polymorphic virtual method.
/// </summary>
internal abstract record WalkerEvent;

/// <summary>
/// "Just walk to the next route point." Returns <see cref="EventResult.End"/>
/// immediately when dispatched (the walker has reached this route point and
/// can step to the next one without delay).
/// </summary>
internal sealed record WalkerEventIgnore : WalkerEvent
{
    /// <summary>Singleton — the event carries no state.</summary>
    public static readonly WalkerEventIgnore Instance = new();
}

/// <summary>
/// "Wait for <see cref="TimeRemaining"/> seconds, then end." Used as a
/// generic pause point on the route (not currently emitted by the C++
/// route-construction code, but supported by the visitor for parity).
/// </summary>
/// <param name="TimeRemaining">Seconds left before the event ends.</param>
internal sealed record WalkerEventWait(double TimeRemaining) : WalkerEvent;

/// <summary>
/// "Stop the agent, look for a traffic light and oncoming vehicles, then
/// either continue waiting, end (path clear), or time out (re-plan)."
/// Emitted whenever the route enters a road or crosswalk from a safe area.
/// </summary>
/// <param name="TimeRemaining">
/// Seconds left before the event abandons the route and forces a replan
/// (default 60.0 in upstream <c>WalkerManager::SetWalkerRoute</c>).
/// </param>
/// <param name="CheckForTrafficLight">
/// <c>true</c> on first dispatch — triggers the one-time lookup of the
/// affecting traffic light. Cleared by the visitor after the lookup so
/// subsequent ticks skip the search.
/// </param>
/// <param name="TrafficLightActor">
/// The actor id of the affecting traffic light, or <c>null</c> if either
/// the lookup has not run yet or no traffic light is nearby. Mirrors the
/// C++ <c>SharedPtr&lt;carla::client::TrafficLight&gt;</c> field.
/// </param>
internal sealed record WalkerEventStopAndCheck(
    double TimeRemaining,
    bool CheckForTrafficLight = true,
    ActorId? TrafficLightActor = null) : WalkerEvent;
