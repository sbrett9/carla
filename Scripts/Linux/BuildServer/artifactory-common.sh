#!/usr/bin/env bash
#
# artifactory-common.sh - shared Artifactory access for the BuildServer helper scripts.
#
# Source this (do not execute it). It provides:
#
#   art_curl <curl args...>        curl with authentication, retries and fail-on-error
#   art_url <repo/path>            absolute URL for a repository path
#   ARTIFACTORY_BASE_URL           service root, overridable from the environment
#
# Authentication: the token is read from a file on the runner host (mode 600) so it
# never appears in workflow YAML, command lines or logs. Both credential formats are
# supported automatically:
#
#   - a legacy Artifactory API key  -> sent as the X-JFrog-Art-Api header
#   - an identity / access token    -> sent as an Authorization: Bearer header
#
# Identity tokens are JWTs and start with "eyJ", which is how they are told apart.
# API keys are removed entirely in Artifactory 7.77 and later, so new runners should be
# provisioned with an identity token; this detection means the switch needs no code change.
# Set ARTIFACTORY_AUTH to "bearer" or "apikey" to force one scheme.
#

ARTIFACTORY_BASE_URL="${ARTIFACTORY_BASE_URL:-https://iasartifact.sncorp.com:8443/artifactory}"
ARTIFACTORY_CREDENTIALS="${ARTIFACTORY_CREDENTIALS:-/home/catgithubrunner/.artifactory/credentials}"

# Retry transient network failures. The Unreal Engine archive is ~25 GB and the CARLA
# distribution is comparable; a single dropped connection must not fail an hours-long
# build, so every request retries with backoff and downloads resume where they stopped.
ARTIFACTORY_CURL_RETRIES="${ARTIFACTORY_CURL_RETRIES:-5}"

_art_header_file=""

art_load_credentials() {
    if [ -n "$_art_header_file" ] && [ -f "$_art_header_file" ]; then
        return 0
    fi

    if [ ! -f "$ARTIFACTORY_CREDENTIALS" ]; then
        echo "ERROR: Artifactory credentials file not found: $ARTIFACTORY_CREDENTIALS" >&2
        return 1
    fi

    local token
    token="$(tr -d '\r\n' < "$ARTIFACTORY_CREDENTIALS")"

    if [ -z "$token" ]; then
        echo "ERROR: Artifactory credentials file is empty: $ARTIFACTORY_CREDENTIALS" >&2
        return 1
    fi

    local scheme="${ARTIFACTORY_AUTH:-auto}"
    if [ "$scheme" = "auto" ]; then
        case "$token" in
            eyJ*) scheme="bearer" ;;
            *)    scheme="apikey" ;;
        esac
    fi

    local header
    case "$scheme" in
        bearer) header="Authorization: Bearer ${token}" ;;
        apikey) header="X-JFrog-Art-Api: ${token}" ;;
        *)
            echo "ERROR: unknown ARTIFACTORY_AUTH scheme: $scheme (expected bearer or apikey)" >&2
            return 1
            ;;
    esac

    # The token goes into a mode-600 header file rather than onto curl's command line,
    # where any other user on the build host could read it out of /proc.
    _art_header_file="$(mktemp "${TMPDIR:-/tmp}/artifactory-auth.XXXXXX")"
    chmod 600 "$_art_header_file"
    printf '%s\n' "$header" > "$_art_header_file"
    trap 'rm -f "$_art_header_file"' EXIT
}

art_url() {
    printf '%s/%s' "$ARTIFACTORY_BASE_URL" "${1#/}"
}

# curl with authentication and retry policy applied.
art_curl() {
    art_load_credentials || return 1
    curl --fail --show-error --silent \
        --retry "$ARTIFACTORY_CURL_RETRIES" \
        --retry-delay 5 \
        --retry-connrefused \
        --connect-timeout 30 \
        -H "@${_art_header_file}" "$@"
}
