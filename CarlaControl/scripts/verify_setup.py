#!/usr/bin/env python3
"""Verify CarlaControl package setup and structure.

This script runs the setup verification tests using pytest.
"""

import subprocess
import sys
from pathlib import Path


class SetupVerifier:
    """Wrapper to run setup verification tests using pytest."""

    @staticmethod
    def run() -> int:
        """Run setup verification tests.
        
        Returns:
            Exit code (0 for success, non-zero for failure)
        """
        base_dir = Path(__file__).parent.parent
        test_file = base_dir / "test" / "test_setup.py"
        
        print("Verifying CarlaControl package setup...")
        print(f"Base directory: {base_dir}\n")
        
        result = subprocess.run(
            [sys.executable, "-m", "pytest", str(test_file), "-v"],
            cwd=base_dir,
        )
        
        if result.returncode == 0:
            print("\n" + "=" * 60)
            print("✓ Package structure verification PASSED")
        else:
            print("\n" + "=" * 60)
            print("✗ Package structure verification FAILED")
            print("Please fix the issues above.")
        
        return result.returncode


if __name__ == "__main__":
    sys.exit(SetupVerifier.run())
