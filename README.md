# ForEach.Infrastructure.External.Banking

Bank file formats used in Serbia, as five NuGet packages from one repository: the shared standard
readers and the payment-order model, and one package per bank's own export and import formats.
Targets net8.0 and net10.0, no dependencies beyond the framework.

| Package | What it holds |
|---|---|
| `ForEach.Infrastructure.External.Banking` | ISO 20022 camt.053 and MT940 statement readers, `BankStatement`, `PaymentOrder` / `ForeignPaymentOrder` |
| `ForEach.Infrastructure.External.Banking.Asseco` | Asseco iBank / OfficeBanking XML and text statements, payment-order export |
| `ForEach.Infrastructure.External.Banking.Halcom` | Halcom Hal E-Bank fixed-width and extended statements, payment-order export (CP1250) |
| `ForEach.Infrastructure.External.Banking.Rol` | Raiffeisen OnLine (ROL) XML and text statements, dinar and foreign payment orders |
| `ForEach.Infrastructure.External.Banking.Otp` | OTP banka Srbija statement import |

Each bank package depends on the shared `Banking` package of the same version. Every package ships
its own README with the format details. Sibling of
[eOtpremnica](https://github.com/foreach-rs/eOtpremnica), with the same layout and release process.

## Layout

```
src/ForEach.Infrastructure.External.Banking/
src/ForEach.Infrastructure.External.Banking.Asseco/
src/ForEach.Infrastructure.External.Banking.Halcom/
src/ForEach.Infrastructure.External.Banking.Rol/
src/ForEach.Infrastructure.External.Banking.Otp/
tests/ForEach.Infrastructure.External.Banking.UnitTests/
tests/ForEach.Infrastructure.External.Banking.Asseco.UnitTests/
tests/ForEach.Infrastructure.External.Banking.Halcom.UnitTests/
tests/ForEach.Infrastructure.External.Banking.Rol.UnitTests/
tests/ForEach.Infrastructure.External.Banking.Otp.UnitTests/
.github/workflows/ci.yml        build + test + pack check on push to main and on pull requests
.github/workflows/publish.yml   on tag v*: test, pack all five with the tag's version, push to nuget.org
```

## Releasing

```
git tag v0.1.0
git push origin v0.1.0
```

The tag is the version of all five packages (`v0.2.0` → `0.2.0`; `v0.2.0-beta.1` → a
prerelease). The `<Version>` in each csproj is only the fallback for a local `dotnet pack`. A push
to `main` builds and tests only.

After the first release, set `<PackageValidationBaselineVersion>` in each csproj to that version so
a breaking change against the published API fails the build.

### One-time setup: Trusted Publishing on nuget.org

The publish workflow holds no API key. It authenticates with a GitHub OIDC token that nuget.org
exchanges for a one-hour key, which works only once a policy on nuget.org trusts this workflow:

1. On nuget.org, open your profile menu → **Trusted Publishing** → add a policy, owned by the
   account that owns the `ForEach.*` packages (the `ForEachRS` organisation):
   - Repository Owner: `foreach-rs`
   - Repository: `Banking`
   - Workflow File: `publish.yml` (the file name only)
   - Environment: leave empty
2. In this repository, Settings → Secrets and variables → Actions → **Variables**, set
   `NUGET_USER` to the nuget.org profile name that owns the policy (`ForEachRS`).

A policy created for a private repository is only provisionally active for seven days until the
first successful publish; push a tag within that window.

## Consuming the packages

Published to nuget.org, so no feed configuration is needed. Take the bank you integrate; the
shared package comes with it:

```
dotnet add package ForEach.Infrastructure.External.Banking.Rol
```

## Local build

```
dotnet build
dotnet test
dotnet pack -c Release -o packages
```
