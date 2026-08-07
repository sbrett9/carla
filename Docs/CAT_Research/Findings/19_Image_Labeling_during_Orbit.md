# Recording and Post-Process Labeling Workflow

This document describes the workflow for recording aerial imagery from CARLA simulations and generating YOLO training labels through post-processing.

### 1. Orbit Mode in SCTMV.py

The orbital camera functionality in `SCTMV.py` enables smooth, continuous aerial recording around a point of interest.

**Key Features Added:**
- `CameraController` class with orbit capabilities
- Command-line arguments for orbit configuration (`--orbit`, `--orbit-x`, `--orbit-y`, `--orbit-lat`, `--orbit-lon`, `--orbit-radius`, `--orbit-altitude`, `--orbit-speed`)
- Background thread for smooth 50Hz orbit updates
- Orbit visualization in HUD
- Hotkey controls (P to pause/resume)

**Usage Example:**
```Powershell 7.6.2
# Orbit around CARLA coordinates
python .\CarlaNet\python\SCTMV.py --osm .\Import\map.osm --height-align drape  --terrain-res 2.0 --drape-cache-dir .\Build\drape-cache --orbit --orbit-x 133.7 --orbit-y -327.3 --orbit-radius 1000 --orbit-altitude 200 --orbit-speed 300 --record-dir ./recordings --async

# Orbit around lat/lon coordinates
python .\CarlaNet\python\SCTMV.py --osm .\Import\map.osm --height-align drape  --terrain-res 2.0 --drape-cache-dir .\Build\drape-cache --orbit --orbit-lat 27.251424 --orbit-lon 56.191762 --orbit-radius 1000 --orbit-altitude 200 --orbit-speed 300 --async
```

**Key Parameters:**
- `--orbit`: Starts up camera in orbit mode and enables the orbit features
- `--orbit-x`: Orbit center X (CARLA metres)
- `--orbit-y`: Orbit center Y (CARLA metres)
- `--orbit-lat`: Orbit center latitude
- `--orbit-lon`: Orbit center longitude
- `--orbit-radius`: Orbit radius in feet (default: 656 ft = 200m)
- `--orbit-altitude`: Camera altitude in feet (default: 1700 ft)
- `--orbit-speed`: Time for one complete orbit in seconds (default: 240s = 4 minutes)

### 2. Native Recording via FrameRecorder (C#)

The recording system uses the native C# `FrameRecorder` class which:
- Captures frames at specified Hz rate (decimated from sensor stream)
- Writes lossless PNG images with timestamp filenames (`SCTMV_YYYY.MM.DD_HH.MM.SS.mmm.png`)
- Generates paired XML telemetry files with vehicle positions and sensor pose
- Runs entirely on .NET thread pool (no Python GIL blocking)
- Keeps viewer smooth during recording

**File Format:**
- **Images**: `SCTMV_2026.07.07_22.31.43.801.png`
- **Telemetry**: `SCTMV_2026.07.07_22.31.43.801.xml` (CoT XML format)

**Recording Controls:**
- Press `F` to toggle recording on/off
- HUD shows recording status and frame count
- Files saved to `--record-dir` (default: `Build/SCTMV_recordings`)

## Demo Workflow

### Step 1: Start CARLA Server
```powershell 7.6.2
.\Scripts\Windows\RunCarlaServer.ps1
```

### Step 2: Run SCTMV with Orbit Mode

SCTMV.py is an all-in-one tool that:
1. **Builds** the digital twin world from OSM (formerly `test_digital_twin.py`)
2. **Spawns** traffic (formerly `generate_traffic_carlanet.py`)
3. **Provides** an interactive viewer with recording capabilities
4. **Records** aerial imagery with telemetry

```bash
python .\CarlaNet\python\SCTMV.py --osm .\Import\wichita.osm --height-align drape --terrain-res 2.0 --drape-cache-dir .\Build\drape-cache --orbit --orbit-lat 37.673480 --orbit-lon -97.179188 --async
```

**Key Parameters:**
- `--orbit`: Starts up camera in orbit mode and enables the orbit features
- `--orbit-lat`: Orbit center latitude
- `--orbit-lon`: Orbit center longitude

**Interactive Controls:**
- `F`: Start/stop recording
- `P`: Pause/resume orbit
- `T`: Toggle traffic on/off
- `C`: Toggle photoreal tileset rendering
- `G`: Toggle ground terrain rendering
- `R`: Toggle road mesh rendering
- Camera orbits smoothly while recording frames + telemetry
- HUD shows recording status, orbit progress, and frame count

After running the python script toggle on traffic and wait for traffic to spawn.
Once the traffic has populated begin recording.

### Step 3: Post-Process to Generate YOLO Labels with Visualization

After or during recording, use `Training_Data_Generator.py` to convert the XML telemetry into YOLO format labels and generate preview images:

```bash
python .\CarlaNet\python\Training_Data_Generator.py \path\to\carla\data\ -o \path\to\output
```

**What This Does:**
1. Parses paired PNG/XML files from recording directory
2. Extracts sensor pose (camera position, orientation, intrinsics) from XML
3. Extracts vehicle poses (position, orientation, dimensions) from XML
4. Projects 3D vehicle bounding boxes to 2D image coordinates
5. Generates YOLO format labels (oriented bounding boxes)
6. Splits data into train/val/test sets
7. Creates `data.yaml` configuration file
8. **Automatically generates labeled preview images** showing bounding boxes drawn on frames for verification

**Output Structure:**
```
output_dataset/
├── images/
│   ├── train/
│   ├── val/
│   └── test/
├── labels/
│   ├── train/
│   ├── val/
│   └── test/
├── labeled_images/  (preview images with bboxes drawn)
└── data.yaml
```

**Label Format:**
- **OBB (Oriented Bounding Box)**: `class_id x1 y1 x2 y2 x3 y3 x4 y4`
- All coordinates normalized to [0, 1] range
- Class 0 = vehicle

**Visualization:**
The `labeled_images/` folder contains annotated preview images showing:
- Green bounding boxes around detected vehicles
- Visual verification of projection accuracy
- Easy inspection before training

## Key Technical Details

### Coordinate System Transformations

The post-processing pipeline performs these transformations:

1. **WGS84 (lat/lon/hae)** → **Local ENU (East-North-Up)**
   - Sensor and vehicle positions converted to local frame
   - Sensor position used as origin

2. **ENU** → **Camera Frame**
   - Apply camera yaw, pitch, roll rotations
   - Accounts for camera orientation

3. **Camera Frame** → **Image Plane**
   - Pinhole camera projection model
   - Uses FOV to calculate focal length

4. **Pixel Coordinates** → **Normalized [0,1]**
   - YOLO format requirement

### Projection Math

The `Training_Data_Generator.py` script implements the projection logic:
- Handles full 3D bounding boxes (8 corners)
- Projects all corners to 2D using pinhole camera model
- Computes minimum area oriented bounding rectangle
- Filters out-of-frame and behind-camera vehicles

### Timing Synchronization

- **FrameRecorder** captures frame timestamp from sensor stream
- **XML telemetry** includes vehicle positions at capture time
- **Training_Data_Generator** matches timestamps to ensure alignment
- Small timing differences handled by timestamp proximity matching

## Advantages of This Workflow

1. **Decoupled Recording and Labeling**
   - Record once, experiment with different label parameters
   - No need to re-run simulation for label adjustments

2. **Native Performance**
   - C# FrameRecorder runs off Python GIL
   - Smooth 60fps viewer during recording
   - Fast PNG encoding on .NET thread pool

3. **Accurate Telemetry**
   - Sensor pose captured at exact frame time
   - Vehicle positions synchronized to frame
   - Full 3D bounding box information preserved

4. **Flexible Post-Processing**
   - Adjust bbox filtering (size, type)
   - Toggle OBB vs AABB format
   - Configure train/val/test splits
   - Filter by vehicle type

5. **Verification Tools**
   - Visualize labels before training
   - Inspect projection accuracy
   - Debug coordinate transformations