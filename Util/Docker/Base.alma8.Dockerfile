# AlmaLinux 8 build environment for CARLA UE5 (RHEL8-compatible).
#
# AlmaLinux 8 is binary-compatible with RHEL 8 (glibc 2.28), so artifacts built here run on a
# RHEL 8 deployment target. This matters for the parts of the build that use the SYSTEM compiler
# rather than the engine's hermetic clang toolchain -- most importantly SUMO `netconvert`, which
# CarlaSetup.sh builds with the container's gcc and which would otherwise pick up a newer glibc
# (e.g. from an AlmaLinux 10 image) and fail to run on RHEL 8.
#
# The build context is tiny (no files are COPYed in), so it can be built from anywhere, e.g.:
#   podman build -f Util/Docker/Base.alma8.Dockerfile -t carla-base:alma8 Util/Docker
#
# This image installs PREREQUISITES only. Unreal Engine and CARLA are built at container runtime
# into a mounted/volume workspace (see Docs/build_container_rhel8.md), so the heavy build artifacts
# are not baked into image layers.
#
# On a corporate network with internal CA/TLS interception, the build automatically bootstraps
# trust by retrieving the certificate chain from the Artifactory server (recommended).
#
# Alternatively, build with TLS verification disabled (not recommended):
#   podman build --build-arg INSECURE_SSL=1 -f Util/Docker/Base.alma8.Dockerfile -t carla-base:alma8 Util/Docker

FROM almalinux:8

# Optional: disable TLS certificate verification for ALL package fetches during the image build.
# Off by default. Set --build-arg INSECURE_SSL=1 only for trusted but TLS-intercepted networks
# where the automatic CA bootstrap fails.
ARG INSECURE_SSL=0

# Bootstrap corporate CA trust and configure repositories.
# For TLS-intercepting corporate networks (default): installs openssl with TLS verification
# temporarily disabled, uses it to retrieve the proxy's certificate chain, then re-enables
# verification for all subsequent operations.
# For INSECURE_SSL=1 (fallback): disables TLS verification globally.
RUN if [ "$INSECURE_SSL" != "1" ]; then \
        echo "sslverify=False" >> /etc/dnf/dnf.conf && \
        dnf -y install openssl && \
        echo | openssl s_client -showcerts -servername mirrors.almalinux.org \
            -connect mirrors.almalinux.org:443 2>/dev/null | \
            awk '/BEGIN CERTIFICATE/,/END CERTIFICATE/ {print}' > /etc/pki/ca-trust/source/anchors/corporate-proxy-chain.pem && \
        update-ca-trust extract && \
        sed -i '/sslverify=False/d' /etc/dnf/dnf.conf && \
        echo "[CA Trust] Installed corporate proxy certificate chain"; \
    else \
        echo "sslverify=False" >> /etc/dnf/dnf.conf && \
        echo "[INSECURE_SSL] dnf TLS verification DISABLED"; \
    fi \
    && dnf -y install dnf-plugins-core epel-release \
    && dnf config-manager --set-enabled powertools \
    && dnf -y makecache

# ---------------------------------------------------------------------------
# UTF-8 locale (CMake archive extraction of e.g. Boost needs it for non-ASCII filenames).
# ---------------------------------------------------------------------------
RUN dnf -y install glibc-langpack-en
ENV LANG=en_US.UTF-8
ENV LC_ALL=en_US.UTF-8

# ---------------------------------------------------------------------------
# Toolchain + libraries.
#   - Development Tools group: gcc/g++/make/autoconf/automake/libtool/...
#   - ninja-build, nasm, patchelf: CARLA + cesium-native (vcpkg) build helpers
#   - xerces-c-devel, proj-devel: SUMO netconvert (proj-devel pulls proj, which provides proj.db
#     at /usr/share/proj; there is no separate proj-data package on EL8, unlike Debian/Ubuntu)
#   - openssl-devel: Fast-DDS (ROS2) build
#   - the libpng/tiff/jpeg/nss/at-spi2/xkbcommon/gbm/pango/alsa/vulkan/SDL2 set: UE5 editor +
#     CARLA Python API image libs
#   - zip/unzip/tar/curl/wget/pkgconf/perl: vcpkg + general build plumbing
# ---------------------------------------------------------------------------
RUN dnf -y groupinstall "Development Tools" \
    && dnf -y install \
        gcc gcc-c++ make ninja-build \
        nasm patchelf \
        git git-lfs openssh-clients rsync sed which \
        xdg-user-dirs pigz \
        curl wget zip unzip tar \
        libtool autoconf automake pkgconf-pkg-config perl \
        xerces-c-devel proj-devel \
        openssl-devel libxml2-devel \
        libpng-devel libtiff-devel libjpeg-turbo-devel \
        nss-devel at-spi2-atk-devel libxkbcommon-devel \
        mesa-libgbm-devel pango-devel alsa-lib-devel \
        vulkan-loader SDL2-devel \
        tzdata \
    && dnf clean all

RUN git lfs install --system

# ---------------------------------------------------------------------------
# CMake >= 3.28 (CARLA's configure enforces it). AlmaLinux 8 ships an older CMake, so install the
# Kitware binary, mirroring the Ubuntu base image.
# ---------------------------------------------------------------------------
RUN CURL_K=""; [ "$INSECURE_SSL" = "1" ] && CURL_K="-k"; \
    curl $CURL_K -L -O https://github.com/Kitware/CMake/releases/download/v3.28.3/cmake-3.28.3-linux-x86_64.tar.gz \
    && mkdir -p /opt \
    && tar -xzf cmake-3.28.3-linux-x86_64.tar.gz -C /opt \
    && rm -f cmake-3.28.3-linux-x86_64.tar.gz
ENV PATH=/opt/cmake-3.28.3-linux-x86_64/bin:$PATH

# ---------------------------------------------------------------------------
# .NET SDK 10 (CarlaNet targets .NET 10; build_wheel.sh runs `dotnet publish`). AlmaLinux 8
# AppStream carries dotnet-sdk-10.0 directly. If a future minor ever drops it, add Microsoft's
# feed first: rpm -Uvh https://packages.microsoft.com/config/rhel/8/packages-microsoft-prod.rpm
# ---------------------------------------------------------------------------
RUN dnf -y install dotnet-sdk-10.0 \
    && dnf clean all

# ---------------------------------------------------------------------------
# Python 3.11 (AlmaLinux 8's default python3 is 3.6, too old for `python -m build`). Do NOT
# repoint the system /usr/bin/python3 (dnf depends on it); instead expose 3.11 via /usr/local/bin,
# which precedes /usr/bin on PATH, and tell build_wheel.sh to use it via $PYTHON.
# ---------------------------------------------------------------------------
RUN dnf -y install python3.11 python3.11-devel python3.11-pip \
    && dnf clean all \
    && ln -sf /usr/bin/python3.11 /usr/local/bin/python3 \
    && ln -sf /usr/bin/python3.11 /usr/local/bin/python
ENV PYTHON=python3.11

# Wheel tooling for CarlaNet (the default CarlaNet-first build). The legacy PythonAPI is OFF by
# default; if you enable it (--with-python-api), install the repo's requirements at runtime from
# the mounted checkout: python3.11 -m pip install -r requirements.txt
RUN PIP_TRUST=""; [ "$INSECURE_SSL" = "1" ] && PIP_TRUST="--trusted-host pypi.org --trusted-host files.pythonhosted.org"; \
    python3.11 -m pip install $PIP_TRUST --upgrade pip build

WORKDIR /workspaces
