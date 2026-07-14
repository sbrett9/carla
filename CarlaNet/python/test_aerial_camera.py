#!/usr/bin/env python3
"""Basic test script for aerial_camera.py projection math validation."""

import sys
import os

# Add parent directory to path to import aerial_camera
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

# Import just the method we need - create a minimal wrapper to avoid CARLA dependencies
from aerial_camera import Aerial_Camera


def create_test_telemetry(cam_x, cam_y, cam_z, cam_pitch, cam_yaw, vehicle_x, vehicle_y, 
                          vehicle_yaw=0.0, vehicle_width=4.0, vehicle_length=2.0,
                          img_width=1280, img_height=720, fov=90.0, cam_roll=0.0):
    return {
        'camera': {
            'fov': fov, 'image_width': img_width, 'image_height': img_height,
            'location': {'x': cam_x, 'y': cam_y, 'z': cam_z},
            'rotation': {'pitch': cam_pitch, 'yaw': cam_yaw, 'roll': cam_roll}
        },
        'vehicles': [{
            'location': {'x': vehicle_x, 'y': vehicle_y, 'z': 0.0},
            'rotation': {'pitch': 0.0, 'yaw': vehicle_yaw, 'roll': 0.0},
            'bounding_box': {'extent_x': vehicle_length, 'extent_y': vehicle_width, 'extent_z': 1.5}
        }]
    }


def test_straight_down_center():
    print("\n=== Test 1: Camera Straight Down, Vehicle at Center ===")
    # Create a dummy instance just to call the method (no CARLA client needed for this method)
    camera = Aerial_Camera.__new__(Aerial_Camera)
    telem = create_test_telemetry(0.0, 0.0, 100.0, -90.0, 0.0, 0.0, 0.0)
    labels = camera.convert_telem_to_labels(telem, obb=False)
    print(f"  Labels returned: {labels}")
    if len(labels) == 1:
        parts = labels[0].split()
        center_x, center_y = float(parts[1]), float(parts[2])
        print(f"  Center: ({center_x:.3f}, {center_y:.3f})")
        if abs(center_x - 0.5) < 0.01 and abs(center_y - 0.5) < 0.01:
            print("  ✓ PASS: Vehicle at image center")
            return True
        print(f"  ✗ FAIL: Expected (0.5, 0.5)")
    else:
        print(f"  ✗ FAIL: Expected 1 label, got {len(labels)}")
    return False


def test_straight_down_offset():
    print("\n=== Test 2: Camera Straight Down, Vehicle Offset ===")
    camera = Aerial_Camera.__new__(Aerial_Camera)
    # Vehicle at +Y world (right in CARLA) should appear right in image (+X)
    telem = create_test_telemetry(0.0, 0.0, 100.0, -90.0, 0.0, 0.0, 20.0)
    labels = camera.convert_telem_to_labels(telem, obb=False)
    print(labels)
    if len(labels) == 1:
        parts = labels[0].split()
        center_x, center_y = float(parts[1]), float(parts[2])
        print(f"  Center: ({center_x:.3f}, {center_y:.3f})")
        if center_x > 0.5 and abs(center_y - 0.5) < 0.1:
            print("  ✓ PASS: Vehicle offset right in image")
            return True
        print(f"  ✗ FAIL: Expected x > 0.5, y ≈ 0.5, got ({center_x:.3f}, {center_y:.3f})")
    else:
        print(f"  ✗ FAIL: Expected 1 label, got {len(labels)}")
    return False


def test_tilted_camera():
    print("\n=== Test 3: Camera Tilted 45° ===")
    camera = Aerial_Camera.__new__(Aerial_Camera)
    telem = create_test_telemetry(0.0, 0.0, 100.0, -45.0, 0.0,     100.0, 0.0)
    labels = camera.convert_telem_to_labels(telem, obb=False)
    print(labels)
    if len(labels) == 1:
        print("  ✓ PASS: Tilted camera working")
        return True
    print(f"  ✗ FAIL: Expected 1 label, got {len(labels)}")
    return False


def test_out_of_frame():
    print("\n=== Test 4: Vehicle Out of Frame ===")
    camera = Aerial_Camera.__new__(Aerial_Camera)
    telem = create_test_telemetry(0.0, 0.0, 100.0, -90.0, 0.0, 500.0, 0.0)
    labels = camera.convert_telem_to_labels(telem, obb=False)
    print(labels)
    if len(labels) == 0:
        print("  ✓ PASS: Out of frame detected")
        return True
    print(f"  ✗ FAIL: Expected 0 labels, got {len(labels)}")
    return False


def test_obb_format():
    print("\n=== Test 5: OBB Format ===")
    camera = Aerial_Camera.__new__(Aerial_Camera)
    telem = create_test_telemetry(0.0, 0.0, 100.0, -90.0, 0.0, 0.0, 0.0, vehicle_yaw=45.0)
    labels = camera.convert_telem_to_labels(telem, obb=True)
    print(labels)
    if len(labels) == 1 and len(labels[0].split()) == 9:
        print("  ✓ PASS: OBB format correct")
        return True
    print("  ✗ FAIL: Expected 9 values")
    return False


def test_camera_roll():
    print("\n=== Test 6: Camera Roll 45° ===")
    camera = Aerial_Camera.__new__(Aerial_Camera)
    # Camera rolled 45° with vehicle at +Y (right) - should appear rotated in image
    telem = create_test_telemetry(0.0, 0.0, 100.0, -90.0, 0.0, 0.0, 20.0, cam_roll=45.0)
    labels = camera.convert_telem_to_labels(telem, obb=False)
    print(labels)
    if len(labels) == 1:
        parts = labels[0].split()
        center_x, center_y = float(parts[1]), float(parts[2])
        print(f"  Center: ({center_x:.3f}, {center_y:.3f})")
        # With 45° roll, a point at +Y should appear at roughly (+X, +Y) diagonal
        if center_x > 0.5 and center_y > 0.5:
            print("  ✓ PASS: Camera roll affects projection correctly")
            return True
        print(f"  ✗ FAIL: Expected both x,y > 0.5 for rolled camera")
    else:
        print(f"  ✗ FAIL: Expected 1 label, got {len(labels)}")
    return False


def test_coordinate_validation():
    print("\n=== Test 7: Coordinate System Validation ===")
    try:
        result = Aerial_Camera.validate_coordinate_system()
        if result:
            print("  ✓ PASS: All coordinate system checks passed")
            return True
    except AssertionError as e:
        print(f"  ✗ FAIL: {e}")
    except Exception as e:
        print(f"  ✗ EXCEPTION: {e}")
    return False


def test_horizon_case():
    print("\n=== Test 8: Horizon in View (Extreme Angle) ===")
    camera = Aerial_Camera.__new__(Aerial_Camera)
    # Camera at shallow angle (-10°) looking nearly horizontal
    # Vehicle far away should be handled gracefully (near horizon)
    telem = create_test_telemetry(0.0, 0.0, 100.0, -10.0, 0.0, 500.0, 0.0)
    try:
        labels = camera.convert_telem_to_labels(telem, obb=False)
        print(f"  Labels: {len(labels)}")
        print("  ✓ PASS: Horizon case handled without crash")
        return True
    except Exception as e:
        print(f"  ✗ FAIL: Exception raised: {e}")
        return False


def test_partially_visible():
    print("\n=== Test 9: Partially Visible Vehicle ===")
    camera = Aerial_Camera.__new__(Aerial_Camera)
    # Vehicle near edge of frame - should still be detected
    telem = create_test_telemetry(0.0, 0.0, 100.0, -90.0, 0.0, 50.0, 0.0)
    labels = camera.convert_telem_to_labels(telem, obb=False)
    if len(labels) == 1:
        print("  ✓ PASS: Partially visible vehicle detected")
        return True
    print(f"  ✗ FAIL: Expected 1 label, got {len(labels)}")
    return False


def test_behind_camera():
    print("\n=== Test 10: Vehicle Behind Camera ===")
    camera = Aerial_Camera.__new__(Aerial_Camera)
    # Camera looking forward (pitch=-45), vehicle behind camera
    telem = create_test_telemetry(0.0, 0.0, 100.0, -45.0, 0.0, -50.0, 0.0)
    labels = camera.convert_telem_to_labels(telem, obb=False)
    if len(labels) == 0:
        print("  ✓ PASS: Vehicle behind camera correctly excluded")
        return True
    print(f"  ✗ FAIL: Expected 0 labels, got {len(labels)}")
    return False


def test_vehicle_height():
    print("\n=== Test 11: Vehicle Height (3D Bounding Box) ===")
    camera = Aerial_Camera.__new__(Aerial_Camera)
    # Camera straight down - taller vehicle should have larger bounding box
    # Create telemetry with vehicle_height parameter
    telem_short = create_test_telemetry(0.0, 0.0, 100.0, -90.0, 0.0, 0.0, 0.0, 
                                        vehicle_length=2.0, vehicle_width=4.0)
    # Modify to add height
    telem_short['vehicles'][0]['bounding_box']['extent_z'] = 0.75  # Short vehicle (1.5m tall)
    
    telem_tall = create_test_telemetry(0.0, 0.0, 100.0, -90.0, 0.0, 0.0, 0.0,
                                       vehicle_length=2.0, vehicle_width=4.0)
    telem_tall['vehicles'][0]['bounding_box']['extent_z'] = 2.0  # Tall vehicle (4m tall)
    
    labels_short = camera.convert_telem_to_labels(telem_short, obb=False)
    labels_tall = camera.convert_telem_to_labels(telem_tall, obb=False)
    
    if len(labels_short) == 1 and len(labels_tall) == 1:
        parts_short = labels_short[0].split()
        parts_tall = labels_tall[0].split()
        width_short = float(parts_short[3])
        height_short = float(parts_short[4])
        width_tall = float(parts_tall[3])
        height_tall = float(parts_tall[4])
        
        area_short = width_short * height_short
        area_tall = width_tall * height_tall
        
        print(f"  Short vehicle bbox: {width_short:.4f} x {height_short:.4f} = {area_short:.6f}")
        print(f"  Tall vehicle bbox:  {width_tall:.4f} x {height_tall:.4f} = {area_tall:.6f}")
        
        # For straight-down view, taller vehicle should have same or slightly larger bbox
        # (height doesn't affect much for top-down, but should not be smaller)
        if area_tall >= area_short * 0.95:  # Allow 5% tolerance
            print("  ✓ PASS: Vehicle height properly handled")
            return True
        print(f"  ✗ FAIL: Tall vehicle bbox should be >= short vehicle bbox")
    else:
        print(f"  ✗ FAIL: Expected 1 label each, got {len(labels_short)}, {len(labels_tall)}")
    return False


def test_visualization():
    print("\n=== Test 12: Visualization Function ===")
    import numpy as np
    import tempfile
    import os
    
    camera = Aerial_Camera.__new__(Aerial_Camera)
    
    # Create a test image
    img = np.zeros((720, 1280, 3), dtype=np.uint8)
    img[:] = (50, 50, 50)  # Gray background
    
    # Create test labels (both AABB and OBB)
    labels_aabb = ["0 0.5 0.5 0.2 0.15"]
    labels_obb = ["0 0.3 0.3 0.4 0.3 0.4 0.4 0.3 0.4"]
    
    try:
        # Test AABB drawing
        img_aabb = camera.draw_labels_on_image(labels_aabb, img.copy())
        if img_aabb.shape == img.shape:
            print("  ✓ AABB drawing successful")
        else:
            print("  ✗ FAIL: AABB image shape mismatch")
            return False
        
        # Test OBB drawing
        img_obb = camera.draw_labels_on_image(labels_obb, img.copy())
        if img_obb.shape == img.shape:
            print("  ✓ OBB drawing successful")
        else:
            print("  ✗ FAIL: OBB image shape mismatch")
            return False
        
        # Test that something was actually drawn (image changed)
        if not np.array_equal(img, img_aabb):
            print("  ✓ Image modified (boxes drawn)")
        else:
            print("  ✗ FAIL: Image unchanged")
            return False
        
        # Test visualize_labels static method
        with tempfile.TemporaryDirectory() as tmpdir:
            import cv2
            img_path = os.path.join(tmpdir, "test.png")
            label_path = os.path.join(tmpdir, "test.txt")
            output_path = os.path.join(tmpdir, "test_annotated.png")
            
            # Save test image and labels
            cv2.imwrite(img_path, img)
            with open(label_path, 'w') as f:
                f.write(labels_aabb[0] + '\n')
            
            # Test visualization
            result = Aerial_Camera.visualize_labels(img_path, label_path, output_path)
            
            if os.path.exists(output_path):
                print("  ✓ visualize_labels saved output")
                print(output_path)
            else:
                print("  ✗ FAIL: Output file not created")
                return False
        
        print("  ✓ PASS: All visualization tests passed")
        return True
        
    except Exception as e:
        print(f"  ✗ EXCEPTION: {e}")
        return False


if __name__ == "__main__":
    print("=" * 60)
    print("Aerial Camera Projection Test Suite")
    print("=" * 60)
    tests = [test_straight_down_center, test_straight_down_offset, test_tilted_camera,
             test_out_of_frame, test_obb_format, test_camera_roll, test_coordinate_validation,
             test_horizon_case, test_partially_visible, test_behind_camera, test_vehicle_height,
             test_visualization]
    results = []
    for test in tests:
        try:
            results.append(test())
        except Exception as e:
            print(f"  ✗ EXCEPTION: {e}")
            results.append(False)
    print("\n" + "=" * 60)
    print(f"Results: {sum(results)}/{len(results)} tests passed")
    print("=" * 60)
    sys.exit(0 if all(results) else 1)
