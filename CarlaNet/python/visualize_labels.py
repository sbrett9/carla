#!/usr/bin/env python3
"""
Visualize YOLO labels on aerial camera images.

This script scans a folder for frame PNG images with matching label TXT files,
and draws bounding boxes on the frames for visualization.

Usage:
    python visualize_labels.py <input_folder> [--output <output_folder>]
    
Example:
    python visualize_labels.py /path/to/frames --output /path/to/annotated
"""

import argparse
import os
import sys
from pathlib import Path


def parse_arguments():
    """Parse command line arguments before importing aerial_camera."""
    parser = argparse.ArgumentParser(
        description="Visualize YOLO labels on aerial camera frames",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  # Visualize frames in a folder (saves to ./annotated)
  python visualize_labels.py /path/to/frames
  
  # Specify custom output folder
  python visualize_labels.py /path/to/frames --output /path/to/output
  
  # Visualize frames in current directory
  python visualize_labels.py .
        """
    )
    
    parser.add_argument(
        'input_folder',
        help='Folder containing frame_*.png and frame_*.txt files'
    )
    
    parser.add_argument(
        '--output', '-o',
        help='Output folder for annotated images (default: <input_folder>/annotated)',
        default=None
    )
    
    return parser.parse_args()


def find_frame_label_pairs(folder):
    """
    Scan folder for SCTMV_*.png files and match with closest timestamp label files.
    
    Matches images with labels based on timestamp proximity, handling the case where
    FrameRecorder and listen() callbacks may have slightly different timestamps.
    
    Args:
        folder: Path to folder containing frames and labels
        
    Returns:
        List of tuples: [(frame_path, label_path), ...]
    """
    from datetime import datetime
    
    folder = Path(folder)
    pairs = []
    
    # Find all SCTMV PNG files and label files
    frame_files = sorted(folder.glob("SCTMV_*.png"))
    label_files = sorted(folder.glob("SCTMV_*.txt"))
    
    if not frame_files:
        print(f"Warning: No SCTMV_*.png files found in {folder}")
        return pairs
    
    if not label_files:
        print(f"Warning: No SCTMV_*.txt files found in {folder}")
        return pairs
    
    def parse_timestamp(filename):
        """Extract timestamp from SCTMV_YYYY.MM.DD_HH.MM.SS.mmm.ext format"""
        try:
            # Remove extension and SCTMV_ prefix
            stem = filename.stem if hasattr(filename, 'stem') else Path(filename).stem
            if stem.startswith("SCTMV_"):
                stem = stem[6:]  # Remove "SCTMV_" prefix
            
            # Parse timestamp: YYYY.MM.DD_HH.MM.SS.mmm
            return datetime.strptime(stem, "%Y.%m.%d_%H.%M.%S.%f")
        except Exception as e:
            print(f"Warning: Could not parse timestamp from {filename}: {e}")
            return None
    
    # Parse timestamps for all label files
    label_timestamps = []
    for label_path in label_files:
        ts = parse_timestamp(label_path)
        if ts:
            label_timestamps.append((ts, label_path))
    
    # Match each frame with closest label by timestamp
    for frame_path in frame_files:
        frame_ts = parse_timestamp(frame_path)
        if not frame_ts:
            continue
        
        # Find label with closest timestamp
        closest_label = None
        min_diff = None
        
        for label_ts, label_path in label_timestamps:
            diff = abs((frame_ts - label_ts).total_seconds())
            if min_diff is None or diff < min_diff:
                min_diff = diff
                closest_label = label_path
        
        if closest_label:
            if min_diff > 1.0:  # Warn if timestamps differ by more than 1 second
                print(f"Warning: Large timestamp difference ({min_diff:.3f}s) between {frame_path.name} and {closest_label.name}")
            pairs.append((str(frame_path), str(closest_label)))
        else:
            print(f"Warning: No label file found for {frame_path.name}")
    
    return pairs


def visualize_folder(input_folder, output_folder=None):
    """
    Visualize all frame/label pairs in a folder.
    
    Args:
        input_folder: Folder containing frame_*.png and frame_*.txt files
        output_folder: Folder to save annotated images (default: input_folder/annotated)
        
    Returns:
        Number of images processed
    """
    input_folder = Path(input_folder)
    
    if not input_folder.exists():
        print(f"Error: Input folder does not exist: {input_folder}")
        return 0
    
    # Set default output folder
    if output_folder is None:
        output_folder = input_folder / "annotated"
    else:
        output_folder = Path(output_folder)
    
    # Create output folder
    output_folder.mkdir(parents=True, exist_ok=True)
    
    # Find all frame/label pairs
    pairs = find_frame_label_pairs(input_folder)
    
    if not pairs:
        print(f"No frame/label pairs found in {input_folder}")
        return 0
    
    print(f"Found {len(pairs)} frame/label pairs")
    print(f"Output folder: {output_folder}")
    print()
    
    # Process each pair
    processed = 0
    for frame_path, label_path in pairs:
        frame_name = Path(frame_path).name
        output_path = output_folder / frame_name
        
        try:
            # Use Aerial_Camera.visualize_labels static method
            Aerial_Camera.visualize_labels(frame_path, label_path, str(output_path))
            processed += 1
            print(f"✓ Processed: {frame_name}")
        except Exception as e:
            print(f"✗ Error processing {frame_name}: {e}")
    
    print()
    print(f"Successfully processed {processed}/{len(pairs)} images")
    print(f"Annotated images saved to: {output_folder}")
    
    return processed


def main():
    # Parse arguments BEFORE importing aerial_camera (which has its own argparse)
    args = parse_arguments()
    
    # Import aerial_camera after parsing to avoid argparse conflicts
    # Use sys.argv manipulation to prevent aerial_camera's argparse from seeing our args
    original_argv = sys.argv.copy()
    sys.argv = [sys.argv[0]]  # Only keep script name
    
    try:
        from aerial_camera import Aerial_Camera
        # Make Aerial_Camera available to visualize_folder
        globals()['Aerial_Camera'] = Aerial_Camera
    finally:
        sys.argv = original_argv
    
    # Run visualization
    count = visualize_folder(args.input_folder, args.output)
    
    # Exit with appropriate code
    sys.exit(0 if count > 0 else 1)


if __name__ == "__main__":
    main()
