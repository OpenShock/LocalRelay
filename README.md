# OpenShock Local Relay

A module for [OpenShock Desktop](https://github.com/OpenShock/Desktop) that relays shock, vibrate, and sound commands from the OpenShock cloud to locally-connected hubs over a serial (USB) connection — letting you use OpenShock without putting your hub on Wi-Fi.

## What it does

`LocalRelay` registers as a desktop module and:

- Authenticates with the OpenShock API using a token with the `Devices_Auth` permission.
- Subscribes to the OpenShock live control stream for your account.
- Forwards incoming commands to a physical OpenShock hub connected over a serial port.

The module adds two tabs to OpenShock Desktop:

- **Hub** — pair the relay with a hub registered to your account.
- **Serial** — pick the serial port for the connected hub and monitor the link.

## Requirements

- Windows, Linux, or macOS with [OpenShock Desktop](https://github.com/OpenShock/Desktop) installed.
- .NET 10 runtime (bundled with OpenShock Desktop).
- An OpenShock hub flashed with firmware that supports serial control, connected via USB.
- An OpenShock account and an API token with the `Devices_Auth` permission.

## Installation

1. Grab the latest `OpenShock.LocalRelay.module.zip` from the [Releases](../../releases) page.
2. Drop it into your OpenShock Desktop modules folder (or install it via the Desktop app's module manager).
3. Restart OpenShock Desktop.
4. Open the **Local Relay** module, pair your hub on the **Hub** tab, then select its port on the **Serial** tab.

## Building from source

```bash
dotnet restore LocalRelay.slnx
dotnet publish LocalRelay/LocalRelay.csproj --configuration Release -o publish
```

The packed module ends up at `publish/OpenShock.LocalRelay.module.zip`.

During local development, `copy-module-dll.cmd` can copy the built assemblies into your OpenShock Desktop module directory for quick iteration.

## License

See [LICENSE](LICENSE).
