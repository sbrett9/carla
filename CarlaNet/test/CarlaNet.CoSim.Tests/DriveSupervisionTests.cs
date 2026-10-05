using CarlaNet.Types.Supervision;

namespace CarlaNet.CoSim.Tests;

/// <summary>
/// What the interval binder hands a drive: the supervision in force per SUMO vehicle and the absences in
/// force for the world, bound to one plan and never minted.
/// </summary>
public sealed class DriveSupervisionTests
{
    private static readonly SupervisionPlanIdentity Plan = new("Shahid_Bahonar_Port_PatternOfLife", 2, "e357");

    private static readonly AnnotationInForce Lead = new(
        "Shahid_Bahonar_Port_PatternOfLife/pi_escort_drydock_d3", ["bahonar:coordinated_group_transit"],
        "transit", "bahonar:lead");

    private static readonly AbsenceInForce Unmanned = new(
        "Shahid_Bahonar_Port_PatternOfLife/pi_tower_relief_d4_h7_t3_unmanned", ["bahonar:post_unmanned"],
        ["tower_03"], "vacancy");

    [Fact]
    public void Nothing_Is_Held_Until_A_Plan_Is_Bound()
    {
        var supervision = new DriveSupervision();

        Assert.Throws<InvalidOperationException>(
            () => supervision.Set("escort_0", new SupervisionInForce(SupervisionState.Annotated, [Lead])));
        Assert.Throws<InvalidOperationException>(() => supervision.Open(Unmanned));
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
    public void The_Revision_Moves_Only_When_What_Is_Held_Changes()
    {
        var supervision = new DriveSupervision();
        supervision.Bind(Plan);
        long bound = supervision.Revision;

        supervision.Set("escort_0", new SupervisionInForce(SupervisionState.Annotated, [Lead]));
        long annotated = supervision.Revision;
        // The same assertion again, in a new instance: nothing changed.
        supervision.Set("escort_0", new SupervisionInForce(SupervisionState.Annotated, [Lead with { Labels = [.. Lead.Labels] }]));
        supervision.Open(Unmanned);
        long opened = supervision.Revision;
        supervision.Open(Unmanned with { Areas = ["tower_03"] });
        supervision.Close("Shahid_Bahonar_Port_PatternOfLife/pi_never_opened");
        supervision.Set("corridor_d0_p0_h6.12", SupervisionInForce.Unlabelled);

        Assert.True(annotated > bound);
        Assert.True(opened > annotated);
        Assert.Equal(opened, supervision.Revision);

        supervision.Set("escort_0", SupervisionInForce.Unlabelled);
        Assert.True(supervision.Revision > opened);
        Assert.Same(SupervisionInForce.Unlabelled, supervision.Of("escort_0"));
        Assert.Empty(supervision.Vehicles);
    }

    [Fact]
    public void Binding_A_Plan_Starts_Afresh_And_Withdrawing_Leaves_Nothing()
    {
        var supervision = new DriveSupervision();
        supervision.Bind(Plan);
        supervision.Set("escort_0", new SupervisionInForce(SupervisionState.Annotated, [Lead]));
        supervision.Open(Unmanned);

        supervision.Bind(Plan with { VocabularyDigest = "f00d" });
        Assert.Empty(supervision.Vehicles);
        Assert.Empty(supervision.Absences);
        Assert.Equal("f00d", supervision.Plan!.VocabularyDigest);

        supervision.Open(Unmanned);
        Assert.True(supervision.IsOpen(Unmanned.InstanceId));
        supervision.Withdraw();
        Assert.Null(supervision.Plan);
        Assert.Empty(supervision.Absences);
        Assert.False(supervision.IsOpen(Unmanned.InstanceId));
    }
}
