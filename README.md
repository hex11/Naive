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
- [ ] Documentation / tutorials
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

### .NET Core (3.1 or above)

Run `run.sh` or `run.bat` in `NaiveSocks_dotnetcore.zip/tar.gz`.

### Linux (with bundled runtime)

Install the latest stable version that bundled with .NET Core runtime for Linux x64:
```shell
nsocks_pack="https://github.com/hex11/Naive/releases/latest/download/NaiveSocks_dotnetcore_linux-x64.tar.gz" \
  wget $nsocks_pack -O nsocks.tar.gz \
  && sudo tar xvf nsocks.tar.gz -C /opt/nsocks/ && sudo sh /opt/nsocks/install.sh
```

### Linux

Ensure that the required [.NET Core](https://dotnet.microsoft.com/download) runtime is installed.

Install the latest stable version:
```shell
nsocks_pack="https://github.com/hex11/Naive/releases/latest/download/NaiveSocks_dotnetcore.tar.gz" \
# ...
```

Install the latest dev version:
```shell
nsocks_pack="https://ci.appveyor.com/api/projects/hex11/Naive/artifacts/bin%2Fupload%2FNaiveSocks_dotnetcore.tar.gz" \
# ...
```

### Android

Install [NaiveSocksAndroid.apk](https://github.com/hex11/Naive/releases/latest/download/NaiveSocksAndroid.apk) and run.

### Docker

```
docker run -it --network host -v $(pwd)/config:/app/config hex0011/naivesocks
```

## Configuration

See [NaiveSocks/naivesocks-example.tml](NaiveSocks/naivesocks-example.tml) for example.

### Configuration Paths

NaiveSocks finds the configuration file in the following paths, if no path specified by the command line argument `-c`.

* <b>\[Current working folder\]/</b>naivesocks.tml
* <b>\[Program folder\]/config/</b>naivesocks.tml
* <b>\[Program folder\]/</b>naivesocks.tml
* <b>\[User folder\]/.config/nsocks/</b>naivesocks.tml
* <b>\[User folder\]/.config/</b>naivesocks.tml
* <b>\[User AppData folder\]/nsocks/</b>naivesocks.tml
* <b>\[User AppData folder\]/</b>naivesocks.tml
* <b>\[User folder\]/nsocks/</b>naivesocks.tml
* <b>\[User folder\]/</b>naivesocks.tml


## Related Projects

`NaiveSvrLib`: HTTP server implementation and socket helpers

`NaiveZip`: A simple archiver that can bundle .NET Framework apps into a single excutable.

`Nett`: [A customized fork](https://github.com/hex11/Nett) of the TOML library [Nett](https://github.com/paiden/Nett)
