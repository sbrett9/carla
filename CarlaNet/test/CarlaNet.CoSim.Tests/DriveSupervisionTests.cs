using CarlaNet.Types.Supervision;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// What the interval binder hands a drive: the supervision in force per SUMO vehicle, bound to one plan
/// and never minted. Nothing is held for the world apart from the plan (06 §3.5).
/// </summary>
public sealed class DriveSupervisionTests
{
    private static readonly SupervisionPlanIdentity Plan = new("Shahid_Bahonar_Port_PatternOfLife", 3, "e357");

    private static readonly AnnotationInForce Lead = new(
        "Shahid_Bahonar_Port_PatternOfLife/pi_escort_drydock_d3", ["bahonar:coordinated_group_transit"],
        "transit", "bahonar:lead");

    [Fact]
    public void Nothing_Is_Held_Until_A_Plan_Is_Bound()
    {
        var supervision = new DriveSupervision();

        Assert.Throws<InvalidOperationException>(
            () => supervision.Set("escort_0", new SupervisionInForce(SupervisionState.Annotated, [Lead])));
        Assert.Null(supervision.Plan);
        Assert.Same(SupervisionInForce.Unlabelled, supervision.Of("escort_0"));
    }

    [Fact]
    public void A_State_No_Vehicle_Can_Be_In_Is_Refused()
    {
        var supervision = new DriveSupervision();
        supervision.Bind(Plan);

        Assert.Throws<ArgumentException>(
            () => supervision.Set("escort_0", new SupervisionInForce(SupervisionState.Annotated, [])));
        Assert.Throws<ArgumentException>(
            () => supervision.Set("escort_0", new SupervisionInForce(SupervisionState.Unlabelled, [Lead])));
        Assert.Throws<ArgumentException>(
            () => supervision.Set("escort_0", new SupervisionInForce(SupervisionState.Nominal, [Lead with { InstanceId = "" }])));
        Assert.Throws<ArgumentException>(() => supervision.Set("escort_0", new SupervisionInForce((SupervisionState)7, [])));
        Assert.Empty(supervision.Vehicles);

        // Nominal may name the ordinary behaviour it is, or nothing.
        supervision.Set("guard_d4_h15_t3", new SupervisionInForce(SupervisionState.Nominal, []));
        Assert.Equal(SupervisionState.Nominal, supervision.Of("guard_d4_h15_t3").State);
    }

    [Fact]
    public void Every_Row_Is_A_Vehicle_s()
    {
        // The owner's ruling of 2026-10-05: SUMO reports vehicles, not places, so this table has no row
        // for the world and no way to hold one. A vehicle id is the only key.
        var supervision = new DriveSupervision();
        supervision.Bind(Plan);

        Assert.Throws<ArgumentException>(
            () => supervision.Set(string.Empty, new SupervisionInForce(SupervisionState.Annotated, [Lead])));
        Assert.DoesNotContain(typeof(DriveSupervision).GetMembers(), member => member.Name.Contains("Absence", StringComparison.Ordinal));
        Assert.Empty(supervision.Vehicles);
    }

    [Fact]
    public void The_Revision_Moves_Only_When_What_Is_Held_Changes()
    {
        var supervision = new DriveSupervision();
        supervision.Bind(Plan);
        long bound = supervision.Revision;

        supervision.Set("escort_0", new SupervisionInForce(SupervisionState.Annotated, [Lead]));
        long annotated = supervision.Revision;
        // The same assertion again, in a new instance: nothing changed.
        supervision.Set("escort_0", new SupervisionInForce(SupervisionState.Annotated, [Lead with { Labels = [.. Lead.Labels] }]));
        supervision.Set("corridor_d0_p0_h6.12", SupervisionInForce.Unlabelled);

        Assert.True(annotated > bound);
        Assert.Equal(annotated, supervision.Revision);

        supervision.Set("escort_0", SupervisionInForce.Unlabelled);
        Assert.True(supervision.Revision > annotated);
        Assert.Same(SupervisionInForce.Unlabelled, supervision.Of("escort_0"));
        Assert.Empty(supervision.Vehicles);
    }

    [Fact]
    public void Binding_A_Plan_Starts_Afresh_And_Withdrawing_Leaves_Nothing()
    {
        var supervision = new DriveSupervision();
        supervision.Bind(Plan);
        supervision.Set("escort_0", new SupervisionInForce(SupervisionState.Annotated, [Lead]));

        supervision.Bind(Plan with { VocabularyDigest = "f00d" });
        Assert.Empty(supervision.Vehicles);
        Assert.Equal("f00d", supervision.Plan!.VocabularyDigest);

        supervision.Set("escort_0", new SupervisionInForce(SupervisionState.Annotated, [Lead]));
        long held = supervision.Revision;
        supervision.Withdraw();
        Assert.Null(supervision.Plan);
        Assert.Empty(supervision.Vehicles);
        Assert.True(supervision.Revision > held);

        // Withdrawing what is already withdrawn changes nothing.
        long withdrawn = supervision.Revision;
        supervision.Withdraw();
        Assert.Equal(withdrawn, supervision.Revision);
    }
}
