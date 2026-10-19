#!/bin/bash

set -e

python_path_default='python3'
python_path=$python_path_default

options=$(\
    getopt \
    -o "pypath:" \
    --long "python-path:" \
    -n 'CarlaSetup.sh' -- "$@")

eval set -- "$options"
while true; do
    case "$1" in
        -pypath|--python-path)
            python_path=$2
            shift 2
            ;;
        --)
            shift
            break
            ;;
        *)
            ;;
    esac
done

# -- DETECT UBUNTU VERSION --
if [ -f /etc/os-release ]; then
    . /etc/os-release
    UBUNTU_VERSION_ID="${VERSION_ID}"
else
    UBUNTU_VERSION_ID="22.04"
fi

# Select libtiff package name based on Ubuntu version
if dpkg --compare-versions "$UBUNTU_VERSION_ID" ge "24.04"; then
    LIBTIFF_PKG="libtiff-dev"
else
    LIBTIFF_PKG="libtiff5-dev"
fi

# -- INSTALL APT PACKAGES --
# libxerces-c-dev, libproj-dev and proj-data build the SUMO toolchain, and libfox-1.6-dev is the FOX
# toolkit sumo-gui is built on (it brings the X11, GL and GLU headers SUMO's configure also asks for).
# The CI image never runs this script: Util/Docker/Base.alma8.Dockerfile declares the same set.
echo "Installing Ubuntu Packages..."
sudo apt-get update
sudo apt-get -y install \
    build-essential \
    make \
    ninja-build \
    libvulkan1 \
    libpng-dev \
    "$LIBTIFF_PKG" \
    libjpeg-dev \
    tzdata \
    sed \
    curl \
    libtool \
    rsync \
    libxml2-dev \
    git \
    git-lfs \
    libnss3-dev \
    libatk-bridge2.0-dev \
    libxkbcommon-dev \
    libgbm-dev \
    libpango1.0-dev \
    libasound2-dev \
    libxerces-c-dev \
    libproj-dev \
    proj-data \
    libfox-1.6-dev \
    nasm \
    patchelf \
    xdg-user-dirs

if [ "$python_path" == "python3" ]; then
    sudo apt-get -y install \
        python3 \
        python3-dev \
        python3-pip
    # The Python the build runs has to satisfy every Python requirement of the build: carlacontrol
    # needs 3.11 or newer, and requirements.txt pins numpy below 2.0 (the legacy Boost.Python module
    # needs it), whose last release publishes wheels up to Python 3.12. Ubuntu 24.04's python3 is
    # 3.12; Ubuntu 22.04's is 3.10, so where the system python3 is older than 3.11, Ubuntu's
    # python3.11 is installed beside it and used instead.
    if ! python3 -c 'import sys; sys.exit(0 if sys.version_info >= (3, 11) else 1)'; then
        if apt-cache show python3.11 >/dev/null 2>&1; then
            echo "python3 is $(python3 --version 2>&1 | cut -d' ' -f2); installing python3.11 for the build..."
            sudo apt-get -y install python3.11 python3.11-dev python3.11-venv
            if python3.11 -m pip --version >/dev/null 2>&1; then
                python_path=python3.11
            else
                echo "WARNING: python3.11 is installed but has no pip, so the packages below go to python3." >&2
                echo "         Give python3.11 a pip and name it (--python-path here, --python-root to CarlaSetup.sh)." >&2
            fi
        else
            echo "WARNING: python3 is $(python3 --version 2>&1 | cut -d' ' -f2) and this system offers no python3.11 package." >&2
            echo "         carlacontrol needs Python 3.11 or 3.12: install one and name it (--python-path here," >&2
            echo "         --python-root to CarlaSetup.sh)." >&2
        fi
    fi
fi

# -- CONFIGURE GIT LFS --
git lfs install

# -- INSTALL PYTHON PACKAGES --
echo "Installing Python Packages..."
PIP_EXTRA_ARGS=""
if dpkg --compare-versions "$UBUNTU_VERSION_ID" ge "24.04"; then
    PIP_EXTRA_ARGS="--break-system-packages"
fi
$python_path -m pip install --upgrade pip $PIP_EXTRA_ARGS
$python_path -m pip install -r requirements.txt $PIP_EXTRA_ARGS

# -- INSTALL CMAKE --
check_cmake_version() {
    CMAKE_VERSION="$($2 --version | grep -Eo '[0-9]+\.[0-9]+\.[0-9]+')"
    CMAKE_MINIMUM_VERSION=$1
    MAJOR="${CMAKE_VERSION%%.*}"
    REMAINDER="${CMAKE_VERSION#*.}"
    MINOR="${REMAINDER%.*}"
    REVISION="${REMAINDER#*.}"
    MINIMUM_MAJOR="${CMAKE_MINIMUM_VERSION%%.*}"
    MINIMUM_REMAINDER="${CMAKE_MINIMUM_VERSION#*.}"
    MINIMUM_MINOR="${MINIMUM_REMAINDER%.*}"

    if [ -z "$CMAKE_VERSION" ]; then
        false
    else
        if [ $MAJOR -gt $MINIMUM_MAJOR ] || ([ $MAJOR -eq $MINIMUM_MAJOR ] && ([ $MINOR -gt $MINIMUM_MINOR ] || [ $MINOR -eq $MINIMUM_MINOR ])); then
            true
        else
            false
        fi
    fi
}

CMAKE_MINIMUM_VERSION=3.28.0
if (check_cmake_version $CMAKE_MINIMUM_VERSION cmake) || (check_cmake_version $CMAKE_MINIMUM_VERSION /opt/cmake-3.28.3-linux-x86_64/bin/cmake); then
    echo "Found CMake $CMAKE_MINIMUM_VERSION"
else
    echo "Could not find CMake >=$CMAKE_MINIMUM_VERSION."
    echo "Installing CMake 3.28.3..."
    curl -L -O https://github.com/Kitware/CMake/releases/download/v3.28.3/cmake-3.28.3-linux-x86_64.tar.gz
    sudo mkdir -p /opt
    sudo tar -xzf cmake-3.28.3-linux-x86_64.tar.gz -C /opt
    if [[ ":$PATH:" != *":/opt/cmake-3.28.3-linux-x86_64/bin:"* ]]; then
        echo -e '\n#CARLA CMake 3.28.3\nPATH=/opt/cmake-3.28.3-linux-x86_64/bin:$PATH' >> ~/.bashrc
        export PATH=/opt/cmake-3.28.3-linux-x86_64/bin:$PATH
    fi
    rm -rf cmake-3.28.3-linux-x86_64.tar.gz
    echo "Installed CMake 3.28.3."
fi
