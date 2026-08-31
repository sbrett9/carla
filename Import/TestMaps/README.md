# Generated road-network snapshots

Successive outputs of the OSM -> OpenDRIVE pipeline for the Arapahoe / I-25 area, kept so a
change to the conversion can be measured against what came before rather than only looked at.
Each was produced from the same OSM extract; they differ only in what the pipeline did with it.

| file | what it captures |
|---|---|
| `Arapahoe_I25_Plain.xodr` | pure netconvert, no elevation injection at all |
| `Arapahoe_I25_crossfall.xodr` | measured crossfall written as `<superelevation>` |
| `Arapahoe_I25_collapsed.xodr` | pass-through junctions joined into their neighbours |
| `Arapahoe_I25_gradefix.xodr` | grade-separation connectivity scoped per lane section |
| `Arapahoe_I25_continuity.xodr` | flat cross-section, road heights reconciled at contacts |
| `Arapahoe_I25_decks3.xodr` | bridges shaped as ramp-deck-ramp |
| `Arapahoe_I25_junction_surfaces.xodr` | overlapping connector surfaces inside each junction reconciled |

`elev_snap`, `nodeheights`, `flatnode`, `decks` and `decks2` are experiments that were measured
and set aside; they are kept because the measurements that rejected them are worth repeating
before anyone tries the same idea again. See Docs/CAT_Research for the write-ups.
