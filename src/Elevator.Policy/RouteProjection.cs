using Elevator.Domain;
using Elevator.Domain.Events;
using ElevatorUnit = Elevator.Domain.Elevator;

namespace Elevator.Policy;

/// <summary>
/// Projects an elevator's future from a snapshot by running a copy of it forward: the same mechanics, the same
/// scheduling policy, and riders boarding and alighting as the passenger model does (in order, up to capacity).
/// Because it is the real code rather than a separate heuristic, the estimate matches what the elevator will do
/// given no new input (verified in CostBasedDispatchTests).
/// </summary>
public sealed class RouteProjection(ISchedulingPolicy scheduling, ElevatorTiming timing, long horizonMs = 3_600_000) : IEtaEstimator
{
    /// <param name="PickupAt">When the extra request would be picked up (doors open, it boards); null if it would not be.</param>
    /// <param name="LoadAtPickup">Load on board just before the extra request boards (L̂), or the load that made the
    /// elevator hand it back (full-load bypass) when it would not be picked up.</param>
    /// <param name="Completions">Drop-off time of every rider and pending pickup known to the elevator, plus the extra one.</param>
    public sealed record Result(SimTime? PickupAt, int LoadAtPickup, IReadOnlyDictionary<int, SimTime> Completions, SimTime Horizon);

    public SimTime? EstimatePickup(ElevatorSnapshot snapshot, Request request, SimTime now) => Project(snapshot, request, now).PickupAt;

    /// <summary>
    /// Rebuilds the elevator from <paramref name="snapshot"/>, replays it to <paramref name="now"/> (the snapshot may be
    /// older: an idle elevator's snapshot is from when it last changed), inserts <paramref name="extra"/> at
    /// <paramref name="now"/>, and runs until it has nothing left to do or the horizon is reached.
    /// </summary>
    public Result Project(ElevatorSnapshot snapshot, Request? extra, SimTime now)
    {
        var start = snapshot.At > now ? snapshot.At : now;
        var run = new Run(ElevatorUnit.FromSnapshot(snapshot, timing, scheduling), snapshot, extra);
        run.AdvanceTo(start);
        if (extra is not null) run.Insert(extra);
        run.AdvanceTo(start.AddMs(horizonMs));
        return new Result(run.PickupAt, run.LoadAtPickup, run.Completions, start.AddMs(horizonMs));
    }

    private sealed class Run(ElevatorUnit e, ElevatorSnapshot snapshot, Request? extra)
    {
        private readonly Dictionary<(int Floor, Direction Direction), List<Request>> _waiting =
            snapshot.HallCalls.ToDictionary(c => (c.Floor, c.Direction), c => c.Requests.ToList());
        private readonly List<Request> _riders = [.. snapshot.Riders];

        public Dictionary<int, SimTime> Completions { get; } = [];

        public SimTime? PickupAt { get; private set; }

        public int LoadAtPickup { get; private set; }

        public void Insert(Request request)
        {
            e.AssignPickup(request);
            var key = (request.PickupFloor, request.Direction);
            if (!_waiting.TryGetValue(key, out var list)) _waiting[key] = list = [];
            list.Add(request);
            Process();
        }

        public void AdvanceTo(SimTime until)
        {
            e.Resume();
            Process();
            while (e.NextDeadline is { } next && next <= until)
            {
                e.Tick(next);
                Process();
            }
            if (until > e.Now) e.Tick(until);
        }

        private void Process()
        {
            for (var events = e.DrainEvents(); events.Count > 0; events = e.DrainEvents())
            {
                foreach (var ev in events)
                {
                    switch (ev)
                    {
                        case DoorsOpened opened:
                            foreach (var r in _riders.Where(r => r.DestinationFloor == opened.Floor).ToList())
                            {
                                Completions[r.Id] = opened.At;
                                _riders.Remove(r);
                                e.Alight(r.Id);
                            }
                            break;
                        case HallCallAnswered answered when _waiting.Remove((answered.Floor, answered.Direction), out var boarding):
                            foreach (var r in boarding)
                            {
                                if (e.Load + r.Load > snapshot.Capacity) break;
                                if (r.Id == extra?.Id)
                                {
                                    PickupAt = answered.At;
                                    LoadAtPickup = e.Load;
                                }
                                _riders.Add(r);
                                e.Board(r);
                            }
                            break;
                        case AssignmentReleased released:
                            foreach (var list in _waiting.Values) list.RemoveAll(r => r.Id == released.RequestId);
                            if (released.RequestId == extra?.Id) LoadAtPickup = e.Load;
                            break;
                    }
                }
            }
        }
    }
}
