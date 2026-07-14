############################# INTELLECTUAL PROPERTY RIGHTS #############################
##                                                                                    ##
##                   Copyright (c) 2026 Sierra Nevada Corporation                     ##
##                                All Rights Reserved.                                ##
##                                                                                    ##
##          Sierra Nevada Corporation (SNC) ("COMPANY") CONFIDENTIAL                  ##
##                                                                                    ##
##   Unpublished Copyright (c) 2026 Sierra Nevada Corporation, All Rights Reserved.   ##
##                                                                                    ##
##   NOTICE: All information contained herein is, and remains the property of         ##
##   Sierra Nevada Corporation.                                                       ##
##                                                                                    ##
##   The intellectual and technical concepts contained herein are proprietary to      ##
##   Sierra Nevada Corporation and may be covered by U.S. and Foreign Patents,        ##
##   patents in process, and are protected by trade secret or copyright law.          ##
##                                                                                    ##
##   Dissemination of this information or reproduction of this material is strictly   ##
##   forbidden unless prior written permission is obtained from Sierra Nevada         ##
##   Corporation.                                                                     ##
##                                                                                    ##
##   Access to the source code contained herein is hereby forbidden to anyone         ##
##   except current Sierra Nevada Corporation employees, managers or contractors      ##
##   who have executed Confidentiality and Non-disclosure agreements explicitly       ##
##   covering such access.                                                            ##
##                                                                                    ##
##   The copyright notice above does not evidence any actual or intended              ##
##   publication or disclosure of this source code, which includes information        ##
##   that is confidential and/or proprietary, and is a trade secret, of Sierra        ##
##   Nevada Corporation.                                                              ##
##                                                                                    ##
##   ANY REPRODUCTION, MODIFICATION, DISTRIBUTION, PUBLIC PERFORMANCE, OR PUBLIC      ##
##   DISPLAY OF OR THROUGH USE OF THIS SOURCE CODE WITHOUT THE EXPRESS WRITTEN        ##
##   CONSENT OF Sierra Nevada Corporation IS STRICTLY PROHIBITED, AND IN VIOLATION    ##
##   OF APPLICABLE LAWS AND INTERNATIONAL TREATIES. THE RECEIPT OR POSSESSION OF      ##
##   THIS SOURCE CODE AND/OR RELATED INFORMATION DOES NOT CONVEY OR IMPLY ANY         ##
##   RIGHTS TO REPRODUCE, DISCLOSE OR DISTRIBUTE ITS CONTENTS, OR TO MANUFACTURE,     ##
##   USE, OR SELL ANYTHING THAT IT MAY DESCRIBE, IN WHOLE OR IN PART.                 ##
##                                                                                    ##
############################# INTELLECTUAL PROPERTY RIGHTS #############################
#
#    File:    verify_setup.py
#    Author:  SNC Team
#    Date:    2026-07-14
#
#    Purpose:  Verify CarlaControl package setup
#

"""Verify CarlaControl package setup and structure."""

import sys
from pathlib import Path


def verify_structure():
    """Verify package directory structure."""
    base_dir = Path(__file__).parent.parent
    
    required_files = [
        "pyproject.toml",
        "README.md",
        "python-rules.md",
        ".gitignore",
        "MANIFEST.in",
        "src/carlacontrol/__init__.py",
        "src/carlacontrol/version.py",
        "test/test_version.py",
    ]
    
    required_dirs = [
        "src/carlacontrol",
        "test",
        "docs",
        "scripts",
    ]
    
    print("Verifying CarlaControl package structure...")
    print(f"Base directory: {base_dir}\n")
    
    all_ok = True
    
    print("Checking required files:")
    for file_path in required_files:
        full_path = base_dir / file_path
        exists = full_path.exists()
        status = "✓" if exists else "✗"
        print(f"  {status} {file_path}")
        if not exists:
            all_ok = False
    
    print("\nChecking required directories:")
    for dir_path in required_dirs:
        full_path = base_dir / dir_path
        exists = full_path.is_dir()
        status = "✓" if exists else "✗"
        print(f"  {status} {dir_path}/")
        if not exists:
            all_ok = False
    
    print("\nVerifying package can be imported:")
    try:
        sys.path.insert(0, str(base_dir / "src"))
        import carlacontrol
        print(f"  ✓ carlacontrol imported successfully")
        print(f"  ✓ Version: {carlacontrol.__version__}")
    except ImportError as e:
        print(f"  ✗ Failed to import carlacontrol: {e}")
        all_ok = False
    
    print("\n" + "=" * 60)
    if all_ok:
        print("✓ Package structure verification PASSED")
        print("\nNext steps:")
        print("  1. Install in development mode: pip install -e .")
        print("  2. Run tests: pytest")
        print("  3. Format code: black src/ test/")
        print("  4. Check linting: ruff check src/ test/")
    else:
        print("✗ Package structure verification FAILED")
        print("Please fix the missing files/directories above.")
        return 1
    
    return 0


if __name__ == "__main__":
    sys.exit(verify_structure())
