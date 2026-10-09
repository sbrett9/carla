"""Check 41's statistic reproduces the measured figure, buckets by band, and only ever warns.

`07_Scenario_Authoring.md` §5.6.1 measured `I(hour; label) / H(label) = 0.600` on the sizing scenario
from a published contingency table (610 route entries, 9 annotated, by departure hour). The statistic
does not care what its buckets are, so feeding it that table must give that number; a computation
that did not would be computing something other than what the plan measured. The band function is
pinned at the edges doc 11 §4.4 names, and is the table in `CarlaNet.Types` the recorder writes every
capture's band from.
"""
from __future__ import annotations

import sys
from pathlib import Path

import pytest

_REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(_REPO / "CarlaControl" / "src"))

from carlacontrol.AnnotationVocabulary import CORE_TERMS  # noqa: E402
from carlacontrol.IlluminationBand import BAND_SOURCE, IlluminationBand  # noqa: E402
from carlacontrol.IlluminationLabelAssociation import IlluminationLabelAssociation  # noqa: E402

# 07 §5.6.1: hour -> (annotated, total), the shipped sizing scenario's 610 route entries.
BAHONAR_BY_HOUR = {
    "00": (0, 21), "02": (1, 1), "06": (0, 35), "07": (0, 125), "08": (1, 15), "09": (0, 7),
    "10": (5, 40), "11": (2, 2), "12-14": (0, 35), "15": (0, 126), "16-18": (0, 56), "20": (0, 21),
    "23": (0, 126),
}


def bahonar_pairs() -> list[tuple[str, str]]:
    pairs = []
    for hour, (annotated, total) in BAHONAR_BY_HOUR.items():
        pairs += [(hour, "annotated")] * annotated + [(hour, "unlabelled")] * (total - annotated)
    return pairs


def test_the_measured_figure_is_reproduced_from_its_own_table():
    table = IlluminationLabelAssociation._table(bahonar_pairs())
    assert table["entries"] == 610
    assert table["state_totals"]["annotated"] == 9
    assert table["normalized_mutual_information"] == 0.600


def test_the_bands_where_one_state_alone_occurs_and_where_both_do_are_named():
    table = IlluminationLabelAssociation._table(bahonar_pairs())
    assert {"02", "11"} <= set(table["degenerate_bands"])
    assert set(table["mixed_bands"]) == {"08", "10"}
    # Every key is a count, a ratio or a list of bands: no word that judges what they mean.
    assert "usable_bands" not in table


def test_one_supervision_state_leaves_the_statistic_undefined_rather_than_zero():
    table = IlluminationLabelAssociation._table([("day", "unlabelled"), ("night", "unlabelled")])
    assert table["normalized_mutual_information"] is None
    assert table["degenerate_bands"] == []


def test_independence_scores_zero_and_determination_scores_one():
    independent = [("day", "annotated"), ("day", "nominal"), ("night", "annotated"),
                   ("night", "nominal")]
    determined = [("day", "annotated"), ("day", "annotated"), ("night", "nominal"),
                  ("night", "nominal")]
    assert IlluminationLabelAssociation._table(independent)["normalized_mutual_information"] == 0.0
    assert IlluminationLabelAssociation._table(determined)["normalized_mutual_information"] == 1.0


@pytest.mark.parametrize(("elevation", "band"), [
    (45.0, "day"), (6.0001, "day"), (6.0, "golden"), (0.0001, "golden"), (0.0, "civil_twilight"),
    (-6.0, "nautical_twilight"), (-11.99, "nautical_twilight"), (-12.0, "astronomical_twilight"),
    (-18.0, "night"), (-60.95, "night"),
])
def test_the_band_edges_are_doc_11s(elevation, band):
    assert IlluminationBand.of(elevation) == band


def test_the_vocabulary_spells_the_bands_the_statistic_buckets_by():
    """Doc 11 §4.4 is the single definition of the bands (07 §12 question 13): the core vocabulary's
    `illumination_band` terms are the names the band function assigns, in its order."""
    assert CORE_TERMS["illumination_band"] == IlluminationBand.names() == [
        "day", "golden", "civil_twilight", "nautical_twilight", "astronomical_twilight", "night"]
    assert {IlluminationBand.of(e) for e in range(-90, 91)} == set(IlluminationBand.names())


def test_the_bands_are_the_table_the_recorder_writes_every_capture_s_band_from():
    """One table in both languages: the band function here is `CarlaNet.Types`'s, read through
    `carlanet`, the one `CotWriter` and `SolarMetadata` write each capture's band with, so the two
    agree at every edge and on either side of each."""
    import carlanet  # noqa: F401
    from CarlaNet.Types.Illumination import IlluminationBands

    assert IlluminationBand.edges() == [
        {"band": str(edge.Name),
         "above_deg": None if edge.AboveDegrees is None else float(edge.AboveDegrees)}
        for edge in IlluminationBands.Table] == [
        {"band": "day", "above_deg": 6.0}, {"band": "golden", "above_deg": 0.0},
        {"band": "civil_twilight", "above_deg": -6.0}, {"band": "nautical_twilight", "above_deg": -12.0},
        {"band": "astronomical_twilight", "above_deg": -18.0}, {"band": "night", "above_deg": None}]
    assert BAND_SOURCE == str(IlluminationBands.Source) == (
        "the sun's refraction-corrected elevation in degrees: day above 6, golden above 0, "
        "civil_twilight above -6, nautical_twilight above -12, astronomical_twilight above -18, "
        "night at -18 and below")
    for edge in (6.0, 0.0, -6.0, -12.0, -18.0):
        for elevation in (edge - 1e-9, edge, edge + 1e-9):
            assert IlluminationBand.of(elevation) == str(IlluminationBands.NameOf(elevation))


@pytest.mark.parametrize("elevation", [-180.0, 90.5, float("nan")])
def test_an_elevation_that_is_not_a_sun_s_has_no_band(elevation):
    """-180 is the engine's sun for an impossible date (doc 11 F4): below -18, and not a night."""
    with pytest.raises(ValueError, match="not a sun's elevation"):
        IlluminationBand.of(elevation)
