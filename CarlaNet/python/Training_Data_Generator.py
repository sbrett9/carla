# this script takes a folder as input. inside that folder there is captured data from a carla simulation. This data comes in pairs, images and telemetry.

#SCTMV_2026.07.07_22.31.43.801.png
#SCTMV_2026.07.07_22.31.43.801.xml


# this script searches through the folder, and for each pair of image and telemetry, it generates a label file, and a labeled image with the bounding boxes drawn on.


import argparse
import os
from PIL import Image
import json

from datetime import datetime
import random
import shutil
import math
import numpy as np
import cv2

import xml.etree.ElementTree as ET



class Sensor_Pose():
    def __init__(self, uid=None, type=None, callsign=None, lat=None, lon=None, hae=None, 
                 align_offset_m=None, az_deg=None, el_deg=None, roll_deg=None, 
                 course_deg=None, speed_mps=None, intrinsics=None):
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
    def from_xml(xml_node):
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
    def __init__(self, uid=None, type=None, callsign=None, lat=None, lon=None, hae=None,
                 az_deg=None, el_deg=None, roll_deg=None, course_deg=None, speed_mps=None,
                 ce=None, le=None, time=None, how=None, actor_id=None, type_id=None,
                 base_type=None, special_type=None, length_m=None, width_m=None, height_m=None,
                 color=None, role_name=None, vx=None, vy=None, vz=None):
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
    def from_xml(xml_node):
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
    def __init__(self, input_folder_path, output_folder_path, make_timestamp_folder = True, label_images=True, generate_labeled_images=True):
        
        self.input_folder_path = input_folder_path
        

        self.output_folder_path = output_folder_path
        if make_timestamp_folder:
            timestamp = datetime.now().strftime("%Y.%m.%d_%H.%M.%S")
            self.output_folder_path = os.path.join(output_folder_path, timestamp)
        
        self.label_images = label_images

        print(f"Generating training data from {input_folder_path}")
        print(f"Output will be saved to {self.output_folder_path}")
        
        self.data = {} # dict to store info, see process_data for how its populated
        
    def generate(self):
        # process input data
        self.process_input_data()

        # generate labels from telemetry
        self.generate_labels()
        
        # save in yolo format
        self.save_dataset()

        # generate and save labeled images
        if self.label_images:
            self.draw_and_save_labeled_images()



    def process_input_data(self):
        # search for image and telemetry files in the input folder
        image_files = [f for f in os.listdir(self.input_folder_path) if f.endswith(".png")]
        telemetry_files = [f for f in os.listdir(self.input_folder_path) if f.endswith(".xml")]


        for image_file in image_files:
            frame_name = image_file.strip(".png")
            telem_file = frame_name + ".xml"

            # Store the image path instead of loading the image
            image_path = os.path.join(self.input_folder_path, image_file)

            # check if matching telem exists
            if telem_file not in telemetry_files:
                print(f"Missing telemetry file for {frame_name}")
                continue
            
            tree = ET.parse(os.path.join(self.input_folder_path, telem_file))
            root = tree.getroot()
            print(f"\n\nParsed xml for {frame_name}:")

            # Parse sensor platform data (if present in XML)
            sensor_pose = None
            for event in root.findall("event"):
                uid = event.get('uid', '')
                if 'SENSOR' in uid:
                    # This is the sensor platform event
                    sensor_pose = Sensor_Pose.from_xml(event)
                    break
            
            # Parse all vehicle events
            vehicles = []
            for event in root.findall("event"):
                uid = event.get('uid', '')
                if 'TRUTH' in uid:
                    # This is a vehicle event
                    vehicle = Vehicle_Pose.from_xml(event)
                    if vehicle:
                        vehicles.append(vehicle)
            
            print(f"\n  Parsed {len(vehicles)} vehicles")
            
            self.data[frame_name] = {
                "Image_Path" : image_path,
                "Vehicles" : vehicles,
                "Sensor_Pose" : sensor_pose
            }

    def generate_labels(self, obb=True):
        """
        Generate YOLO format labels from parsed telemetry data.
        Converts lat/lon/hae coordinates to local ENU frame for projection.
        
        Args:
            obb: If True, output oriented bounding boxes (8 coords), else axis-aligned (4 coords)
        """
        
        for frame_name, frame_data in self.data.items():
            print(f"Generating labels for {frame_name}")
            
            sensor_pose = frame_data['Sensor_Pose']
            vehicles = frame_data['Vehicles']
            
            if sensor_pose is None:
                print(f"  Warning: No sensor pose data for {frame_name}, skipping")
                continue
            
            if not vehicles:
                print(f"  No vehicles in frame {frame_name}")
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
                print(f"  Warning: No intrinsics for {frame_name}, using defaults")
                img_width = 2048
                img_height = 2048
                fov_deg = 90.0
            
            fov_rad = math.radians(fov_deg)
            
            # Camera orientation (azimuth=yaw, elevation=pitch, roll)
            cam_yaw_deg = sensor_pose.az_deg if sensor_pose.az_deg is not None else 0.0
            cam_pitch_deg = sensor_pose.el_deg if sensor_pose.el_deg is not None else -90.0
            cam_roll_deg = sensor_pose.roll_deg if sensor_pose.roll_deg is not None else 0.0
            
            labels = []
            
            for vehicle in vehicles:
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
                    print(f"  Warning: Vehicle {vehicle.uid} missing dimensions (L={vehicle.length_m}, W={vehicle.width_m}, H={vehicle.height_m}), skipping")
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
                if abs(math.tan(fov_rad / 2.0)) < 1e-6:
                    focal_length_pixels = img_width * 1000
                else:
                    focal_length_pixels = (img_width / 2.0) / math.tan(fov_rad / 2.0)
                
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
                    if obb:
                        # Oriented bounding box
                        valid_2d = [(x, y) for x, y in corners_normalized if x >= 0 and y >= 0]
                        
                        if len(valid_2d) >= 4:
                            points_pixels = np.array([
                                [x * img_width, y * img_height] for x, y in valid_2d
                            ], dtype=np.float32)
                            
                            rect = cv2.minAreaRect(points_pixels)
                            center, (width, height), angle = rect
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
                            
                            label = f"0 {' '.join(coords)}"
                            labels.append(label)
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
                            label = f"0 {center_x:.6f} {center_y:.6f} {width:.6f} {height:.6f}"
                            labels.append(label)
            
            frame_data['Labels'] = labels
            print(f"  Generated {len(labels)} labels") 

    def draw_and_save_labeled_images(self, add_label_text=False):
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
        
        print(f"\nGenerating and saving labeled images to {labeled_dir}/")
        
        saved_count = 0
        
        for frame_name, frame_data in self.data.items():
            image_path = frame_data.get('Image_Path')
            labels = frame_data.get('Labels', [])
            
            if image_path is None:
                print(f"  Warning: No image path for {frame_name}, skipping")
                continue
            
            if not labels:
                print(f"  Skipping {frame_name}: no labels to draw")
                continue
            
            # Load image from disk using cv2
            img_array = cv2.imread(image_path)
            if img_array is None:
                print(f"  Warning: Could not load image from {image_path}, skipping")
                continue
            
            img_height, img_width = img_array.shape[:2]
            
            # Colors for different classes (BGR format)
            colors = [
                (0, 255, 0),    # Green for class 0 (vehicles)
            ]
            
            # Draw each label
            for label in labels:
                parts = label.split()
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
            
            # Save the labeled image immediately
            labeled_path = os.path.join(labeled_dir, f"{frame_name}.png")
            cv2.imwrite(labeled_path, img_array)
            saved_count += 1
            
            print(f"  Saved {frame_name} with {len(labels)} bounding boxes")
        
        print(f"\nLabeled images complete: {saved_count} frames saved")

        
    def save_dataset(self):
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
            print("No data to save")
            return
        
        # Use the timestamped output directory from __init__
        output_dir = self.output_folder_path
        
        # Create YOLO directory structure
        splits = ['train', 'val', 'test']
        for split in splits:
            os.makedirs(os.path.join(output_dir, 'images', split), exist_ok=True)
            os.makedirs(os.path.join(output_dir, 'labels', split), exist_ok=True)
        
        print(f"\nSaving dataset to {output_dir}")
        
        # Get all frame names and shuffle for random split
        frame_names = list(self.data.keys())
        random.shuffle(frame_names)
        
        # Calculate split indices (70/20/10)
        total = len(frame_names)
        train_end = int(total * 0.7)
        val_end = int(total * 0.9)
        
        split_indices = {
            'train': (0, train_end),
            'val': (train_end, val_end),
            'test': (val_end, total)
        }
        
        print(f"Total frames: {total}")
        print(f"  Train: {train_end} ({train_end/total*100:.1f}%)")
        print(f"  Val: {val_end - train_end} ({(val_end-train_end)/total*100:.1f}%)")
        print(f"  Test: {total - val_end} ({(total-val_end)/total*100:.1f}%)")
        
        # Save frames to appropriate splits
        saved_counts = {'train': 0, 'val': 0, 'test': 0}
        total_saved = 0
        
        print("\nSaving files...")
        for split, (start_idx, end_idx) in split_indices.items():
            split_size = end_idx - start_idx
            print(f"  Saving {split} split ({split_size} frames)...")
            
            for i in range(start_idx, end_idx):
                frame_name = frame_names[i]
                frame_data = self.data[frame_name]
                
                # Copy image from source to destination
                source_image_path = frame_data.get('Image_Path')
                if source_image_path is not None:
                    dest_image_path = os.path.join(output_dir, 'images', split, f"{frame_name}.png")
                    shutil.copy2(source_image_path, dest_image_path)
                
                # Save labels
                labels = frame_data.get('Labels', [])
                label_path = os.path.join(output_dir, 'labels', split, f"{frame_name}.txt")
                with open(label_path, 'w') as f:
                    f.write('\n'.join(labels))
                
                saved_counts[split] += 1
                total_saved += 1
                
                # Progress update every 10 files
                if total_saved % 10 == 0:
                    print(f"    Progress: {total_saved}/{total} files saved ({total_saved/total*100:.1f}%)")
        
        # Create data.yaml for YOLO training
        yaml_content = f"""# YOLO dataset configuration

path: {os.path.abspath(output_dir)}
train: images/train
val: images/val
test: images/test

# Classes
names:
  0: vehicle

# Dataset info
nc: 1  # number of classes
"""
        
        yaml_path = os.path.join(output_dir, 'data.yaml')
        with open(yaml_path, 'w') as f:
            f.write(yaml_content)
        
        print(f"\nDataset saved successfully:")
        print(f"  Train: {saved_counts['train']} frames")
        print(f"  Val: {saved_counts['val']} frames")
        print(f"  Test: {saved_counts['test']} frames")
        print(f"  Config: {yaml_path}")
        print(f"\nTo train YOLO, use: yolo train data={yaml_path}")






if __name__ == "__main__":
    #parse args for paths and label images
    # parser = argparse.ArgumentParser()

    # parser.add_argument("input_folder", help="Folder containing image and telemetry files")

    # parser.add_argument("--output", "-o", help="Output folder for annotated images (default: <input_folder>/annotated)", default="None")

    # parser.add_argument("--label_images", "-l", help="Generate labeled images (default: True)", action="store_true")
    
    # args = parser.parse_args()
    


    # #create generator
    # generator = Training_Data_Generator(args.input_folder, args.output, args.label_images)
    # generator.generate()



    
    test_input = "/home/cdavies/Build/SCTMV_recordings"
    test_output = "/home/cdavies/carla_datasets/test/"

    generator = Training_Data_Generator(test_input,
                                        test_output,
                                        generate_labeled_images=False,
                                        make_timestamp_folder=False,
                                        )
    generator.generate()


