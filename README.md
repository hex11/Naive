# The NaiveSocks with Related Projects

NaiveSocks is an all-in-one networking tool operating at the transport layer.

It can serve as proxy server/client, port forwarder, DNS server/client, HTTP file server and more...

## Build Status

[![Appveyor build status](https://img.shields.io/appveyor/build/hex11/Naive/dev?label=dev)](https://ci.appveyor.com/project/hex11/naive)
[![Appveyor build status](https://img.shields.io/appveyor/build/hex11/Naive/master?label=master)](https://ci.appveyor.com/project/hex11/naive/branch/master)
[![Docker Cloud Build Status](https://img.shields.io/docker/cloud/build/hex0011/naivesocks)](https://hub.docker.com/r/hex0011/naivesocks)

[**GitHub Releases**](https://github.com/hex11/Naive/releases)

## Feature & Future

- [x] Interactive CLI
- [x] Configuration file using TOML
- [x] Supported protocols
  - [x] 'direct' (in/out)
  - [x] 'socks5' (in/out)
  - [x] 'http' (in/out)
  - [x] 'tlssni' (in)
  - [x] 'ss' (in/out)
  - [x] 'naive' (in/out)
    - [x] Multiplexing (for 0-RTT handshake) and optional inversed multiplexing (for bandwidth)
    - [x] Optional compression
- [x] 'dns' - simple DNS server (UDP) and client (UDP/DoH)
- [x] 'router'
  - [x] [ABP Filter](https://adblockplus.org/filter-cheatsheet) support
  - [x] Auto download ABP Filters from URL
- [x] Web server
  - [x] "Virtual host" / path rules support
  - [x] 'webfile' - Static website / file server
  - [x] 'webauth' - Basic HTTP authentication
  - [x] 'webcon' - Web console
- [x] Linux
  - [x] Async socket implementation using epoll
  - [x] 'tproxy' (in) - Transparent proxy support
- [x] Android
  - [x] UI: Status / Adapters / Connections / Log / Console
  - [x] Simple text configuration editor
  - [x] VpnService implementation using tun2socks
  - [ ] Configuration management UI
- [ ] Windows
  - [x] UI: Connections / Log / Console
  - [ ] Systray
  - [ ] Configuration management UI
- [ ] UDP support
  - [ ] 'direct'
  - [ ] 'socks5'
  - [ ] 'naive'

## Download & Run

Download from
[**GitHub Releases**](https://github.com/hex11/Naive/releases)
or
[Latest build on Appveyor](https://ci.appveyor.com/project/hex11/naive/build/artifacts)

### .NET Framework (4.5.1 or above) / Mono

Run `NaiveSocks.exe` in `NaiveSocks_net45.zip/tar.gz` or `NaiveSocks_SingleFile.exe`.

### .NET Core (2.1 or above)

Run `run.sh` or `run.bat` in `NaiveSocks_dotnetcore.zip/tar.gz`.

### Android

Install `NaiveSocksAndroid.apk` and run.

### Docker

```
docker run -it -v ./naivesocks.tml:/app/naivesocks.tml hex0011/naivesocks
```

## Configuration Example

See [NaiveSocks/naivesocks-example.tml](NaiveSocks/naivesocks-example.tml)

## Related Projects

`NaiveSvrLib`: HTTP server implementation and socket helpers

`NaiveZip`: A simple archiver that can bundle .NET Framework apps into a single excutable.

`Nett`: [A customized fork](https://github.com/hex11/Nett) of the TOML library [Nett](https://github.com/paiden/Nett)
