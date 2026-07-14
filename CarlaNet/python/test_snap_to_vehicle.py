#!/usr/bin/env python3
"""
Test script for snap_to_vehicle function.

This demonstrates how to use the snap_to_vehicle function to move
the aerial camera directly above vehicles from captured telemetry.
"""

import sys
import os

# Mock telemetry data for testing without CARLA
def create_mock_telemetry():
    """Create mock telemetry with 3 vehicles at different positions."""
    return {
        'frame': 12345,
        'camera': {
            'id': 1,
            'fov': 90.0,
            'image_width': 1280,
            'image_height': 720,
            'location': {'x': 0.0, 'y': 0.0, 'z': 100.0},
            'rotation': {'pitch': -90.0, 'yaw': 0.0, 'roll': 0.0}
        },
        'vehicles': [
            {
                'id': 101,
                'location': {'x': 50.0, 'y': 25.0, 'z': 0.0},
                'rotation': {'pitch': 0.0, 'yaw': 45.0, 'roll': 0.0},
                'bounding_box': {'extent_x': 2.0, 'extent_y': 4.0, 'extent_z': 1.5}
            },
            {
                'id': 102,
                'location': {'x': -30.0, 'y': 60.0, 'z': 0.0},
                'rotation': {'pitch': 0.0, 'yaw': 90.0, 'roll': 0.0},
                'bounding_box': {'extent_x': 2.5, 'extent_y': 5.0, 'extent_z': 2.0}
            },
            {
                'id': 103,
                'location': {'x': 100.0, 'y': -50.0, 'z': 0.0},
                'rotation': {'pitch': 0.0, 'yaw': 180.0, 'roll': 0.0},
                'bounding_box': {'extent_x': 2.0, 'extent_y': 4.5, 'extent_z': 1.8}
            }
        ]
    }


def test_snap_to_vehicle_mock():
    """Test snap_to_vehicle with mock data (no CARLA required)."""
    print("=" * 60)
    print("Testing snap_to_vehicle with mock data")
    print("=" * 60)
    
    # Create mock telemetry
    telemetry = create_mock_telemetry()
    
    print(f"\nMock telemetry contains {len(telemetry['vehicles'])} vehicles:")
    for i, vehicle in enumerate(telemetry['vehicles']):
        loc = vehicle['location']
        print(f"  Vehicle {i}: ID={vehicle['id']}, Location=({loc['x']:.1f}, {loc['y']:.1f}, {loc['z']:.1f})")
    
    # Mock camera class for testing
    class MockCamera:
        def __init__(self):
            self.location = {'x': 0.0, 'y': 0.0, 'z': 100.0}
            
        def get_location(self):
            class Loc:
                def __init__(self, x, y, z):
                    self.x, self.y, self.z = x, y, z
            return Loc(self.location['x'], self.location['y'], self.location['z'])
        
        def move(self, x, y, z_ft, r, p, yaw):
            from aerial_camera import FT_PER_M
            self.location = {'x': x, 'y': y, 'z': z_ft / FT_PER_M}
            print(f"  → Camera moved to ({x:.1f}, {y:.1f}, {z_ft / FT_PER_M:.1f}m)")
    
    # Import the function
    from aerial_camera import Aerial_Camera, FT_PER_M
    
    # Create mock camera instance
    camera = Aerial_Camera.__new__(Aerial_Camera)
    camera.camera = MockCamera()
    camera.move = camera.camera.move
    
    # Test 1: Snap to first vehicle
    print("\n--- Test 1: Snap to vehicle 0 at 1000ft ---")
    success = camera.snap_to_vehicle(telemetry, vehicle_index=0, altitude_ft=1000.0)
    print(f"Result: {'✓ Success' if success else '✗ Failed'}")
    
    # Test 2: Snap to second vehicle, maintain altitude
    print("\n--- Test 2: Snap to vehicle 1, maintain altitude ---")
    success = camera.snap_to_vehicle(telemetry, vehicle_index=1)
    print(f"Result: {'✓ Success' if success else '✗ Failed'}")
    
    # Test 3: Snap to third vehicle with custom angles
    print("\n--- Test 3: Snap to vehicle 2 with 45° pitch ---")
    success = camera.snap_to_vehicle(telemetry, vehicle_index=2, altitude_ft=500.0, pitch=-45.0, yaw=90.0)
    print(f"Result: {'✓ Success' if success else '✗ Failed'}")
    
    # Test 4: Invalid vehicle index
    print("\n--- Test 4: Invalid vehicle index (should fail) ---")
    success = camera.snap_to_vehicle(telemetry, vehicle_index=10)
    print(f"Result: {'✓ Handled correctly' if not success else '✗ Should have failed'}")
    
    # Test 5: Empty telemetry
    print("\n--- Test 5: Empty telemetry (should fail) ---")
    empty_telem = {'vehicles': []}
    success = camera.snap_to_vehicle(empty_telem, vehicle_index=0)
    print(f"Result: {'✓ Handled correctly' if not success else '✗ Should have failed'}")
    
    print("\n" + "=" * 60)
    print("All tests completed!")
    print("=" * 60)


if __name__ == "__main__":
    test_snap_to_vehicle_mock()
