Build status:

[![Appveyor build status](https://ci.appveyor.com/api/projects/status/brdxtqcek50ny38b?svg=true)](https://ci.appveyor.com/project/hex11/naive) (latest commit)
[![Appveyor build status](https://ci.appveyor.com/api/projects/status/brdxtqcek50ny38b/branch/master?svg=true)](https://ci.appveyor.com/project/hex11/naive/branch/master) (master branch)

[**GitHub Releases**](https://github.com/hex11/Naive/releases)

# Feature & Future

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
  - [ ] Advanced configuration UI
- [x] Linux
  - [x] Async socket implementation using epoll
  - [x] 'tproxy' (in) - Transparent proxy support
- [x] Android
  - [x] UI: Status / Adapters / Connections / Log / Console
  - [x] Simple text configuration editor
  - [ ] Advanced configuration UI
  - [x] VpnService implementation using tun2socks
- [ ] Windows
  - [ ] GUI with systray
- [ ] UDP support
  - [ ] 'direct'
  - [ ] 'socks5'
  - [ ] 'naive'

# Download & Run

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

# Configration Example

See [NaiveSocks/naivesocks-example.tml](NaiveSocks/naivesocks-example.tml)
