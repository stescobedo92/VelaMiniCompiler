# Securing the self-hosted registry

`Vela.Registry.Server` serves `.vlpkg` dependency archives. Its publication API
is distinct from the `.vpkg` gallery protocol used by `vela package push`.
Reads remain public; publication requires a configured publisher credential.

## Configure publication

Set `RegistryRoot` to a directory writable only by the registry service and its
administrator. Supply `Registry:ApiKey` from a secret configuration provider, or
`Registry__ApiKey` in the service environment. Use a random secret containing at
least 32 characters (maximum 512, with no whitespace). Do not commit that secret.

Without a key the server runs read-only. The one configured publisher has
administrative permission to publish any package; this is not a multi-tenant
publisher/namespace ownership model. Rotate the key by updating the secret and
restarting the service.

Publication requires HTTPS by default. Configure a Kestrel HTTPS endpoint and
certificate through standard ASP.NET Core configuration. For isolated local
development only, `Registry__AllowInsecureHttp=true` permits HTTP. Keep that
setting false on an untrusted network; a reverse proxy must not be allowed to
forge the authenticated transport decision through untrusted forwarded headers.

Accepted credentials are exactly one of:

- `Authorization: Bearer <key>`
- `X-Api-Key: <key>`
- `X-Vela-ApiKey: <key>`

Missing, incorrect, duplicate or ambiguous credentials produce HTTP 401.
Keys are compared using fixed-time SHA-256 digest comparisons and are not
included in application error messages.

## Publish an immutable version

Send the archive bytes as the body of:

```http
POST /packages/acme.demo/1.0.0
Authorization: Bearer <publisher-key>
Content-Type: application/zip
```

Package IDs start with a lowercase ASCII letter and contain lowercase letters,
digits, dots, underscores and hyphens (at most 128 characters). Consecutive dots,
trailing dots and Windows device names are rejected. Versions must be canonical
`major.minor.patch` numbers, matching the current restore client's version model.
The archive's `vela.toml` name and version must match the URL exactly.

The server stages the upload, performs bounded extraction and validates the
manifest, then publishes the archive and its metadata with a single directory
rename. Requests cannot observe a partially uploaded version. A version is
immutable: attempts to replace it, including concurrent publishers losing the
race, return HTTP 409. Publish a new version instead. The service account's
filesystem permissions must prevent other users from bypassing this policy.

Responses:

| Status | Meaning |
| --- | --- |
| 201 | Archive and manifest published successfully. |
| 400 | Invalid route identity, archive, manifest, expansion limit or incomplete body. A disconnected client may receive no response. |
| 401 | Missing/incorrect credentials or insecure publication transport. |
| 409 | The package version already exists. |
| 413 | Compressed upload exceeds its byte limit. |
| 429 | All upload validation slots are in use. |

Limits are configured through the `Registry` configuration section (use `__`
instead of `:` in environment variables):

| Setting | Default | Accepted range |
| --- | --- | --- |
| `Registry:MaxPackageBytes` | 32 MiB | 1 byte–256 MiB |
| `Registry:MaxExpandedBytes` | 256 MiB | 1 byte–256 MiB |
| `Registry:MaxArchiveEntries` | 10,000 | 1–10,000 |
| `Registry:MaxConcurrentUploads` | 4 | 1–64 |

Archive validation rejects traversal, duplicate paths, symbolic links and other
special files. Expansion is limited while reading, not only by ZIP metadata.
The server also refuses reads and writes through filesystem links below its
configured storage root. Size limits bound each upload, not total registry
storage; administrators should monitor disk capacity.

## Signed metadata

The administrator publishes signed metadata under `RegistryRoot/tuf`:

- `root.json`: current root metadata.
- `1.root.json`, `2.root.json`, etc.: consecutive root versions needed by clients
  to verify key rotation against both the previous and the new root.
- `targets.json`: signed archive lengths and digests.

These files are publicly served at `/tuf/root.json`, `/tuf/{version}.root.json`
and `/tuf/targets.json`. Versioned root names accept only positive canonical
integers. They are not writable through the package upload endpoint.

Package publication does not sign metadata or distribute signing keys. Update
targets using the administrator's signing process before clients can restore the
new package under a trusted-root policy. Provision the initial trusted root to
clients over an independent trusted channel; downloading `root.json` from this
same registry alone does not establish its identity.

## Verification

`tests/Vela.Registry.Server.Tests` uses local Kestrel instances to check
authentication, transport policy, public reads, path validation, archive identity,
upload/expansion limits, truncated bodies, immutable concurrent publication and
versioned roots. It does not contact a deployed registry.
