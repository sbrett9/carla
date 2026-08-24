"""Convert CARLA CoT-XML recording sidecars to CSV format.

Reads Cursor-on-Target XML files produced by SCTMV recording sessions and extracts
vehicle truth data into a consolidated CSV file suitable for analysis or ML training.

Each XML file represents one captured frame containing:
  * Timestamp
  * Filename
  * Vehicle Identification 
  * Multiple vehicle tracks with position (lat/lon), speed

The output CSV contains one row per vehicle per frame with all relevant attributes.

Usage:
    python XmlToCsvConverter.py -i path/to/recordings -o path/to/output.csv
"""

import argparse
import csv
import logging
import sys
import xml.etree.ElementTree as ET
from dataclasses import dataclass, fields
from datetime import datetime
from pathlib import Path


@dataclass
class VehicleTruthRecord:
    """One vehicle observation at a single timestamp (one recorded frame)."""

    # Timestamp and source metadata
    timestamp: str              # ISO-8601 UTC timestamp when captured
    frame_file: str             # Source XML filename (links to corresponding PNG)

    # Vehicle identification
    actor_id: int               # CARLA actor ID
    uid: str                    # CoT unique ID (CARLA-TRUTH-{actor_id})
    callsign: str               # Vehicle callsign (e.g., "car-412")

    # Position (geodetic WGS84)
    lat: float                  # Latitude (degrees)
    lon: float                  # Longitude (degrees)

    # Kinematics
    speed_mps: float            # Speed (meters per second)


class XmlToCsvConverter:
    """Converts CoT-XML recording files to CSV format."""

    def __init__(self, input_dir: Path, output_csv: Path):
        """Initialize the converter.

        Args:
            input_dir: Directory containing XML recording files
            output_csv: Path to output CSV file
        """
        self.input_dir = input_dir
        self.output_csv = output_csv
        self.logger = logging.getLogger(__name__)

    def convert(self) -> None:
        """Process all XML files in input directory and write to CSV."""
        if not self.input_dir.exists():
            self.logger.error(f"Input directory does not exist: {self.input_dir}")
            raise FileNotFoundError(f"Input directory not found: {self.input_dir}")

        if not self.input_dir.is_dir():
            self.logger.error(f"Input path is not a directory: {self.input_dir}")
            raise NotADirectoryError(f"Not a directory: {self.input_dir}")

        # Find all XML files
        xml_files = sorted(self.input_dir.glob("*.xml"))
        if not xml_files:
            self.logger.warning(f"No XML files found in {self.input_dir}")
            return

        self.logger.info(f"Found {len(xml_files)} XML files in {self.input_dir}")

        # Parse all files and collect records
        all_records = []
        for xml_file in xml_files:
            self.logger.debug(f"Parsing {xml_file.name}")
            records = self._parse_xml_file(xml_file)
            all_records.extend(records)
            if records:
                self.logger.debug(f"  Extracted {len(records)} vehicle record(s)")

        if not all_records:
            self.logger.warning("No vehicle records found in any XML files")
            return

        self.logger.info(f"Extracted {len(all_records)} total vehicle records from {len(xml_files)} files")

        # Write to CSV
        self._write_csv(all_records)
        self.logger.info(f"CSV conversion complete: {self.output_csv}")

    def _parse_xml_file(self, xml_path: Path) -> list[VehicleTruthRecord]:
        """Parse one XML file and return all vehicle records it contains."""
        try:
            tree = ET.parse(xml_path)
            root = tree.getroot()

            if root.tag != "events":
                self.logger.warning(f"Skipping {xml_path.name}: root tag is '{root.tag}', expected 'events'")
                return []

            # Extract the capture timestamp from the root element
            capture_time = root.get("captured", "")
            if not capture_time:
                self.logger.warning(f"Skipping {xml_path.name}: no 'captured' timestamp")
                return []

            records = []
            for event in root.findall("event"):
                uid = event.get("uid", "")
                # Only process vehicle truth events (not the sensor platform)
                if not uid.startswith("CARLA-TRUTH-"):
                    continue

                record = self._parse_vehicle_event(event, capture_time, xml_path.name)
                if record:
                    records.append(record)

            return records

        except ET.ParseError as e:
            self.logger.error(f"XML parse error in {xml_path.name}: {e}")
            return []
        except Exception as e:
            self.logger.error(f"Failed to parse {xml_path.name}: {e}")
            return []

    def _parse_vehicle_event(self, event: ET.Element, timestamp: str, filename: str) -> VehicleTruthRecord | None:
        """Parse a single vehicle <event> element into a VehicleTruthRecord."""
        try:
            uid = event.get("uid", "")

            # Parse <point> element
            point = event.find("point")
            if point is None:
                return None
            lat = float(point.get("lat", 0.0))
            lon = float(point.get("lon", 0.0))

            # Parse <detail> sub-elements
            detail = event.find("detail")
            if detail is None:
                return None

            track = detail.find("track")
            if track is None:
                return None
            speed_mps = float(track.get("speed", 0.0))

            contact = detail.find("contact")
            callsign = contact.get("callsign", "") if contact is not None else ""

            # Parse <_carla> element (extended vehicle attributes)
            carla_elem = detail.find("_carla")
            if carla_elem is None:
                return None

            actor_id = int(carla_elem.get("actor_id", 0))
            
            return VehicleTruthRecord(
                timestamp=timestamp,
                frame_file=filename,
                actor_id=actor_id,
                uid=uid,
                callsign=callsign,
                lat=lat,
                lon=lon,
                speed_mps=speed_mps,
            )

        except (ValueError, AttributeError) as e:
            self.logger.warning(f"Failed to parse vehicle event in {filename}: {e}")
            return None

    def _write_csv(self, records: list[VehicleTruthRecord]) -> None:
        """Write all records to the CSV file."""
        if not records:
            self.logger.warning("No records to write")
            return

        # Ensure output directory exists
        self.output_csv.parent.mkdir(parents=True, exist_ok=True)

        # If file exists, create a new one with timestamp
        output_path = self.output_csv
        if output_path.exists():
            timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
            stem = output_path.stem
            suffix = output_path.suffix
            output_path = output_path.parent / f"{stem}_{timestamp}{suffix}"
            self.logger.info(f"Output file exists, creating new file: {output_path.name}")

        # Get field names from the dataclass
        field_names = [f.name for f in fields(VehicleTruthRecord)]

        try:
            with open(output_path, "w", newline="", encoding="utf-8") as f:
                writer = csv.DictWriter(f, fieldnames=field_names)
                writer.writeheader()

                for record in records:
                    # Convert dataclass to dict
                    row = {field.name: getattr(record, field.name) for field in fields(record)}
                    writer.writerow(row)

            self.logger.info(f"Wrote {len(records)} records to {output_path}")

        except IOError as e:
            self.logger.error(f"Failed to write CSV to {output_path}: {e}")
            raise


def main():
    """CLI entry point for the XML to CSV converter."""
    # Default input uses relative path for portability
    default_input = Path("Build/SCTMV_recordings")

    parser = argparse.ArgumentParser(
        description="Convert CARLA CoT-XML recording sidecars to CSV format.",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""

Reads all .xml files from the input directory and extracts vehicle truth data 
into a CSV file.
        """,
    )

    parser.add_argument(
        "-i",
        "--input",
        dest="input_dir",
        type=Path,
        default=default_input,
        help=f"Directory containing recording XML files (default: {default_input})",
    )
    parser.add_argument(
        "-o",
        "--output",
        dest="output_csv",
        type=Path,
        required=True,
        help="Output CSV file path or directory (required). If a directory, creates YYYYMMDD_HHMMSS_truth.csv inside.",
    )
    parser.add_argument(
        "-v",
        "--verbose",
        action="store_true",
        help="Enable verbose logging (debug level)",
    )

    args = parser.parse_args()

    # Configure logging
    log_level = logging.DEBUG if args.verbose else logging.INFO
    logging.basicConfig(
        level=log_level,
        format="%(levelname)s: %(message)s",
        handlers=[logging.StreamHandler(sys.stdout)],
    )

    logger = logging.getLogger(__name__)

    # Handle case where user specifies a directory instead of a file
    output_path = args.output_csv
    if output_path.exists() and output_path.is_dir():
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        output_path = output_path / f"{timestamp}_truth.csv"
        logger.info(f"Output is a directory, using: {output_path}")
    elif str(output_path).endswith(('\\', '/')):
        # Path ends with separator, treat as directory
        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        output_path = Path(str(output_path).rstrip('\\/')) / f"{timestamp}_truth.csv"
        logger.info(f"Output appears to be a directory, using: {output_path}")

    try:
        converter = XmlToCsvConverter(args.input_dir, output_path)
        converter.convert()
    except Exception as e:
        logger.error(f"Conversion failed: {e}")
        sys.exit(1)


if __name__ == "__main__":
    main()
