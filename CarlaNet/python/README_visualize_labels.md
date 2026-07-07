# Visualize Labels Script

## Overview

`visualize_labels.py` is a utility script that visualizes YOLO format labels on aerial camera images. It scans a folder for frame PNG images with matching label TXT files and draws bounding boxes on the frames for easy visual verification.

## Features

- Automatically finds matching frame/label pairs (`frame_*.png` + `frame_*.txt`)
- Supports both AABB and OBB label formats
- Color-coded bounding boxes with corner markers
- Batch processing of entire folders
- Customizable output directory

## Usage

### Basic Usage

```bash
# Visualize frames in a folder (saves to ./annotated)
python visualize_labels.py /path/to/frames
```

### Custom Output Folder

```bash
# Specify custom output folder
python visualize_labels.py /path/to/frames --output /path/to/output
```

### Current Directory

```bash
# Visualize frames in current directory
python visualize_labels.py .
```

## Requirements

- Python 3.x
- OpenCV (cv2)
- NumPy
- aerial_camera.py module

## Input Format

The script expects:
- **Images**: `frame_0.png`, `frame_1.png`, etc.
- **Labels**: `frame_0.txt`, `frame_1.txt`, etc.

Label files should be in YOLO format:
- **AABB**: `class_id center_x center_y width height`
- **OBB**: `class_id x1 y1 x2 y2 x3 y3 x4 y4`

All coordinates are normalized (0-1 range).

## Output

- Annotated images saved to `<input_folder>/annotated/` by default
- Original images are not modified
- Bounding boxes drawn in green (class 0) with Rectangle/polygon outline

## Example

```bash
$ python visualize_labels.py /data/aerial_frames --output /data/visualized

Found 150 frame/label pairs
Output folder: /data/visualized

✓ Processed: frame_0.png
✓ Processed: frame_1.png
...
✓ Processed: frame_149.png

Successfully processed 150/150 images
Annotated images saved to: /data/visualized
```

## Implementation Details

The script uses the `Aerial_Camera.visualize_labels()` static method from `aerial_camera.py`, minimizing code duplication and ensuring consistent visualization across the codebase.
