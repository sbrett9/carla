# Additional CA trust anchors for the build image

Any `*.pem` or `*.crt` file placed in this directory is installed as a system trust anchor
in `Base.alma8.Dockerfile` before the image reaches the network.

This directory is normally empty. The image bootstraps trust by reading the certificate
chain the network presents (see the CA TRUST section of the Dockerfile), which needs no
configuration but trusts whatever it is offered on first contact.

Drop the corporate root CA here when you want that trust to be explicit and auditable
rather than discovered:

```sh
cp /etc/pki/ca-trust/source/anchors/corporate-root.pem Util/Docker/ca-certs/
```

Certificates committed here are public keys only and carry nothing secret.
