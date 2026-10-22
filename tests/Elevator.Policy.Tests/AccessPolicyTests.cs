using Elevator.Domain;
using Elevator.Policy.Access;
using ElevatorUnit = Elevator.Domain.Elevator;

namespace Elevator.Policy.Tests;

public class AccessPolicyTests
{
    private const int Floors = 20;

    private static readonly ElevatorSnapshot[] Fleet =
    [
        Make(0, "local", FloorMask.All, 10),
        Make(1, "local", FloorMask.All, 10),
        Make(2, "express", FloorMask.Of([1, .. Enumerable.Range(12, 9)]), 10),
        Make(3, "freight", FloorMask.All, 20),
    ];

    private static ElevatorSnapshot Make(int id, string type, ulong served, int capacity) =>
        new ElevatorUnit(id, Floors, ElevatorTiming.Default, new LookScheduling(), 1, capacity, 0.8, served, type).Snapshot();

    private static readonly IAccessPolicy[] Standard =
    [
        new ServedFloorsPolicy(),
        new CapacityUnitsPolicy(),
        new FloorRestrictionPolicy(new Dictionary<int, int> { [20] = 2, [15] = 1 }),
        new VipPolicy(dedicated: [1]),
    ];

    private static Request R(int from, int to, int load = 1, bool vip = false, int level = 0) =>
        Request.Create(from, to, Instant.Zero, Floors, id: 1, load: load).Value! with { Vip = vip, AccessLevel = level };

    public static readonly TheoryData<string, Request, int, string?> Table = new()
    {
        { "restricted destination, no access",    R(1, 20),                        0, "floor-restriction: floor 20 requires access level 2 (has 0)" },
        { "restricted destination, enough",       R(1, 20, level: 2),              0, null },
        { "restricted pickup, not enough",        R(15, 3, level: 0),              0, "floor-restriction: floor 15 requires access level 1 (has 0)" },
        { "restricted pickup, enough",            R(15, 3, level: 1),              0, null },
        { "express, unserved destination",        R(1, 5),                         2, "served-floors: E2 (express) does not serve floor 5" },
        { "express, unserved pickup",             R(5, 14),                        2, "served-floors: E2 (express) does not serve floor 5" },
        { "express, served both",                 R(12, 19),                       2, null },
        { "express, lobby to top",                R(1, 18),                        2, null },
        { "freight load on a local",              R(1, 3, load: 15),               0, "capacity: load 15 exceeds E0 (local) capacity 10" },
        { "freight load on the freight",          R(1, 3, load: 15),               3, null },
        { "non-VIP on the VIP elevator",          R(1, 3),                         1, "vip: E1 is dedicated to VIPs" },
        { "VIP on the VIP elevator",              R(1, 3, vip: true),              1, null },
        { "VIP on any other elevator",            R(1, 3, vip: true),              0, null },
        { "unserved and too heavy: served first", R(1, 5, load: 15),               2, "served-floors: E2 (express) does not serve floor 5" },
        { "too heavy and not VIP: capacity first",R(1, 3, load: 15),               1, "capacity: load 15 exceeds E1 (local) capacity 10" },
        { "restricted and not VIP",               R(1, 20),                        1, "floor-restriction: floor 20 requires access level 2 (has 0)" },
        { "everything wrong on the express",      R(5, 20, load: 15),              2, "served-floors: E2 (express) does not serve floor 5" },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Policy_request_elevator_table(string name, Request request, int elevator, string? expected)
    {
        var denial = new AccessPipeline(Standard).Check(request, Fleet[elevator]);
        Assert.True(expected == (denial is null ? null : $"{denial.Policy}: {denial.Reason}"), $"{name}: got {denial}");
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void Order_changes_only_the_reported_reason_never_the_outcome(string name, Request request, int elevator, string? expected)
    {
        var reversed = new AccessPipeline([.. Standard.Reverse()]).Check(request, Fleet[elevator]);
        Assert.True((expected is null) == (reversed is null), name);
    }

    [Fact]
    public void Each_policy_alone()
    {
        Assert.Null(new ServedFloorsPolicy().Deny(R(1, 20, load: 99), Fleet[0]));
        Assert.Null(new CapacityUnitsPolicy().Deny(R(1, 5), Fleet[2]));
        Assert.Equal("floor 20 requires access level 2 (has 1)", new FloorRestrictionPolicy(new Dictionary<int, int> { [20] = 2 }).Deny(R(20, 1, level: 1), Fleet[3]));
        Assert.Null(new VipPolicy(dedicated: []).Deny(R(1, 3), Fleet[1]));
    }

    [Fact]
    public void Empty_pipeline_allows_everything_with_normal_priority()
    {
        foreach (var s in Fleet) Assert.Null(AccessPipeline.AllowAll.Check(R(5, 20, load: 99), s));
    }

}
