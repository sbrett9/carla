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
#    File:    test_setup.py
#    Author:  SNC Team
#    Date:    2026-07-14
#
#    Purpose:  Test CarlaControl package setup and structure
#

"""Test CarlaControl package setup and structure."""

from pathlib import Path


class TestPackageStructure:
    """Test package directory structure and required files."""

    @staticmethod
    def _get_base_dir() -> Path:
        """Get the base directory of the package."""
        return Path(__file__).parent.parent

    def test_required_files_exist(self):
        """Check that all required files exist."""
        base_dir = self._get_base_dir()
        
        required_files = [
            "pyproject.toml",
            "README.md",
            "docs/python-rules.md",
            ".gitignore",
            "MANIFEST.in",
            "src/carlacontrol/__init__.py",
            "src/carlacontrol/version.py",
            "test/test_version.py",
        ]
        
        for file_path in required_files:
            full_path = base_dir / file_path
            assert full_path.exists(), f"Required file missing: {file_path}"

    def test_required_directories_exist(self):
        """Check that all required directories exist."""
        base_dir = self._get_base_dir()
        
        required_dirs = [
            "src/carlacontrol",
            "test",
            "docs",
            "scripts",
        ]
        
        for dir_path in required_dirs:
            full_path = base_dir / dir_path
            assert full_path.is_dir(), f"Required directory missing: {dir_path}/"

    def test_package_can_be_imported(self):
        """Check that the package can be imported."""
        import carlacontrol
        
        assert hasattr(carlacontrol, "__version__")
        assert isinstance(carlacontrol.__version__, str)
        assert len(carlacontrol.__version__) > 0
