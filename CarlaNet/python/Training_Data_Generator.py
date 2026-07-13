"""Training Data Generator for CARLA Simulation

This script processes CARLA simulation data to generate YOLO format training datasets.
It takes a folder containing paired image (.png) and telemetry (.xml) files, extracts
vehicle positions and sensor poses, projects 3D bounding boxes to 2D image coordinates,
and outputs a properly formatted YOLO dataset with train/val/test splits.

Input Format:
    - Images: PNG files (e.g., SCTMV_2026.07.07_22.31.43.801.png)
    - Telemetry: XML files with matching names (e.g., SCTMV_2026.07.07_22.31.43.801.xml)
    - XML contains sensor pose (lat/lon/hae, orientation, intrinsics) and vehicle poses

Output Format:
    - YOLO dataset structure: images/{train,val,test}/ and labels/{train,val,test}/
    - Labels in YOLO format: class_id x1 y1 x2 y2 x3 y3 x4 y4 (OBB) or class_id cx cy w h (AABB)
    - data.yaml configuration file for YOLO training
    - Optional labeled preview images with bounding boxes drawn

Coordinate Systems:
    - Input: WGS84 geodetic (lat/lon/hae)
    - Intermediate: Local ENU (East-North-Up) frame relative to sensor
    - Output: Normalized image coordinates (0-1)

Usage:
    python Training_Data_Generator.py /path/to/carla/data -o /path/to/output
    python Training_Data_Generator.py /path/to/carla/data -o /path/to/output --verbose
    python Training_Data_Generator.py /path/to/carla/data -o /path/to/output --train-split 0.8 --val-split 0.15 --test-split 0.05

Author: CARLA Dataset Team
Version: 2.0
"""

import argparse
import os
from PIL import Image
import json
import logging
from datetime import datetime
import random
import shutil
import math
import numpy as np
import cv2

import xml.etree.ElementTree as ET
from typing import Optional, List, Dict, Tuple, Any


class Sensor_Pose():
    """Represents a sensor's pose and configuration in the CARLA simulation.
    
    This class stores the sensor's position (lat/lon/hae), orientation (azimuth, elevation, roll),
    motion (course, speed), and camera intrinsics (FOV, image dimensions).
    
    Attributes:
        uid (str): Unique identifier for the sensor
        type (str): Sensor type (e.g., 'camera')
        callsign (str): Sensor callsign/name
        lat (float): Latitude in degrees (WGS84)
        lon (float): Longitude in degrees (WGS84)
        hae (float): Height above ellipsoid in meters
        align_offset_m (float): Alignment offset in meters
        az_deg (float): Azimuth/yaw angle in degrees (0=North, 90=East)
        el_deg (float): Elevation/pitch angle in degrees (-90=down, 0=horizon, 90=up)
        roll_deg (float): Roll angle in degrees
        course_deg (float): Course/heading in degrees
        speed_mps (float): Speed in meters per second
        intrinsics (dict): Camera intrinsics with 'fov' and 'image_size_x'/'image_size_y'
    """
    
    def __init__(self, uid: Optional[str] = None, type: Optional[str] = None, 
                 callsign: Optional[str] = None, lat: Optional[float] = None, 
                 lon: Optional[float] = None, hae: Optional[float] = None, 
                 align_offset_m: Optional[float] = None, az_deg: Optional[float] = None, 
                 el_deg: Optional[float] = None, roll_deg: Optional[float] = None, 
                 course_deg: Optional[float] = None, speed_mps: Optional[float] = None, 
                 intrinsics: Optional[Dict[str, Any]] = None):
        self.uid = uid
        self.type = type
        self.callsign = callsign
        self.lat = lat
        self.lon = lon
        self.hae = hae
        self.align_offset_m = align_offset_m
        self.az_deg = az_deg
        self.el_deg = el_deg
        self.roll_deg = roll_deg
        self.course_deg = course_deg
        self.speed_mps = speed_mps
        self.intrinsics = intrinsics
    
    @staticmethod
    def from_json(json_str):
        try:
            data = json.loads(json_str)
            return Sensor_Pose(
                uid=data.get('uid'),
                type=data.get('type'),
                callsign=data.get('callsign'),
                lat=data.get('lat'),
                lon=data.get('lon'),
                hae=data.get('hae'),
                align_offset_m=data.get('align_offset_m'),
                az_deg=data.get('az_deg'),
                el_deg=data.get('el_deg'),
                roll_deg=data.get('roll_deg'),
                course_deg=data.get('course_deg'),
                speed_mps=data.get('speed_mps'),
                intrinsics=data.get('intrinsics')
            )
        except (json.JSONDecodeError, KeyError) as e:
            print(f"Error parsing sensor JSON: {e}")
            return None
    
    @staticmethod
    def from_xml(xml_node: ET.Element) -> Optional['Sensor_Pose']:
        """
        Create Sensor_Pose from an XML <event> element (sensor platform track).
        
        Args:
            xml_node: ET.Element representing a sensor platform <event> node
            
        Returns:
            Sensor_Pose object with all parsed sensor data, or None if parsing fails
        """
        try:
            # Parse event attributes
            uid = xml_node.get('uid')
            event_type = xml_node.get('type')
            time = xml_node.get('time')
            
            # Parse point data
            point = xml_node.find('point')
            if point is None:
                return None
            
            lat = float(point.get('lat', 0))
            lon = float(point.get('lon', 0))
            hae = float(point.get('hae', 0))
            
            # Parse detail section
            detail = xml_node.find('detail')
            if detail is None:
                return None
            
            # Parse contact data
            contact = detail.find('contact')
            callsign = contact.get('callsign', '') if contact is not None else ''
            
            # Parse track data
            track = detail.find('track')
            course_deg = float(track.get('course', 0)) if track is not None else 0.0
            speed_mps = float(track.get('speed', 0)) if track is not None else 0.0
            
            # Parse sensor data
            sensor = detail.find('sensor')
            az_deg = float(sensor.get('azimuth', 0)) if sensor is not None else 0.0
            el_deg = float(sensor.get('elevation', 0)) if sensor is not None else 0.0
            roll_deg = float(sensor.get('roll', 0)) if sensor is not None else 0.0
            
            # Parse CARLA intrinsics
            intrinsics_elem = detail.find('_carla_intrinsics')
            intrinsics = None
            align_offset_m = 0.0
            if intrinsics_elem is not None:
                align_offset_m = float(intrinsics_elem.get('align_offset_m', 0))
                intrinsics = {
                    'width': int(intrinsics_elem.get('width', 0)),
                    'height': int(intrinsics_elem.get('height', 0)),
                    'fx': float(intrinsics_elem.get('fx', 0)),
                    'fy': float(intrinsics_elem.get('fy', 0)),
                    'cx': float(intrinsics_elem.get('cx', 0)),
                    'cy': float(intrinsics_elem.get('cy', 0)),
                    'hfov_deg': float(intrinsics_elem.get('hfov_deg', 0)),
                    'vfov_deg': float(intrinsics_elem.get('vfov_deg', 0)),
                    'model': intrinsics_elem.get('model', ''),
                    'distortion': intrinsics_elem.get('distortion', '')
                }
            
            return Sensor_Pose(
                uid=uid,
                type=event_type,
                callsign=callsign,
                lat=lat,
                lon=lon,
                hae=hae,
                align_offset_m=align_offset_m,
                az_deg=az_deg,
                el_deg=el_deg,
                roll_deg=roll_deg,
                course_deg=course_deg,
                speed_mps=speed_mps,
                intrinsics=intrinsics
            )
            
        except (ValueError, AttributeError) as e:
            print(f"Error parsing sensor XML: {e}")
            return None
    
    def __repr__(self):
        return (f"Sensor_Pose(uid={self.uid}, callsign={self.callsign}, "
                f"lat={self.lat}, lon={self.lon}, hae={self.hae}, "
                f"az={self.az_deg}, el={self.el_deg}, roll={self.roll_deg})")


class Vehicle_Pose():
    """Represents a vehicle's pose and properties in the CARLA simulation.
    
    This class stores the vehicle's position, orientation, motion, and physical dimensions
    for generating bounding box labels.
    
    Attributes:
        uid (str): Unique identifier for the vehicle
        type (str): Event type
        callsign (str): Vehicle callsign/name
        base_type (str): Vehicle type (e.g., 'vehicle.car', 'vehicle.truck')
        lat (float): Latitude in degrees (WGS84)
        lon (float): Longitude in degrees (WGS84)
        hae (float): Height above ellipsoid in meters
        az_deg (float): Azimuth/yaw angle in degrees
        el_deg (float): Elevation/pitch angle in degrees
        roll_deg (float): Roll angle in degrees
        course_deg (float): Course/heading in degrees
        speed_mps (float): Speed in meters per second
        length_m (float): Vehicle length in meters
        width_m (float): Vehicle width in meters
        height_m (float): Vehicle height in meters
        actor_id (int): CARLA actor ID
        type_id (str): CARLA type ID
        color (str): Vehicle color
        role_name (str): CARLA role name
        vx, vy, vz (float): Velocity components
    """
    
    def __init__(self, uid: Optional[str] = None, type: Optional[str] = None, 
                 callsign: Optional[str] = None, lat: Optional[float] = None, 
                 lon: Optional[float] = None, hae: Optional[float] = None,
                 az_deg: Optional[float] = None, el_deg: Optional[float] = None, 
                 roll_deg: Optional[float] = None, course_deg: Optional[float] = None, 
                 speed_mps: Optional[float] = None, ce: Optional[float] = None, 
                 le: Optional[float] = None, time: Optional[str] = None, 
                 how: Optional[str] = None, actor_id: Optional[int] = None, 
                 type_id: Optional[str] = None, base_type: Optional[str] = None, 
                 special_type: Optional[str] = None, length_m: Optional[float] = None, 
                 width_m: Optional[float] = None, height_m: Optional[float] = None,
                 color: Optional[str] = None, role_name: Optional[str] = None, 
                 vx: Optional[float] = None, vy: Optional[float] = None, 
                 vz: Optional[float] = None):
        self.uid = uid
        self.type = type
        self.callsign = callsign
        self.lat = lat
        self.lon = lon
        self.hae = hae
        self.az_deg = az_deg
        self.el_deg = el_deg
        self.roll_deg = roll_deg
        self.course_deg = course_deg
        self.speed_mps = speed_mps
        self.ce = ce
        self.le = le
        self.time = time
        self.how = how
        self.actor_id = actor_id
        self.type_id = type_id
        self.base_type = base_type
        self.special_type = special_type
        self.length_m = length_m
        self.width_m = width_m
        self.height_m = height_m
        self.color = color
        self.role_name = role_name
        self.vx = vx
        self.vy = vy
        self.vz = vz
    
    @staticmethod
    def from_xml(xml_node: ET.Element) -> Optional['Vehicle_Pose']:
        """
        Create Vehicle_Pose from an XML <event> element.
        
        Args:
            xml_node: ET.Element representing an <event> node
            
        Returns:
            Vehicle_Pose object with all parsed vehicle data, or None if parsing fails
        """
        try:
            # Parse event attributes
            uid = xml_node.get('uid')
            event_type = xml_node.get('type')
            how = xml_node.get('how')
            time = xml_node.get('time')
            
            # Parse point data
            point = xml_node.find('point')
            if point is None:
                return None
            
            lat = float(point.get('lat', 0))
            lon = float(point.get('lon', 0))
            hae = float(point.get('hae', 0))
            ce = float(point.get('ce', 0))
            le = float(point.get('le', 0))
            
            # Parse detail section
            detail = xml_node.find('detail')
            if detail is None:
                return None
            
            # Parse track data
            track = detail.find('track')
            course_deg = float(track.get('course', 0)) if track is not None else 0.0
            speed_mps = float(track.get('speed', 0)) if track is not None else 0.0
            
            # Parse contact data
            contact = detail.find('contact')
            callsign = contact.get('callsign', '') if contact is not None else ''
            
            # Parse CARLA-specific data
            carla = detail.find('_carla')
            if carla is not None:
                actor_id = int(carla.get('actor_id', 0))
                type_id = carla.get('type_id', '')
                base_type = carla.get('base_type', '')
                special_type = carla.get('special_type', '')
                length_m = float(carla.get('length_m', 0))
                width_m = float(carla.get('width_m', 0))
                height_m = float(carla.get('height_m', 0))
                color = carla.get('color', '')
                role_name = carla.get('role_name', '')
                vx = float(carla.get('vx', 0))
                vy = float(carla.get('vy', 0))
                vz = float(carla.get('vz', 0))
            else:
                actor_id = 0
                type_id = base_type = special_type = color = role_name = ''
                length_m = width_m = height_m = vx = vy = vz = 0.0
            
            # Create and return Vehicle_Pose object with all attributes
            return Vehicle_Pose(
                uid=uid,
                type=event_type,
                callsign=callsign,
                lat=lat,
                lon=lon,
                hae=hae,
                ce=ce,
                le=le,
                time=time,
                how=how,
                course_deg=course_deg,
                speed_mps=speed_mps,
                actor_id=actor_id,
                type_id=type_id,
                base_type=base_type,
                special_type=special_type,
                length_m=length_m,
                width_m=width_m,
                height_m=height_m,
                color=color,
                role_name=role_name,
                vx=vx,
                vy=vy,
                vz=vz
            )
            
        except (ValueError, AttributeError) as e:
            print(f"Error parsing vehicle XML: {e}")
            return None
    
    def __repr__(self):
        return (f"Vehicle_Pose(uid={self.uid}, callsign={self.callsign}, "
                f"lat={self.lat}, lon={self.lon}, hae={self.hae}, "
                f"course={self.course_deg}, speed={self.speed_mps})")
        







class Training_Data_Generator():
    """Main class for generating YOLO training datasets from CARLA simulation data.
    
    This class handles the complete pipeline: parsing XML telemetry, converting coordinates,
    projecting 3D bounding boxes to 2D, filtering labels, and saving in YOLO format.
    
    Args:
        input_folder_path (str): Path to folder containing paired .png and .xml files
        output_folder_path (str): Path to output folder for YOLO dataset
        make_timestamp_folder (bool): If True, create timestamped subfolder in output
        label_images (bool): If True, generate labeled preview images with bboxes drawn
        generate_labeled_images (bool): Deprecated, use label_images instead
        log_level (int): Logging level (logging.DEBUG, INFO, WARNING, ERROR)
        train_split (float): Fraction of data for training (0-1)
        val_split (float): Fraction of data for validation (0-1)
        test_split (float): Fraction of data for testing (0-1)
        min_bbox_area (float): Minimum bbox area (normalized 0-1) to include
        max_bbox_area (float): Maximum bbox area (normalized 0-1) to include
        class_names (dict): Mapping of class IDs to names, e.g., {0: 'vehicle'}
        filter_vehicle_types (list): Only include these vehicle types, e.g., ['vehicle.car']
        obb (bool): If True, use oriented bounding boxes; if False, use axis-aligned
        skip_frames_without_labels (bool): If True, exclude frames with no valid labels
    
    Raises:
        ValueError: If train/val/test splits don't sum to 1.0
    
    Example:
        >>> generator = Training_Data_Generator(
        ...     '/data/carla', '/data/output',
        ...     train_split=0.8, val_split=0.15, test_split=0.05,
        ...     min_bbox_area=0.01, obb=True
        ... )
        >>> generator.generate()
    """
    
    def __init__(self, input_folder_path: str, output_folder_path: str, 
                 make_timestamp_folder: bool = True, label_images: bool = True, 
                 generate_labeled_images: bool = True, log_level: int = logging.INFO,
                 train_split: float = 0.7, val_split: float = 0.2, test_split: float = 0.1,
                 min_bbox_area: float = 0.0, max_bbox_area: float = 1.0,
                 class_names: Optional[Dict[int, str]] = None, 
                 filter_vehicle_types: Optional[List[str]] = None,
                 obb: bool = True, skip_frames_without_labels: bool = False):
        
        self.input_folder_path = input_folder_path
        

        self.output_folder_path = output_folder_path
        if make_timestamp_folder:
            timestamp = datetime.now().strftime("%Y.%m.%d_%H.%M.%S")
            self.output_folder_path = os.path.join(output_folder_path, timestamp)
        
        self.label_images = label_images
        
        # Configuration options
        self.train_split = train_split
        self.val_split = val_split
        self.test_split = test_split
        self.min_bbox_area = min_bbox_area
        self.max_bbox_area = max_bbox_area
        self.class_names = class_names if class_names else {0: 'vehicle'}
        self.filter_vehicle_types = filter_vehicle_types
        self.obb = obb
        self.skip_frames_without_labels = skip_frames_without_labels
        
        # Validate splits
        total_split = self.train_split + self.val_split + self.test_split
        if abs(total_split - 1.0) > 0.01:
            raise ValueError(f"Train/val/test splits must sum to 1.0, got {total_split}")
        
        self._setup_logging(log_level)
        
        self.logger.info(f"Generating training data from {input_folder_path}")
        self.logger.info(f"Output will be saved to {self.output_folder_path}")
        
        self.data = {}
        self.stats = {
            'total_frames': 0,
            'frames_processed': 0,
            'frames_with_labels': 0,
            'total_labels': 0,
            'skipped_no_sensor': 0,
            'skipped_no_vehicles': 0,
            'skipped_parse_error': 0,
            'labeled_images_saved': 0,
            'labels_filtered_by_size': 0,
            'labels_filtered_by_type': 0
        }
    
    def _setup_logging(self, log_level: int) -> None:
        """Setup logging configuration.
        
        Args:
            log_level: Logging level (logging.DEBUG, INFO, WARNING, ERROR)
        """
        self.logger = logging.getLogger('TrainingDataGenerator')
        self.logger.setLevel(log_level)
        
        if not self.logger.handlers:
            handler = logging.StreamHandler()
            handler.setLevel(log_level)
            formatter = logging.Formatter('%(levelname)s: %(message)s')
            handler.setFormatter(formatter)
            self.logger.addHandler(handler)
    
    def _print_summary(self) -> None:
        """Print summary statistics of the dataset generation process.
        
        Displays total frames, processed frames, labels generated, and any filtering/skipping
        that occurred during processing.
        """
        self.logger.info("\n" + "="*60)
        self.logger.info("PROCESSING SUMMARY")
        self.logger.info("="*60)
        self.logger.info(f"Total frames found: {self.stats['total_frames']}")
        self.logger.info(f"Frames processed: {self.stats['frames_processed']}")
        self.logger.info(f"Frames with labels: {self.stats['frames_with_labels']}")
        self.logger.info(f"Total labels generated: {self.stats['total_labels']}")
        
        if self.stats['skipped_parse_error'] > 0:
            self.logger.warning(f"Skipped (parse errors): {self.stats['skipped_parse_error']}")
        if self.stats['skipped_no_sensor'] > 0:
            self.logger.warning(f"Skipped (no sensor pose): {self.stats['skipped_no_sensor']}")
        if self.stats['skipped_no_vehicles'] > 0:
            self.logger.info(f"Skipped (no vehicles): {self.stats['skipped_no_vehicles']}")
        if self.stats['labels_filtered_by_size'] > 0:
            self.logger.info(f"Labels filtered by size: {self.stats['labels_filtered_by_size']}")
        if self.stats['labels_filtered_by_type'] > 0:
            self.logger.info(f"Labels filtered by type: {self.stats['labels_filtered_by_type']}")
        
        if self.label_images:
            self.logger.info(f"Labeled images saved: {self.stats['labeled_images_saved']}")
        
        self.logger.info("="*60)
        
    def generate(self) -> None:
        """Execute the complete dataset generation pipeline.
        
        This is the main entry point that orchestrates the four-stage process:
        1. Process input data (parse XML and images)
        2. Generate labels (project 3D to 2D, apply filters)
        3. Save dataset (split into train/val/test, write YOLO format)
        4. Draw labeled images (optional visualization)
        """
        self.logger.info("\nStarting dataset generation...\n")
        
        self.logger.info("[1/4] Processing input data...")
        self.process_input_data()

        self.logger.info("\n[2/4] Generating labels from telemetry...")
        self.generate_labels()
        
        self.logger.info("\n[3/4] Saving dataset in YOLO format...")
        self.save_dataset()

        if self.label_images:
            self.logger.info("\n[4/4] Generating labeled preview images...")
            self.draw_and_save_labeled_images()
        else:
            self.logger.info("\n[4/4] Skipping labeled images (disabled)")
        
        self._print_summary()



    def process_input_data(self) -> None:
        """Parse all image and XML telemetry files from the input folder.
        
        For each paired .png and .xml file, this method:
        - Parses the XML to extract sensor pose and vehicle poses
        - Stores the data in self.data dictionary keyed by frame name
        - Updates statistics for tracking progress and errors
        
        The XML structure is expected to contain:
        - <Sensor_Pose> with position, orientation, and intrinsics
        - Multiple <Vehicle_Pose> elements with position, orientation, and dimensions
        """
        try:
            image_files = [f for f in os.listdir(self.input_folder_path) if f.endswith(".png")]
            telemetry_files = [f for f in os.listdir(self.input_folder_path) if f.endswith(".xml")]
        except PermissionError as e:
            self.logger.error(f"Permission denied accessing input folder: {self.input_folder_path}")
            self.logger.error(f"Details: {e}")
            return
        except Exception as e:
            self.logger.error(f"Failed to list files in input folder: {self.input_folder_path}")
            self.logger.error(f"Details: {e}")
            return

        if not image_files:
            self.logger.warning(f"No .png image files found in {self.input_folder_path}")
            return
        
        if not telemetry_files:
            self.logger.warning(f"No .xml telemetry files found in {self.input_folder_path}")
            return
        
        self.stats['total_frames'] = len(image_files)
        self.logger.info(f"Found {len(image_files)} images and {len(telemetry_files)} telemetry files")

        for image_file in image_files:
            frame_name = image_file.strip(".png")
            telem_file = frame_name + ".xml"

            image_path = os.path.join(self.input_folder_path, image_file)

            if telem_file not in telemetry_files:
                self.logger.warning(f"Missing telemetry file for {frame_name}")
                continue
            
            telem_path = os.path.join(self.input_folder_path, telem_file)
            
            try:
                tree = ET.parse(telem_path)
                root = tree.getroot()
            except ET.ParseError as e:
                self.logger.error(f"Failed to parse XML for {frame_name}: {e}")
                self.stats['skipped_parse_error'] += 1
                continue
            except Exception as e:
                self.logger.error(f"Failed to read telemetry file {telem_file}: {e}")
                self.stats['skipped_parse_error'] += 1
                continue
            
            self.logger.debug(f"Parsed xml for {frame_name}")

            sensor_pose = None
            try:
                for event in root.findall("event"):
                    uid = event.get('uid', '')
                    if 'SENSOR' in uid:
                        sensor_pose = Sensor_Pose.from_xml(event)
                        if sensor_pose:
                            break
            except Exception as e:
                self.logger.warning(f"Error parsing sensor pose for {frame_name}: {e}")
            
            vehicles = []
            try:
                for event in root.findall("event"):
                    uid = event.get('uid', '')
                    if 'TRUTH' in uid:
                        vehicle = Vehicle_Pose.from_xml(event)
                        if vehicle:
                            vehicles.append(vehicle)
            except Exception as e:
                self.logger.warning(f"Error parsing vehicles for {frame_name}: {e}")
            
            self.logger.debug(f"Parsed {len(vehicles)} vehicles for {frame_name}")
            
            if sensor_pose is None:
                self.logger.warning(f"No sensor pose found for {frame_name}")
            
            self.data[frame_name] = {
                "Image_Path" : image_path,
                "Vehicles" : vehicles,
                "Sensor_Pose" : sensor_pose
            }
            self.stats['frames_processed'] += 1

    def generate_labels(self) -> None:
        """Generate YOLO format labels from parsed telemetry data.
        
        This method performs the core coordinate transformation and projection:
        1. Convert vehicle lat/lon/hae to local ENU frame relative to sensor
        2. Apply camera rotation to transform to camera coordinate system
        3. Project 3D bounding box corners to 2D image coordinates
        4. Generate oriented (OBB) or axis-aligned (AABB) bounding boxes
        5. Apply filtering based on bbox area and vehicle type
        
        The projection uses pinhole camera model with focal length calculated from FOV.
        Bounding boxes are normalized to [0, 1] range for YOLO format.
        
        Coordinate System Transformations:
        - WGS84 (lat/lon/hae) -> Local ENU (East-North-Up)
        - ENU -> Camera frame (accounting for yaw/pitch/roll)
        - Camera frame -> Image plane (pinhole projection)
        - Pixel coordinates -> Normalized coordinates (0-1)
        """
        
        for frame_name, frame_data in self.data.items():
            self.logger.debug(f"Generating labels for {frame_name}")
            
            sensor_pose = frame_data['Sensor_Pose']
            vehicles = frame_data['Vehicles']
            
            if sensor_pose is None:
                self.logger.warning(f"No sensor pose data for {frame_name}, skipping")
                self.stats['skipped_no_sensor'] += 1
                frame_data['Labels'] = []
                continue
            
            if not vehicles:
                self.logger.debug(f"No vehicles in frame {frame_name}")
                self.stats['skipped_no_vehicles'] += 1
                frame_data['Labels'] = []
                continue
            
            # Convert sensor and vehicle positions from lat/lon/hae to local ENU coordinates
            # Use sensor position as origin for local coordinate system
            sensor_lat_rad = math.radians(sensor_pose.lat)
            sensor_lon_rad = math.radians(sensor_pose.lon)
            sensor_hae = sensor_pose.hae
            
            # Earth radius (WGS84 semi-major axis)
            R_EARTH = 6378137.0  # meters
            
            # Camera parameters
            if sensor_pose.intrinsics:
                img_width = sensor_pose.intrinsics.get('width', 2048)
                img_height = sensor_pose.intrinsics.get('height', 2048)
                fov_deg = sensor_pose.intrinsics.get('hfov_deg', 90.0)
            else:
                self.logger.warning(f"No intrinsics for {frame_name}, using defaults")
                img_width = 2048
                img_height = 2048
                fov_deg = 90.0
            
            if img_width <= 0 or img_height <= 0:
                self.logger.error(f"Invalid image dimensions ({img_width}x{img_height}) for {frame_name}, skipping")
                frame_data['Labels'] = []
                continue
            
            if fov_deg <= 0 or fov_deg >= 180:
                self.logger.warning(f"Invalid FOV ({fov_deg}°) for {frame_name}, using default 90°")
                fov_deg = 90.0
            
            fov_rad = math.radians(fov_deg)
            
            # Camera orientation (azimuth=yaw, elevation=pitch, roll)
            cam_yaw_deg = sensor_pose.az_deg if sensor_pose.az_deg is not None else 0.0
            cam_pitch_deg = sensor_pose.el_deg if sensor_pose.el_deg is not None else -90.0
            cam_roll_deg = sensor_pose.roll_deg if sensor_pose.roll_deg is not None else 0.0
            
            labels = []
            
            for vehicle in vehicles:
                # Filter by vehicle type if specified
                if self.filter_vehicle_types:
                    if vehicle.base_type not in self.filter_vehicle_types:
                        self.logger.debug(f"Filtering out vehicle {vehicle.uid} with type {vehicle.base_type}")
                        self.stats['labels_filtered_by_type'] += 1
                        continue
                
                # Convert vehicle lat/lon/hae to ENU coordinates relative to sensor
                vehicle_lat_rad = math.radians(vehicle.lat)
                vehicle_lon_rad = math.radians(vehicle.lon)
                vehicle_hae = vehicle.hae
                
                # Approximate local ENU conversion (flat-earth approximation, valid for small distances)
                # East: positive longitude difference
                # North: positive latitude difference  
                # Up: altitude difference
                dlat = vehicle_lat_rad - sensor_lat_rad
                dlon = vehicle_lon_rad - sensor_lon_rad
                
                # Convert to meters
                east = dlon * R_EARTH * math.cos(sensor_lat_rad)
                north = dlat * R_EARTH
                up = vehicle_hae - sensor_hae
                
                # Vehicle dimensions (skip if not available)
                if not vehicle.length_m or not vehicle.width_m or not vehicle.height_m:
                    self.logger.debug(f"Vehicle {vehicle.uid} missing dimensions, skipping")
                    continue
                
                half_length = vehicle.length_m / 2.0
                half_width = vehicle.width_m / 2.0
                half_height = vehicle.height_m / 2.0
                
                # Vehicle orientation (course = yaw)
                vehicle_yaw_deg = vehicle.course_deg if vehicle.course_deg is not None else 0.0
                vehicle_yaw_rad = math.radians(vehicle_yaw_deg)
                cos_yaw = math.cos(vehicle_yaw_rad)
                sin_yaw = math.sin(vehicle_yaw_rad)
                
                # Generate 8 corners of vehicle bounding box in ENU frame
                # Vehicle local axes: length=forward/back, width=left/right, height=up/down
                corners_enu = []
                for dh in [0, half_height * 2]:  # Bottom and top
                    for dl, dw in [(-half_length, -half_width), (half_length, -half_width),
                                   (half_length, half_width), (-half_length, half_width)]:
                        # Rotate by vehicle yaw in ENU frame (yaw rotates in horizontal plane)
                        corner_east = east + dl * sin_yaw + dw * cos_yaw
                        corner_north = north + dl * cos_yaw - dw * sin_yaw
                        corner_up = up + dh
                        corners_enu.append((corner_east, corner_north, corner_up))
                
                # Project corners to image using camera model
                corners_normalized = []
                
                # Camera rotation angles
                cam_yaw_rad = math.radians(cam_yaw_deg)
                cam_pitch_rad = math.radians(cam_pitch_deg)
                cam_roll_rad = math.radians(cam_roll_deg)
                
                cos_yaw_cam = math.cos(cam_yaw_rad)
                sin_yaw_cam = math.sin(cam_yaw_rad)
                cos_pitch = math.cos(cam_pitch_rad)
                sin_pitch = math.sin(cam_pitch_rad)
                cos_roll = math.cos(cam_roll_rad)
                sin_roll = math.sin(cam_roll_rad)
                
                # Focal length from FOV
                try:
                    tan_half_fov = math.tan(fov_rad / 2.0)
                    if abs(tan_half_fov) < 1e-6:
                        focal_length_pixels = img_width * 1000
                    else:
                        focal_length_pixels = (img_width / 2.0) / tan_half_fov
                except (ValueError, ZeroDivisionError) as e:
                    self.logger.warning(f"Error calculating focal length: {e}, using default")
                    focal_length_pixels = img_width
                
                for corner_east, corner_north, corner_up in corners_enu:
                    # Transform ENU to camera frame
                    # ENU: East=+Y (right), North=+X (forward), Up=+Z
                    # Camera yaw: rotation around Up axis (azimuth from north)
                    # Camera pitch: rotation around East axis (elevation angle)
                    # Camera roll: rotation around forward axis
                    
                    # Rotate by yaw (azimuth): align camera forward with azimuth direction
                    # Azimuth 0° = North, 90° = East
                    cam_x_temp = corner_north * cos_yaw_cam + corner_east * sin_yaw_cam
                    cam_y_temp = -corner_north * sin_yaw_cam + corner_east * cos_yaw_cam
                    cam_z_temp = corner_up
                    
                    # Rotate by pitch (elevation): tilt camera up/down
                    # Elevation -90° = straight down, 0° = horizontal
                    cam_forward_temp = cam_x_temp * cos_pitch + cam_z_temp * sin_pitch
                    cam_right_temp = cam_y_temp
                    cam_down_temp = -cam_x_temp * sin_pitch + cam_z_temp * cos_pitch
                    
                    # Rotate by roll: rotate image plane
                    cam_forward = cam_forward_temp
                    cam_right = cam_right_temp * cos_roll - cam_down_temp * sin_roll
                    cam_down = cam_right_temp * sin_roll + cam_down_temp * cos_roll
                    
                    # Project to image plane
                    if cam_forward <= 1e-3:
                        # Behind camera or at horizon
                        norm_x = -1.0
                        norm_y = -1.0
                    else:
                        pixel_x = (cam_right / cam_forward) * focal_length_pixels + (img_width / 2.0)
                        pixel_y = (cam_down / cam_forward) * focal_length_pixels + (img_height / 2.0)
                        
                        norm_x = pixel_x / img_width
                        norm_y = 1.0 - (pixel_y / img_height)
                        
                        # Clamp extreme values
                        if abs(norm_x) > 10.0 or abs(norm_y) > 10.0:
                            norm_x = -1.0
                            norm_y = -1.0
                    
                    corners_normalized.append((norm_x, norm_y))
                
                # Check if vehicle is in frame
                valid_corners = [(x, y) for x, y in corners_normalized if 0 <= x <= 1 and 0 <= y <= 1]
                xs = [x for x, y in corners_normalized if x >= 0]
                ys = [y for x, y in corners_normalized if y >= 0]
                
                bbox_crosses_frame = False
                if len(xs) >= 2 and len(ys) >= 2:
                    min_x, max_x = min(xs), max(xs)
                    min_y, max_y = min(ys), max(ys)
                    bbox_crosses_frame = (min_x < 1 and max_x > 0 and min_y < 1 and max_y > 0)
                
                if valid_corners or bbox_crosses_frame:
                    if self.obb:
                        # Oriented bounding box
                        valid_2d = [(x, y) for x, y in corners_normalized if x >= 0 and y >= 0]
                        
                        if len(valid_2d) >= 4:
                            try:
                                points_pixels = np.array([
                                    [x * img_width, y * img_height] for x, y in valid_2d
                                ], dtype=np.float32)
                                
                                rect = cv2.minAreaRect(points_pixels)
                                center, (width, height), angle = rect
                                
                                if width <= 0 or height <= 0:
                                    self.logger.debug(f"Invalid bbox dimensions for vehicle {vehicle.uid}, skipping")
                                    continue
                                
                                width *= 1.05
                                height *= 1.05
                                rect = (center, (width, height), angle)
                                
                                box_pixels = cv2.boxPoints(rect)
                                
                                coords = []
                                for px, py in box_pixels:
                                    norm_x = px / img_width
                                    norm_y = py / img_height
                                    coords.append(f"{norm_x:.6f}")
                                    coords.append(f"{norm_y:.6f}")
                                
                                # Calculate bbox area for filtering
                                bbox_area = (width * height) / (img_width * img_height)
                                
                                if bbox_area < self.min_bbox_area or bbox_area > self.max_bbox_area:
                                    self.logger.debug(f"Filtering bbox with area {bbox_area:.4f} (min={self.min_bbox_area}, max={self.max_bbox_area})")
                                    self.stats['labels_filtered_by_size'] += 1
                                    continue
                                
                                label = f"0 {' '.join(coords)}"
                                labels.append(label)
                            except Exception as e:
                                self.logger.debug(f"Error creating OBB for vehicle {vehicle.uid}: {e}")
                                continue
                    else:
                        # Axis-aligned bounding box
                        valid_xs = [x for x, y in corners_normalized if x >= 0]
                        valid_ys = [y for x, y in corners_normalized if y >= 0]
                        
                        if valid_xs and valid_ys:
                            min_x, max_x = min(valid_xs), max(valid_xs)
                            min_y, max_y = min(valid_ys), max(valid_ys)
                            center_x = (min_x + max_x) / 2.0
                            center_y = (min_y + max_y) / 2.0
                            width = max_x - min_x
                            height = max_y - min_y
                            
                            # Calculate bbox area for filtering
                            bbox_area = width * height
                            
                            if bbox_area < self.min_bbox_area or bbox_area > self.max_bbox_area:
                                self.logger.debug(f"Filtering bbox with area {bbox_area:.4f} (min={self.min_bbox_area}, max={self.max_bbox_area})")
                                self.stats['labels_filtered_by_size'] += 1
                                continue
                            
                            label = f"0 {center_x:.6f} {center_y:.6f} {width:.6f} {height:.6f}"
                            labels.append(label)
            
            frame_data['Labels'] = labels
            if len(labels) > 0:
                self.stats['frames_with_labels'] += 1
                self.stats['total_labels'] += len(labels)
            self.logger.debug(f"Generated {len(labels)} labels for {frame_name}") 

    def draw_and_save_labeled_images(self, add_label_text: bool = False) -> None:
        """
        Draw YOLO format labels (bounding boxes) onto images and save them.
        Processes each image one at a time: load -> draw -> save -> close.
        This is memory efficient as only one image is in memory at a time.
        
        Saves to output_folder_path/labeled_images/ directory.
        
        Args:
            add_label_text: If True, add class label text above bounding boxes
        """
        labeled_dir = os.path.join(self.output_folder_path, 'labeled_images')
        os.makedirs(labeled_dir, exist_ok=True)
        
        self.logger.info(f"Saving labeled images to {labeled_dir}/")
        
        frames_with_labels = [(name, data) for name, data in self.data.items() if data.get('Labels')]
        
        for frame_name, frame_data in frames_with_labels:
            image_path = frame_data.get('Image_Path')
            labels = frame_data.get('Labels', [])
            
            if image_path is None:
                self.logger.warning(f"No image path for {frame_name}, skipping")
                continue
            
            if not labels:
                continue
            
            try:
                img_array = cv2.imread(image_path)
                if img_array is None:
                    self.logger.error(f"Could not load image from {image_path}, skipping")
                    continue
            except Exception as e:
                self.logger.error(f"Failed to read image {image_path}: {e}")
                continue
            
            img_height, img_width = img_array.shape[:2]
            
            # Colors for different classes (BGR format)
            colors = [
                (0, 255, 0),    # Green for class 0 (vehicles)
            ]
            
            # Draw each label
            for label in labels:
                try:
                    parts = label.split()
                    if len(parts) < 5:
                        self.logger.warning(f"Invalid label format for {frame_name}: {label}")
                        continue
                    
                    class_id = int(parts[0])
                    color = colors[class_id % len(colors)]
                    
                    if len(parts) == 5:
                        # AABB format: class_id center_x center_y width height
                        center_x = float(parts[1]) * img_width
                        center_y = float(parts[2]) * img_height
                        width = float(parts[3]) * img_width
                        height = float(parts[4]) * img_height
                        
                        # Convert to corner coordinates
                        x1 = int(center_x - width / 2)
                        y1 = int(center_y - height / 2)
                        x2 = int(center_x + width / 2)
                        y2 = int(center_y + height / 2)
                        
                        # Draw rectangle
                        cv2.rectangle(img_array, (x1, y1), (x2, y2), color, 1, cv2.LINE_AA)
                        
                        # Add class label text
                        if add_label_text:
                            label_text = f"Class {class_id}"
                            cv2.putText(img_array, label_text, (x1, y1 - 5), 
                                      cv2.FONT_HERSHEY_SIMPLEX, 0.5, color, 1)
                        
                    elif len(parts) == 9:
                        # OBB format: class_id x1 y1 x2 y2 x3 y3 x4 y4
                        points = []
                        for i in range(1, 9, 2):
                            x = int(float(parts[i]) * img_width)
                            y = int(float(parts[i+1]) * img_height)
                            points.append([x, y])
                        
                        # Draw oriented bounding box
                        points = np.array(points, dtype=np.int32)
                        cv2.polylines(img_array, [points], isClosed=True, color=color, thickness=1, lineType=cv2.LINE_AA)
                        
                        # Add class label text
                        if add_label_text:
                            label_text = f"Class {class_id}"
                            text_x, text_y = int(points[0][0]), int(points[0][1]) - 5
                            cv2.putText(img_array, label_text, (text_x, text_y), 
                                      cv2.FONT_HERSHEY_SIMPLEX, 0.5, color, 1)
                    
                except (ValueError, IndexError) as e:
                    self.logger.warning(f"Error drawing label for {frame_name}: {e}")
                    continue
                except Exception as e:
                    self.logger.warning(f"Unexpected error drawing label: {e}")
                    continue
            
            try:
                labeled_path = os.path.join(labeled_dir, f"{frame_name}.png")
                success = cv2.imwrite(labeled_path, img_array)
                if not success:
                    self.logger.error(f"Failed to write labeled image: {labeled_path}")
                    continue
                self.stats['labeled_images_saved'] += 1
                self.logger.debug(f"Saved {frame_name} with {len(labels)} bounding boxes")
            except Exception as e:
                self.logger.error(f"Failed to save labeled image {frame_name}: {e}")
                continue
        
        self.logger.info(f"Labeled images complete: {self.stats['labeled_images_saved']} frames saved")

        
    def save_dataset(self) -> None:
        """
        Save dataset in YOLO format with train/val/test split (70/20/10).
        Creates timestamped output folder with standard YOLO directory structure.
        
        Directory structure:
            output_folder/
                YYYY.MM.DD_HH.MM.SS/
                    labeled_images/
                    images/
                        train/
                        val/
                        test/
                    labels/
                        train/
                        val/
                        test/
                    data.yaml
        """
        
        if not self.data:
            self.logger.warning("No data to save")
            return
        
        # Use the timestamped output directory from __init__
        output_dir = self.output_folder_path
        
        # Create YOLO directory structure
        splits = ['train', 'val', 'test']
        try:
            for split in splits:
                os.makedirs(os.path.join(output_dir, 'images', split), exist_ok=True)
                os.makedirs(os.path.join(output_dir, 'labels', split), exist_ok=True)
        except PermissionError as e:
            self.logger.error(f"Permission denied creating output directories: {e}")
            return
        except Exception as e:
            self.logger.error(f"Failed to create output directories: {e}")
            return
        
        self.logger.info(f"Saving dataset to {output_dir}")
        
        # Get all frame names
        frame_names = list(self.data.keys())
        
        # Optionally skip frames without labels
        if self.skip_frames_without_labels:
            original_count = len(frame_names)
            frame_names = [name for name in frame_names if self.data[name].get('Labels')]
            skipped = original_count - len(frame_names)
            if skipped > 0:
                self.logger.info(f"Skipped {skipped} frames without labels")
        
        random.shuffle(frame_names)
        
        # Calculate split indices using configured ratios
        total = len(frame_names)
        train_end = int(total * self.train_split)
        val_end = int(total * (self.train_split + self.val_split))
        
        split_indices = {
            'train': (0, train_end),
            'val': (train_end, val_end),
            'test': (val_end, total)
        }
        
        self.logger.info(f"Total frames to save: {total}")
        self.logger.info(f"  Train: {train_end} ({train_end/total*100:.1f}%) [target: {self.train_split*100:.1f}%]")
        self.logger.info(f"  Val: {val_end - train_end} ({(val_end-train_end)/total*100:.1f}%) [target: {self.val_split*100:.1f}%]")
        self.logger.info(f"  Test: {total - val_end} ({(total-val_end)/total*100:.1f}%) [target: {self.test_split*100:.1f}%]")
        
        # Save frames to appropriate splits
        saved_counts = {'train': 0, 'val': 0, 'test': 0}
        total_saved = 0
        
        self.logger.info("\nSaving files...")
        for split, (start_idx, end_idx) in split_indices.items():
            split_size = end_idx - start_idx
            self.logger.info(f"  Saving {split} split ({split_size} frames)...")
            
            for i in range(start_idx, end_idx):
                frame_name = frame_names[i]
                frame_data = self.data[frame_name]
                
                # Copy image from source to destination
                source_image_path = frame_data.get('Image_Path')
                if source_image_path is not None:
                    try:
                        dest_image_path = os.path.join(output_dir, 'images', split, f"{frame_name}.png")
                        shutil.copy2(source_image_path, dest_image_path)
                    except FileNotFoundError:
                        self.logger.warning(f"Source image not found: {source_image_path}")
                        continue
                    except PermissionError as e:
                        self.logger.error(f"Permission denied copying {frame_name}: {e}")
                        continue
                    except Exception as e:
                        self.logger.error(f"Failed to copy image {frame_name}: {e}")
                        continue
                
                # Save labels
                labels = frame_data.get('Labels', [])
                label_path = os.path.join(output_dir, 'labels', split, f"{frame_name}.txt")
                try:
                    with open(label_path, 'w') as f:
                        f.write('\n'.join(labels))
                except PermissionError as e:
                    self.logger.error(f"Permission denied writing labels for {frame_name}: {e}")
                    continue
                except Exception as e:
                    self.logger.error(f"Failed to write labels for {frame_name}: {e}")
                    continue
                
                saved_counts[split] += 1
                total_saved += 1
        
        # Create data.yaml for YOLO training
        class_names_yaml = '\n'.join([f"  {class_id}: {name}" for class_id, name in sorted(self.class_names.items())])
        
        yaml_content = f"""# YOLO dataset configuration

path: {os.path.abspath(output_dir)}
train: images/train
val: images/val
test: images/test

# Classes
names:
{class_names_yaml}

# Dataset info
nc: {len(self.class_names)}  # number of classes

# Generation settings
train_split: {self.train_split}
val_split: {self.val_split}
test_split: {self.test_split}
obb: {self.obb}
min_bbox_area: {self.min_bbox_area}
max_bbox_area: {self.max_bbox_area}
"""
        
        yaml_path = os.path.join(output_dir, 'data.yaml')
        try:
            with open(yaml_path, 'w') as f:
                f.write(yaml_content)
        except PermissionError as e:
            self.logger.error(f"Permission denied writing data.yaml: {e}")
            return
        except Exception as e:
            self.logger.error(f"Failed to write data.yaml: {e}")
            return
        
        self.logger.info(f"\nDataset saved successfully:")
        self.logger.info(f"  Train: {saved_counts['train']} frames")
        self.logger.info(f"  Val: {saved_counts['val']} frames")
        self.logger.info(f"  Test: {saved_counts['test']} frames")
        self.logger.info(f"  Config: {yaml_path}")
        self.logger.info(f"\nTo train YOLO, use: yolo train data={yaml_path}")






if __name__ == "__main__":
    parser = argparse.ArgumentParser(
        description="Generate YOLO training dataset from CARLA simulation data (images + XML telemetry)",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Basic usage with default settings
  python Training_Data_Generator.py /path/to/carla/data -o /path/to/output
  
  # Skip generating labeled preview images
  python Training_Data_Generator.py /path/to/carla/data -o /path/to/output --no-labeled-images
  
  # Use existing output folder without timestamp subfolder
  python Training_Data_Generator.py /path/to/carla/data -o /path/to/output --no-timestamp
        """
    )

    parser.add_argument(
        "input_folder", 
        help="Folder containing paired image (.png) and telemetry (.xml) files from CARLA simulation"
    )

    parser.add_argument(
        "--output", "-o",
        dest="output_folder",
        help="Output folder for YOLO dataset (default: ./output)",
        default="./output"
    )

    parser.add_argument(
        "--no-labeled-images",
        dest="label_images",
        help="Skip generating labeled preview images (saves time and disk space)",
        action="store_false",
        default=True
    )
    
    parser.add_argument(
        "--no-timestamp",
        dest="make_timestamp_folder",
        help="Don't create timestamped subfolder in output directory",
        action="store_false",
        default=True
    )
    
    parser.add_argument(
        "--verbose", "-v",
        help="Enable verbose output (DEBUG level logging)",
        action="store_true",
        default=False
    )
    
    parser.add_argument(
        "--quiet", "-q",
        help="Minimal output (WARNING level logging only)",
        action="store_true",
        default=False
    )
    
    parser.add_argument(
        "--train-split",
        type=float,
        help="Fraction of data for training (default: 0.7)",
        default=0.7
    )
    
    parser.add_argument(
        "--val-split",
        type=float,
        help="Fraction of data for validation (default: 0.2)",
        default=0.2
    )
    
    parser.add_argument(
        "--test-split",
        type=float,
        help="Fraction of data for testing (default: 0.1)",
        default=0.1
    )
    
    parser.add_argument(
        "--min-bbox-area",
        type=float,
        help="Minimum bounding box area (normalized 0-1) to include (default: 0.0)",
        default=0.0
    )
    
    parser.add_argument(
        "--max-bbox-area",
        type=float,
        help="Maximum bounding box area (normalized 0-1) to include (default: 1.0)",
        default=1.0
    )
    
    parser.add_argument(
        "--filter-vehicle-types",
        nargs='+',
        help="Only include specific vehicle types (e.g., 'vehicle.car' 'vehicle.truck')",
        default=None
    )
    
    parser.add_argument(
        "--aabb",
        dest="obb",
        help="Use axis-aligned bounding boxes instead of oriented bounding boxes",
        action="store_false",
        default=True
    )
    
    parser.add_argument(
        "--skip-empty-frames",
        dest="skip_frames_without_labels",
        help="Skip frames that have no valid labels in the dataset",
        action="store_true",
        default=False
    )
    
    args = parser.parse_args()
    
    if not os.path.exists(args.input_folder):
        print(f"Error: Input folder does not exist: {args.input_folder}")
        exit(1)
    
    if not os.path.isdir(args.input_folder):
        print(f"Error: Input path is not a directory: {args.input_folder}")
        exit(1)
    
    log_level = logging.INFO
    if args.verbose:
        log_level = logging.DEBUG
    elif args.quiet:
        log_level = logging.WARNING
    
    generator = Training_Data_Generator(
        args.input_folder,
        args.output_folder,
        make_timestamp_folder=args.make_timestamp_folder,
        label_images=args.label_images,
        log_level=log_level,
        train_split=args.train_split,
        val_split=args.val_split,
        test_split=args.test_split,
        min_bbox_area=args.min_bbox_area,
        max_bbox_area=args.max_bbox_area,
        filter_vehicle_types=args.filter_vehicle_types,
        obb=args.obb,
        skip_frames_without_labels=args.skip_frames_without_labels
    )
    generator.generate()
