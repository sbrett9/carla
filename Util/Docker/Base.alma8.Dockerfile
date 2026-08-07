# AlmaLinux 8 build environment for CARLA UE5 (RHEL8-compatible).
#
# AlmaLinux 8 is binary-compatible with RHEL 8 (glibc 2.28), so artifacts built here run on a
# RHEL 8 deployment target. This matters for the parts of the build that use the SYSTEM compiler
# rather than the engine's hermetic clang toolchain -- most importantly SUMO `netconvert`, which
# CarlaSetup.sh builds with the container's gcc and which would otherwise pick up a newer glibc
# (e.g. from an AlmaLinux 10 image) and fail to run on RHEL 8.
#
# The build context is tiny (only the optional ca-certs/ trust anchors are COPYed in), so it can be
# built from anywhere, e.g.:
#   podman build -f Util/Docker/Base.alma8.Dockerfile -t carla-base:alma8 Util/Docker
#
# This image installs PREREQUISITES only. Unreal Engine and CARLA are built at container runtime
# into a mounted/volume workspace (see Docs/build_container_rhel8.md), so the heavy build artifacts
# are not baked into image layers.
#
# On a corporate network that intercepts TLS, the image bootstraps trust by retrieving the
# interception certificate chain from well-known public hosts and installing the issuing CAs as
# trust anchors. See the CA TRUST section below for what that does and does not guarantee.
#
# As a last resort, build with TLS verification disabled entirely (leaves the whole image
# untrusting, so avoid it unless the bootstrap cannot be made to work):
#   podman build --build-arg INSECURE_SSL=1 -f Util/Docker/Base.alma8.Dockerfile -t carla-base:alma8 Util/Docker

FROM almalinux:8

# Optional: disable TLS certificate verification for ALL package fetches during the image build.
# Off by default. Set --build-arg INSECURE_SSL=1 only for trusted but TLS-intercepted networks
# where the automatic CA bootstrap fails.
ARG INSECURE_SSL=0

# Hosts probed for the interception certificate. One is an operating-system mirror (covers dnf),
# one is the Python package index (covers pip); a proxy may present different chains for each, and
# installing both means neither path has to fall back to disabled verification.
ARG CA_PROBE_HOSTS="mirrors.almalinux.org pypi.org"

# ---------------------------------------------------------------------------
# CA TRUST
#
# What this does: opens a TLS connection with verification disabled, reads back the certificate
# chain the network presents, discards the leaf, and installs the remaining issuer certificates as
# system trust anchors. On an intercepted network those issuers are the corporate CA.
#
# What it does not do: authenticate that CA. Whatever the network offers on the first connection is
# what gets trusted, so this is only sound on a network already trusted by other means -- which is
# the case for this build host. The verification step at the end of the block is the real safeguard:
# it re-runs the fetch with verification switched back on and fails the build if trust was not
# actually established, so the image can never be published in a silently untrusting state.
#
# The leaf certificate is dropped deliberately. Proxies reissue leaves every few months; anchoring
# one would make the image start failing on a schedule that has nothing to do with this repository.
# Issuer CAs are valid for years.
#
# To skip the network probe entirely, drop PEM files into Util/Docker/ca-certs/ and they are
# installed as anchors first. That is the more auditable option if the CA is ever published to
# configuration management.
# ---------------------------------------------------------------------------
COPY ca-certs/ /tmp/ca-certs/

RUN set -eux; \
    ANCHORS=/etc/pki/ca-trust/source/anchors; \
    if [ "$INSECURE_SSL" = "1" ]; then \
        echo "sslverify=False" >> /etc/dnf/dnf.conf; \
        echo "[CA Trust] INSECURE_SSL=1 -- TLS verification DISABLED for this image"; \
    else \
        for pem in /tmp/ca-certs/*.pem /tmp/ca-certs/*.crt; do \
            [ -f "$pem" ] || continue; \
            echo "[CA Trust] Installing supplied anchor: $(basename "$pem")"; \
            cp "$pem" "$ANCHORS/"; \
        done; \
        update-ca-trust extract; \
        echo "sslverify=False" >> /etc/dnf/dnf.conf; \
        dnf -y install openssl ca-certificates; \
        for host in $CA_PROBE_HOSTS; do \
            echo "[CA Trust] Probing $host for an interception certificate chain"; \
            echo | openssl s_client -showcerts -servername "$host" -connect "$host":443 2>/dev/null \
                | awk '/BEGIN CERTIFICATE/ { n++ } n > 1' \
                > "$ANCHORS/network-issuers-$host.pem"; \
            if [ ! -s "$ANCHORS/network-issuers-$host.pem" ]; then \
                echo "[CA Trust] No issuer certificates returned by $host"; \
                rm -f "$ANCHORS/network-issuers-$host.pem"; \
            fi; \
        done; \
        update-ca-trust extract; \
        sed -i '/sslverify=False/d' /etc/dnf/dnf.conf; \
        for host in $CA_PROBE_HOSTS; do \
            echo "[CA Trust] Verifying https://$host with verification enabled"; \
            curl --fail --silent --show-error --head "https://$host/" -o /dev/null; \
        done; \
        echo "[CA Trust] Trust established for: $CA_PROBE_HOSTS"; \
    fi; \
    rm -rf /tmp/ca-certs; \
    dnf -y install dnf-plugins-core epel-release; \
    dnf config-manager --set-enabled powertools; \
    dnf -y makecache

# update-ca-trust only reaches consumers that read the system bundle. Python (requests/pip), Node
# and git each ship or prefer their own store, so point them at the system bundle explicitly --
# otherwise pip fails against an intercepted package index even though dnf and curl work.
ENV SSL_CERT_FILE=/etc/pki/tls/certs/ca-bundle.crt \
    REQUESTS_CA_BUNDLE=/etc/pki/tls/certs/ca-bundle.crt \
    PIP_CERT=/etc/pki/tls/certs/ca-bundle.crt \
    GIT_SSL_CAINFO=/etc/pki/tls/certs/ca-bundle.crt \
    NODE_EXTRA_CA_CERTS=/etc/pki/tls/certs/ca-bundle.crt

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
