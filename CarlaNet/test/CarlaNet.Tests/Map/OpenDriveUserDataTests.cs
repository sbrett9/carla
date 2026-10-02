// Offline tests for the road-level <userData> CarlaNet.Map reads from an OpenDRIVE document.
//
// netconvert names the SUMO edge each road was converted from in a <userData code="sumoId"> entry, and
// nothing else in the file records it; the SUMO drive joins its lanes to the world's roads through it.
using System.Linq;
using CarlaNet.Map.OpenDrive;

namespace CarlaNet.Tests.Map;

public class OpenDriveUserDataTests
{
    private const string Document = """
        <OpenDRIVE>
          <road name="South Valley Highway" length="20.0" id="2054" junction="-1">
            <planView><geometry s="0.0" x="0.0" y="0.0" hdg="0.0" length="20.0"><line/></geometry></planView>
            <lanes><laneSection s="0.0">
              <right><lane id="-1" type="driving" level="false"><width sOffset="0.0" a="3.35" b="0.0" c="0.0" d="0.0"/>
                <userData code="laneData" value="not the road's"/></lane></right>
            </laneSection></lanes>
            <userData code="sumoId" value="106308386"/>
            <userData code="sumoId" value="a second one is not taken"/>
            <userData code="origin" value="osm"/>
          </road>
          <road name=":432193470_1" length="3.6" id="2639" junction="104">
            <link><predecessor elementType="road" elementId="2054" contactPoint="end"/><successor elementType="road" elementId="2054" contactPoint="start"/></link>
            <planView><geometry s="0.0" x="20.0" y="0.0" hdg="0.0" length="3.6"><line/></geometry></planView>
            <lanes><laneSection s="0.0">
              <right><lane id="-1" type="driving" level="false"><width sOffset="0.0" a="3.35" b="0.0" c="0.0" d="0.0"/></lane></right>
            </laneSection></lanes>
          </road>
        </OpenDRIVE>
        """;

    [Fact]
    public void ARoadCarriesItsOwnUserDataByCodeAndTheFirstValueOfARepeatedOne()
    {
        var map = OpenDriveParser.Load(Document)!;

        var highway = map.Roads[2054];
        Assert.Equal("106308386", highway.UserData["sumoId"]);
        Assert.Equal("osm", highway.UserData["origin"]);

        // A lane's own userData is the lane's, not the road's.
        Assert.False(highway.UserData.ContainsKey("laneData"));
        Assert.Equal(2, highway.UserData.Count);
    }

    [Fact]
    public void AJunctionConnectorCarriesNoneAndKeepsItsName()
    {
        var map = OpenDriveParser.Load(Document)!;

        var connector = map.Roads[2639];
        Assert.Empty(connector.UserData);
        Assert.Equal(":432193470_1", connector.Name);
        Assert.True(connector.IsJunction);
        Assert.Equal(2, map.Roads.Values.Count());
    }

    [Fact]
    public void ARoadLinkCarriesWhichEndOfTheLinkedRoadItMeets()
    {
        var map = OpenDriveParser.Load(Document)!;

        var connector = map.Roads[2639];
        Assert.Equal(2054u, connector.PredecessorRoadId);
        Assert.Equal("end", connector.PredecessorContactPoint);
        Assert.Equal(2054u, connector.SuccessorRoadId);
        Assert.Equal("start", connector.SuccessorContactPoint);

        // A road with no link, or one naming a junction, has none.
        Assert.Equal(string.Empty, map.Roads[2054].PredecessorContactPoint);
        Assert.Equal(string.Empty, map.Roads[2054].SuccessorContactPoint);
    }
}
