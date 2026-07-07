import math
import sys
import threading
import time
import logging
from datetime import datetime

import numpy as np
import cv2
        


import carlanet as carla



FT_PER_M = 3.28084

# wrapper class for Carla camera blueprint creation and manipulation
# this extends the functionality to include things like recording imagry, saving training labels for vehicle telemetry, and eventually scripted movement

class Aerial_Camera():
    # attributes
    def __init__(   self, 
                    carla_client,
                    frame_width = 2048, 
                    frame_height = 2048,
                    fov = 90, 
                    ev = 0,
                    x = -200, 
                    y = 0, 
                    z_ft = 1000, 
                    yaw = 0, 
                    pitch = -60, 
                    roll = 0,
                    follow_vehicle = False,
                    record_dir = "/home/cdavies/runs/",
                    enable_record = True,
                    record_hz = 2.0,
                    affiliation = "n",
                    stale = 3.0,
                    ):

        print(f"starting Camera init")

        self.carla_client = carla_client
        
        # Store camera parameters
        self.frame_width = frame_width
        self.frame_height = frame_height
        self.fov = fov

        # other params used for recording and testing
        self.enable_record = enable_record
        
        # other params used for testing
        self.follow_vehicle = follow_vehicle
        
        # Native recording parameters
        self.record_dir = record_dir
        self.record_hz = record_hz
        self.affiliation = affiliation
        self.stale = stale
        self._recording_handle = None
        self._recording_active = False
        self._recording_available = bool(getattr(carla, "_CARLANET_RECORDING_AVAILABLE", False))

        # Create Initial position of the camera, then create camera and set up FOV, image size, and exposure compensation 
        print(f"Creating camera")
        self.initial_tf = carla.Transform(carla.Location(x=x, y=y, z=z_ft / FT_PER_M), carla.Rotation(pitch=pitch, yaw=yaw, roll=roll))

        bp = carla_client.get_world().get_blueprint_library().find("sensor.camera.rgb")
        bp.set_attribute("image_size_x", str(frame_width))
        bp.set_attribute("image_size_y", str(frame_height))
        if bp.has_attribute("fov"):
            bp.set_attribute("fov", str(fov))
        if ev and bp.has_attribute("exposure_compensation"):
            bp.set_attribute("exposure_compensation", str(ev))
        # setting this makes sure the camera ticks at the same rate as the recording for telemetry
        if record_hz and bp.has_attribute("sensor_tick"):
            bp.set_attribute("sensor_tick", str(1.0 / record_hz))

        self.camera = carla_client.get_world().spawn_actor(bp, self.initial_tf)

        
        # link camera listen function to this objects callback
        if self.enable_record:
            self.camera.listen(self.listen)
            self.start_recording()

        print(f"Created RGB camera id={self.camera.id}")



    def start_recording(self):
        """
        Start native recording using CarlaNet's built-in frame recorder.
        Frames are captured, encoded to PNG, and written with CoT-XML telemetry
        entirely on the .NET thread pool without crossing to Python.
        
        Returns:
            True if recording started successfully, False otherwise
        """
        if self._recording_active:
            print("Recording already active")
            return True
            
        if not self._recording_available:
            print("Native recording unavailable: CarlaNet.Recording not built (rebuild the DLLs).", file=sys.stderr)
            return False
        
        try:
            self._recording_handle = self.carla_client.get_world().start_recording(
                self.camera, self.record_dir, self.record_hz,
                self.affiliation, self.stale)
            
            if self._recording_handle is None:
                print("Failed to start recording: start_recording returned None", file=sys.stderr)
                return False
            
            self._recording_active = True
            note = "" if self._recording_handle.HaveTelemetryOrigin else " (PNG only; no georef origin for XML)"
            print(f"Recording started (native) -> {self.record_dir} @ {self.record_hz} Hz{note}")
            return True
            
        except Exception as e:
            print(f"Failed to start recording: {e}", file=sys.stderr)
            return False
    
    def stop_recording(self):
        """
        Stop native recording and report the number of captures saved.
        
        Returns:
            Number of captures saved
        """
        if not self._recording_active:
            print("Recording not active")
            return 0
        
        try:
            saved_count = self.get_recording_count()
            self.carla_client.get_world().stop_recording()
            self._recording_active = False
            self._recording_handle = None
            print(f"Recording stopped: {saved_count} capture(s) saved")
            return saved_count
            
        except Exception as e:
            print(f"Error stopping recording: {e}", file=sys.stderr)
            self._recording_active = False
            self._recording_handle = None
            return 0
    
    def get_recording_count(self):
        """
        Get the number of frames saved by the native recorder.
        
        Returns:
            Number of saved frames, or 0 if not recording
        """
        try:
            return int(self._recording_handle.Saved) if self._recording_handle is not None else 0
        except Exception:
            return 0
    
    def is_recording(self):
        """
        Check if native recording is currently active.
        
        Returns:
            True if recording, False otherwise
        """
        return self._recording_active
    
    def is_recording_available(self):
        """
        Check if native recording functionality is available.
        
        Returns:
            True if CarlaNet.Recording is built and available, False otherwise
        """
        return self._recording_available




    def listen(self, image):
        now = datetime.now()
        timestamp_str = now.strftime("%Y.%m.%d_%H.%M.%S.") + f"{now.microsecond // 1000:03d}"
        print(f"received image from camera id={self.camera.id} at timestamp {timestamp_str}, frame={image.frame}, sim_time={image.timestamp}")

        frame_number = timestamp_str
        img_filename = f"SCTMV_{frame_number}.png"
        label_filename = f"SCTMV_{frame_number}.txt"

        # Capture telemetry immediately when image arrives to minimize delay
        telem = self.capture_telemetry(frame_number, image)
        print(telem)
        # if enabled, snap to vehicle
        if self.follow_vehicle:
            self.snap_to_vehicle(telem)
            
        labels = self.convert_telem_to_labels(telem)
        print(f"{len(labels)} labels")
        print(labels)

        with open(self.record_dir + label_filename, 'a') as f:
            f.write('\n'.join(labels))

        # Convert the image to cv2 RGB and write it
        # write_start = time.time()
        # array = np.frombuffer(image.raw_data, dtype=np.dtype("uint8"))
        # array = np.reshape(array, (image.height, image.width, 4))
        # array = array[:, :, :3]  # Remove alpha channel
        # conversion_duration = time.time() - write_start

        # cv2.imwrite(self.record_dir + img_filename, cv2.cvtColor(array, cv2.COLOR_RGB2BGR))
        # write_duration = time.time() - write_start
        # print(f"Image conversion took {conversion_duration*1000:.2f}ms, and conversion and write took {write_duration*1000:.2f}ms")


    def capture_telemetry(self, frame_number, image=None):
        print(f"Capturing Telemetry for frame {frame_number}")
        
        # Use camera transform from image if available (more accurate timing)
        if image is not None and hasattr(image, 'transform') and image.transform is not None:
            cam_transform = image.transform
            location = cam_transform.location
            rotation = cam_transform.rotation
        else:
            location = self.camera.get_location()
            rotation = self.camera.get_transform().rotation
            
        telemetry = {
            'frame': frame_number,
            'camera':{
                'id': self.camera.id,
                'fov': self.fov,
                'image_width': self.frame_width,
                'image_height': self.frame_height,
                'location': {
                    'x': location.x,
                    'y': location.y,
                    'z': location.z
                },
                'rotation': {
                    'pitch': rotation.pitch,
                    'yaw': rotation.yaw,
                    'roll': rotation.roll
                }
            },
            'vehicles': []
        }
        print(f"capturing vehicle telemetry for {len(self.carla_client.get_world().get_actors().filter('vehicle.*'))} vehicles")
        
        # Calculate actual timing delay using image timestamp
        delay_seconds = 0.0
        if image is not None and hasattr(image, 'timestamp'):
            # image.timestamp is simulation time when image was captured
            # Compare to current wall-clock time to estimate processing delay
            capture_time = getattr(self, '_last_image_time', None)
            current_time = time.time()
            
            if capture_time is not None:
                delay_seconds = current_time - capture_time
                print(f"[TIMING] Processing delay: {delay_seconds*1000:.1f}ms")
            
            self._last_image_time = current_time
        
        for actor in self.carla_client.get_world().get_actors().filter('vehicle.*'):
            actor_location = actor.get_location()
            actor_rotation = actor.get_transform().rotation
            
            # Compensate for timing delay by extrapolating backwards using velocity
            if delay_seconds > 0.001:  # Only compensate if delay is significant (>1ms)
                try:
                    print(f"Compensating for telemetry delay of {delay_seconds*1000:.1f}ms")
                    velocity = actor.get_velocity()

                    print (f"compensation: {velocity.x * delay_seconds}, {velocity.y * delay_seconds}, {velocity.z * delay_seconds}")


                    # Move position backwards in time to match image capture moment
                    actor_location.x -= velocity.x * delay_seconds
                    actor_location.y -= velocity.y * delay_seconds
                    actor_location.z -= velocity.z * delay_seconds
                    print(f"new location: {actor_location}")

                except Exception:
                    pass  # If velocity unavailable, use current position
            
            vehicle_data = {
                'id': actor.id,
                'type': actor.type_id,
                'location':{
                    'x': actor_location.x,
                    'y': actor_location.y,
                    'z': actor_location.z
                },
                'rotation': {
                    'pitch': actor_rotation.pitch,
                    'yaw': actor_rotation.yaw,
                    'roll': actor_rotation.roll
                },
                'bounding_box': {
                    'extent_x': actor.bounding_box.extent.x * 2,  # full width,  left/right from vehicle center
                    'extent_y': actor.bounding_box.extent.y * 2,  # full length, forward/back from vehicle center
                    'extent_z': actor.bounding_box.extent.z * 2   # full height, up/down from vehicle center
                }
            }
            telemetry['vehicles'].append(vehicle_data)

        return telemetry

    def _world_to_camera(self, world_point, camera_transform):
        """Convert a 3D world point to camera coordinate system"""
        # Translate to camera origin
        dx = world_point.x - camera_transform.location.x
        dy = world_point.y - camera_transform.location.y
        dz = world_point.z - camera_transform.location.z
        
        # Rotate to camera frame (inverse of camera rotation)
        # CARLA uses left-handed coordinate system
        pitch = math.radians(camera_transform.rotation.pitch)
        yaw = math.radians(camera_transform.rotation.yaw)
        roll = math.radians(camera_transform.rotation.roll)
        
        # Build rotation matrix (inverse = transpose for rotation matrices)
        # Camera looks along +X axis in camera space
        cos_p, sin_p = math.cos(pitch), math.sin(pitch)
        cos_y, sin_y = math.cos(yaw), math.sin(yaw)
        cos_r, sin_r = math.cos(roll), math.sin(roll)
        
        # Apply inverse rotation: yaw -> pitch -> roll (in reverse order)
        # Simplified for camera coordinate system
        x_cam = dx * cos_y + dy * sin_y
        y_cam = -dx * sin_y * cos_p + dy * cos_y * cos_p + dz * sin_p
        z_cam = dx * sin_y * sin_p - dy * cos_y * sin_p + dz * cos_p
        
        return np.array([x_cam, y_cam, z_cam])
    
    def _camera_to_image(self, point_camera, calibration):
        """Project a 3D camera-space point to 2D image coordinates"""
        # Perspective projection: [x/z, y/z, 1]
        point_2d_homogeneous = np.array([
            point_camera[0] / point_camera[2],
            point_camera[1] / point_camera[2],
            1.0
        ])
        
        # Apply calibration matrix
        point_image = calibration @ point_2d_homogeneous
        
        return point_image[:2]


    @staticmethod
    def validate_coordinate_system():
        """
        Validates CARLA coordinate system conventions used in projection math.
        
        CARLA Coordinate System (Unreal Engine, Left-Handed):
        - World: +X forward (north), +Y right (east), +Z up
        - Rotations: Yaw (Z-axis), Pitch (Y-axis), Roll (X-axis)
        - Yaw: 0° = north, increases counter-clockwise when viewed from above
        - Pitch: 0° = horizontal, -90° = straight down
        - Roll: 0° = level, positive = right side down
        
        Camera Coordinate System (after rotations):
        - Forward: Direction camera is looking (depth into scene)
        - Right: Horizontal right in image (+X in image coordinates)
        - Down: Vertical down in image (+Y in image coordinates)
        
        Returns True if validation passes, raises AssertionError otherwise.
        """
        import math
        
        # Test 1: Yaw rotation direction
        # Vehicle at (0, 10, 0), camera at origin with yaw=0 should see vehicle to the right
        dx, dy = 0, 10
        yaw = 0
        cam_x = dx * math.cos(yaw) + dy * math.sin(yaw)
        cam_y = -dx * math.sin(yaw) + dy * math.cos(yaw)
        assert cam_y > 0, "Yaw rotation failed: vehicle at +Y should appear right (cam_y > 0)"
        
        # Test 2: Pitch rotation for downward camera
        # Point below camera should have positive forward distance when pitch=-90
        dz = -100  # Camera at z=100, point at z=0
        pitch = math.radians(-90)
        cam_forward = 0 * math.cos(pitch) + dz * math.sin(pitch)
        assert cam_forward > 0, "Pitch rotation failed: point below should have positive forward distance"
        
        # Test 3: Roll rotation
        # With roll=45°, point at right should appear at diagonal (right+down)
        cam_right_temp, cam_down_temp = 10, 0
        roll = math.radians(45)
        cam_right = cam_right_temp * math.cos(roll) - cam_down_temp * math.sin(roll)
        cam_down = cam_right_temp * math.sin(roll) + cam_down_temp * math.cos(roll)
        assert cam_right > 0 and cam_down > 0, "Roll rotation failed: should rotate right toward down"
        
        return True


    def convert_telem_to_labels(self, telemetry, obb=True, validate=False):
        """
        Converts vehicle telemetry to YOLO training labels using proper perspective projection.
        
        This function projects 3D vehicle bounding boxes onto the 2D image plane using a pinhole
        camera model with full 3D rotations (yaw, pitch, roll).
        
        Args:
            telemetry: Dict containing camera and vehicle data
            obb: If True, output oriented bounding box (8 coords), else axis-aligned (4 coords)
            validate: If True, run coordinate system validation checks
            
        Returns:
            List of label strings in YOLO format (normalized coordinates 0-1)
            
        Label format:
            OBB: "class_id x1 y1 x2 y2 x3 y3 x4 y4" (4 corners, counter-clockwise)
            AABB: "class_id center_x center_y width height"
        """
        if validate:
            self.validate_coordinate_system()
            
        labels = []
        
        cam_loc = telemetry['camera']['location']
        cam_rot = telemetry['camera']['rotation']
        
        # Camera intrinsics
        fov_rad = math.radians(telemetry['camera']['fov'])
        img_width = telemetry['camera']['image_width']
        img_height = telemetry['camera']['image_height']
        
        # step 1: Calculate camera parameters
        pitch_rad = math.radians(cam_rot['pitch'])
        
        # NOTE: The following ground coverage calculations are kept for debugging/validation
        # but are no longer used in the projection. The new projection uses proper perspective
        # math with pinhole camera model (see step 3 below).
        
        # Distance from camera to ground plane intersection at image center
        # For pitch=-90 (straight down), this is just altitude
        # For other angles, calculate where the center ray hits the ground
        if abs(pitch_rad + math.pi/2) < 0.01:  # Nearly straight down
            ground_distance = cam_loc['z']
        elif abs(pitch_rad) < 0.01:  # Nearly horizontal (pitch ~= 0)
            # Camera is looking at horizon, ground distance is effectively infinite
            ground_distance = 1e12
        else:
            # Ray from camera pointing at pitch angle hits ground at z=0
            # tan(pitch) = -z / horizontal_distance
            # For negative pitch (looking down), horizontal_distance is positive
            ground_distance = cam_loc['z'] / abs(math.sin(pitch_rad))
        

        
        # Calculate ground coverage accounting for perspective skew
        # When camera is tilted, the ground projection is not uniform
        # Top of image sees farther ground, bottom sees closer ground
        
        # Calculate where top and bottom rays of the camera frustum hit the ground
        half_fov_v = math.atan(math.tan(fov_rad / 2.0) * (img_height / img_width))
        
        # Top ray angle (more negative pitch = looking further up in image)
        top_ray_pitch = pitch_rad + half_fov_v
        # Bottom ray angle (less negative pitch = looking down in image)
        bottom_ray_pitch = pitch_rad - half_fov_v
        
        # Calculate ground intersection distances for top and bottom rays
        # Distance along ground from camera's XY position
        if abs(top_ray_pitch) > 0.01:  # Top ray hits ground
            top_ground_dist = cam_loc['z'] / abs(math.tan(top_ray_pitch))
        else:
            top_ground_dist = 1e6  # Ray is nearly horizontal, very far
            
        if abs(bottom_ray_pitch) > 0.01:  # Bottom ray hits ground
            bottom_ground_dist = cam_loc['z'] / abs(math.tan(bottom_ray_pitch))
        else:
            bottom_ground_dist = 1e6
        
        # Ground coverage in the view direction (accounting for skew)
        ground_height = abs(top_ground_dist - bottom_ground_dist)
        
        # Width is simpler - perpendicular to view direction
        # Use the center distance for width calculation
        center_ground_dist = (top_ground_dist + bottom_ground_dist) / 2.0
        ground_width = 2 * center_ground_dist * math.tan(fov_rad / 2.0)
        
        
        for vehicle in telemetry['vehicles']:
            # step 2: Get vehicle bounding box corners in world space (full 3D box)
            v_loc = vehicle['location']
            v_rot = vehicle['rotation']
            bb = vehicle['bounding_box']
            
            # Half extents (full 3D)
            half_x = bb['extent_x'] / 2.0
            half_y = bb['extent_y'] / 2.0
            half_z = bb['extent_z'] / 2.0
            
            # Calculate 8 corners of vehicle 3D bounding box
            # Vehicle rotation (only yaw matters for ground vehicles)
            yaw_rad = math.radians(v_rot['yaw'])
            cos_yaw = math.cos(yaw_rad)
            sin_yaw = math.sin(yaw_rad)
            
            # Corners in vehicle local space, then rotated to world space
            # Generate all 8 corners: 4 bottom + 4 top
            corners_world_3d = []
            for dz in [0, half_z * 2]:  # Bottom (z=0) and top (z=height)
                for dx, dy in [(-half_x, -half_y), (half_x, -half_y), (half_x, half_y), (-half_x, half_y)]:
                    # Rotate by yaw (only horizontal rotation for ground vehicles)
                    world_x = v_loc['x'] + dx * cos_yaw - dy * sin_yaw
                    world_y = v_loc['y'] + dx * sin_yaw + dy * cos_yaw
                    world_z = v_loc['z'] + dz  # Vehicle center is at v_loc['z'], add height offset
                    corners_world_3d.append((world_x, world_y, world_z))
            
            # step 3: Convert world coordinates to normalized image coordinates using proper perspective projection
            corners_normalized = []
            
            # Build camera rotation matrix (yaw, then pitch, then roll)
            cam_yaw_rad = math.radians(cam_rot['yaw'])
            cam_roll_rad = math.radians(cam_rot['roll'])
            cos_yaw = math.cos(cam_yaw_rad)
            sin_yaw = math.sin(cam_yaw_rad)
            cos_pitch = math.cos(pitch_rad)
            sin_pitch = math.sin(pitch_rad)
            cos_roll = math.cos(cam_roll_rad)
            sin_roll = math.sin(cam_roll_rad)
            
            for world_x, world_y, world_z in corners_world_3d:
                # Transform world point to camera-relative coordinates
                # CARLA world: +X forward, +Y right, +Z up
                dx = world_x - cam_loc['x']
                dy = world_y - cam_loc['y']
                dz = world_z - cam_loc['z']  # Full 3D: vehicle corners at various heights
                
                # Rotate by camera yaw (around Z axis) to align with camera's horizontal orientation
                cam_x = dx * cos_yaw + dy * sin_yaw
                cam_y = -dx * sin_yaw + dy * cos_yaw
                cam_z = dz
                
                # Rotate by camera pitch (around Y axis)
                # Camera coordinate system: forward = where camera looks, right = cam_y, down = image down
                # For pitch=-90 (looking down), forward should point toward ground (positive distance)
                cam_forward_temp = cam_x * cos_pitch + cam_z * sin_pitch
                cam_right_temp = cam_y
                cam_down_temp = -cam_x * sin_pitch + cam_z * cos_pitch
                
                # Rotate by camera roll (around forward axis)
                # Roll rotates the image plane - affects right and down directions
                cam_forward = cam_forward_temp
                cam_right = cam_right_temp * cos_roll - cam_down_temp * sin_roll
                cam_down = cam_right_temp * sin_roll + cam_down_temp * cos_roll
                
                # Project to normalized image coordinates using pinhole camera model
                # For a point in camera space, the projection is:
                # image_x = (cam_right / cam_forward) * focal_length_x
                # image_y = (cam_down / cam_forward) * focal_length_y
                
                # Focal length in pixels from FOV (horizontal FOV)
                # Edge case: Prevent division by zero for extreme FOV
                if abs(math.tan(fov_rad / 2.0)) < 1e-6:
                    focal_length_pixels = img_width * 1000  # Very narrow FOV approximation
                else:
                    focal_length_pixels = (img_width / 2.0) / math.tan(fov_rad / 2.0)
                
                # Edge case: Check if point is behind camera or at horizon
                if cam_forward <= 1e-3:  # Small epsilon to handle numerical precision
                    # Point is behind camera or at horizon, mark as out of frame
                    norm_x = -1.0
                    norm_y = -1.0
                else:
                    # Project to image plane
                    pixel_x = (cam_right / cam_forward) * focal_length_pixels + (img_width / 2.0)
                    pixel_y = (cam_down / cam_forward) * focal_length_pixels + (img_height / 2.0)
                    
                    # Normalize to 0-1
                    norm_x = pixel_x / img_width
                    norm_y = 1.0 - (pixel_y / img_height)
                    
                    # Edge case: Clamp extremely large values (far out of frame)
                    # This prevents numerical overflow in downstream processing
                    if abs(norm_x) > 10.0 or abs(norm_y) > 10.0:
                        norm_x = -1.0
                        norm_y = -1.0
                
                corners_normalized.append((norm_x, norm_y))
            
            # Edge case: Check if vehicle is in frame (at least partially)
            # A vehicle is visible if any corner is within the image bounds
            # or if the bounding box crosses the image (some corners out, but box intersects)
            valid_corners = [(x, y) for x, y in corners_normalized if 0 <= x <= 1 and 0 <= y <= 1]
            
            # Also check if bounding box straddles the image (all corners out but box crosses frame)
            xs = [x for x, y in corners_normalized if x >= 0]  # Exclude marked invalid points
            ys = [y for x, y in corners_normalized if y >= 0]
            
            bbox_crosses_frame = False
            if len(xs) >= 2 and len(ys) >= 2:
                min_x, max_x = min(xs), max(xs)
                min_y, max_y = min(ys), max(ys)
                # Check if bbox overlaps [0,1] x [0,1] region
                bbox_crosses_frame = (min_x < 1 and max_x > 0 and min_y < 1 and max_y > 0)
            
            if valid_corners or bbox_crosses_frame:
                if obb:
                    # OBB format: class_id x1 y1 x2 y2 x3 y3 x4 y4
                    # For 3D->2D projection, compute oriented bounding box from all 8 projected corners
                    
                    # Get all valid projected corners (filter out behind-camera points)
                    valid_2d = [(x, y) for x, y in corners_normalized if x >= 0 and y >= 0]
                    
                    if len(valid_2d) >= 4:
                        # Convert to pixel coordinates for cv2.minAreaRect
                        points_pixels = np.array([
                            [x * img_width, y * img_height] for x, y in valid_2d
                        ], dtype=np.float32)
                        
                        # Compute minimum area oriented bounding rectangle
                        rect = cv2.minAreaRect(points_pixels)
                        
                        # Inflate bounding box by 5% to account for timing uncertainties
                        center, (width, height), angle = rect
                        width *= 1.05
                        height *= 1.05
                        rect = (center, (width, height), angle)
                        
                        box_pixels = cv2.boxPoints(rect)  # Get 4 corners
                        
                        # Convert back to normalized coordinates
                        coords = []
                        for px, py in box_pixels:
                            norm_x = px / img_width
                            norm_y = py / img_height
                            coords.append(f"{norm_x:.6f}")
                            coords.append(f"{norm_y:.6f}")
                        
                        label = f"0 {' '.join(coords)}"  # class_id=0 for vehicle
                    else:
                        continue  # Not enough valid corners
                else:
                    # AABB format: class_id center_x center_y width height
                    # Use all valid projected corners to get tightest axis-aligned box
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
                    else:
                        continue  # Skip if no valid corners
                
                labels.append(label)
        
        return labels



    def draw_labels_on_image(self, labels, img, add_label_text=False):
        """
        Draws YOLO format labels (bounding boxes) onto an image.
        
        Args:
            labels: List of label strings in YOLO format
            img: cv2 image (numpy array) to draw on
            
        Returns:
            Image with bounding boxes drawn
        """
        
        img_height, img_width = img.shape[:2]
        
        # Colors for different classes (BGR format)
        colors = [
            (0, 255, 0),    # Green for class 0 (vehicles)
        ]
        
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
                cv2.rectangle(img, (x1, y1), (x2, y2), color, 1)
                # Draw center point
                # cv2.circle(img, (int(center_x), int(center_y)), 3, color, -1)
                
            elif len(parts) == 9:
                # OBB format: class_id x1 y1 x2 y2 x3 y3 x4 y4
                points = []
                for i in range(1, 9, 2):
                    x = int(float(parts[i]) * img_width)
                    y = int(float(parts[i+1]) * img_height)
                    points.append([x, y])
                
                # Draw oriented bounding box
                points = np.array(points, dtype=np.int32)
                cv2.polylines(img, [points], isClosed=True, color=color, thickness=1)
                
                # Draw center
                center_x = int(np.mean(points[:, 0]))
                center_y = int(np.mean(points[:, 1]))
                # cv2.circle(img, (center_x, center_y), 5, (255, 255, 255), -1)
                
            # Add class label text
            if add_label_text:
                label_text = f"Class {class_id}"
                if len(parts) == 5:
                    text_x, text_y = x1, y1 - 5
                else:
                    text_x, text_y = int(points[0][0]), int(points[0][1]) - 5
                
                cv2.putText(img, label_text, (text_x, text_y), 
                        cv2.FONT_HERSHEY_SIMPLEX, 0.5, color, 1)
        
        return img
    
    
    @staticmethod
    def visualize_labels(image_path, label_path, output_path=None):
        """
        Standalone function to visualize labels on a saved image.
        
        Args:
            image_path: Path to input image (PNG/JPG)
            label_path: Path to label file (YOLO format .txt)
            output_path: Path to save annotated image (optional)
            
        Returns:
            Annotated image
        """
        import cv2
        
        # Read image
        img = cv2.imread(image_path)
        if img is None:
            raise ValueError(f"Could not read image: {image_path}")
        
        # Read labels
        labels = []
        try:
            with open(label_path, 'r') as f:
                labels = [line.strip() for line in f if line.strip()]
        except FileNotFoundError:
            print(f"Warning: Label file not found: {label_path}")
            return img
        
        # Create temporary instance to use draw method
        temp_camera = Aerial_Camera.__new__(Aerial_Camera)
        img_annotated = temp_camera.draw_labels_on_image(labels, img.copy())
        
        # Save if output path provided
        if output_path:
            cv2.imwrite(output_path, img_annotated)
            print(f"Saved annotated image to: {output_path}")
        
        return img_annotated


    def snap_to_vehicle(self, telemetry, vehicle_index=0, altitude_ft=None, pitch=-90.0, yaw=0.0, roll=0.0):
        """
        Move camera to position directly above a vehicle from captured telemetry.
        
        Args:
            telemetry: Telemetry dict containing vehicle data (from capture_telemetry)
            vehicle_index: Index of vehicle in telemetry['vehicles'] list (default: 0)
            altitude_ft: Camera altitude in feet (default: maintain current altitude)
            pitch: Camera pitch angle in degrees (default: -90 for straight down)
            yaw: Camera yaw angle in degrees (default: 0)
            roll: Camera roll angle in degrees (default: 0)
            
        Returns:
            True if successful, False if vehicle not found
            
        Example:
            # Capture telemetry
            telem = camera.capture_telemetry(frame_number)
            
            # Snap to first vehicle at 1000ft altitude
            camera.follow_vehicle(telem, vehicle_index=0, altitude_ft=1000.0)
            
            # Snap to second vehicle, maintaining current altitude
            camera.follow_vehicle(telem, vehicle_index=1)
        """
        # Validate vehicle exists
        if not telemetry.get('vehicles'):
            print("Warning: No vehicles in telemetry")
            return False
        
        if vehicle_index >= len(telemetry['vehicles']):
            print(f"Warning: Vehicle index {vehicle_index} out of range (only {len(telemetry['vehicles'])} vehicles)")
            return False
        
        # Get vehicle location
        vehicle = telemetry['vehicles'][vehicle_index]
        v_loc = vehicle['location']
        
        # Use current altitude if not specified
        if altitude_ft is None:
            current_z = self.camera.get_location().z
            altitude_ft = current_z * FT_PER_M
        
        # Move camera to vehicle position
        print(f"Snapping camera to vehicle {vehicle['id']} at ({v_loc['x']:.1f}, {v_loc['y']:.1f}), altitude {altitude_ft:.0f}ft")
        self.move(v_loc['x'], v_loc['y'], altitude_ft, roll, pitch, yaw)
        
        return True





    # passthroughs for external functionality
    def set_transform(self, tf):
        self.camera.set_transform(tf)

    def stop(self):
        self.camera.stop()

    def destroy(self):
        print(f"Destroying carla camera id={self.camera.id}")
        # Stop recording if active
        if self._recording_active:
            self.stop_recording()
        self.camera.destroy()

    def move(self, x, y, z_ft, r, p, yaw):
        print(f"Camera moved to ({x}, {y}, {z_ft}) with pitch {p}")
        self.pose = carla.Transform(carla.Location(x=x, y=y, z=z_ft / FT_PER_M), carla.Rotation(pitch=p, yaw=yaw, roll=r))
        self.camera.set_transform(self.pose)
        


















def main():
    print(f"kicking off test")
    # make carla stuff

    client = carla.Client(args.host, args.port)
    client.set_timeout(15.0)
    print(f"server version: {client.get_server_version()}")
    world = client.get_world()

    pose = {"x": args.x, "y": args.y, "z": args.z / FT_PER_M, "pitch": -90.0, "yaw": 0.0}
    start = dict(pose)
    speed = args.speed

    cam = Aerial_Camera(client)

    running = True
    count_seconds = 0
    run_seconds = 120

    while running:
        print(f"running ...")
        time.sleep(1.0)
        count_seconds += 1
        if count_seconds >=run_seconds:
            running = False

    cam.destroy()




if __name__ == "__main__":
    
    import argparse

    ap = argparse.ArgumentParser()
    ap.add_argument("--z", type=float, default=1000.0, help="start altitude in FEET (default 1000)")
    ap.add_argument("--x", type=float, default=0.0)
    ap.add_argument("--y", type=float, default=0.0, help="CARLA metres; -Y is North")
    ap.add_argument("--fov", type=float, default=90.0)
    ap.add_argument("--ev", type=float, default=0.0, help="camera exposure_compensation (EV); >0 brightens")
    ap.add_argument("--speed", type=float, default=60.0, help="initial move speed (m/s)")
    ap.add_argument("--width", type=int, default=1280)
    ap.add_argument("--height", type=int, default=720)
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=2000)
    ap.add_argument("--output-folder", type=str, default= "/home/cdavies/runs/")
    args = ap.parse_args()

    sys.exit(main())